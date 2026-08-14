/** Page 3: live QR video stream playback. */
import { useEffect, useState } from "react"
import { QrStream, type QrStreamStats } from "@/components/QrStream"
import type { SenderSessionWasm } from "@/wasm/loader"
import type { TransferConfig } from "@/types"

interface Props {
  session: SenderSessionWasm
  config: TransferConfig
  sessionId: { lo: bigint; hi: bigint }
  /** Total bytes to send (compressed payload) for the current segment. */
  totalBytes: number
  /** Total segment count (1 for non-segmented transfers). */
  segmentCount?: number
  /** Zero-based index of the currently-active segment. */
  segmentIndex?: number
  /** Advance to another segment (only for segmented transfers). */
  onSegmentChange?: (nextIndex: number) => void
  /** Stop rendering and return to the transfer parameters. */
  onStop: () => void
}

function hex(lo: bigint, hi: bigint): string {
  // Render as 32-hex-digit string.
  const lo32 = lo.toString(16).padStart(16, "0")
  const hi32 = hi.toString(16).padStart(16, "0")
  return `${hi32}${lo32}`
}

function formatDuration(seconds: number): string {
  if (!isFinite(seconds) || seconds <= 0) return "—"
  const s = Math.ceil(seconds)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  const rem = s % 60
  if (m < 60) return `${m}m ${rem}s`
  return `${Math.floor(m / 60)}h ${m % 60}m`
}

export function PlayPage({
  session,
  config,
  sessionId,
  totalBytes,
  segmentCount = 1,
  segmentIndex = 0,
  onSegmentChange,
  onStop
}: Props) {
  const [stats, setStats] = useState<QrStreamStats | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [jumpValue, setJumpValue] = useState(String(segmentIndex + 1))
  const isSegmented = segmentCount > 1

  useEffect(() => setJumpValue(String(segmentIndex + 1)), [segmentIndex])

  const jumpToSegment = () => {
    const oneBased = Number(jumpValue)
    if (!Number.isInteger(oneBased) || oneBased < 1 || oneBased > segmentCount) {
      setError(`请输入 1–${segmentCount} 之间的段号`)
      return
    }
    setError(null)
    onSegmentChange?.(oneBased - 1)
  }

  // Total bytes including redundancy overhead (sender emits K source + K*redundancy/100 repair).
  const totalWithRedundancy = totalBytes * (1 + config.redundancyPct / 100)
  // The percentage is an estimate through one source+redundancy budget. After
  // that point the fountain encoder keeps emitting fresh repair symbols; it
  // does not loop a finite plan.
  const passPct = stats && totalWithRedundancy > 0
    ? (stats.bytes / totalWithRedundancy) * 100
    : 0
  const progressPct = Math.min(100, passPct)
  const supplementing = passPct >= 100
  const remainingInPass = Math.max(0, totalWithRedundancy - (stats?.bytes ?? 0))
  const etaSeconds = stats && stats.throughputBps > 0
    ? remainingInPass / stats.throughputBps
    : 0

  return (
    <div className="page">
      <h2>正在播放</h2>
      <p className="page-desc">将接收端摄像头对准屏幕，保持画面完整可见</p>
      {error && <p className="error">{error}</p>}
      {isSegmented && (
        <div className="segment-bar">
          <div className="segment-info">
            <span className="segment-current">
              第 <strong>{segmentIndex + 1}</strong> / {segmentCount} 段
            </span>
            <span className="muted">接收端确认本段完成后再切换；续传可直达缺失段</span>
          </div>
          <div className="segment-nav">
            <button
              className="btn"
              disabled={segmentIndex <= 0}
              onClick={() => onSegmentChange?.(segmentIndex - 1)}
            >
              ← 上一段
            </button>
            <form
              className="segment-jump"
              onSubmit={(event) => {
                event.preventDefault()
                jumpToSegment()
              }}
            >
              <label htmlFor="segment-jump-input">跳到</label>
              <input
                id="segment-jump-input"
                type="number"
                min={1}
                max={segmentCount}
                step={1}
                inputMode="numeric"
                value={jumpValue}
                onChange={(event) => setJumpValue(event.target.value)}
                aria-label={`段号，范围 1 到 ${segmentCount}`}
              />
              <button className="btn" type="submit">跳转</button>
            </form>
            <button
              className="btn primary"
              disabled={segmentIndex >= segmentCount - 1}
              onClick={() => onSegmentChange?.(segmentIndex + 1)}
            >
              下一段 →
            </button>
          </div>
        </div>
      )}
      <QrStream
        session={session}
        fps={config.fps}
        brightness={config.brightness}
        autoOptimize={config.autoOptimize}
        multiQr={config.multiQr}
        ditherJitter={config.ditherJitter}
        onStop={onStop}
        onStats={setStats}
        onError={(e) => setError(e.message)}
      />
      {stats && (
        <div className="stats-bar">
          <div className="stat-item">
            <div className="stat-value">{stats.fps.toFixed(0)}</div>
            <div className="stat-label">符号/秒</div>
          </div>
          <div className="stat-item">
            <div className="stat-value">{(stats.throughputBps / 1024).toFixed(1)}</div>
            <div className="stat-label">KB/s</div>
          </div>
          <div className="stat-item">
            <div className="stat-value">
              {supplementing ? "补码中" : `${progressPct.toFixed(0)}%`}
            </div>
            <div className="stat-label">估算进度</div>
          </div>
          <div className="stat-item">
            <div className="stat-value">{supplementing ? "持续中" : formatDuration(etaSeconds)}</div>
            <div className="stat-label">预计剩余</div>
          </div>
        </div>
      )}
    </div>
  )
}
