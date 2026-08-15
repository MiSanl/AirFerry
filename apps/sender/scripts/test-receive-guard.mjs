/**
 * Logic-level unit test for the receive worker's session bootstrap/relock
 * predicates (`apps/sender/src/receive/sessionGuard.ts`).
 *
 * Follows the pattern of `core/transfer-engine/scripts/e2e_receiver.mjs` —
 * a plain Node script with assert(), no test framework. Requires Node >=
 * 23.6 for native TS type-stripping of the imported .ts module.
 *
 * Run: node apps/sender/scripts/test-receive-guard.mjs  (or `npm test` in apps/sender)
 */
import {
  isCacheBootstrapProbe,
  isDescriptorCandidate,
  looksLikeAirFerryFrame,
  shouldDropMismatchedSession,
  MISMATCH_RELOCK_THRESHOLD,
} from "../src/receive/sessionGuard.ts"

let passed = 0
function assert(cond, msg) {
  if (!cond) {
    console.error(`FAIL: ${msg}`)
    process.exit(1)
  }
  passed++
}

/** Build a minimal frame-like byte array with a controllable header. */
function frame(h0 = 0x45, h1 = 0x54, version = 1, flags = 0x00, len = 64) {
  const f = new Uint8Array(len)
  f[0] = h0
  f[1] = h1
  f[2] = version
  f[3] = flags
  return f
}

// ── looksLikeAirFerryFrame: H4 fix (a) — probe magic/version validation ──
assert(looksLikeAirFerryFrame(frame()), "valid ET frame header accepted")
assert(!looksLikeAirFerryFrame(frame(0x00, 0x54, 1)), "wrong magic[0] rejected")
assert(!looksLikeAirFerryFrame(frame(0x45, 0x00, 1)), "wrong magic[1] rejected")
assert(!looksLikeAirFerryFrame(frame(0x45, 0x54, 2)), "wrong version rejected")
assert(!looksLikeAirFerryFrame(frame(0x45, 0x54, 1, 0x00, 63)), "<64B frame rejected")
assert(!looksLikeAirFerryFrame(new Uint8Array(0)), "empty payload rejected")

// An arbitrary environmental QR payload (≥64B URL) must NOT bootstrap a
// session any more — the pre-fix length+flag check used to accept it.
const urlQr = new TextEncoder().encode(
  "https://example.com/airferry/abcdef?session=0123456789#frag".padEnd(80, "x")
)
assert(urlQr.length >= 64, "test fixture: URL QR is >=64B")
assert(!looksLikeAirFerryFrame(urlQr), "environmental QR (URL) rejected by probe")
assert(
  !isCacheBootstrapProbe(urlQr),
  "environmental QR (URL) is not a cache-bootstrap probe"
)

// A mis-decode that happens to carry ET magic + version + data flag still
// passes the cheap probe BY DESIGN (full CRC validation happens in Rust
// Frame::from_bytes; the mismatch relock below is the backstop for the
// CRC-passing/wrong-sid corner).
assert(isCacheBootstrapProbe(frame(0x45, 0x54, 1, 0x00)), "ET data frame is a probe")

// ── descriptor vs data-flag discrimination ──
assert(isDescriptorCandidate(frame(0x45, 0x54, 1, 0x01)), "descriptor flag accepted")
assert(isDescriptorCandidate(frame(0x45, 0x54, 1, 0x03)), "descriptor flag (+ other bits) accepted")
assert(!isDescriptorCandidate(frame(0x45, 0x54, 1, 0x00)), "data frame is not a descriptor")
assert(!isDescriptorCandidate(frame(0x00, 0x00, 1, 0x01)), "garbage with descriptor bit rejected")
assert(!isDescriptorCandidate(frame(0x45, 0x54, 1, 0x01, 63)), "<64B descriptor rejected")
assert(
  !isCacheBootstrapProbe(frame(0x45, 0x54, 1, 0x01)),
  "descriptor flag excluded from probe"
)

// ── shouldDropMismatchedSession: H4 fix (b) — mirrors Android/Windows ──
assert(MISMATCH_RELOCK_THRESHOLD === 3, "threshold matches Android (streak >= 3)")
assert(
  shouldDropMismatchedSession({ metaConfirmed: false, everAccepted: false, mismatchStreak: 3 }),
  "garbage bootstrap drops at streak 3"
)
assert(
  shouldDropMismatchedSession({ metaConfirmed: false, everAccepted: false, mismatchStreak: 1000 }),
  "garbage bootstrap drops at clamped-high streak"
)
assert(
  !shouldDropMismatchedSession({ metaConfirmed: false, everAccepted: false, mismatchStreak: 2 }),
  "streak below threshold keeps the session"
)
assert(
  !shouldDropMismatchedSession({ metaConfirmed: false, everAccepted: false, mismatchStreak: 0 }),
  "fresh session (streak 0) is kept"
)
assert(
  !shouldDropMismatchedSession({ metaConfirmed: false, everAccepted: true, mismatchStreak: 99 }),
  "live session (accepted something) is never dropped"
)
assert(
  !shouldDropMismatchedSession({ metaConfirmed: true, everAccepted: false, mismatchStreak: 99 }),
  "meta-confirmed session (descriptor-validated) is never dropped"
)

console.log(`OK: sessionGuard ${passed} assertions passed`)
