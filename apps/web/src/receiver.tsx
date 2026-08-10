/**
 * AirFerry web receiver — entry point.
 *
 * Thin shell mirroring main.tsx: mounts the shared ReceivePage from the sender
 * source. All receive logic (camera capture, QR decode, ingest, decompress,
 * parse, result rendering) lives in `apps/sender/src/pages/ReceivePage.tsx`,
 * reused here via the `@/` cross-project alias.
 */
import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import ReceivePage from "@/pages/ReceivePage"

const rootEl = document.getElementById("root")
if (!rootEl) throw new Error("#root element missing in receiver.html")

createRoot(rootEl).render(
  <StrictMode>
    <ReceivePage />
  </StrictMode>
)
