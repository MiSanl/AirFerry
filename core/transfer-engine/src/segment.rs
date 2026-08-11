//! Large-transfer segment metadata and invariants.
//!
//! A large file is split into independently compressed, independently
//! RaptorQ-encoded objects. The existing 60-byte frame header remains unchanged:
//! each object uses a deterministic child session id, while descriptor v4 carries
//! the stable root transfer id and the segment's canonical file range.

use crate::descriptor::FileMeta;
use qr_protocol::{frame::SessionIdRaw, SessionId};

/// Fixed uncompressed segment size used by descriptor v4.
///
/// Keeping this protocol constant makes offsets canonical, bounds memory on all
/// receivers, and limits a safe-pause rollback to at most one 8 MiB segment.
pub const SEGMENT_RAW_BYTES: u64 = 8 * 1024 * 1024;

/// Resource ceiling for a single root task (1 TiB at 8 MiB/segment). Hosts may
/// enforce lower product/storage limits, but must never allocate from an
/// untrusted descriptor count above this bound.
pub const MAX_SEGMENT_COUNT: u32 = 131_072;

/// Descriptor-v4 metadata for one independently recoverable segment.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SegmentMeta {
    /// Stable identity of the complete file/task.
    pub root_session_id: SessionIdRaw,
    /// Zero-based segment index.
    pub segment_index: u32,
    /// Total number of segments in the root file.
    pub segment_count: u32,
    /// Canonical byte offset in the uncompressed root file.
    pub original_offset: u64,
    /// Total uncompressed size of the root file.
    pub root_original_size: u64,
    /// SHA-256 of this segment's uncompressed bytes.
    pub raw_sha256: [u8; 32],
}

impl SegmentMeta {
    /// Validate descriptor-controlled segment coordinates before a host uses
    /// them for allocation or positioned writes.
    pub fn validate(
        &self,
        child_session_id: SessionIdRaw,
        file_meta: &FileMeta,
    ) -> Result<(), &'static str> {
        if self.segment_count == 0 || self.segment_count > MAX_SEGMENT_COUNT {
            return Err("segment count out of range");
        }
        if self.segment_index >= self.segment_count {
            return Err("segment index out of range");
        }
        if self.root_original_size == 0 {
            return Err("root original size must be non-zero");
        }

        let expected_count_u64 = self
            .root_original_size
            .checked_sub(1)
            .and_then(|n| n.checked_div(SEGMENT_RAW_BYTES))
            .and_then(|n| n.checked_add(1))
            .ok_or("segment count overflow")?;
        let expected_count = u32::try_from(expected_count_u64)
            .map_err(|_| "segment count exceeds protocol budget")?;
        if expected_count != self.segment_count {
            return Err("segment count inconsistent with root size");
        }

        let expected_offset = u64::from(self.segment_index)
            .checked_mul(SEGMENT_RAW_BYTES)
            .ok_or("segment offset overflow")?;
        if self.original_offset != expected_offset {
            return Err("segment offset is not canonical");
        }
        let remaining = self
            .root_original_size
            .checked_sub(expected_offset)
            .ok_or("segment offset exceeds root size")?;
        let expected_raw_length = remaining.min(SEGMENT_RAW_BYTES);
        if file_meta.original_size != expected_raw_length {
            return Err("segment raw length inconsistent with root range");
        }
        if !file_meta.compressed_size_known || file_meta.compressed_size == 0 {
            return Err("segment compressed size missing or empty");
        }
        if file_meta.compression == qr_protocol::compress::COMPRESSION_NONE
            && file_meta.compressed_size != file_meta.original_size
        {
            return Err("raw segment compressed size must equal raw length");
        }
        if SessionId::derive_segment(self.root_session_id, self.segment_index).0 != child_session_id
        {
            return Err("segment child session id mismatch");
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn file_meta(size: u64) -> FileMeta {
        FileMeta {
            filename: "large.bin".into(),
            original_size: size,
            crc32: 0,
            compression: qr_protocol::compress::COMPRESSION_NONE,
            compressed_size: size,
            compressed_size_known: true,
            crc32_known: true,
        }
    }

    #[test]
    fn validates_canonical_final_segment() {
        let root = 7u128;
        let raw = 1234u64;
        let segment = SegmentMeta {
            root_session_id: root,
            segment_index: 2,
            segment_count: 3,
            original_offset: SEGMENT_RAW_BYTES * 2,
            root_original_size: SEGMENT_RAW_BYTES * 2 + raw,
            raw_sha256: [9; 32],
        };
        let child = SessionId::derive_segment(root, 2).0;
        assert_eq!(segment.validate(child, &file_meta(raw)), Ok(()));
    }

    #[test]
    fn rejects_holes_and_wrong_child_identity() {
        let root = 9u128;
        let mut segment = SegmentMeta {
            root_session_id: root,
            segment_index: 1,
            segment_count: 2,
            original_offset: SEGMENT_RAW_BYTES,
            root_original_size: SEGMENT_RAW_BYTES + 5,
            raw_sha256: [1; 32],
        };
        let fm = file_meta(5);
        assert!(segment
            .validate(SessionId::derive_segment(root, 0).0, &fm)
            .is_err());
        segment.original_offset += 1;
        assert!(segment
            .validate(SessionId::derive_segment(root, 1).0, &fm)
            .is_err());
    }
}
