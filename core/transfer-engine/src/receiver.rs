//! Receiver-side session orchestration.

use crate::{progress::Progress, Error, Result};
use qr_protocol::{frame::SessionIdRaw, Frame};
use raptorq_core::{Decoder, ObjectMeta, Symbol, MAX_ORIGINAL_BYTES};
use std::collections::{HashMap, HashSet};
use std::vec::Vec;

/// Hard cap on pre-descriptor bootstrap symbols held in [`ReceiverSession`]'s
/// replay cache. Fountain codes tolerate drops; bounding the cache prevents a
/// CRC-valid hostile stream that never supplies a descriptor from OOM-killing
/// the receiver process. At T≈1400 this is ~16 MiB of payload storage.
pub const PRE_META_SYMBOL_CACHE_MAX: usize = 12_000;

/// A receiver session.
///
/// The session is created **without** object metadata: until an authoritative
/// descriptor frame arrives, every data frame is only buffered in a replay
/// cache. This avoids the previous design's bug of building a *guessed*
/// decoder from the frame-header totals (`derive_meta_from_totals`), whose
/// per-block layout never matched the real RaptorQ partitioning for multi-block
/// objects — feeding cached symbols into that wrong decoder corrupted progress
/// or silently stalled recovery. With the cache-only bootstrap, the first
/// descriptor frame rebuilds a *correct* decoder and replays all buffered
/// symbols into it.
///
/// For checkpoint persistence, build a [`crate::ResumeState`] via
/// [`ReceiverSession::save_state`] and reload with [`ReceiverSession::restore`]
/// (requires the `serde` feature for JSON helpers on `ResumeState`).
pub struct ReceiverSession {
    session_id: SessionIdRaw,
    /// Authoritative object metadata. `None` until a descriptor frame arrives.
    meta: Option<ObjectMeta>,
    decoder: Option<Decoder>,
    /// Whether `meta` came from an authoritative descriptor frame.
    meta_confirmed: bool,
    /// Whether the first valid descriptor has been accepted. Once set, both
    /// object and file metadata are immutable for the lifetime of the session.
    descriptor_seen: bool,
    /// File metadata learned from the descriptor frame (filename, size, CRC32).
    file_meta: crate::descriptor::FileMeta,
    received: Vec<HashSet<u32>>,
    /// Per-block count of distinct *source* symbols received (esi < K_block),
    /// maintained incrementally so [`refresh_decoded_counts`] is O(1)/frame
    /// instead of re-scanning every block's received-set each frame (which was
    /// O(n²) over a full transfer — a real slowdown on large files). Index is
    /// block position, matching `meta.blocks` / `received`.
    source_recv: Vec<u32>,
    /// Pre-descriptor bootstrap cache. Keyed by (sbn, esi); drained into the
    /// decoder once a validated descriptor confirms OTI. Hard-capped by
    /// [`PRE_META_SYMBOL_CACHE_MAX`] so a CRC-valid hostile stream without a
    /// descriptor cannot grow RAM without bound.
    symbol_cache: HashMap<(u32, u32), Vec<u8>>,
    progress: Progress,
    /// Consecutive session-mismatch count (reset on successful ingest).
    session_mismatch_streak: u32,
    /// Cached symbol_size from the frame header for approximate progress while
    /// `meta` is still `None`. Harmless to keep; only read before confirmation.
    pending_symbol_size: u32,
    /// Last [`assemble_result`] failure message (cleared on successful assemble).
    last_assemble_error: Option<String>,
}

impl ReceiverSession {
    /// Create a receiver for a known session + object metadata.
    ///
    /// `meta` is normally reconstructed from the first frame's totals; see
    /// [`ReceiverSession::from_first_frame`].
    pub fn new(session_id: SessionIdRaw, meta: ObjectMeta) -> Result<Self> {
        Self::new_confirmed(session_id, meta)
    }

    /// Create a receiver with **confirmed** (authoritative) metadata — data
    /// frames will be decoded immediately instead of buffered. Used when the
    /// caller already has the real OTI (e.g. from a descriptor).
    pub fn new_confirmed(session_id: SessionIdRaw, meta: ObjectMeta) -> Result<Self> {
        let decoder = Decoder::new(meta.clone())?;
        Ok(Self::build(session_id, Some(meta), Some(decoder), true))
    }

    /// Build a fully-confirmed session from authoritative metadata.
    fn build(
        session_id: SessionIdRaw,
        meta: Option<ObjectMeta>,
        decoder: Option<Decoder>,
        meta_confirmed: bool,
    ) -> Self {
        let total_symbols = meta
            .as_ref()
            .map(|m| m.blocks.iter().map(|b| b.num_source_symbols).sum())
            .unwrap_or(0);
        let total_blocks = meta.as_ref().map(|m| m.blocks.len() as u32).unwrap_or(0);
        let symbol_size = meta.as_ref().map(|m| m.symbol_size).unwrap_or(0);
        let received = meta
            .as_ref()
            .map(|m| m.blocks.iter().map(|_| HashSet::new()).collect())
            .unwrap_or_default();
        let source_recv = meta
            .as_ref()
            .map(|m| vec![0u32; m.blocks.len()])
            .unwrap_or_default();
        let progress = Progress {
            total_symbols,
            total_blocks,
            ..Progress::default()
        };
        Self {
            session_id,
            meta,
            decoder,
            meta_confirmed,
            descriptor_seen: false,
            file_meta: crate::descriptor::FileMeta::default(),
            received,
            source_recv,
            symbol_cache: HashMap::new(),
            progress,
            pending_symbol_size: symbol_size,
            session_mismatch_streak: 0,
            last_assemble_error: None,
        }
    }

    /// Human-readable reason the last [`assemble_result`] failed, if any.
    pub fn last_assemble_error(&self) -> Option<&str> {
        self.last_assemble_error.as_deref()
    }

    /// Bootstrap a receiver from the first observed frame.
    ///
    /// The session starts with **no** object metadata: data frames are buffered
    /// until a descriptor frame supplies the authoritative OTI. This replaces
    /// the old heuristic `derive_meta_from_totals`, which produced a wrong
    /// per-block layout for multi-block objects.
    pub fn from_first_frame(frame: &Frame) -> Self {
        Self::build(frame.header.session_id, None, None, false)
    }

    /// Create a "cache-only" receiver — no metadata yet, data frames buffered
    /// until the first descriptor arrives. Used by JNI (`receiverCreate`) and
    /// [`from_first_frame`] when no authoritative OTI is known.
    pub fn new_pending(session_id: SessionIdRaw) -> Self {
        Self::build(session_id, None, None, false)
    }

    pub fn session_id(&self) -> SessionIdRaw {
        self.session_id
    }
    pub fn total_symbols(&self) -> u32 {
        self.progress.total_symbols
    }
    pub fn is_complete(&self) -> bool {
        self.decoder.as_ref().is_some_and(|d| d.is_complete())
    }

    /// File metadata learned from descriptor frames (filename, size, CRC32).
    pub fn file_meta(&self) -> &crate::descriptor::FileMeta {
        &self.file_meta
    }

    /// True once the authoritative OTI has been received via a descriptor frame.
    /// Before this, data frames are only buffered (not decoded).
    pub fn is_meta_confirmed(&self) -> bool {
        self.meta_confirmed
    }

    /// Ingest a frame.
    ///
    /// - Descriptor frames (`FLAG_DESCRIPTOR`) update the session's object
    ///   metadata from the authoritative OTI carried in the payload, rebuilding
    ///   the decoder while preserving all already-received symbols.
    /// - Data frames are buffered until metadata is confirmed, then deduplicated
    ///   and fed to the RaptorQ decoder.
    pub fn ingest(&mut self, frame: Frame) -> Result<bool> {
        if frame.header.session_id != self.session_id {
            self.session_mismatch_streak += 1;
            self.progress.session_mismatch_streak = self.session_mismatch_streak;
            return Err(Error::SessionMismatch {
                expected: self.session_id,
                got: frame.header.session_id,
            });
        }
        self.session_mismatch_streak = 0;
        self.progress.session_mismatch_streak = 0;
        self.progress.frames_seen += 1;

        // Descriptor frame: adopt authoritative metadata + file meta.
        if frame.header.flags & qr_protocol::frame::FLAG_DESCRIPTOR != 0 {
            if let Some(info) = crate::descriptor::parse_payload(&frame.payload) {
                // Reject hostile descriptor metadata before it reaches raptorq.
                // The descriptor is decoded off an arbitrary screen (attacker-
                // controllable); invalid OTI/block params make raptorq panic
                // (divide-by-zero / assert / slice OOB) or allocate gigabytes,
                // and `panic = "abort"` would crash the whole receiver.
                let file_meta_invalid =
                    !qr_protocol::compress::is_known_compression_tag(info.file_meta.compression)
                        || info.file_meta.original_size > MAX_ORIGINAL_BYTES
                        || (info.file_meta.compressed_size_known
                            && info.file_meta.compressed_size > info.meta.transfer_length);
                if info.meta.validate().is_err() || file_meta_invalid {
                    self.progress.frames_corrupt += 1;
                    return Ok(self.is_complete());
                }

                // A session's descriptor is immutable. Accept the first valid
                // one, then ignore mismatching repeats instead of resetting a
                // live decoder or changing the filename/checksum at completion.
                if self.descriptor_seen {
                    if self.meta.as_ref() != Some(&info.meta) || self.file_meta != info.file_meta {
                        self.progress.frames_corrupt += 1;
                    }
                    return Ok(self.is_complete());
                }

                match &self.meta {
                    None => self.apply_meta(info.meta.clone())?,
                    Some(cur) if *cur != info.meta => {
                        self.progress.frames_corrupt += 1;
                        return Ok(self.is_complete());
                    }
                    Some(_) => {}
                }
                self.meta_confirmed = true;
                self.descriptor_seen = true;
                // The replay cache is only needed while the OTI is unknown;
                // once confirmed, no future rebuild can happen — drop it so it
                // doesn't grow unboundedly for the rest of the transfer.
                self.symbol_cache.clear();
                self.file_meta = info.file_meta;
            } else {
                // Descriptor flag set but payload is not a parseable descriptor
                // (truncated extension, bad magic inside payload, etc.). Count as
                // corrupt so the UI can surface "waiting for descriptor" vs silence.
                self.progress.frames_corrupt += 1;
            }
            return Ok(self.is_complete());
        }

        // No authoritative metadata yet → buffer only. We must not feed symbols
        // into a guessed decoder (the old `derive_meta_from_totals` path did
        // exactly that and corrupted multi-block recovery). The cache is keyed
        // by (sbn, esi); on descriptor arrival `apply_meta` replays it into the
        // correct decoder. RaptorQ is a fountain code, so holding symbols in the
        // cache (vs decoding immediately) costs no information — and drops past
        // PRE_META_SYMBOL_CACHE_MAX are also safe (fresh repair will refill).
        if !self.meta_confirmed {
            self.pending_symbol_size = frame.header.symbol_size;
            let key = (frame.header.sbn, frame.header.esi);
            if self.symbol_cache.contains_key(&key) {
                // Duplicate while bootstrapping — keep the first copy.
                self.progress.frames_duplicate += 1;
            } else if self.symbol_cache.len() >= PRE_META_SYMBOL_CACHE_MAX {
                // Cap reached: refuse new keys so RAM stays bounded. Count as
                // corrupt so hosts can surface "waiting for descriptor / cache
                // full" rather than silent progress.
                self.progress.frames_corrupt += 1;
            } else {
                self.symbol_cache.insert(key, frame.payload);
            }
            // Approximate progress from cache size (capped by UI).
            self.progress.received_symbols = self.symbol_cache.len() as u32;
            self.progress.decoded_symbols = 0;
            return Ok(false);
        }

        let sbn = frame.header.sbn as usize;
        if sbn >= self.received.len() {
            // Block out of range — ignore but count as corrupt.
            self.progress.frames_corrupt += 1;
            return Ok(self.is_complete());
        }
        let esi = frame.header.esi;
        // Reject hostile symbol coordinates that would panic the RaptorQ decoder:
        // ESI must fit RFC 6330's 24-bit space, and the payload must be exactly
        // symbol_size bytes (else sub-block unpacking slices out of range).
        let sym_size = self.meta.as_ref().map(|m| m.symbol_size).unwrap_or(0);
        if esi >= (1 << 24) || frame.payload.len() as u32 != sym_size {
            self.progress.frames_corrupt += 1;
            return Ok(self.is_complete());
        }
        if !self.received[sbn].insert(esi) {
            self.progress.frames_duplicate += 1;
            return Ok(self.is_complete());
        }
        self.progress.received_symbols += 1;
        // O(1) source-symbol counter for progress (counts distinct esi < K_block).
        if let Some(meta) = &self.meta {
            if esi < meta.blocks[sbn].num_source_symbols {
                self.source_recv[sbn] += 1;
            }
        }

        // NOTE: we do NOT cache the payload here. The replay cache is only
        // populated while `!meta_confirmed` (above) for the case where the
        // authoritative OTI arrives later and the decoder must be rebuilt.
        // Once metadata is confirmed no rebuild can happen, so holding every
        // symbol's bytes would leak memory for the rest of the transfer.

        let symbol = Symbol::new(sbn as u32, esi, frame.payload);
        if let Some(dec) = self.decoder.as_mut() {
            let _ = dec.add_symbol(&symbol)?;
        }

        // Refresh decoded-symbol / decoded-block counts.
        self.refresh_decoded_counts();
        Ok(self.is_complete())
    }

    /// Replace object metadata + decoder, replaying every stored symbol so no
    /// progress is lost when the authoritative layout arrives.
    fn apply_meta(&mut self, meta: ObjectMeta) -> Result<()> {
        // Validate and allocate before changing any existing session state, so
        // a hostile descriptor cannot leave a partially-reset receiver.
        let decoder = Decoder::new(meta.clone())?;
        // Save the cached symbols before we reset state.
        let cached_symbols = std::mem::take(&mut self.symbol_cache);

        // Update metadata and rebuild decoder.
        self.meta = Some(meta.clone());
        self.pending_symbol_size = meta.symbol_size;
        self.progress.total_symbols = meta.blocks.iter().map(|b| b.num_source_symbols).sum();
        self.progress.total_blocks = meta.blocks.len() as u32;
        self.received = meta.blocks.iter().map(|_| HashSet::new()).collect();
        self.source_recv = vec![0u32; meta.blocks.len()];
        self.decoder = Some(decoder);

        // Replay all cached symbols into the new decoder. We do NOT re-cache
        // them: once the authoritative OTI is applied the caller sets
        // meta_confirmed = true and clears the cache, and no further rebuild
        // can occur, so keeping the bytes around would just leak memory.
        for ((sbn, esi), data) in cached_symbols {
            let bi = sbn as usize;
            // Cache entries predate the authoritative descriptor. Validate all
            // descriptor-dependent coordinates before counting or replaying
            // them so malformed bootstrap frames cannot inflate progress.
            if bi >= self.received.len()
                || esi >= (1 << 24)
                || data.len() != meta.symbol_size as usize
            {
                self.progress.frames_corrupt += 1;
                continue;
            }

            let symbol = Symbol::new(sbn, esi, data);
            if let Some(dec) = self.decoder.as_mut() {
                dec.add_symbol(&symbol)?;
            }
            if self.received[bi].insert(esi) {
                // Keep the O(1) source counter in sync with the replay.
                if esi < meta.blocks[bi].num_source_symbols {
                    self.source_recv[bi] += 1;
                }
            }
        }

        // Count how many unique symbols survived the replay.
        self.progress.received_symbols = self.received.iter().map(|s| s.len() as u32).sum();

        // Refresh progress counters after replay.
        self.refresh_decoded_counts();
        Ok(())
    }

    fn refresh_decoded_counts(&mut self) {
        let Some(meta) = &self.meta else { return };
        let Some(dec) = &self.decoder else { return };
        let mut decoded_symbols = 0u32;
        let mut decoded_blocks = 0u32;
        for (i, b) in meta.blocks.iter().enumerate() {
            if dec.block_progress(b.sbn).is_some() {
                decoded_symbols += b.num_source_symbols;
                decoded_blocks += 1;
            } else {
                // Approximate progress from the incrementally-maintained source
                // counter — O(1) here, replacing the old per-block rescan of the
                // received-set that made this O(n²) over a full transfer.
                let k = b.num_source_symbols;
                decoded_symbols += self.source_recv[i].min(k);
            }
        }
        self.progress.decoded_symbols = decoded_symbols;
        self.progress.decoded_blocks = decoded_blocks;
    }

    /// Reassemble the RaptorQ object bytes exactly as transmitted (including
    /// any symbol-padding), without applying decompression. Used by callers
    /// and tests that want the raw recovered bytes.
    pub fn assemble_raw(&self) -> Option<Vec<u8>> {
        self.decoder.as_ref()?.assemble()
    }

    /// Reassemble the original file once complete.
    ///
    /// The RaptorQ decoder yields the transmitted payload padded with zeros up
    /// to a symbol boundary. This method trims that padding back to the real
    /// payload length and — when the descriptor advertised a compression
    /// algorithm — runs the matching decompressor to recover the original file
    /// bytes. Returns `None` if decoding is incomplete or decompression fails.
    pub fn assemble(&mut self) -> Option<Vec<u8>> {
        // `assemble_result` is `Result<Option<Vec<u8>>>`: `Ok(Some)` on success,
        // `Ok(None)` when decoding isn't complete yet, `Err` on a recoverable
        // decompression failure. Collapse both non-success cases to `None` —
        // `.ok()` turns the `Err` into `None`, and `.flatten()` unwraps the
        // `Option<Option<...>>` so an incomplete decode (`Ok(None)`) also maps
        // to `None`.
        self.assemble_result().ok().flatten()
    }

    /// Like [`assemble`](Self::assemble) but surfaces the decompression error
    /// instead of collapsing it to `None`. Returns `Ok(None)` when decoding is
    /// not yet complete (no bytes to assemble), `Ok(Some(bytes))` on success,
    /// and `Err(_)` if the bytes were recovered but the payload could not be
    /// decompressed (e.g. compressed_size was wrong or the stream is corrupt).
    pub fn assemble_result(&mut self) -> Result<Option<Vec<u8>>> {
        let Some(dec) = self.decoder.as_ref() else {
            self.last_assemble_error = None;
            return Ok(None);
        };
        let mut raw = match dec.assemble() {
            Some(b) => b,
            None => {
                self.last_assemble_error = None;
                return Ok(None);
            }
        };
        // Trim zero padding back to the true payload length. For compressed
        // payloads that is `compressed_size`; for uncompressed payloads the
        // v2/v3 parser sets compressed_size == original_size. The
        // `compressed_size_known` flag distinguishes "0 bytes is the real
        // length" from "we never learned it" (e.g. a receiver built without a
        // descriptor) — operating on the raw padded bytes in the latter case.
        if self.file_meta.compressed_size_known {
            let len = usize::try_from(self.file_meta.compressed_size).map_err(|_| {
                Error::Compress("descriptor compressed_size does not fit this platform".into())
            })?;
            // A corrupt/overflowing descriptor should never claim more than we
            // actually recovered; clamp rather than panic.
            if len <= raw.len() {
                raw.truncate(len);
            } else {
                // The sender claimed a larger payload than RaptorQ recovered —
                // the object cannot be valid. Treat as a decompression failure
                // so the caller surfaces it instead of silently truncating.
                let msg = format!(
                    "descriptor compressed_size ({len}) exceeds recovered payload ({})",
                    raw.len()
                );
                self.last_assemble_error = Some(msg.clone());
                return Err(Error::Compress(msg));
            }
        }
        if self.file_meta.compression == qr_protocol::compress::COMPRESSION_NONE {
            self.last_assemble_error = None;
            Ok(Some(raw))
        } else {
            // Bound the decompressed output against a decompression bomb in the
            // untrusted payload. The descriptor's original_size is the exact
            // expected output; fall back to a conservative ceiling when unknown.
            // The cap is MAX_ORIGINAL_BYTES (not MAX_OBJECT_BYTES) so a highly
            // compressible object can legitimately expand well beyond the wire
            // (transfer_length) ceiling yet still be recovered.
            let max_decompressed_bytes =
                usize::try_from(MAX_ORIGINAL_BYTES).unwrap_or(usize::MAX);
            let cap = if self.file_meta.original_size > 0 {
                let expected = usize::try_from(self.file_meta.original_size).map_err(|_| {
                    Error::Compress("descriptor original_size does not fit this platform".into())
                })?;
                if expected > max_decompressed_bytes {
                    let msg = "descriptor original_size exceeds receiver limit".to_string();
                    self.last_assemble_error = Some(msg.clone());
                    return Err(Error::Compress(msg));
                }
                expected
            } else {
                max_decompressed_bytes
            };
            match qr_protocol::compress::decompress_with_limit(
                &raw,
                self.file_meta.compression,
                cap,
            ) {
                Ok(v)
                    if self.file_meta.original_size > 0
                        && v.len() as u64 != self.file_meta.original_size =>
                {
                    let msg = format!(
                        "decompressed size mismatch: expected {}, got {}",
                        self.file_meta.original_size,
                        v.len()
                    );
                    self.last_assemble_error = Some(msg.clone());
                    Err(Error::Compress(msg))
                }
                Ok(v) => {
                    self.last_assemble_error = None;
                    Ok(Some(v))
                }
                Err(e) => {
                    let msg = e.to_string();
                    self.last_assemble_error = Some(msg.clone());
                    Err(Error::Compress(msg))
                }
            }
        }
    }

    pub fn progress(&self) -> Progress {
        let mut progress = self.progress.clone();
        progress.meta_confirmed = self.meta_confirmed;
        // Expose symbol_size so UIs can compute wire throughput even before
        // meta is confirmed (pending_symbol_size caches the frame-header value).
        progress.symbol_size = self.pending_symbol_size;
        progress
    }

    /// Snapshot progress (clone).
    pub fn progress_snapshot(&self) -> Progress {
        self.progress()
    }

    /// Serialize checkpoint state for resume after a restart.
    ///
    /// Returns `None` until authoritative [`ObjectMeta`] is known (descriptor
    /// confirmed). Persisted symbols are taken from the replay cache and from
    /// in-flight storage only — symbols already fed to the decoder are not
    /// re-exported as bytes, so a mid-transfer snapshot after long progress may
    /// require the sender to retransmit repair symbols (fountain code).
    pub fn save_state(&self) -> Option<crate::ResumeState> {
        let meta = self.meta.as_ref()?.clone();
        let symbols: Vec<(u32, u32, Vec<u8>)> = self
            .symbol_cache
            .iter()
            .map(|((sbn, esi), data)| (*sbn, *esi, data.clone()))
            .collect();
        Some(crate::ResumeState {
            session_id: self.session_id,
            meta,
            received: self.received.clone(),
            symbols,
        })
    }

    /// Restore a receiver from a [`crate::ResumeState`] snapshot.
    ///
    /// Rebuilds the decoder from stored metadata and replays any persisted symbol
    /// payloads. Received ESI sets from the snapshot are merged as symbols are
    /// replayed.
    pub fn restore(state: crate::ResumeState) -> Result<Self> {
        state.validate().map_err(Error::InvalidResume)?;
        let mut rx = Self::new_confirmed(state.session_id, state.meta)?;
        for (sbn, esi, data) in state.symbols {
            let bi = sbn as usize;
            let valid = bi < rx.received.len()
                && esi < (1 << 24)
                && data.len() as u32 == rx.pending_symbol_size;
            if valid && rx.received[bi].insert(esi) {
                if let Some(meta) = &rx.meta {
                    if esi < meta.blocks[bi].num_source_symbols {
                        rx.source_recv[bi] += 1;
                    }
                }
                let symbol = Symbol::new(sbn, esi, data);
                if let Some(dec) = rx.decoder.as_mut() {
                    // A checkpoint may contain a redundant/decode-complete
                    // replay. Validation above guarantees safe coordinates;
                    // an individual upstream rejection must not discard the
                    // rest of an otherwise usable checkpoint.
                    let _ = dec.add_symbol(&symbol);
                }
            }
        }
        // Merge stored received sets (ESIs we knew about but may lack payloads).
        for (i, set) in state.received.into_iter().enumerate() {
            if i < rx.received.len() {
                for esi in set {
                    if esi < (1 << 24) {
                        rx.received[i].insert(esi);
                    }
                }
            }
        }
        rx.progress.received_symbols = rx.received.iter().map(|s| s.len() as u32).sum();
        rx.refresh_decoded_counts();
        Ok(rx)
    }
}

/// Derive a single-block `ObjectMeta` from totals.
///
/// **Deprecated / retained only for diagnostic tests.** This heuristic cannot
/// reproduce the real RaptorQ per-block partitioning (`partition()` in RFC 6330
/// §4.4.1.2) from aggregate totals alone, so a decoder built from it decodes
/// multi-block objects incorrectly. The live receiver path now buffers data
/// frames until a descriptor frame supplies the authoritative OTI (see
/// [`ReceiverSession::new_pending`]); the JNI layer no longer calls this
/// (`receiverCreate` uses cache-only bootstrap). Callers should treat the
/// returned metadata as a *placeholder* and never feed it symbols.
#[deprecated(
    since = "1.0.0",
    note = "heuristic OTI that mis-decodes multi-block objects; use new_pending + a descriptor frame instead"
)]
pub fn derive_meta_from_totals(
    total_blocks: u32,
    total_symbols: u32,
    symbol_size: u32,
) -> ObjectMeta {
    use raptorq::ObjectTransmissionInformation;
    // Heuristic: place all symbols in the number of blocks reported, splitting
    // as evenly as possible. The OTI we hand raptorq must satisfy its internal
    // constraints; we use source_blocks = max(1, total_blocks).
    let nblocks = total_blocks.max(1) as u8;
    let oti = ObjectTransmissionInformation::with_defaults(
        total_symbols as u64 * symbol_size as u64,
        symbol_size as u16 + 4,
    );
    // The OTI's internal block count is determined by the library and may not
    // match our requested nblocks. We serialize it for the wire format.
    let raw = oti.serialize();
    // Even split: divide total_symbols across nblocks as evenly as possible.
    let per = total_symbols / nblocks as u32;
    let rem = total_symbols % nblocks as u32;
    let blocks: Vec<raptorq_core::SourceBlockMeta> = (0..nblocks as u32)
        .map(|i| raptorq_core::SourceBlockMeta {
            sbn: i,
            num_source_symbols: if i < rem { per + 1 } else { per },
            block_length: (if i < rem { per + 1 } else { per }) as u64 * symbol_size as u64,
        })
        .collect();
    let total_len = total_symbols as u64 * symbol_size as u64;
    ObjectMeta {
        transfer_length: total_len,
        symbol_size,
        oti_bytes: raw,
        blocks,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sender::{SenderConfig, SenderSession};
    use qr_protocol::SessionId;
    use raptorq_core::Config;

    fn payload(n: usize) -> Vec<u8> {
        (0..n).map(|i| (i & 0xff) as u8).collect()
    }

    fn run_roundtrip(data: &[u8], redundancy: u8, drop_every: u32) {
        let sid = SessionId::derive("file", data.len() as u64, 0, &[]);
        let sender = SenderSession::new(
            data,
            sid,
            SenderConfig {
                codec: Config::default(),
                redundancy_pct: redundancy,
            },
            crate::descriptor::FileMeta::default(),
        )
        .unwrap();
        let meta = sender.meta().clone();
        // The sender advertises the padded payload length as compressed_size
        // (uncompressed path), so the receiver trims zero-padding back to it.
        // Without this the descriptor would carry compressed_size=0 and the
        // receiver would trim the recovered object to zero bytes.
        let mut fm = sender.file_meta().clone();
        fm.compressed_size = meta.transfer_length;
        fm.compressed_size_known = true;
        // Re-create the sender so its descriptor carries the corrected meta.
        let mut sender = SenderSession::new(
            data,
            sid,
            SenderConfig {
                codec: Config::default(),
                redundancy_pct: redundancy,
            },
            fm,
        )
        .unwrap();

        let mut rx = ReceiverSession::new_confirmed(sid.into(), meta).unwrap();
        let total = sender.total_k();
        // Emit enough frames: one full source pass + a few repair rounds.
        let frames_needed = (total + total * redundancy as u32 / 100 + 10) as usize;
        let mut emitted = 0u32;
        let mut ingested = 0u32;
        for _ in 0..(frames_needed * 3) {
            if rx.is_complete() {
                break;
            }
            let frame = sender.next_frame().unwrap();
            emitted += 1;
            if emitted % drop_every == 0 {
                continue; // simulate frame loss
            }
            // Re-serialize/parse to exercise the wire path.
            let bytes = frame.to_bytes();
            let parsed = Frame::from_bytes(&bytes).unwrap();
            rx.ingest(parsed).unwrap();
            ingested += 1;
        }
        assert!(
            rx.is_complete(),
            "failed to recover: emitted={emitted} ingested={ingested} total_k={total}"
        );
        let out = rx.assemble().unwrap();
        // The assembled bytes are the symbol-padded payload; the original data
        // occupies the first `data.len()` bytes (trailing bytes are zero pad).
        // In the real pipeline, the payload is a zstd stream whose own length
        // is self-describing, so truncation to the true length happens at
        // decompression time.
        assert!(
            out.len() >= data.len(),
            "assembled too short: out={} data={}",
            out.len(),
            data.len()
        );
        assert_eq!(&out[..data.len()], data, "payload bytes must match");
        // Padding region must be all zero.
        assert!(
            out[data.len()..].iter().all(|&b| b == 0),
            "trailing pad must be zero"
        );
    }

    #[test]
    fn roundtrip_small_nodrop() {
        let data = payload(50_000);
        run_roundtrip(&data, 10, 1000); // drop_every huge → no drops
    }

    #[test]
    fn roundtrip_with_20pct_loss() {
        // Drop ~1 in 5 frames to simulate ~20% loss; 50% repair redundancy
        // should still allow recovery.
        let data = payload(30_000);
        run_roundtrip(&data, 50, 5);
    }

    /// Regression test for Bug 1: a receiver bootstrapped with `from_first_frame`
    /// (cache-only, no guessed meta) must still recover a multi-block object
    /// once the descriptor arrives — even if it appears *after* many data frames.
    #[test]
    fn from_first_frame_recovers_multiblock_late_descriptor() {
        let data = payload(120_000); // spans several source blocks
        let sid = SessionId::derive("late", data.len() as u64, 0, &[]);
        // First create the sender to learn the padded transfer_length, then
        // rebuild with a FileMeta whose compressed_size matches it so the
        // descriptor advertises the correct payload size.
        let probe = SenderSession::new(
            &data,
            sid,
            SenderConfig {
                codec: Config::default(),
                redundancy_pct: 20,
            },
            crate::descriptor::FileMeta::default(),
        )
        .unwrap();
        let padded_len = probe.meta().transfer_length;
        let fm = crate::descriptor::FileMeta {
            filename: String::new(),
            original_size: data.len() as u64,
            crc32: 0,
            compression: qr_protocol::compress::COMPRESSION_NONE,
            compressed_size: padded_len,
            compressed_size_known: true,
            crc32_known: false,
        };
        let mut sender = SenderSession::new(
            &data,
            sid,
            SenderConfig {
                codec: Config::default(),
                redundancy_pct: 20,
            },
            fm,
        )
        .unwrap();
        sender.set_descriptor_interval(8);

        // Buffer a batch of frames with the descriptor deliberately delayed.
        let mut frames: Vec<Frame> = Vec::new();
        for _ in 0..(sender.total_k() as usize * 2 + 32) {
            frames.push(sender.next_frame().unwrap());
        }

        // Receiver bootstraps from the very first (descriptor) frame via
        // from_first_frame, which now creates a cache-only session.
        let mut rx = ReceiverSession::from_first_frame(&frames[0]);
        for f in frames {
            let parsed = Frame::from_bytes(&f.to_bytes()).unwrap();
            let _ = rx.ingest(parsed);
            if rx.is_complete() {
                break;
            }
        }
        assert!(rx.is_complete(), "late-descriptor receiver must recover");
        let out = rx.assemble().unwrap();
        assert_eq!(&out[..data.len()], data);
    }

    /// A descriptor frame decoded off a hostile screen must be rejected (not
    /// confirmed) without panicking, even though it passed magic/CRC.
    #[test]
    fn rejects_unparseable_descriptor_payload() {
        use qr_protocol::FLAG_DESCRIPTOR;
        let sid = SessionId::derive("bad-desc", 100, 0, &[]).into();
        let mut rx = ReceiverSession::new_pending(sid);
        let payload = vec![0u8; 1024]; // no DESC_MAGIC at payload[0]
        let f = Frame::build(sid, FLAG_DESCRIPTOR, 0, 0, 1, 1, 1024, 0, 0, &payload);
        let _ = rx.ingest(f).unwrap();
        assert!(!rx.is_meta_confirmed());
        assert!(rx.progress().frames_corrupt >= 1);
    }

    #[test]
    fn rejects_tampered_descriptor_without_panic() {
        let data = payload(20_000);
        let sid = SessionId::derive("evil", data.len() as u64, 0, &[]);
        let sender = SenderSession::new(
            &data,
            sid,
            SenderConfig::default(),
            crate::descriptor::FileMeta::default(),
        )
        .unwrap();
        let meta = sender.meta().clone();
        let mut desc = crate::descriptor::build_frame(
            &meta,
            &crate::descriptor::FileMeta::default(),
            sid.into(),
            1,
            0,
        )
        .unwrap();
        // Corrupt the descriptor's symbol_size field (offset 12..16) so it no
        // longer matches the embedded OTI → validate() must reject it.
        desc.payload[12..16].copy_from_slice(&999u32.to_be_bytes());

        let mut rx = ReceiverSession::new_pending(sid.into());
        let _ = rx.ingest(desc); // must not panic
        assert!(
            !rx.is_meta_confirmed(),
            "tampered descriptor must be rejected"
        );
        assert!(
            rx.progress().frames_corrupt >= 1,
            "rejected descriptor counts as corrupt"
        );
    }

    #[test]
    fn first_valid_descriptor_is_immutable() {
        let data = payload(20_000);
        let sid = SessionId::derive("immutable", data.len() as u64, 0, &[]);
        let sender = SenderSession::new(
            &data,
            sid,
            SenderConfig::default(),
            crate::descriptor::FileMeta::default(),
        )
        .unwrap();
        let meta = sender.meta().clone();
        let first_meta = crate::descriptor::FileMeta {
            filename: "first.bin".into(),
            original_size: data.len() as u64,
            compressed_size: data.len() as u64,
            compressed_size_known: true,
            ..Default::default()
        };
        let mut changed_meta = first_meta.clone();
        changed_meta.filename = "changed.exe".into();

        let mut rx = ReceiverSession::new_pending(sid.into());
        rx.ingest(crate::descriptor::build_frame(&meta, &first_meta, sid.into(), 1, 0).unwrap())
            .unwrap();
        let corrupt_before = rx.progress().frames_corrupt;
        rx.ingest(crate::descriptor::build_frame(&meta, &changed_meta, sid.into(), 2, 0).unwrap())
            .unwrap();

        assert_eq!(rx.file_meta().filename, "first.bin");
        assert!(rx.progress().frames_corrupt > corrupt_before);
    }

    /// Pre-descriptor bootstrap cache must refuse growth past
    /// [`PRE_META_SYMBOL_CACHE_MAX`] so a hostile CRC-valid stream cannot OOM.
    #[test]
    fn pre_meta_symbol_cache_is_bounded() {
        let sid = SessionId::derive("cache-cap", 1, 0, &[]).into();
        let mut rx = ReceiverSession::new_pending(sid);
        let payload = vec![0u8; 64];
        // Fill to the hard cap with distinct (sbn, esi) keys.
        for i in 0..PRE_META_SYMBOL_CACHE_MAX {
            let f = Frame::build(sid, 0, 0, i as u32, 1, 1, 64, 1, 0, &payload);
            let _ = rx.ingest(f).unwrap();
        }
        assert_eq!(rx.symbol_cache.len(), PRE_META_SYMBOL_CACHE_MAX);
        assert_eq!(
            rx.progress().received_symbols as usize,
            PRE_META_SYMBOL_CACHE_MAX
        );
        let corrupt_before = rx.progress().frames_corrupt;
        // One more distinct key must be dropped, not stored.
        let overflow = Frame::build(
            sid,
            0,
            0,
            PRE_META_SYMBOL_CACHE_MAX as u32,
            1,
            1,
            64,
            1,
            0,
            &payload,
        );
        let _ = rx.ingest(overflow).unwrap();
        assert_eq!(rx.symbol_cache.len(), PRE_META_SYMBOL_CACHE_MAX);
        assert!(rx.progress().frames_corrupt > corrupt_before);
        // A duplicate of an already-cached key must not grow the cache either.
        let dup = Frame::build(sid, 0, 0, 0, 1, 1, 64, 1, 0, &payload);
        let dup_before = rx.progress().frames_duplicate;
        let _ = rx.ingest(dup).unwrap();
        assert_eq!(rx.symbol_cache.len(), PRE_META_SYMBOL_CACHE_MAX);
        assert!(rx.progress().frames_duplicate > dup_before);
    }

    /// A data frame whose ESI exceeds RFC 6330's 24-bit space (which would panic
    /// raptorq's PayloadId) must be dropped without panic or acceptance.
    #[test]
    fn drops_hostile_esi_frame_without_panic() {
        let data = payload(20_000);
        let sid = SessionId::derive("e", data.len() as u64, 0, &[]);
        let meta = SenderSession::new(
            &data,
            sid,
            SenderConfig::default(),
            crate::descriptor::FileMeta::default(),
        )
        .unwrap()
        .meta()
        .clone();
        let mut rx = ReceiverSession::new_confirmed(sid.into(), meta.clone()).unwrap();

        let payload_bytes = vec![0u8; meta.symbol_size as usize];
        let f = Frame::build(
            sid.into(),
            0,
            0,
            1u32 << 24,
            meta.blocks.len() as u32,
            1,
            meta.symbol_size,
            1,
            0,
            &payload_bytes,
        );
        let _ = rx.ingest(f); // must not panic
        assert_eq!(
            rx.progress().received_symbols,
            0,
            "hostile ESI must not be accepted"
        );
        assert!(rx.progress().frames_corrupt >= 1);
    }
}
