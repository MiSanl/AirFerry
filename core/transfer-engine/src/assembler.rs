//! Large-transfer segment assembly.
//!
//! A large file is split into N independently-compressed, independently
//! RaptorQ-encoded segments (fixed `SEGMENT_RAW_BYTES` raw bytes each). Each
//! segment is a *separate* wire session carrying its own child session id (see
//! [`crate::SessionId::derive_segment`]) and a descriptor-v4 frame whose
//! `SegmentMeta` describes the root transfer and the segment's canonical range.
//!
//! The core [`crate::ReceiverSession`] therefore recovers one segment at a time
//! with **no protocol or 32 MiB-budget change** — each segment is an ordinary
//! single object. [`TransferAssembler`] is the receiving-side coordinator: it
//! accumulates the recovered *uncompressed* bytes of each segment, verifies each
//! segment's length and SHA-256 against its descriptor-v4 `SegmentMeta`, and
//! once every segment has arrived reassembles the full original file by
//! `original_offset`.
//!
//! The assembler is deliberately **memory-inclusive**: for a file of N segments
//! it holds the whole decoded file in RAM. Hosts that need bounded memory should
//! stream each completed segment straight to disk (a `.partial` file at the
//! canonical offset) and only use this assembler to validate / reassemble in
//! memory when the whole file fits their budget. Product hosts use durable
//! segment storage instead: Web persists verified segment blobs in IndexedDB;
//! Android/Windows write a task-owned `.partial` file at canonical offsets.

use crate::descriptor::FileMeta;
use crate::segment::{SegmentMeta, MAX_SEGMENT_COUNT, SEGMENT_RAW_BYTES};
use crate::{Error, Result};
use sha2::{Digest, Sha256};
use std::vec::Vec;

/// Hard ceiling for the convenience in-memory reassembler. Native hosts use a
/// disk-backed positioned writer for larger roots; keeping this bounded avoids
/// turning an otherwise valid 1 TiB descriptor into a fatal allocation.
const MAX_IN_MEMORY_ASSEMBLY_BYTES: u64 = 256 * 1024 * 1024;

/// In-memory, per-segment receipt state for one logical file.
///
/// Each entry holds the recovered *uncompressed* bytes of that segment once it
/// arrives and passes validation. `None` means "not yet received".
pub struct TransferAssembler {
    root_session_id: u128,
    /// Display name of the root file (from the first segment's file_meta).
    filename: String,
    /// Total number of segments (== segment_count in the first v4 descriptor).
    total_segments: u32,
    /// Total uncompressed size of the root file.
    root_original_size: u64,
    /// Per-segment uncompressed bytes. Index == segment_index.
    segments: Vec<Option<Vec<u8>>>,
    /// Per-segment descriptor metadata, used for length / SHA-256 validation.
    meta: Vec<Option<SegmentMeta>>,
    /// Number of segments currently received (== number of `Some` in `segments`).
    received_count: u32,
}

impl TransferAssembler {
    /// Start a new assembly from the first-observed descriptor-v4 segment.
    ///
    /// `seg`/`file_meta` come from the first segment whose descriptor is seen.
    /// The assembler allocates `seg.segment_count` slots up front so a hostile
    /// descriptor cannot force unbounded allocation later.
    pub fn new(seg: &SegmentMeta, file_meta: &FileMeta) -> Result<Self> {
        if seg.segment_count == 0 || seg.segment_count > MAX_SEGMENT_COUNT {
            return Err(Error::InvalidSegment("segment count out of range"));
        }
        let n = usize::try_from(seg.segment_count)
            .map_err(|_| Error::InvalidSegment("segment_count does not fit this platform"))?;
        // Sanity-bounds allocation against an untrusted descriptor. The protocol
        // ceiling (MAX_SEGMENT_COUNT) is a resource ceiling; additionally cap
        // the pre-allocation so a single hostile descriptor cannot claim a
        // gigantic count. `root_original_size / SEGMENT_RAW_BYTES` is the true
        // expected count (validated per-segment by `SegmentMeta::validate`).
        if seg.root_original_size == 0 {
            return Err(Error::InvalidSegment("segment count or root size is empty"));
        }
        let expected = expected_segment_count(seg.root_original_size)
            .ok_or(Error::InvalidSegment("segment count overflow"))?;
        if expected != seg.segment_count {
            return Err(Error::InvalidSegment(
                "segment count inconsistent with root size",
            ));
        }
        validate_segment_range(seg, file_meta, seg.root_original_size)?;

        let mut segments = Vec::new();
        segments
            .try_reserve_exact(n)
            .map_err(|_| Error::InvalidSegment("segment slot allocation failed"))?;
        segments.resize_with(n, || None);
        let mut meta = Vec::new();
        meta.try_reserve_exact(n)
            .map_err(|_| Error::InvalidSegment("segment metadata allocation failed"))?;
        meta.resize_with(n, || None);
        Ok(Self {
            root_session_id: seg.root_session_id,
            filename: file_meta.filename.clone(),
            total_segments: seg.segment_count,
            root_original_size: seg.root_original_size,
            segments,
            meta,
            received_count: 0,
        })
    }

    /// The root transfer id this assembler coordinates.
    pub fn root_session_id(&self) -> u128 {
        self.root_session_id
    }

    /// Display name of the root file.
    pub fn filename(&self) -> &str {
        &self.filename
    }

    /// Total uncompressed size of the root file.
    pub fn root_original_size(&self) -> u64 {
        self.root_original_size
    }

    /// Total number of segments in the root file.
    pub fn total_segments(&self) -> u32 {
        self.total_segments
    }

    /// Number of segments received and validated so far.
    pub fn received_segments(&self) -> u32 {
        self.received_count
    }

    /// Whether every segment has been received and validated.
    pub fn is_complete(&self) -> bool {
        self.received_count == self.total_segments
    }

    /// Whether `segment_index` has already been stored.
    pub fn has_segment(&self, segment_index: u32) -> bool {
        self.segments
            .get(segment_index as usize)
            .map(|s| s.is_some())
            .unwrap_or(false)
    }

    /// Store one validated segment's uncompressed bytes.
    ///
    /// Verifies:
    /// - `seg.segment_index` is in range and not a duplicate,
    /// - `seg.root_session_id` matches this assembler,
    /// - the segment's length matches its canonical range (`SegmentMeta` range),
    /// - the segment's SHA-256 matches the descriptor's `raw_sha256`.
    ///
    /// Returns `Ok(false)` if the segment is a duplicate (already received),
    /// `Ok(true)` if newly stored.
    pub fn add_segment(
        &mut self,
        seg: &SegmentMeta,
        file_meta: &FileMeta,
        bytes: Vec<u8>,
    ) -> Result<bool> {
        if seg.root_session_id != self.root_session_id {
            return Err(Error::InvalidSegment(
                "segment root id does not match assembler",
            ));
        }
        if seg.segment_count != self.total_segments {
            return Err(Error::InvalidSegment(
                "segment count does not match assembler",
            ));
        }
        if file_meta.filename != self.filename {
            return Err(Error::InvalidSegment(
                "segment filename does not match assembler",
            ));
        }
        // Validate the segment's canonical range and length.
        validate_segment_range(seg, file_meta, self.root_original_size)?;
        let idx = seg.segment_index as usize;
        // The recovered bytes are the *uncompressed* segment. Its length must
        // equal the segment's canonical raw length.
        let expected_len = segment_raw_len(self.root_original_size, seg.segment_index);
        if bytes.len() as u64 != expected_len {
            return Err(Error::InvalidSegment("segment length mismatch"));
        }
        // SHA-256 of the uncompressed segment bytes.
        let actual = Sha256::digest(&bytes);
        if actual.as_slice() != seg.raw_sha256 {
            return Err(Error::InvalidSegment("segment sha256 mismatch"));
        }
        if self.segments[idx].is_some() {
            if self.meta[idx].as_ref() != Some(seg) {
                return Err(Error::InvalidSegment(
                    "duplicate segment metadata conflicts with stored segment",
                ));
            }
            return Ok(false);
        }
        self.segments[idx] = Some(bytes);
        self.meta[idx] = Some(seg.clone());
        self.received_count += 1;
        Ok(true)
    }

    /// Reassemble the full original file by writing each stored segment at its
    /// canonical offset. Returns `None` until every segment has been received.
    pub fn reassemble(&self) -> Option<Vec<u8>> {
        if !self.is_complete() {
            return None;
        }
        if self.root_original_size > MAX_IN_MEMORY_ASSEMBLY_BYTES {
            return None;
        }
        let output_len = usize::try_from(self.root_original_size).ok()?;
        let mut out = Vec::new();
        out.try_reserve_exact(output_len).ok()?;
        out.resize(output_len, 0);
        for (i, slot) in self.segments.iter().enumerate() {
            let bytes = slot.as_ref()?;
            let off =
                usize::try_from(u64::try_from(i).ok()?.checked_mul(SEGMENT_RAW_BYTES)?).ok()?;
            let end = off.checked_add(bytes.len())?;
            if end > out.len() {
                return None;
            }
            out[off..end].copy_from_slice(bytes);
        }
        Some(out)
    }

    /// Per-segment range descriptors, for hosts that stream each segment to
    /// disk instead of keeping it in memory. Returns `(segment_index,
    /// original_offset, raw_len, sha256)` for every known segment.
    pub fn segment_ranges(&self) -> Vec<(u32, u64, u64, [u8; 32])> {
        let mut out = Vec::with_capacity(self.total_segments as usize);
        for seg in self.meta.iter().flatten() {
            out.push((
                seg.segment_index,
                seg.original_offset,
                segment_raw_len(self.root_original_size, seg.segment_index),
                seg.raw_sha256,
            ));
        }
        out
    }
}

/// Expected total segment count for a root file of `root_original_size` bytes
/// at fixed `SEGMENT_RAW_BYTES` granularity.
fn expected_segment_count(root_original_size: u64) -> Option<u32> {
    if root_original_size == 0 {
        Some(0)
    } else {
        let count = root_original_size
            .checked_sub(1)?
            .checked_div(SEGMENT_RAW_BYTES)?
            .checked_add(1)?;
        u32::try_from(count).ok()
    }
}

/// Canonical raw length of `segment_index` within a root file of `root_size`.
fn segment_raw_len(root_size: u64, segment_index: u32) -> u64 {
    let off = u64::from(segment_index) * SEGMENT_RAW_BYTES;
    let remaining = root_size.saturating_sub(off);
    remaining.min(SEGMENT_RAW_BYTES)
}

/// Cross-check a segment's descriptor metadata against the assembler's root
/// (range consistency) — a subset of `SegmentMeta::validate` that only needs
/// the assembler's known root size, not the child session id.
fn validate_segment_range(
    seg: &SegmentMeta,
    file_meta: &FileMeta,
    root_original_size: u64,
) -> Result<()> {
    if seg.segment_count == 0 || seg.segment_count > MAX_SEGMENT_COUNT {
        return Err(Error::InvalidSegment("segment count out of range"));
    }
    if expected_segment_count(root_original_size) != Some(seg.segment_count) {
        return Err(Error::InvalidSegment(
            "segment count inconsistent with root size",
        ));
    }
    if seg.segment_index >= seg.segment_count {
        return Err(Error::InvalidSegment("segment index out of range"));
    }
    if seg.root_original_size != root_original_size {
        return Err(Error::InvalidSegment(
            "segment root size does not match assembler",
        ));
    }
    let expected_offset = u64::from(seg.segment_index)
        .checked_mul(SEGMENT_RAW_BYTES)
        .ok_or(Error::InvalidSegment("segment offset overflow"))?;
    if seg.original_offset != expected_offset {
        return Err(Error::InvalidSegment("segment offset is not canonical"));
    }
    let expected_len = segment_raw_len(root_original_size, seg.segment_index);
    if file_meta.original_size != expected_len {
        return Err(Error::InvalidSegment(
            "segment raw length inconsistent with root range",
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn file_meta(name: &str, size: u64) -> FileMeta {
        FileMeta {
            filename: name.into(),
            original_size: size,
            crc32: 0,
            compression: qr_protocol::compress::COMPRESSION_NONE,
            compressed_size: size,
            compressed_size_known: true,
            crc32_known: false,
        }
    }

    fn seg_meta(root: u128, idx: u32, count: u32, root_size: u64, bytes: &[u8]) -> SegmentMeta {
        let digest = Sha256::digest(bytes);
        let mut raw_sha256 = [0u8; 32];
        raw_sha256.copy_from_slice(&digest);
        SegmentMeta {
            root_session_id: root,
            segment_index: idx,
            segment_count: count,
            original_offset: u64::from(idx) * SEGMENT_RAW_BYTES,
            root_original_size: root_size,
            raw_sha256,
        }
    }

    #[test]
    fn assembles_multiple_segments_in_order() {
        let root = 0xabcdu128;
        // Segment 0 is a full 8 MiB segment; segment 1 is the final short tail.
        let seg0 = vec![7u8; SEGMENT_RAW_BYTES as usize];
        let seg1 = vec![9u8; 5 * 1024];
        let root_size = SEGMENT_RAW_BYTES + 5 * 1024;
        let s0 = seg_meta(root, 0, 2, root_size, &seg0);
        let s1 = seg_meta(root, 1, 2, root_size, &seg1);

        // `new` validates the first segment's file_meta against its canonical
        // range: for segment 0 that is a full 8 MiB (== seg0.len()).
        let mut a = TransferAssembler::new(&s0, &file_meta("big.bin", seg0.len() as u64)).unwrap();
        assert_eq!(a.total_segments(), 2);
        assert!(!a.is_complete());

        // Receive out of order: segment 1 first.
        assert!(a
            .add_segment(&s1, &file_meta("big.bin", seg1.len() as u64), seg1.clone())
            .unwrap());
        assert!(!a.is_complete());
        assert!(a
            .add_segment(&s0, &file_meta("big.bin", seg0.len() as u64), seg0.clone())
            .unwrap());
        assert!(a.is_complete());

        let full = a.reassemble().unwrap();
        assert_eq!(full.len(), root_size as usize);
        assert_eq!(&full[..seg0.len()], &seg0[..]);
        assert_eq!(&full[seg0.len()..], &seg1[..]);
    }

    #[test]
    fn rejects_duplicate_segment() {
        let root = 1u128;
        let data = vec![3u8; 512];
        let root_size = 512;
        let s0 = seg_meta(root, 0, 1, root_size, &data);
        let mut a = TransferAssembler::new(&s0, &file_meta("f.bin", root_size)).unwrap();
        assert!(a
            .add_segment(&s0, &file_meta("f.bin", data.len() as u64), data.clone())
            .unwrap());
        // Duplicate must be ignored (returns false), not error.
        assert!(!a
            .add_segment(&s0, &file_meta("f.bin", data.len() as u64), data.clone())
            .unwrap());
        assert_eq!(a.received_segments(), 1);
    }

    #[test]
    fn rejects_tampered_sha256() {
        let root = 2u128;
        let data = vec![5u8; 1024];
        let root_size = 1024;
        let mut s0 = seg_meta(root, 0, 1, root_size, &data);
        s0.raw_sha256[0] ^= 0xff; // corrupt the hash
        let mut a = TransferAssembler::new(&s0, &file_meta("t.bin", root_size)).unwrap();
        assert!(a
            .add_segment(&s0, &file_meta("t.bin", data.len() as u64), data.clone())
            .is_err());
        assert_eq!(a.received_segments(), 0);
    }

    #[test]
    fn rejects_wrong_root_id() {
        let data = vec![1u8; 64];
        let s0 = seg_meta(0xAAAAu128, 0, 1, 64, &data);
        let mut a = TransferAssembler::new(&s0, &file_meta("x.bin", 64)).unwrap();
        // Segment claims a different root id.
        let wrong = seg_meta(0xBBBBu128, 0, 1, 64, &data);
        assert!(a
            .add_segment(&wrong, &file_meta("x.bin", data.len() as u64), data.clone())
            .is_err());
    }

    #[test]
    fn rejects_assembler_when_count_inconsistent() {
        let data = vec![1u8; 64];
        // root_size 64 → expected 1 segment, but declare count=3.
        let bad = SegmentMeta {
            root_session_id: 1u128,
            segment_index: 0,
            segment_count: 3,
            original_offset: 0,
            root_original_size: 64,
            raw_sha256: Sha256::digest(&data).into(),
        };
        assert!(TransferAssembler::new(&bad, &file_meta("y.bin", 64)).is_err());
    }
}
