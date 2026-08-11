//! End-to-end large-transfer segmentation test.
//!
//! A logical file larger than a single object budget is split into N fixed
//! `SEGMENT_RAW_BYTES` (8 MiB) segments. Each segment is independently
//! compressed and independently RaptorQ-encoded with its own child session id
//! (see `SessionId::derive_segment`) and a descriptor-v4 frame. The receiver
//! recovers each segment with an ordinary `ReceiverSession`, then hands the
//! recovered *uncompressed* bytes to a `TransferAssembler` which validates each
//! segment's length + SHA-256 and reassembles the full file once all segments
//! have arrived.
//!
//! This test drives the whole chain through the real QR frame wire format,
//! including simulated frame loss, so it exercises descriptor-v4 parsing, the
//! sender's `new_segment`, the receiver's segment metadata exposure, and the
//! assembler's validation + concatenation.

use qr_protocol::{Frame, SessionId};
use raptorq_core::Config;
use sha2::{Digest, Sha256};
use transfer_engine::assembler::TransferAssembler;
use transfer_engine::receiver::ReceiverSession;
use transfer_engine::sender::{SenderConfig, SenderSession};
use transfer_engine::{FileMeta, SegmentMeta, SEGMENT_RAW_BYTES};

fn pseudo_random(n: usize) -> Vec<u8> {
    (0..n)
        .map(|i| ((i * 1103515245 + 12345) & 0xff) as u8)
        .collect()
}

/// Segment the `root` file into its canonical 8 MiB (minus final) raw slices,
/// each independently compressed (here: no-op / COMPRESSION_NONE) and wrapped in
/// a `SegmentMeta` whose `raw_sha256` is the SHA-256 of that raw slice.
fn split_root(root: &[u8], root_session_id: u128) -> Vec<SegmentMeta> {
    let root_sha256: [u8; 32] = Sha256::digest(root).into();
    let count = if root.is_empty() {
        1
    } else {
        (root.len() as u64).div_ceil(SEGMENT_RAW_BYTES) as u32
    };
    (0..count)
        .map(|i| {
            let start = (i as usize) * SEGMENT_RAW_BYTES as usize;
            let end = (start + SEGMENT_RAW_BYTES as usize).min(root.len());
            let raw = &root[start..end];
            let digest = Sha256::digest(raw);
            let mut raw_sha256 = [0u8; 32];
            raw_sha256.copy_from_slice(&digest);
            SegmentMeta {
                root_session_id,
                segment_index: i,
                segment_count: count,
                original_offset: (i as u64) * SEGMENT_RAW_BYTES,
                root_original_size: root.len() as u64,
                root_sha256,
                raw_sha256,
            }
        })
        .collect()
}

/// Recover one segment over the wire with a `ReceiverSession`, returning the
/// recovered uncompressed bytes. Applies simulated loss (drop 1 in `drop_every`).
fn recover_segment(
    root_session_id: u128,
    seg: &SegmentMeta,
    raw: &[u8],
    redundancy: u8,
    drop_every: u32,
) -> Vec<u8> {
    let child = SessionId::derive_segment(root_session_id, seg.segment_index);
    // `compressed_size` is the real pre-padding payload length. The receiver
    // trims RaptorQ symbol padding back to this value before returning bytes.
    let fm = FileMeta {
        filename: "big.bin".into(),
        original_size: raw.len() as u64,
        crc32: 0,
        compression: qr_protocol::compress::COMPRESSION_NONE,
        compressed_size: raw.len() as u64,
        compressed_size_known: true,
        crc32_known: false,
    };
    let mut sender = SenderSession::new_segment(
        raw,
        child,
        SenderConfig {
            // This test exercises the full descriptor/frame/RaptorQ pipeline,
            // not QR matrix capacity. A large legal symbol keeps the real
            // 8 MiB segment test to ~129 source symbols instead of ~8192, so a
            // debug `cargo test` finishes in seconds rather than hours.
            codec: Config::new(65_528).expect("large test symbol"),
            redundancy_pct: redundancy,
        },
        fm,
        seg.clone(),
    )
    .expect("segment sender");

    let total_k = sender.total_k();
    let batch = (total_k as usize) * 3 + 64;
    let mut rx: Option<ReceiverSession> = None;
    let mut emitted = 0u32;
    for _ in 0..batch {
        if rx.as_ref().is_some_and(|r| r.is_complete()) {
            break;
        }
        let f = sender.next_frame().unwrap();
        emitted += 1;
        if drop_every > 0 && emitted % drop_every == 0 {
            continue;
        }
        let bytes = f.to_bytes();
        let parsed = Frame::from_bytes(&bytes).unwrap();
        if rx.is_none() {
            rx = Some(ReceiverSession::from_first_frame(&parsed));
        }
        let _ = rx.as_mut().unwrap().ingest(parsed);
    }
    let mut rx = rx.expect("receiver never created");
    assert!(
        rx.is_complete(),
        "segment {} failed to recover",
        seg.segment_index
    );
    // A v4 descriptor must expose the segment metadata.
    assert_eq!(
        rx.segment_meta().map(|s| s.segment_index),
        Some(seg.segment_index),
        "receiver must expose descriptor-v4 segment meta"
    );
    rx.assemble().expect("assemble segment")
}

/// Full segmented transfer: send every segment, recover each, assemble the root.
fn segmented_cycle(root: &[u8], redundancy: u8, drop_every: u32) -> Vec<u8> {
    let root_session_id = SessionId::derive("big", root.len() as u64, 0, &[]).0;
    let segments = split_root(root, root_session_id);

    // Start the assembler from the first segment's metadata.
    let mut assembler = {
        let first = &segments[0];
        let fm = FileMeta {
            filename: "big.bin".into(),
            original_size: root.len().min(SEGMENT_RAW_BYTES as usize) as u64,
            crc32: 0,
            compression: qr_protocol::compress::COMPRESSION_NONE,
            compressed_size: root.len().min(SEGMENT_RAW_BYTES as usize) as u64,
            compressed_size_known: true,
            crc32_known: false,
        };
        TransferAssembler::new(first, &fm).expect("assembler from first segment")
    };

    for (i, seg) in segments.iter().enumerate() {
        let start = i * SEGMENT_RAW_BYTES as usize;
        let end = (start + SEGMENT_RAW_BYTES as usize).min(root.len());
        let recovered = recover_segment(
            root_session_id,
            seg,
            &root[start..end],
            redundancy,
            drop_every,
        );
        let fm = FileMeta {
            filename: "big.bin".into(),
            original_size: recovered.len() as u64,
            crc32: 0,
            compression: qr_protocol::compress::COMPRESSION_NONE,
            compressed_size: recovered.len() as u64,
            compressed_size_known: true,
            crc32_known: false,
        };
        let stored = assembler
            .add_segment(seg, &fm, recovered)
            .expect("assembler add segment");
        assert!(stored, "segment {i} should be newly stored");
    }

    assert!(assembler.is_complete());
    assert_eq!(assembler.received_segments(), segments.len() as u32);
    assembler.reassemble().expect("reassemble root file")
}

#[test]
fn segmented_transfer_reassembles_multi_segment_file() {
    // 3 segments worth of data (2 full 8 MiB + a tail) without paying 24 MiB in
    // the test allocator twice — use ~17 MiB so it spans 3 segments.
    let root = pseudo_random(SEGMENT_RAW_BYTES as usize * 2 + 4096);
    let out = segmented_cycle(&root, 15, 0);
    assert_eq!(out, root, "reassembled file must match original");
}

#[test]
fn segmented_transfer_survives_frame_loss() {
    let root = pseudo_random(SEGMENT_RAW_BYTES as usize + 8192);
    let out = segmented_cycle(&root, 30, 7); // drop ~1 in 7 frames per segment
    assert_eq!(out, root);
}

#[test]
fn segmented_single_segment_file() {
    // Exercise a payload that is not aligned to the default 1024-byte symbol.
    let root = pseudo_random(1234);
    let out = segmented_cycle(&root, 10, 0);
    assert_eq!(out, root);
}
