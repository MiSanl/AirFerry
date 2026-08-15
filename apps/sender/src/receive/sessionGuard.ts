/**
 * Pure session-bootstrap / relock predicates for the web receive worker
 * (`workers/receive.worker.ts`). Mirrors the host-side state machine of
 * Android `ReceiverSessionManager.kt` (`parseHeader` + mismatch re-init)
 * and Windows `ReceiverSession.cs`, extracted as pure functions so they can
 * be unit-tested without a worker/WASM environment
 * (`apps/sender/scripts/test-receive-guard.mjs`).
 *
 * Frame header layout reference: `core/qr-protocol/src/frame.rs` (60B
 * big-endian header + payload + 4B footer → a decoded frame is ≥ 64 bytes).
 */

/** Header magic, big-endian 0x4554 = ASCII "ET". */
export const FRAME_MAGIC_0 = 0x45
export const FRAME_MAGIC_1 = 0x54
/** Wire protocol version (header byte 2). */
export const FRAME_VERSION = 1
/** Header flags bit 0: this frame is a descriptor frame. */
export const FLAG_DESCRIPTOR = 0x01

/**
 * Consecutive session mismatches after which a never-accepted,
 * never-confirmed session is dropped so the worker can re-bootstrap. Must
 * stay in sync with Android/Windows (`mismatchStreak >= 3`).
 */
export const MISMATCH_RELOCK_THRESHOLD = 3

/** Smallest decoded payload that can hold the 60B header + 4B footer. */
const MIN_FRAME_BYTES = 64

/**
 * True if `f` is long enough and carries the AirFerry frame magic + version
 * in its header — i.e. it is plausibly one of OUR frames. This is a cheap
 * filter only (no CRC); full validation still happens inside Rust
 * `Frame::from_bytes` on ingest. It exists so an arbitrary environmental QR
 * code (a URL, a payment code, …) can never supply a garbage session id to
 * the cache-bootstrap path.
 */
export function looksLikeAirFerryFrame(f: Uint8Array): boolean {
  return (
    f.length >= MIN_FRAME_BYTES &&
    f[0] === FRAME_MAGIC_0 &&
    f[1] === FRAME_MAGIC_1 &&
    f[2] === FRAME_VERSION
  )
}

/**
 * True if `f` is a plausible **data** (non-descriptor) frame, usable as the
 * cache-bootstrap probe that reads the session id from the header.
 */
export function isCacheBootstrapProbe(f: Uint8Array): boolean {
  return looksLikeAirFerryFrame(f) && (f[3] & FLAG_DESCRIPTOR) === 0
}

/**
 * True if `f` is a plausible **descriptor** frame, usable as the preferred
 * bootstrap candidate (`ReceiverSessionWasm.from_descriptor` re-validates the
 * whole frame CRC + descriptor payload and throws on garbage).
 */
export function isDescriptorCandidate(f: Uint8Array): boolean {
  return looksLikeAirFerryFrame(f) && (f[3] & FLAG_DESCRIPTOR) !== 0
}

/**
 * Mismatch-relock decision (mirrors Android `ReceiverSessionManager.ingest`
 * / Windows `ReceiverSession.Ingest`): a session that was cache-bootstrapped
 * onto a wrong session id never accepts anything, and Rust rejects every
 * real frame with `SessionMismatch` forever — the UI would be stuck at
 * "接收中 0" with no recovery but a manual reset. When the streak climbed to
 * the threshold while nothing was ever accepted and the descriptor never
 * confirmed, drop the session; the next batch re-bootstraps (from a
 * descriptor, or a fresh validated probe).
 *
 * - `everAccepted` (any accepted frame since this bootstrap) ⇒ never drop:
 *   a live transfer must not be killed by pointing the camera at another
 *   screen for a moment.
 * - `metaConfirmed` ⇒ never drop: the session was established from a
 *   CRC-validated descriptor, so it cannot be a garbage bootstrap (same
 *   semantics as Android — which never re-locks after confirmation).
 */
export function shouldDropMismatchedSession(state: {
  metaConfirmed: boolean
  everAccepted: boolean
  mismatchStreak: number
}): boolean {
  return (
    !state.metaConfirmed &&
    !state.everAccepted &&
    state.mismatchStreak >= MISMATCH_RELOCK_THRESHOLD
  )
}
