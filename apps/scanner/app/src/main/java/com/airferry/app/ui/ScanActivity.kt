package com.airferry.app.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.hardware.camera2.CaptureRequest
import android.os.Bundle
import android.view.WindowManager
import android.widget.Toast
import android.util.Log
import android.util.Range
import android.util.Size
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.airferry.app.nativelib.NativeBridge
import com.airferry.app.scan.BundleParser
import com.airferry.app.scan.QrDecodePool
import com.airferry.app.scan.QrStreamAnalyzer
import com.airferry.app.scan.ReceiverSessionManager
import com.airferry.app.scan.TextParser
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

// Design tokens
private val BgDark = Color(0xFF0F172A)
private val CardBg = Color(0xCC1E293B)
private val Accent = Color(0xFF3B82F6)
private val TextPrimary = Color(0xFFF1F5F9)
private val TextSecondary = Color(0xFF94A3B8)
private val Success = Color(0xFF22C55E)

class ScanActivity : ComponentActivity() {

    private var session = ReceiverSessionManager()
    private val cameraExecutor = Executors.newSingleThreadExecutor()
    /** Dedicated single-thread executor for the post-recovery heavy work
     *  (JNI assemble, CRC, disk writes, bundle unpacking) so it never blocks
     *  the main thread. The work runs under the decode pool's ingest lock. */
    private val ioExecutor = Executors.newSingleThreadExecutor()
    private var cameraStarted = false
    private var previewView: PreviewView? = null

    /** Parallel QR decode pool (capture → queue → N workers → serialized ingest). */
    private var decodePool: QrDecodePool? = null

    /** Disk-backed assembler for a descriptor-v4 large transfer (null = none in progress). */
    private var segAssembler: com.airferry.app.scan.SegmentAssembler? = null
    /** Optional task selected from history; unrelated roots are ignored. */
    private var resumeRootId: String? = null

    /**
     * Sliding-window rate samples for the UI.
     * Prefer the last [RATE_WINDOW_MS] over whole-session averages so the user
     * sees near-instant throughput when the stream speeds up or stalls.
     *
     * Store symbol *counts* (not wire bytes) so a late-arriving real
     * [symbolSize] does not create a fake throughput spike from a discontinuous
     * byte counter.
     */
    private data class RateSample(val tMs: Long, val decoded: Long, val receivedSymbols: Long)
    private val rateSamples = ArrayDeque<RateSample>()
    private var decodePerSec = 0
    /** Recent wire throughput (bytes/s) over the sliding window. */
    private var recentWireBps = 0L

    // Reactive state observed by Compose
    private val uiState = mutableStateOf(UiState())

    data class UiState(
        val statusText: String = "正在初始化…",
        val progressPct: Int = 0,
        val receivedSymbols: Int = 0,
        val totalSymbols: Int = 0,
        val decodedBlocks: Int = 0,
        val totalBlocks: Int = 0,
        val lossPct: Int = 0,
        val framesSeen: Long = 0,
        val decodePerSec: Int = 0,
        val framesDropped: Long = 0,
        val fileName: String = "",
        val fileSize: Long = 0,
        /** Real compressed payload size (descriptor `compressed_size`); for
         *  segmented transfers this is the whole compressed-stream size. */
        val compressedSize: Long = 0,
        /** Zero-based current segment index when the transfer is segmented (0 otherwise). */
        val segmentIndex: Int = 0,
        /** Total segment count when the transfer is segmented (1 otherwise). */
        val segmentCount: Int = 1,
        val complete: Boolean = false,
        val jniReady: Boolean = false,
        /** Elapsed transfer time in ms (0 = not started yet). */
        val transferElapsedMs: Long = 0,
        /** RaptorQ symbol size in bytes (from the sender's config). */
        val symbolSize: Int = 0,
        /**
         * Recent wire throughput (bytes/s) over ~[RATE_WINDOW_MS], not the
         * whole-session average. 0 when the window is still empty.
         */
        val recentWireBps: Long = 0,
    )

    private val recoveryStage = mutableStateOf<String?>(null)
    /** Wall-clock ms when the transfer first started (totalSymbols > 0). */
    private var transferStartMs = 0L

    private val requestCameraPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) {
                cameraStarted = false
                startCamera()
            } else {
                updateUi { it.copy(statusText = "需要相机权限") }
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        resumeRootId = intent.getStringExtra("RESUME_ROOT_ID")
            ?.lowercase()
            ?.takeIf { id -> id.length == 32 && id.all { it in '0'..'9' || it in 'a'..'f' } }

        // Keep the screen on for the whole scan session. Transfers can run for
        // many minutes; without this the system timeout dims/locks the screen,
        // stops the camera preview, and aborts an in-progress receive.
        // FLAG_KEEP_SCREEN_ON only applies while this window is visible — no
        // WAKE_LOCK permission needed, and leaving the activity restores normal
        // timeout automatically.
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // JNI self-test
        val jniOk = try {
            val h = NativeBridge.receiverCreate(0L, 1L, 1, 100, 1024)
            NativeBridge.receiverDestroy(h)
            true
        } catch (e: Exception) {
            Log.e(TAG, "JNI self-test FAILED", e); false
        }
        updateUi {
            it.copy(
                jniReady = jniOk,
                statusText = if (!jniOk) "JNI 加载失败" else idleStatus(),
            )
        }
        if (!jniOk) {
            setContent { ErrorScreen("原生库加载失败，请重新安装应用。") }
            return
        }

        setContent { ScanScreen() }

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startCamera()
        } else {
            requestCameraPermission.launch(Manifest.permission.CAMERA)
        }
    }

    @Composable
    private fun ScanScreen() {
        val state by uiState
        val recovery by recoveryStage

        BoxWithConstraints(modifier = Modifier.fillMaxSize().background(BgDark)) {

            // Camera preview (full screen) — CameraX PreviewView + ImageAnalysis.
            AndroidView(
                factory = { ctx ->
                    PreviewView(ctx).also { pv ->
                        pv.scaleType = PreviewView.ScaleType.FILL_CENTER
                    }
                },
                modifier = Modifier.fillMaxSize(),
                update = { pv -> bindCameraIfNeeded(pv) }
            )

            Column(
                modifier = Modifier.fillMaxSize().padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Spacer(modifier = Modifier.height(40.dp))

                // Circular progress indicator
                CircularProgress(
                    progress = state.progressPct / 100f,
                    label = "${state.progressPct}%",
                    sublabel = if (state.fileName.isNotEmpty()) state.fileName else "等待扫描…"
                )

                Spacer(modifier = Modifier.weight(1f))

                // Bottom info card
                if (state.totalSymbols > 0) {
                    Card(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(16.dp),
                        colors = CardDefaults.cardColors(containerColor = CardBg)
                    ) {
                        Column(modifier = Modifier.padding(20.dp)) {
                            // 文件标题行（大号字体，仅文件名）
                            if (state.fileName.isNotEmpty()) {
                                Text(
                                    state.fileName,
                                    color = TextPrimary,
                                    fontSize = 17.sp,
                                    fontWeight = FontWeight.Bold,
                                    maxLines = 1,
                                    modifier = Modifier.padding(bottom = 4.dp)
                                )
                            }
                            // 大小行（原大小~压缩后大小）。压缩后大小来自描述符
                            // （分段时为整条压缩流大小），不是线上含冗余的符号字节。
                            val showOrig = state.fileSize > 0
                            val showCompressed = state.compressedSize > 0
                            if (showOrig || showCompressed) {
                                val sizeStr = buildString {
                                    if (showOrig) {
                                        append(formatSize(state.fileSize))
                                        if (showCompressed) append("~压缩后 ")
                                    }
                                    if (showCompressed) append(formatSize(state.compressedSize))
                                }
                                InfoRow("大小", sizeStr)
                            }
                            // 分段传输：明确当前收的是第几段。
                            if (state.segmentCount > 1) {
                                InfoRow("分段", "${state.segmentIndex + 1} / ${state.segmentCount}")
                            }
                            InfoRow("已识别符号", "${state.receivedSymbols} / ${state.totalSymbols}")
                            InfoRow("解码速率", "${state.decodePerSec} 符号/秒")
                            // 传输用时 + 近几秒滑动窗口速度（非全程平均）
                            if (state.transferElapsedMs > 0) {
                                val elapsedStr = formatDuration(state.transferElapsedMs)
                                // 线上吞吐 = 最近 RATE_WINDOW_MS 内 Δ(符号×symbolSize)/Δt
                                val speedStr = if (state.recentWireBps > 0)
                                    formatSize(state.recentWireBps) + "/s" else ""
                                InfoRow("用时", if (speedStr.isNotEmpty()) "$elapsedStr @ $speedStr" else elapsedStr)
                            }
                            LinearProgressIndicator(
                                progress = { state.progressPct / 100f },
                                modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                                color = Accent,
                                trackColor = Color(0xFF334155)
                            )
                        }
                    }
                    Spacer(modifier = Modifier.height(12.dp))
                }

                // Status text. A live recovery stage (assemble/CRC/save) takes
                // precedence over the "✓ 文件恢复完成" snapshot, so the user sees
                // the post-scan pipeline advancing instead of a frozen 100%.
                Text(
                    text = recovery ?: state.statusText,
                    color = if (recovery != null) Accent
                            else if (state.complete) Success else TextPrimary,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth()
                )

                Spacer(modifier = Modifier.height(16.dp))

                // Action buttons row
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly
                ) {
                    ActionButton(Icons.Default.Folder, "文件") {
                        startActivity(Intent(this@ScanActivity, FileListActivity::class.java))
                    }
                    ActionButton(Icons.Default.Settings, "设置") {
                        startActivity(Intent(this@ScanActivity, SettingsActivity::class.java))
                    }
                    if (state.totalSymbols > 0 || state.complete) {
                        ActionButton(Icons.Default.Refresh, "重扫") { resetSession() }
                    }
                }
                Spacer(modifier = Modifier.height(24.dp))
            }
        }
    }

    @Composable
    private fun CircularProgress(progress: Float, label: String, sublabel: String) {
        Box(contentAlignment = Alignment.Center) {
            Surface(
                shape = CircleShape,
                color = CardBg,
                modifier = Modifier.size(160.dp)
            ) {}
            // Progress ring
            androidx.compose.foundation.Canvas(modifier = Modifier.size(160.dp)) {
                val stroke = 8.dp.toPx()
                val diameter = size.minDimension - stroke
                val topLeft = androidx.compose.ui.geometry.Offset(
                    (size.width - diameter) / 2f,
                    (size.height - diameter) / 2f
                )
                val arc = androidx.compose.ui.geometry.Size(diameter, diameter)
                drawArc(
                    color = Color(0xFF334155),
                    startAngle = -90f, sweepAngle = 360f, useCenter = false,
                    topLeft = topLeft, size = arc,
                    style = androidx.compose.ui.graphics.drawscope.Stroke(width = stroke)
                )
                drawArc(
                    color = Accent,
                    startAngle = -90f, sweepAngle = 360f * progress, useCenter = false,
                    topLeft = topLeft, size = arc,
                    style = androidx.compose.ui.graphics.drawscope.Stroke(width = stroke)
                )
            }
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(label, color = TextPrimary, fontSize = 32.sp, fontWeight = FontWeight.Bold)
                Text(
                    sublabel.take(20),
                    color = TextSecondary, fontSize = 12.sp,
                    maxLines = 1
                )
            }
        }
    }

    @Composable
    private fun InfoRow(label: String, value: String) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp)
        ) {
            Text(label, color = TextSecondary, fontSize = 13.sp)
            Text(
                value, color = TextPrimary, fontSize = 13.sp,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
                softWrap = false,
                textAlign = TextAlign.End,
                modifier = Modifier.weight(1f)
            )
        }
    }

    @Composable
    private fun ActionButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, onClick: () -> Unit) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            FilledTonalIconButton(onClick = onClick, modifier = Modifier.size(52.dp)) {
                Icon(icon, contentDescription = label, tint = Accent)
            }
            Text(label, color = TextSecondary, fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp))
        }
    }

    @Composable
    private fun ErrorScreen(msg: String) {
        Box(
            modifier = Modifier.fillMaxSize().background(BgDark),
            contentAlignment = Alignment.Center
        ) {
            Text(msg, color = TextPrimary, textAlign = TextAlign.Center, modifier = Modifier.padding(32.dp))
        }
    }

    // ===== Camera + session logic =====

    private fun bindCameraIfNeeded(previewView: PreviewView) {
        this.previewView = previewView
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) !=
            PackageManager.PERMISSION_GRANTED
        ) return
        if (cameraStarted) return
        cameraStarted = true
        startCameraWithView(previewView)
    }

    private fun startCamera() {
        val view = previewView ?: return
        bindCameraIfNeeded(view)
    }

    /** Lazily create + start the shared parallel decode pool. */
    private fun ensurePool(): QrDecodePool {
        var p = decodePool
        if (p == null) {
            // Multi-QR mode is always on: the pool decodes every code on screen per
            // frame (not just the first), so a sender tiling N codes yields ~N×
            // throughput. Single-code senders decode just as well (the multi path
            // returns one result), so there's no need for a user-facing toggle — it
            // worked regardless of the switch position, and only added confusion.
            p = QrDecodePool(
                onDecoded = { payload, _ -> handleFrameAsync(payload) },
                multiMode = true,
            ).also { it.start() }
            decodePool = p
        }
        return p
    }

    @androidx.annotation.OptIn(androidx.camera.camera2.interop.ExperimentalCamera2Interop::class)
    private fun startCameraWithView(previewView: PreviewView) {
        val cameraProviderFuture = ProcessCameraProvider.getInstance(this)
        cameraProviderFuture.addListener({
            try {
                val cameraProvider = cameraProviderFuture.get()

                // Get/create the parallel decode pool. Each decoded payload is fed
                // to the native receiver via handleFrameAsync, serialized by the
                // pool's ingest lock so the non-thread-safe JNI handle is only ever
                // touched by one thread at a time.
                val pool = ensurePool()

                val preview = Preview.Builder().build().also {
                    it.setSurfaceProvider(previewView.surfaceProvider)
                }

                // Request a 1080p analysis stream so each QR module has more camera pixels,
                // improving ZXing decode reliability — especially important with the
                // reduced quiet zone on multi-QR. CameraX may pick the closest
                // supported size.
                val resolutionSelector = ResolutionSelector.Builder()
                    .setResolutionStrategy(
                        ResolutionStrategy(
                            Size(1920, 1080),
                            ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER
                        )
                    )
                    .build()

                fun buildAnalysis(fpsRange: Range<Int>): ImageAnalysis =
                    ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .setResolutionSelector(resolutionSelector)
                        .also { builder ->
                            // Pin the sensor frame-rate via Camera2Interop (CameraX
                            // 1.3.x has no public ImageAnalysis#setTargetFrameRate).
                            Camera2Interop.Extender(builder).setCaptureRequestOption(
                                CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                                fpsRange
                            )
                        }
                        .build()
                        .also { it.setAnalyzer(cameraExecutor, QrStreamAnalyzer(pool)) }

                // Pin a steady 60fps so low-light AE can't trade frame rate for a
                // longer exposure (the on-screen QR is a bright emissive source, so
                // a fixed 60 is usually fine and reduces motion / rolling-shutter
                // smear). Some devices reject a fixed [60,60]; fall back to [30,60].
                cameraProvider.unbindAll()
                try {
                    cameraProvider.bindToLifecycle(
                        this, CameraSelector.DEFAULT_BACK_CAMERA, preview, buildAnalysis(Range(60, 60))
                    )
                } catch (e: Exception) {
                    Log.w(TAG, "fixed 60fps bind failed; falling back to 30–60", e)
                    cameraProvider.unbindAll()
                    cameraProvider.bindToLifecycle(
                        this, CameraSelector.DEFAULT_BACK_CAMERA, preview, buildAnalysis(Range(30, 60))
                    )
                }
            } catch (e: Exception) {
                Log.e(TAG, "Camera bind failed", e)
                cameraStarted = false
                updateUi { it.copy(statusText = "相机启动失败") }
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private var lastUiUpdate = 0L
    private var completedHandled = false
    /** Once recovery completes, stop feeding the native receiver so the
     *  main-thread assemble() (a `&` borrow) can't race a worker ingest (`&mut`). */
    private val ingestStopped = AtomicBoolean(false)

    /**
     * Pure-data snapshot produced on a decode-worker thread and handed to the
     * main thread. Keeping every JNI / JSON / RaptorQ step off the main thread is
     * what lets the receiver keep up with the camera — the heavy ingest chain
     * runs on the [QrDecodePool]'s serialized ingest path, and only the throttled
     * UI snapshot is posted to the main thread.
     */
    private data class FrameSnapshot(
        val progress: ReceiverSessionManager.Progress,
        val fileName: String,
        val fileSize: Long,
        /** Real compressed size (whole compressed stream for segmented). */
        val compressedSize: Long,
        /** Zero-based current segment index (0 when not segmented). */
        val segmentIndex: Int,
        /** Total segment count (1 when not segmented). */
        val segmentCount: Int
    )

    /** Ingest-thread entry (serialized by the pool): heavy work here, post a snapshot. */
    private fun handleFrameAsync(payload: ByteArray) {
        // After completion, drop further frames: the main thread is (or will be)
        // calling assemble() on the receiver, which must not run concurrently
        // with another ingest. This runs under the pool's ingest lock, so the
        // check+ingest+stop sequence is atomic w.r.t. other workers.
        if (ingestStopped.get()) return
        // ingest() returns a lightweight status (no JSON) so the per-frame path
        // stays cheap; the full progress is fetched only on the throttled UI tick.
        val status = session.ingest(payload) ?: return

        // Duplicate-segment fast path (per-frame, not gated on the UI tick):
        // once the descriptor confirms the segment metadata, if this segment is
        // already stored in the active SegmentAssembler there is no point
        // receiving the whole (~32 MiB) segment again — skip straight to the
        // next one. Runs here (ingest lock) so the re-scan of a completed
        // segment is rejected on the descriptor frame itself, before the UI
        // even shows "receiving".
        if (!status.complete && session.isInitialized && session.isSegmented()) {
            val idx = session.segmentIndex()
            val cnt = session.segmentCount()
            val lo = session.rootSessionIdLo()
            val hi = session.rootSessionIdHi()
            // In-memory ledger for the ongoing transfer; fall back to the
            // durable disk ledger (e.g. after the app restarted / resume) so a
            // re-scanned already-completed segment is still rejected.
            val asm = segAssembler
            val inMem = asm != null && asm.rootSessionIdLo() == lo && asm.rootSessionIdHi() == hi
            val dup = if (inMem) asm!!.hasSegment(idx)
                else com.airferry.app.scan.SegmentAssembler.hasStoredSegment(com.airferry.app.scan.ContentStore.root(this), lo, hi, idx)
            Log.w(TAG, "dupSeg: segIdx=$idx cnt=$cnt inMem=$inMem " +
                "hasSeg=${if (inMem) asm!!.hasSegment(idx) else com.airferry.app.scan.SegmentAssembler.hasStoredSegment(com.airferry.app.scan.ContentStore.root(this), lo, hi, idx)} => dup=$dup")
            if (dup) {
                val dupText = "第 ${idx + 1}/$cnt 段已接收过，自动跳过"
                runOnUiThread { updateUi { it.copy(statusText = dupText) } }
                swapReceiverForNextSegment()
                return
            }
        }

        // UI refresh throttle: ~7 Hz is plenty for a progress bar, and keeps the
        // main thread free. Always let the final "complete" frame through.
        val now = System.currentTimeMillis()
        if (now - lastUiUpdate < 150 && !status.complete) return
        lastUiUpdate = now

        // On the UI tick (or completion), pull the full progress snapshot. This
        // is the only place the JSON is parsed — not every frame.
        val progress = session.progress() ?: return

        // Read file metadata from session (JNI) — keep on this background thread.
        val fn = if (session.isInitialized) session.fileName() else ""
        val fs = if (session.isInitialized) session.fileSize() else 0L
        // Real compressed size. For a segmented transfer each child session only
        // reports THIS segment's compressed length; the user-facing "压缩后" is
        // the whole compressed stream (root_original_size). For a single-object
        // transfer compressed_size is exactly the payload size.
        val segmented = session.isInitialized && session.isSegmented()
        val cs = if (session.isInitialized) {
            if (segmented) session.rootOriginalSize() else session.compressedSize()
        } else {
            0L
        }
        val segIdx = if (segmented) session.segmentIndex() else 0
        val segCount = if (segmented) session.segmentCount() else 1

        val snapshot = FrameSnapshot(progress, fn, fs, cs, segIdx, segCount)
        if (status.complete) {
            // Block any further ingest before the completion path (assemble +
            // file I/O + Activity start) runs on the main thread.
            ingestStopped.set(true)
            runOnUiThread { applySnapshot(snapshot, handleCompletion = true) }
        } else {
            runOnUiThread { applySnapshot(snapshot, handleCompletion = false) }
        }
    }

    /** Main-thread only: apply the precomputed snapshot to Compose state. */
    private fun applySnapshot(s: FrameSnapshot, handleCompletion: Boolean) {
        val progress = s.progress
        // Progress bar tracks *received (de-duplicated) symbols*, not decoded
        // symbols. RaptorQ decodes a whole source block at once when it has
        // collected enough independent symbols, so a "decoded fraction" bar sits
        // flat near 0% for a long time and then jumps in steps — it reads as
        // "stuck". The received-symbol count, by contrast, increments by one
        // for every new symbol the receiver accepts, so the bar climbs ~linearly
        // and matches what the user sees on screen. Fountain repair symbols can
        // push receivedSymbols above totalSymbols K, so clamp to 100.
        val pct = when {
            progress.complete -> 100
            progress.metaConfirmed || progress.totalSymbols > 0 -> {
                if (progress.totalSymbols > 0) {
                    (progress.receivedSymbols * 100 / progress.totalSymbols).coerceIn(0, 100)
                } else {
                    0
                }
            }
            // Cache mode: no confirmed total yet. Estimate from the first frame's
            // total_symbols (advisory only) and cap at 15% — the descriptor may
            // later reveal a larger total, so don't over-promise early.
            progress.receivedSymbols > 0 -> {
                val estimated = session.getEstimatedTotalSymbols()
                if (estimated > 0) {
                    (progress.receivedSymbols * 100 / estimated).coerceIn(0, 15)
                } else {
                    0
                }
            }
            else -> 0
        }
        val statusMsg = when {
            progress.complete -> "✓ 文件恢复完成"
            !progress.metaConfirmed && progress.receivedSymbols > 0 ->
                "⏳ 正在同步… 已缓存 ${progress.receivedSymbols} 符号 (~$pct%)"
            progress.totalSymbols == 0 -> "等待二维码…"
            progress.receivedSymbols > 0 && progress.decodedBlocks == 0 ->
                "接收中… ${progress.receivedSymbols}/${progress.totalSymbols} 符号 (等待解码)"
            else -> "恢复中… $pct%"
        }
        // Sliding-window rates: decode symbols/s + wire bytes/s over RATE_WINDOW_MS.
        // UI ticks are already throttled (~7 Hz), so each sample is a fresh point;
        // prune anything older than the window and derive Δcount/Δt.
        val pool = decodePool
        val nowMs = System.currentTimeMillis()
        // Rate math uses ≥1 so early pre-descriptor ticks don't div0; samples
        // store symbol counts so a late real symbolSize never rewrites history.
        val symbolSize = session.symbolSizeBytes().coerceAtLeast(1)
        val receivedNow = progress.receivedSymbols.toLong().coerceAtLeast(0)
        val decodedNow = pool?.decodedCount() ?: 0L
        if (progress.complete) {
            // Freeze at 0 once done — final tick would otherwise show the last
            // non-zero window forever on the completed card.
            decodePerSec = 0
            recentWireBps = 0L
            rateSamples.clear()
        } else if (receivedNow > 0L || decodedNow > 0L) {
            rateSamples.addLast(RateSample(nowMs, decodedNow, receivedNow))
            while (rateSamples.size > 1 && nowMs - rateSamples.first().tMs > RATE_WINDOW_MS) {
                rateSamples.removeFirst()
            }
            if (rateSamples.size >= 2) {
                val oldest = rateSamples.first()
                val newest = rateSamples.last()
                val dt = newest.tMs - oldest.tMs
                // Need a short baseline so a single tick doesn't explode the rate.
                if (dt >= RATE_MIN_DT_MS) {
                    decodePerSec = (((newest.decoded - oldest.decoded) * 1000L) / dt)
                        .toInt().coerceAtLeast(0)
                    val dSym = (newest.receivedSymbols - oldest.receivedSymbols).coerceAtLeast(0L)
                    recentWireBps = ((dSym * symbolSize * 1000L) / dt).coerceAtLeast(0L)
                }
            } else {
                // Window collapsed (e.g. long stall then one fresh tick) — don't
                // keep showing a stale non-zero rate from before the gap.
                decodePerSec = 0
                recentWireBps = 0L
            }
        }
        val droppedTotal = pool?.droppedCount() ?: 0L

        // Start the transfer timer on first symbol receipt.
        if (progress.totalSymbols > 0 && transferStartMs == 0L) {
            transferStartMs = nowMs
        }
        val elapsedMs = if (transferStartMs > 0) nowMs - transferStartMs else 0L

        updateUi {
            it.copy(
                progressPct = pct,
                receivedSymbols = progress.receivedSymbols,
                totalSymbols = progress.totalSymbols,
                decodedBlocks = progress.decodedBlocks,
                totalBlocks = progress.totalBlocks,
                lossPct = (progress.lossRatio * 100).toInt(),
                framesSeen = progress.framesSeen,
                decodePerSec = decodePerSec,
                framesDropped = droppedTotal,
                fileName = s.fileName,
                fileSize = s.fileSize,
                compressedSize = s.compressedSize,
                segmentIndex = s.segmentIndex,
                segmentCount = s.segmentCount,
                statusText = statusMsg,
                complete = progress.complete,
                transferElapsedMs = elapsedMs,
                symbolSize = symbolSize,
                recentWireBps = recentWireBps,
            )
        }

        if (handleCompletion && progress.complete && !completedHandled) {
            completedHandled = true
            // Move the heavy recovery work (JNI assemble, CRC over the full
            // payload, disk writes, bundle unpacking) off the main thread — it
            // previously ran here synchronously and ANR'd on multi-MB transfers.
            // ingestStopped (set on the completing worker) already guarantees no
            // further ingest touches the native session, and we wrap the JNI
            // access in runExclusive so it cannot race a straggler or destroy().
            val snapshotFileName = s.fileName
            ioExecutor.execute {
                // Run the recovery under the pool's ingest lock (it touches the
                // native session via assemble/crc32/fileSize), then post the
                // resulting Intent to the main thread. runExclusive returns Unit,
                // so capture the Intent in a holder.
                try {
                    var intent: Intent? = null
                    val work = {
                        intent = recoverAndStage(snapshotFileName)
                    }
                    decodePool?.runExclusive(work) ?: work()
                    intent?.let { runOnUiThread { startActivity(it) } }
                } catch (e: Exception) {
                    clearRecoveryStage()
                    resetReceiverAfterRecoveryFailure()
                    runOnUiThread {
                        Toast.makeText(
                            this,
                            e.message ?: "保存接收内容失败",
                            Toast.LENGTH_LONG,
                        ).show()
                    }
                } catch (e: OutOfMemoryError) {
                    // A large recovered payload (e.g. multi-MB text decoded to a
                    // ~2x String) can transiently exceed the default heap. Do not
                    // crash the whole scanner — drop to a graceful message. The
                    // bytes are typically already persisted by this point, so the
                    // user can reopen the file from the list.
                    android.util.Log.e("ScanActivity", "recoverAndStage OOM", e)
                    clearRecoveryStage()
                    resetReceiverAfterRecoveryFailure()
                    runOnUiThread {
                        Toast.makeText(this, "文件过大，接收内存不足", Toast.LENGTH_LONG).show()
                    }
                }
            }
        }
    }

    /**
     * Assemble the recovered bytes, verify CRC, and stage the file(s) to disk.
     * Returns the [Intent] to launch the detail/bundle screen, or null if there
     * was nothing to recover. Runs on a background thread under the decode pool's
     * ingest lock (so it can't race an in-flight ingest or a destroy()).
     */
    private fun recoverAndStage(displayName: String): Intent? {
        updateRecoveryStage("正在组装数据…")
        if (session.isSegmented()) {
            // Descriptor-v4 large transfer: recover this segment's **compressed**
            // bytes (no per-segment decompression — the whole stream is
            // decompressed once after every segment arrives) and store it.
            val compressed = session.assembleRawBytes() ?: run {
                clearRecoveryStage()
                if (session.isComplete()) {
                    runOnUiThread {
                        Toast.makeText(this, "恢复失败: 分段组装失败", Toast.LENGTH_LONG).show()
                    }
                    swapReceiverForNextSegment()
                }
                return null
            }
            return handleSegmentedTransfer(displayName, compressed)
        }
        val fileBytes = session.assemble() ?: run {
            clearRecoveryStage()
            if (session.isComplete()) {
                val detail = session.lastAssembleError().ifEmpty { "数据组装或解压失败" }
                runOnUiThread {
                    Toast.makeText(this, "恢复失败: $detail", Toast.LENGTH_LONG).show()
                }
                // A completed-but-unassemblable child must not leave the
                // scanner permanently stopped. Keep durable earlier segments
                // and accept a fresh scan immediately.
                swapReceiverForNextSegment()
            }
            return null
        }
        if (resumeRootId != null) {
            clearRecoveryStage()
            runOnUiThread {
                updateUi { it.copy(statusText = "已忽略其他传输，继续等待选中的恢复任务…") }
            }
            swapReceiverForNextSegment()
            return null
        }
        val originalSize = session.fileSize()
        // Truncate RaptorQ zero-padding back to the original size. originalSize
        // is a Long (up to 2^63); clamp to the bytes we actually recovered and
        // never let a bogus/large value overflow Int (the old `originalSize.toInt()`
        // would wrap for >2GB and throw IndexOutOfBounds in copyOfRange).
        val truncLen = when {
            originalSize > 0 && originalSize <= fileBytes.size -> originalSize.toInt()
            else -> fileBytes.size
        }
        // v3 assemble normally already returns exactly originalSize. Reuse that
        // array instead of allocating a second full-size ByteArray; only legacy
        // padded results need an actual truncation copy.
        val truncBytes = if (truncLen == fileBytes.size) {
            fileBytes
        } else {
            fileBytes.copyOfRange(0, truncLen)
        }

        updateRecoveryStage("正在校验完整性…")
        val expectedCrc = session.crc32()
        val crcKnown = session.crc32Known()
        val receivedCrc = crc32OfBytes(truncBytes)

        // Content-addressed store: one blob per unique content; detail/share/list
        // all use the blob path (no recovered_* + received/ double-write).
        val store = com.airferry.app.scan.ContentStore

        // Text payload → ETTEXTv1. Detected BEFORE the bundle check.
        // Prefer the descriptor filename (sender select-page name); fall back
        // to the default only when the descriptor never supplied one.
        //
        // Size guard: only decode into the in-memory text UI when it fits within
        // the text cap. Otherwise fall through to single-file handling below,
        // which saves the raw bytes as a `.txt` file — decoding a multi-MB text
        // message into a ~2x UTF-16 String plus a re-encode here balloons the
        // JVM heap past the budget on low-end devices (the sibling text paths in
        // FileListActivity / ReceiveTextActivity / ReceiveBundleActivity all
        // guard with fitsTextUi; this branch is the only one that did not).
        if (TextParser.isText(truncBytes) &&
            com.airferry.app.scan.TextLike.fitsTextUi(truncBytes.size)
        ) {
            val text = TextParser.parse(truncBytes)
            if (text != null) {
                updateRecoveryStage("正在保存文字…")
                val contentBytes = text.toByteArray(Charsets.UTF_8)
                val contentCrc = crc32OfBytes(contentBytes)
                val crcHex = java.lang.Long.toHexString(contentCrc)
                // Prefer descriptor name; ensure a .txt-ish save label for pure text.
                // (Sender already normalizes to *.txt; this is a receive-side belt.)
                val textName = when {
                    displayName.isEmpty() -> TEXT_RECEIVED_NAME
                    displayName.contains('.') -> displayName
                    else -> "$displayName.txt"
                }
                val put = store.putBytes(
                    this, textName, contentBytes,
                    crcHex = crcHex, crcUnknown = false, kind = "text",
                )
                clearRecoveryStage()
                return Intent(this, ReceiveTextActivity::class.java).apply {
                    putExtra("FILE_PATH", put.path.absolutePath)
                    // Prefer the user-facing name (descriptor) over store sanitization.
                    putExtra("FILE_NAME", textName)
                    putExtra("ENTRY_ID", put.entry.id)
                    putExtra("CRC32", expectedCrc)
                    putExtra("CRC32_RECEIVED", receivedCrc)
                    putExtra("CRC32_UNKNOWN", !crcKnown)
                }
            }
        }

        // Multi-file bundle → one ContentStore entry per member, shared bundleId.
        if (BundleParser.isBundle(truncBytes)) {
            val bundle = BundleParser.parse(truncBytes)
            if (bundle != null && bundle.files.isNotEmpty()) {
                val totalFiles = bundle.files.size
                val paths = ArrayList<String>()
                val names = ArrayList<String>()
                val sizes = ArrayList<String>()
                val entryIds = ArrayList<String>()
                val ts = java.text.SimpleDateFormat("MMdd_HHmmss", java.util.Locale.getDefault())
                    .format(java.util.Date())
                val bundleId = java.util.UUID.randomUUID().toString()
                val bundleTitle = "发送_$ts"
                updateRecoveryStage("正在保存 $totalFiles 个文件…")
                val puts = store.putBytesBatch(
                    this,
                    bundle.files.map { f ->
                        com.airferry.app.scan.ContentStore.PutBytesRequest(
                            f.name, f.data,
                        crcHex = "unknown", crcUnknown = true, kind = "file",
                        bundleId = bundleId, bundleTitle = bundleTitle,
                        )
                    },
                )
                for ((f, put) in bundle.files.zip(puts)) {
                    paths.add(put.path.absolutePath)
                    names.add(f.name)
                    sizes.add(f.data.size.toString())
                    entryIds.add(put.entry.id)
                }
                clearRecoveryStage()
                return Intent(this, ReceiveBundleActivity::class.java).apply {
                    putStringArrayListExtra("FILE_PATHS", paths)
                    putStringArrayListExtra("FILE_NAMES", names)
                    putStringArrayListExtra("FILE_SIZES", sizes)
                    putStringArrayListExtra("ENTRY_IDS", entryIds)
                    putExtra("CRC32", expectedCrc)
                    putExtra("CRC32_RECEIVED", receivedCrc)
                    putExtra("CRC32_UNKNOWN", !crcKnown)
                }
            }
        }

        // Single-file path (or text-like). Canonical store path only.
        updateRecoveryStage("正在保存文件…")
        val finalName = if (displayName.isNotEmpty()) displayName else "received_file"
        val contentCrc = crc32OfBytes(truncBytes)
        val crcHex = if (crcKnown) java.lang.Long.toHexString(expectedCrc) else java.lang.Long.toHexString(contentCrc)
        val crcUnknown = !crcKnown

        if (com.airferry.app.scan.TextLike.isTextLikeName(finalName) &&
            com.airferry.app.scan.TextLike.fitsTextUi(truncBytes.size)
        ) {
            val text = com.airferry.app.scan.TextLike.decodeUtf8Strict(truncBytes)
            if (text != null) {
                val archiveLabel =
                    if (finalName.contains('.')) finalName else TEXT_RECEIVED_NAME
                val put = store.putBytes(
                    this, archiveLabel, truncBytes,
                    crcHex = java.lang.Long.toHexString(contentCrc),
                    crcUnknown = false,
                    kind = "text",
                )
                clearRecoveryStage()
                return Intent(this, ReceiveTextActivity::class.java).apply {
                    putExtra("FILE_PATH", put.path.absolutePath)
                    putExtra("FILE_NAME", finalName)
                    putExtra("ENTRY_ID", put.entry.id)
                    putExtra("CRC32", if (crcKnown) expectedCrc else contentCrc)
                    putExtra("CRC32_RECEIVED", contentCrc)
                    putExtra("CRC32_UNKNOWN", !crcKnown)
                }
            }
        }

        val put = store.putBytes(
            this, finalName, truncBytes,
            crcHex = crcHex, crcUnknown = crcUnknown, kind = "file",
        )
        clearRecoveryStage()
        return Intent(this, ReceiveDetailActivity::class.java).apply {
            putExtra("FILE_PATH", put.path.absolutePath)
            putExtra("FILE_SIZE", if (originalSize > 0) originalSize else truncBytes.size.toLong())
            putExtra("FILE_NAME", finalName)
            putExtra("ENTRY_ID", put.entry.id)
            putExtra("CRC32", expectedCrc)
            putExtra("CRC32_RECEIVED", receivedCrc)
            putExtra("CRC32_UNKNOWN", !crcKnown)
            // Already archived into ContentStore — do not copy again.
            putExtra("RESAVE", true)
        }
    }

    /**
     * Store one recovered descriptor-v4 segment into the disk-backed assembler.
     *
     * Returns an Intent (navigates to the detail page) only once every segment
     * of the root transfer has arrived and been merged; otherwise null (the
     * receiver keeps scanning for the next segment).
     */
    private fun handleSegmentedTransfer(displayName: String, compressedBytes: ByteArray): Intent? {
        val index = session.segmentIndex()
        val count = session.segmentCount()
        // rootSize = whole **compressed** stream size (descriptor root_original_size).
        val rootSize = session.rootOriginalSize()
        val lo = session.rootSessionIdLo()
        val hi = session.rootSessionIdHi()
        // segSize = this segment's **compressed** length (descriptor compressed_size).
        val segSize = session.compressedSize()
        val originalOffset = session.originalOffset()
        val compression = session.compression()
        val decompressedSize = session.originalSize()
        val crc32 = session.crc32()
        val crc32Known = session.crc32Known()
        val expectedSha256 = requireNotNull(session.rawSha256()) {
            "分段描述符缺少 SHA-256"
        }
        val rootSha256 = requireNotNull(session.rootSha256()) {
            "分段描述符缺少整文件 SHA-256"
        }

        require(count in 1..com.airferry.app.scan.SegmentAssembler.MAX_SEGMENT_COUNT) {
            "分段数量超出安全上限"
        }
        require(rootSize > 0 && originalOffset == index.toLong() *
            com.airferry.app.scan.SegmentAssembler.SEGMENT_RAW_BYTES) {
            "分段偏移或根文件大小无效"
        }
        require(segSize in 1..com.airferry.app.scan.SegmentAssembler.SEGMENT_RAW_BYTES) {
            "分段长度无效"
        }
        require(compressedBytes.size.toLong() == segSize) {
            "分段实际长度 ${compressedBytes.size} 与描述符 $segSize 不一致"
        }
        require(expectedSha256.size == 32) { "分段描述符 SHA-256 长度无效" }
        require(rootSha256.size == 32) { "整文件 SHA-256 长度无效" }
        val expectedCount = (rootSize - 1) /
            com.airferry.app.scan.SegmentAssembler.SEGMENT_RAW_BYTES + 1
        val expectedLength = minOf(
            com.airferry.app.scan.SegmentAssembler.SEGMENT_RAW_BYTES,
            rootSize - originalOffset,
        )
        require(index in 0 until count && count.toLong() == expectedCount && segSize == expectedLength) {
            "分段数量或本段长度与根文件不一致"
        }

        val actualRootId = rootSessionIdHex(lo, hi)
        val targetRootId = resumeRootId
        if (targetRootId != null && actualRootId != targetRootId) {
            clearRecoveryStage()
            runOnUiThread {
                Toast.makeText(this, "已忽略其他大文件任务", Toast.LENGTH_SHORT).show()
                updateUi { it.copy(statusText = "继续等待选中任务的下一段…") }
            }
            swapReceiverForNextSegment()
            return null
        }

        val root = com.airferry.app.scan.ContentStore.root(this)
        // Reuse the active root so a long, sequential transfer does not reopen
        // the ledger and re-hash every earlier ~32 MiB segment for each child.
        // Interleaved roots still open their own identity-bound assembler.
        val active = segAssembler
        val asm = if (
            active != null &&
            active.matches(lo, hi, count, rootSize, rootSha256, displayName)
        ) {
            active
        } else {
            com.airferry.app.scan.SegmentAssembler.open(
                root, lo, hi, count, rootSize, decompressedSize, compression,
                crc32, crc32Known, rootSha256, displayName
            ).also { segAssembler = it }
        }

        // Crash recovery: all segments may already be durable while promotion
        // into ContentStore was interrupted. Re-run the idempotent promotion.
        if (asm.isComplete()) {
            return archiveSegmentedTransfer(asm, displayName, decompressedSize)
        }

        try {
            val stored = asm.storeSegment(index, compressedBytes, expectedSha256)
            if (!stored) {
                updateSegmentedProgress(asm)
                clearRecoveryStage()
                swapReceiverForNextSegment()
                return null
            }
        } catch (e: Exception) {
            runOnUiThread {
                Toast.makeText(this, "分段写入失败: ${e.message}", Toast.LENGTH_LONG).show()
            }
            clearRecoveryStage()
            // Keep already-verified segments. A bad/current segment can simply
            // be scanned again; deleting the entire task would make resume lie.
            swapReceiverForNextSegment()
            return null
        }

        if (!asm.isComplete()) {
            updateSegmentedProgress(asm)
            clearRecoveryStage()
            // Keep scanning for the remaining segments.
            swapReceiverForNextSegment()
            return null
        }

        return archiveSegmentedTransfer(asm, displayName, decompressedSize)
    }

    private fun archiveSegmentedTransfer(
        asm: com.airferry.app.scan.SegmentAssembler,
        displayName: String,
        rootSize: Long,
    ): Intent? {
        // Concatenate the compressed segments and stream-decompress exactly once
        // to a temp file. The native call already verified the decompressed
        // length + CRC32 (when known) + root SHA-256 over the decompressed bytes.
        val decompressedFile = asm.finish()
            ?: throw IllegalStateException("分段账本已完成，但解压或完整性校验失败")
        updateRecoveryStage("正在校验完整性…")
        val crcKnown = asm.crc32Known()
        val expectedCrc = asm.crc32()
        val store = com.airferry.app.scan.ContentStore

        // Text / bundle detection needs the bytes in memory. Anything larger
        // than the legacy whole-transfer ceiling is a single file by
        // construction, so skip the in-memory dispatch and stream-copy straight
        // to the content store — this is what lets > 256 MiB files be recovered.
        val small = decompressedFile.length() <= 256L * 1024 * 1024
        val originalBytes = if (small) decompressedFile.readBytes() else ByteArray(0)

        if (small) {
            val receivedCrc = crc32OfBytes(originalBytes)

            // Text payload → ETTEXTv1.
            if (com.airferry.app.scan.TextParser.isText(originalBytes) &&
                com.airferry.app.scan.TextLike.fitsTextUi(originalBytes.size)
            ) {
                val text = com.airferry.app.scan.TextParser.parse(originalBytes)
                if (text != null) {
                    updateRecoveryStage("正在保存文字…")
                    val contentBytes = text.toByteArray(Charsets.UTF_8)
                    val contentCrc = crc32OfBytes(contentBytes)
                    val crcHex = java.lang.Long.toHexString(contentCrc)
                    val textName = when {
                        displayName.isEmpty() -> TEXT_RECEIVED_NAME
                        displayName.contains('.') -> displayName
                        else -> "$displayName.txt"
                    }
                    val put = store.putBytes(
                        this, textName, contentBytes,
                        crcHex = crcHex, crcUnknown = false, kind = "text",
                    )
                    asm.commitArchived(); segAssembler = null; resumeRootId = null
                    clearRecoveryStage()
                    return Intent(this, ReceiveTextActivity::class.java).apply {
                        putExtra("FILE_PATH", put.path.absolutePath)
                        putExtra("FILE_NAME", textName)
                        putExtra("ENTRY_ID", put.entry.id)
                        putExtra("CRC32", expectedCrc)
                        putExtra("CRC32_RECEIVED", receivedCrc)
                        putExtra("CRC32_UNKNOWN", !crcKnown)
                    }
                }
            }

            // Multi-file bundle → one ContentStore entry per member.
            if (com.airferry.app.scan.BundleParser.isBundle(originalBytes)) {
                val bundle = com.airferry.app.scan.BundleParser.parse(originalBytes)
                if (bundle != null && bundle.files.isNotEmpty()) {
                    val totalFiles = bundle.files.size
                    val paths = ArrayList<String>()
                    val names = ArrayList<String>()
                    val sizes = ArrayList<String>()
                    val entryIds = ArrayList<String>()
                    val ts = java.text.SimpleDateFormat("MMdd_HHmmss", java.util.Locale.getDefault())
                        .format(java.util.Date())
                    val bundleId = java.util.UUID.randomUUID().toString()
                    val bundleTitle = "发送_$ts"
                    updateRecoveryStage("正在保存 $totalFiles 个文件…")
                    val puts = store.putBytesBatch(
                        this,
                        bundle.files.map { f ->
                            com.airferry.app.scan.ContentStore.PutBytesRequest(
                                f.name, f.data,
                                crcHex = "unknown", crcUnknown = true, kind = "file",
                                bundleId = bundleId, bundleTitle = bundleTitle,
                            )
                        },
                    )
                    for ((f, put) in bundle.files.zip(puts)) {
                        paths.add(put.path.absolutePath)
                        names.add(f.name)
                        sizes.add(f.data.size.toString())
                        entryIds.add(put.entry.id)
                    }
                    asm.commitArchived(); segAssembler = null; resumeRootId = null
                    clearRecoveryStage()
                    return Intent(this, ReceiveBundleActivity::class.java).apply {
                        putStringArrayListExtra("FILE_PATHS", paths)
                        putStringArrayListExtra("FILE_NAMES", names)
                        putStringArrayListExtra("FILE_SIZES", sizes)
                        putStringArrayListExtra("ENTRY_IDS", entryIds)
                        putExtra("CRC32", expectedCrc)
                        putExtra("CRC32_RECEIVED", receivedCrc)
                        putExtra("CRC32_UNKNOWN", !crcKnown)
                    }
                }
            }
        }

        // Single-file path (works for both small and very large files — for the
        // latter, putFile streams/atomically-moves the on-disk original).
        updateRecoveryStage("正在保存文件…")
        val finalName = if (displayName.isNotEmpty()) displayName else "received_file"
        if (small) {
            if (com.airferry.app.scan.TextLike.isTextLikeName(finalName) &&
                com.airferry.app.scan.TextLike.fitsTextUi(originalBytes.size)
            ) {
                val text = com.airferry.app.scan.TextLike.decodeUtf8Strict(originalBytes)
                if (text != null) {
                    val receivedCrc = crc32OfBytes(originalBytes)
                    val archiveLabel =
                        if (finalName.contains('.')) finalName else TEXT_RECEIVED_NAME
                    val put = store.putBytes(
                        this, archiveLabel, originalBytes,
                        crcHex = java.lang.Long.toHexString(receivedCrc),
                        crcUnknown = false,
                        kind = "text",
                    )
                    asm.commitArchived(); segAssembler = null; resumeRootId = null
                    clearRecoveryStage()
                    return Intent(this, ReceiveTextActivity::class.java).apply {
                        putExtra("FILE_PATH", put.path.absolutePath)
                        putExtra("FILE_NAME", finalName)
                        putExtra("ENTRY_ID", put.entry.id)
                        putExtra("CRC32", if (crcKnown) expectedCrc else receivedCrc)
                        putExtra("CRC32_RECEIVED", receivedCrc)
                        putExtra("CRC32_UNKNOWN", !crcKnown)
                    }
                }
            }
        }

        val put = store.putFile(
            this, finalName, decompressedFile,
            crcHex = if (crcKnown) java.lang.Long.toHexString(expectedCrc) else "unknown",
            crcUnknown = !crcKnown,
            kind = "file",
            expectedSha256Hex = asm.rootSha256Hex(),
            expectedSize = rootSize,
            stableEntryId = "segment-${rootSessionIdHex(asm.rootSessionIdLo(), asm.rootSessionIdHi())}",
        )
        // ContentStore index is now durable; the resumable task can be removed.
        asm.commitArchived()
        segAssembler = null
        resumeRootId = null
        clearRecoveryStage()
        return Intent(this, ReceiveDetailActivity::class.java).apply {
            putExtra("FILE_PATH", put.path.absolutePath)
            putExtra("FILE_SIZE", rootSize)
            putExtra("FILE_NAME", finalName)
            putExtra("ENTRY_ID", put.entry.id)
            putExtra("RESAVE", true)
        }
    }

    /** Update the UI progress with how many segments have been stored. */
    private fun updateSegmentedProgress(asm: com.airferry.app.scan.SegmentAssembler) {
        val received = asm.receivedCount()
        val totalSeg = asm.segmentCount()
        runOnUiThread {
            val s = "分段 $received/$totalSeg 已收，继续扫描下一段…"
            Toast.makeText(this, s, Toast.LENGTH_SHORT).show()
            updateUi { it.copy(statusText = s) }
        }
    }

    /**
     * Swap the receiver to a fresh session for the *next* descriptor-v4 segment.
     *
     * Called from `recoverAndStage` which runs *inside* the decode pool's ingest
     * lock, so we must NOT re-acquire it (`resetSession()` would deadlock).
     * `ingestStopped` is cleared so the capture loop keeps feeding frames.
     */
    private fun swapReceiverForNextSegment() {
        session.destroy()
        session = ReceiverSessionManager()
        ingestStopped.set(false)
        completedHandled = false
        lastUiUpdate = 0
        rateSamples.clear()
        runOnUiThread {
            updateUi {
                it.copy(
                    complete = false,
                    progressPct = 0,
                    receivedSymbols = 0,
                    totalSymbols = 0,
                    decodedBlocks = 0,
                    totalBlocks = 0,
                )
            }
        }
    }

    /** Recover from any post-decode failure without stranding the scanner in
     * `completedHandled=true` / `ingestStopped=true`. */
    private fun resetReceiverAfterRecoveryFailure() {
        val swap = {
            session.destroy()
            session = ReceiverSessionManager()
            ingestStopped.set(false)
            completedHandled = false
            lastUiUpdate = 0
            rateSamples.clear()
        }
        try {
            decodePool?.runExclusive(swap) ?: swap()
        } catch (resetError: Exception) {
            Log.e(TAG, "failed to reset receiver after recovery error", resetError)
        }
    }

    private fun rootSessionIdHex(lo: Long, hi: Long): String {
        val low = java.lang.Long.toUnsignedString(lo, 16).padStart(16, '0')
        val high = java.lang.Long.toUnsignedString(hi, 16).padStart(16, '0')
        return "$high$low"
    }

    private fun idleStatus(): String = resumeRootId?.let {
        "继续恢复任务 ${it.take(8)}… — 对准对应分段二维码"
    } ?: "就绪 — 对准二维码…"

    private fun resetSession() {
        segAssembler = null
        // Swap the receiver under the pool's ingest lock so no worker is mid-ingest
        // while we destroy the old native handle.
        val swap = {
            session.destroy()
            session = ReceiverSessionManager()
            ingestStopped.set(false)
        }
        decodePool?.runExclusive(swap) ?: swap()
        completedHandled = false
        lastUiUpdate = 0
        rateSamples.clear()
        decodePerSec = 0
        recentWireBps = 0L
        transferStartMs = 0L
        recoveryStage.value = null
        updateUi {
            UiState(jniReady = true, statusText = idleStatus())
        }
    }

    private fun updateUi(block: (UiState) -> UiState) {
        uiState.value = block(uiState.value)
    }

    /** Set the live recovery-stage status text (posted to the main thread).
     *  Called from [ioExecutor] during [recoverAndStage] so the user sees the
     *  post-scan pipeline advancing instead of a frozen "完成". */
    private fun updateRecoveryStage(text: String) {
        runOnUiThread { recoveryStage.value = text }
    }

    /** Clear the recovery-stage status (e.g. right before launching the result
     *  Activity, or on error / reset). */
    private fun clearRecoveryStage() {
        runOnUiThread { recoveryStage.value = null }
    }

    // slotScreenPos 已移除（火花动画已删除）。

    // refreshOverlay / dedupeSparksBySlot 已移除（火花动画已删除）。
    override fun onResume() {
        super.onResume()
        // If returning from ReceiveDetailActivity after completion, reset for next scan.
        if (completedHandled) {
            val swap = {
                session.destroy()
                session = ReceiverSessionManager()
                ingestStopped.set(false)
            }
            decodePool?.runExclusive(swap) ?: swap()
            completedHandled = false
            lastUiUpdate = 0
            rateSamples.clear()
            decodePerSec = 0
            recentWireBps = 0L
            transferStartMs = 0L
            recoveryStage.value = null
            updateUi { UiState(jniReady = true, statusText = idleStatus()) }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        cameraExecutor.shutdown()
        // Drain the IO executor BEFORE tearing down the decode pool: the pending
        // recovery task holds the pool's ingest lock and touches the native
        // session, so freeing the handle first would race it. Shutdown + await
        // lets an in-flight stage finish (bounded; assemble is the slow part and
        // already running under ingestStopped, which halted further ingest).
        ioExecutor.shutdown()
        try {
            ioExecutor.awaitTermination(30, java.util.concurrent.TimeUnit.SECONDS)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        // Stop workers, then destroy the native receiver UNDER the ingest lock so a
        // straggler that outran shutdown()'s join timeout can't still be mid-ingest
        // (&mut) when destroy() frees the handle (use-after-free).
        val pool = decodePool
        decodePool = null
        if (pool != null) {
            pool.shutdown()
            pool.runExclusive { session.destroy() }
        } else {
            session.destroy()
        }
    }

    companion object {
        private const val TAG = "ScanActivity"
        /**
         * Sliding window for decode rate + wire throughput shown in the info card.
         * ~3s is responsive enough to feel "live" without jittering every tick.
         */
        private const val RATE_WINDOW_MS = 3_000L
        /** Minimum Δt before publishing a rate (avoids 1-tick spikes). */
        private const val RATE_MIN_DT_MS = 300L
        /** Default display/store name when an older TEXT descriptor has no filename. */
        private const val TEXT_RECEIVED_NAME = "文字消息.txt"

        fun formatSize(bytes: Long): String {
            if (bytes < 1024) return "$bytes B"
            if (bytes < 1024 * 1024) return "%.1f KB".format(bytes / 1024.0)
            return "%.1f MB".format(bytes / 1024.0 / 1024.0)
        }

        /** Format milliseconds as a human-readable duration (e.g. "23 秒", "1 分 05 秒"). */
        fun formatDuration(ms: Long): String {
            val totalSec = ms / 1000
            if (totalSec < 60) return "${totalSec} 秒"
            val m = totalSec / 60
            val s = totalSec % 60
            return "${m} 分 ${s.toString().padStart(2, '0')} 秒"
        }

        fun crc32OfBytes(data: ByteArray): Long {
            // Compute CRC32 and return as an unsigned 32-bit value in a Long
            // (0..=0xFFFFFFFF) so it compares correctly with the JNI-supplied
            // expected CRC (also a Long). Using Int would sign-flip high-bit
            // values and break equality.
            var crc = 0xFFFFFFFF.toInt()
            for (b in data) {
                crc = crc xor (b.toInt() and 0xFF)
                repeat(8) {
                    crc = if (crc and 1 != 0) (crc ushr 1) xor 0xEDB88320.toInt() else crc ushr 1
                }
            }
            return (crc xor 0xFFFFFFFF.toInt()).toLong() and 0xFFFFFFFFL
        }
    }
}
