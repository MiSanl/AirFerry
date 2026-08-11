using System.Diagnostics;
using System.IO;
using System.Text;
using AirFerry.Windows.Bundle;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;

namespace AirFerry.Windows.ViewModels;

/// <summary>
/// The scan-page state machine — the Windows counterpart of Android's
/// <c>ScanActivity</c>. Owns the <see cref="VideoCapture"/> (producer),
/// <see cref="QrDecodePool"/> (N parallel decoders + serialized ingest), and a
/// single <see cref="ReceiverSession"/> (the Rust RaptorQ engine). On completion
/// it assembles the bytes, trims RaptorQ zero-padding, verifies CRC, unpacks a
/// bundle if present, and stages the result for the detail/bundle views.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading model</b>: a dedicated producer thread pulls frames from the
/// camera and feeds the pool. The pool's workers do the ZXing decode in
/// parallel; ingest (the <see cref="ReceiverSession.Ingest"/> call) is
/// serialized inside the pool under <see cref="QrDecodePool.IngestLock"/>. The
/// final assemble also runs under that lock (via <see cref="QrDecodePool.RunExclusive{T}"/>)
/// so no straggler ingest can race the borrow. The recovery task remains part
/// of the session lifetime: teardown waits for it and all workers before
/// destroying the native receiver.
/// </para>
/// <para>
/// <b>Files land in</b> the content-addressed <see cref="ContentStore"/> under
/// <c>%USERPROFILE%\Documents\AirFerry\store\</c>.
/// </para>
/// </remarks>
public partial class ScanViewModel : ObservableObject, IDisposable
{
    private AirFerry.Windows.Scan.VideoCapture? _capture;
    private QrDecodePool? _pool;
    private ReceiverSession? _session;
    private Thread? _producerThread;
    private volatile bool _producerRunning;
    private bool _disposed;
    private int _recoveryStarted;
    private int _sessionEpoch;
    private readonly object _lifecycleGate = new();
    private Task<RecoveryResult?>? _recoveryCoreTask;
    private Task _deferredCleanupTask = Task.CompletedTask;
    private readonly Queue<RateSample> _rateSamples = new();
    private long _transferStartTimestamp;
    private long _decodePerSecond;
    private long _recentWireBytesPerSecond;
    private const int PreviewFps = 15;
    private const int RateWindowSeconds = 3;
    private const int RateMinMilliseconds = 500;
    private string? _resumeRootId;
    /// <summary>Disk-backed assembler for a descriptor-v4 large transfer (null = none).</summary>
    private AirFerry.Windows.Bundle.SegmentAssembler? _segAssembler;

    private sealed record AssembledPayload(
        byte[] Bytes,
        ulong ExpectedCrc,
        bool CrcKnown,
        string DisplayName,
        ulong OriginalSize);

    private readonly record struct RateSample(
        long Timestamp, long DecodedSymbols, long ReceivedSymbols);

    private readonly record struct LiveSnapshot(
        ProgressSnapshot? Progress,
        string FileName,
        ulong FileSize,
        uint SymbolSize,
        int EstimatedTotalSymbols);

    public ScanViewModel(string? resumeRootId = null)
    {
        if (resumeRootId is null) return;
        string normalized = resumeRootId.Trim().ToLowerInvariant();
        if (normalized.Length != 32 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("待恢复任务 ID 无效", nameof(resumeRootId));
        _resumeRootId = normalized;
    }

    /// <summary>The device index chosen in the device-select page.</summary>
    [ObservableProperty]
    private int _selectedDeviceIndex;

    [ObservableProperty]
    private string _statusText = "等待扫码…";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _receivedSymbolsText = "0";

    [ObservableProperty]
    private string _totalSymbolsText = "0";

    [ObservableProperty]
    private string _lossRatioText = "0.0%";

    [ObservableProperty]
    private string _recoveryStageText = string.Empty;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private bool _isRecovering;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanMetricsText = "采集 0 帧 · 丢弃 0 帧 · 解码 0 码";

    [ObservableProperty]
    private string _fileSummaryText = "等待描述符…";

    [ObservableProperty]
    private string _transferMetricsText = "解码 0 符号/秒 · 有效 0 B/s · 用时 00:00";

    /// <summary>Raised when a transfer finishes recovering — carries the result.</summary>
    public event Action<RecoveryResult>? TransferCompleted;

    /// <summary>
    /// Raised by the producer thread at most <see cref="PreviewFps"/> times per
    /// second. Subscribers must marshal rendering to their UI dispatcher.
    /// </summary>
    public event Action<PreviewFrame>? PreviewFrameReady;

    /// <summary>Legacy archive directory, retained only for one-time migration.</summary>
    public static string ReceivedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AirFerry", "received");

    /// <summary>Temp dir for staging recovered bytes before archive.</summary>
    private static string TempDir => Path.Combine(Path.GetTempPath(), "AirFerry");

    /// <summary>
    /// Start the pipeline on <paramref name="deviceIndex"/>. Idempotent —
    /// calling while running first stops the previous session.
    /// </summary>
    [RelayCommand]
    public void StartScan(int deviceIndex)
    {
        StopScan();
        lock (_lifecycleGate)
        {
            if (!_deferredCleanupTask.IsCompleted)
            {
                StatusText = "上一个摄像头仍在后台释放，请稍后重试";
                return;
            }
        }
        Interlocked.Increment(ref _sessionEpoch);
        SelectedDeviceIndex = deviceIndex;
        IsComplete = false;
        IsRecovering = false;
        Progress = 0;
        ReceivedSymbolsText = "0";
        TotalSymbolsText = "0";
        LossRatioText = "0.0%";
        ResetLiveMetrics();
        RecoveryStageText = string.Empty;

        try
        {
            uint zxingAbi = ZxingDecoder.AbiVersion();
            if (zxingAbi != 1)
            {
                throw new InvalidOperationException(
                    $"二维码解码库 ABI 不兼容（期望 1，实际 {zxingAbi}）");
            }
            _session = new ReceiverSession();
            Interlocked.Exchange(ref _recoveryStarted, 0);
            _capture = new Scan.VideoCapture(deviceIndex);
            if (!_capture.IsOpen)
            {
                StopScan();
                StatusText = "无法打开设备，请检查是否被其他程序占用";
                return;
            }

            // The onDecoded callback runs under the pool's IngestLock. Returns true
            // when this symbol completes recovery so the pool stops ingesting.
            _pool = new QrDecodePool((payload, bbox) => OnDecoded(payload, bbox));
            _pool.Start();

            // Producer thread: pull frames and enqueue them. The pool handles the
            // drop-newest backpressure when workers can't keep up.
            _producerRunning = true;
            _producerThread = new Thread(ProducerLoop)
            {
                IsBackground = true,
                Name = "video-producer",
            };
            _producerThread.Start();

            IsScanning = true;
            StatusText = _resumeRootId is null
                ? "正在扫描…对准屏幕上的二维码"
                : $"正在继续任务 {_resumeRootId[..8]}…，其他文件会被忽略";
        }
        catch (Exception ex)
        {
            StopScan();
            StatusText = $"启动设备失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void StopScan()
    {
        Thread? producer;
        QrDecodePool? pool;
        Scan.VideoCapture? capture;
        ReceiverSession? session;
        Task<RecoveryResult?>? recoveryTask;
        Task cleanup;
        lock (_lifecycleGate)
        {
            _producerRunning = false;
            IsScanning = false;
            Interlocked.Increment(ref _sessionEpoch);

            // A previously detached camera read is still being cleaned up. Do
            // not lose that task or attempt to dispose the same pipeline twice.
            if (_capture is null && _pool is null && _session is null &&
                !_deferredCleanupTask.IsCompleted)
            {
                StatusText = "摄像头响应缓慢，正在后台安全释放…";
                return;
            }
            producer = _producerThread;
            _producerThread = null;
            pool = _pool;
            _pool = null;
            capture = _capture;
            _capture = null;
            session = _session;
            _session = null;
            recoveryTask = _recoveryCoreTask;
            if (producer is null && pool is null && capture is null &&
                session is null && recoveryTask is null)
            {
                cleanup = Task.CompletedTask;
            }
            else
            {
                // Publish the cleanup task while still holding the lifecycle
                // gate. A simultaneous StopScan then observes it and cannot
                // detach/dispose a second copy of this pipeline.
                cleanup = Task.Run(() => CleanupDetachedPipeline(
                    producer, pool, capture, session, recoveryTask));
                _deferredCleanupTask = cleanup;
            }
        }

        if (ReferenceEquals(cleanup, Task.CompletedTask))
        {
            ResetStoppedUi();
            return;
        }

        // Never free a capture, decode pool or Rust session while a producer,
        // native decode, ingest or recovery call may still be using it. Perform
        // the complete ordered teardown as one task. A wedged DirectShow read is
        // quarantined after a short wait so navigation remains responsive; the
        // task retains every resource and disposes them only after the read exits.
        Task completed = Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(2)))
            .GetAwaiter().GetResult();
        if (!ReferenceEquals(completed, cleanup))
        {
            _ = cleanup.ContinueWith(t =>
            {
                _ = t.Exception; // Observe a delayed teardown fault.
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_deferredCleanupTask, cleanup))
                        _deferredCleanupTask = Task.CompletedTask;
                    if (ReferenceEquals(_recoveryCoreTask, recoveryTask))
                        _recoveryCoreTask = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            StatusText = "摄像头响应缓慢，正在后台安全释放…";
            IsRecovering = false;
            return;
        }

        try
        {
            cleanup.GetAwaiter().GetResult();
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_deferredCleanupTask, cleanup))
                    _deferredCleanupTask = Task.CompletedTask;
                if (ReferenceEquals(_recoveryCoreTask, recoveryTask))
                    _recoveryCoreTask = null;
            }
        }
        ResetStoppedUi();
    }

    private static void CleanupDetachedPipeline(
        Thread? producer,
        QrDecodePool? pool,
        Scan.VideoCapture? capture,
        ReceiverSession? session,
        Task<RecoveryResult?>? recoveryTask)
    {
        // Producer owns ReadGray/SnapshotBgr. It must exit before capture.Dispose.
        if (producer?.IsAlive == true) producer.Join();

        if (recoveryTask is not null)
        {
            try
            {
                recoveryTask.GetAwaiter().GetResult();
            }
            catch
            {
                // The UI continuation reports recovery errors. Teardown still
                // owns and must release all native/managed resources.
            }
        }

        try
        {
            if (pool is not null)
            {
                pool.RunExclusive(() =>
                {
                    pool.IngestStopped = true;
                    return true;
                });
                pool.Dispose();
            }
        }
        finally
        {
            try
            {
                session?.Dispose();
            }
            finally
            {
                capture?.Dispose();
            }
        }
    }

    private void ResetStoppedUi()
    {
        IsRecovering = false;
        if (!IsComplete)
        {
            Progress = 0;
            ReceivedSymbolsText = "0";
            StatusText = "已停止";
        }
    }

    /// <summary>
    /// Reset for a fresh scan: clear completion + progress so a new transfer can
    /// start from zero.
    /// </summary>
    [RelayCommand]
    public void ResetSession()
    {
        StopScan();
        IsComplete = false;
        Progress = 0;
        ReceivedSymbolsText = "0";
        TotalSymbolsText = "0";
        LossRatioText = "0.0%";
        ResetLiveMetrics();
        RecoveryStageText = string.Empty;
        StatusText = "等待扫码…";
    }

    /// <summary>
    /// Producer: perform the only camera read, feed grayscale pixels to the
    /// decode pool, and publish a throttled BGR snapshot for preview.
    /// </summary>
    private void ProducerLoop()
    {
        long previewInterval = Math.Max(1, Stopwatch.Frequency / PreviewFps);
        long nextPreviewAt = 0;
        while (_producerRunning)
        {
            // Snapshot references once per iteration. StopScan may detach the
            // fields while a driver call is blocked, but keeps these objects
            // alive until this producer exits.
            Scan.VideoCapture? capture = _capture;
            QrDecodePool? pool = _pool;
            if (capture is null || pool is null) break;
            Mat? gray = capture.ReadGray();
            if (gray is null)
            {
                // Camera exhausted — a few nulls in a row means the device died.
                Thread.Sleep(10);
                continue;
            }
            // Submit clones the pixels; the Mat itself is reused by VideoCapture.
            pool.Submit(gray);

            long now = Stopwatch.GetTimestamp();
            if (now >= nextPreviewAt)
            {
                PreviewFrame? preview = capture.SnapshotBgr();
                if (preview is not null)
                {
                    Action<PreviewFrame>? handler = PreviewFrameReady;
                    if (handler is null)
                    {
                        preview.Dispose();
                        nextPreviewAt = now + previewInterval;
                        continue;
                    }
                    try
                    {
                        // Ownership transfers to the single UI subscriber.
                        handler(preview);
                    }
                    catch
                    {
                        preview.Dispose();
                        // Preview is cosmetic. A subscriber must never kill the
                        // capture/decode producer thread.
                    }
                }
                nextPreviewAt = now + previewInterval;
            }
        }
    }

    /// <summary>
    /// Per-frame ingest callback (runs under <see cref="QrDecodePool.IngestLock"/>).
    /// Returns true when this symbol completes recovery.
    /// </summary>
    private bool OnDecoded(byte[] payload, int[]? unusedBbox)
    {
        QrDecodePool? pool = _pool;
        ReceiverSession? session = _session;
        if (pool is null || pool.IngestStopped || session is null)
        {
            return false;
        }
        IngestStatus? status = session.Ingest(payload);
        if (status is null)
        {
            return false;
        }
        IngestStatus s = status.Value;
        int epoch = Volatile.Read(ref _sessionEpoch);

        if (s.Complete)
        {
            if (Interlocked.Exchange(ref _recoveryStarted, 1) == 0)
            {
                // Only UI state is changed on the dispatcher. Native assembly,
                // hashing and disk I/O run on the thread pool.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (epoch != Volatile.Read(ref _sessionEpoch) ||
                        !ReferenceEquals(session, _session) ||
                        !ReferenceEquals(pool, _pool))
                    {
                        return;
                    }
                    IsComplete = true;
                    _ = RecoverAndStageAsync(session, pool, epoch);
                });
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Assemble + verify + stage the recovered bytes. Mirrors Android's
    /// <c>recoverAndStage</c> step by step.
    /// </summary>
    private async Task RecoverAndStageAsync(
        ReceiverSession session, QrDecodePool pool, int epoch)
    {
        Task<RecoveryResult?> coreTask;
        lock (_lifecycleGate)
        {
            if (epoch != Volatile.Read(ref _sessionEpoch) ||
                !ReferenceEquals(session, _session) ||
                !ReferenceEquals(pool, _pool))
            {
                return;
            }
            coreTask = Task.Run(() => RecoverAndStageCore(session, pool));
            _recoveryCoreTask = coreTask;
        }
        IsRecovering = true;
        RecoveryStageText = "正在组装数据…";

        RecoveryResult? result;
        try
        {
            result = await coreTask;
        }
        catch (Exception ex)
        {
            bool reset = ResetReceiverAfterRecoveryFailure(session, pool, epoch);
            if (epoch == Volatile.Read(ref _sessionEpoch))
            {
                IsComplete = false;
                IsRecovering = false;
                RecoveryStageText = string.Empty;
                StatusText = reset
                    ? $"当前分段校验失败，可重新扫码: {ex.Message}"
                    : $"恢复失败: {ex.Message}";
            }
            return;
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_recoveryCoreTask, coreTask))
                {
                    _recoveryCoreTask = null;
                }
            }
        }

        if (epoch != Volatile.Read(ref _sessionEpoch))
        {
            return;
        }

        IsRecovering = false;
        RecoveryStageText = string.Empty;
        if (result is null)
        {
            // A large-transfer segment was stored but the transfer is not yet
            // complete — keep scanning for the remaining segments.
            if (_segAssembler is not null)
            {
                IsComplete = false;
                StatusText = _segAssembler.IsComplete()
                    ? "正在合并分段…"
                    : $"分段 {_segAssembler.ReceivedCount()}/{_segAssembler.SegmentCount()} 已收，继续扫描下一段…";
                return;
            }
            IsComplete = false;
            StatusText = _resumeRootId is null
                ? "组装失败"
                : $"等待任务 {_resumeRootId[..8]}… 的分段，其他文件已忽略";
            return;
        }
        StatusText = "接收完成";
        TransferCompleted?.Invoke(result);
    }

    private RecoveryResult? RecoverAndStageCore(ReceiverSession session, QrDecodePool pool)
    {
        pool.IngestStopped = true;

        // descriptor-v4 large transfer: store this segment into the disk-backed
        // assembler and return once every segment has arrived.
        if (pool.RunExclusive(() => session.IsSegmented()))
        {
            return HandleSegmentedTransfer(session, pool);
        }
        if (_resumeRootId is not null)
        {
            SwapReceiverForNextSegment(session, pool);
            return null;
        }

        // Take one coherent native snapshot under the ingest lock. No metadata
        // getter is allowed to outlive or race disposal of the native handle.
        AssembledPayload? payload = pool.RunExclusive<AssembledPayload?>(() =>
        {
            byte[]? bytes = session.Assemble();
            return bytes is null || bytes.Length == 0
                ? null
                : new AssembledPayload(
                    bytes,
                    session.Crc32(),
                    session.Crc32Known(),
                    session.FileName(),
                    session.FileSize());
        });
        if (payload is null)
        {
            SwapReceiverForNextSegment(session, pool);
            return null;
        }

        byte[] bytes = payload.Bytes;
        ulong expectedCrc = payload.ExpectedCrc;
        bool crcKnown = payload.CrcKnown;
        ulong receivedCrc = Crc32.Compute(bytes);
        string displayName = payload.DisplayName;
        ulong originalSize = payload.OriginalSize;

        RecoveryResult? result;
        if (TextParser.IsText(bytes))
        {
            // Text payload → decode UTF-8, stage under the descriptor filename,
            // and carry the string for the copy/share UI. Checked BEFORE the
            // bundle branch: the two magics never collide ("ETTEXTv1" vs
            // "ETBUNDL1"). If decoding fails, fall through to single-file
            // handling so the user still gets something.
            string? text = TextParser.Parse(bytes);
            result = text is not null
                ? StageEtText(text, displayName, expectedCrc, crcKnown, receivedCrc)
                : StageSingleFile(bytes, displayName, originalSize,
                    expectedCrc, crcKnown, receivedCrc);
        }
        else if (BundleParser.IsBundle(bytes))
        {
            result = StageBundle(bytes, expectedCrc, crcKnown, receivedCrc);
            // If parsing failed, fall through to single-file handling.
            result ??= StageSingleFile(bytes, displayName, originalSize,
                expectedCrc, crcKnown, receivedCrc);
        }
        else if (FileNameUtil.IsTextLikeName(
                     string.IsNullOrEmpty(displayName) ? "received_file" : displayName)
                 && FileNameUtil.FitsTextUi(bytes.LongLength))
        {
            // Single text-like document (readme.md, notes.json, …): open the
            // copy/share UI only when the payload is valid UTF-8 and small enough
            // for the in-memory text view. Still stage a temp file so save-as
            // can use the original name.
            string? text = FileNameUtil.DecodeUtf8Strict(bytes);
            result = text is not null
                ? StageTextLikeFile(bytes, displayName, originalSize,
                    expectedCrc, crcKnown, receivedCrc, text)
                : StageSingleFile(bytes, displayName, originalSize,
                    expectedCrc, crcKnown, receivedCrc);
        }
        else
        {
            result = StageSingleFile(bytes, displayName, originalSize,
                expectedCrc, crcKnown, receivedCrc);
        }

        return result;
    }

    /// <summary>
    /// Store one recovered descriptor-v4 segment into the disk-backed assembler.
    /// Returns a <see cref="RecoveryResult"/> only once every segment of the root
    /// transfer has arrived and been merged; otherwise null (the receiver keeps
    /// scanning for the next segment).
    /// </summary>
    private RecoveryResult? HandleSegmentedTransfer(ReceiverSession session, QrDecodePool pool)
    {
        // Take a coherent native snapshot under the ingest lock: metadata +
        // assembled (uncompressed) bytes for this segment.
        SegmentPayload? seg = pool.RunExclusive<SegmentPayload?>(() =>
        {
            byte[]? bytes = session.Assemble();
            if (bytes is null || bytes.Length == 0) return null;
            return new SegmentPayload(
                bytes,
                session.SegmentIndex(),
                session.SegmentCount(),
                session.RootOriginalSize(),
                session.RootSessionIdLo(),
                session.RootSessionIdHi(),
                session.FileName(),
                session.FileSize(),
                session.OriginalOffset(),
                session.RawSha256(),
                session.Crc32(),
                session.Crc32Known());
        });
        if (seg is null)
        {
            SwapReceiverForNextSegment(session, pool);
            return null;
        }

        int index = (int)seg.SegmentIndex;
        int count = (int)seg.SegmentCount;
        ulong rootSize = seg.RootOriginalSize;
        ulong lo = seg.RootLo;
        ulong hi = seg.RootHi;
        string rootId = $"{hi:x16}{lo:x16}";
        string displayName = string.IsNullOrEmpty(seg.FileName) ? "received_file" : seg.FileName;

        byte[] segBytes = seg.Bytes;
        if (count is <= 0 or > AirFerry.Windows.Bundle.SegmentAssembler.MaxSegmentCount)
            throw new InvalidDataException("分段数量超出安全上限");
        if (rootSize == 0 || rootSize > (ulong)long.MaxValue)
            throw new InvalidDataException("根文件大小无效");
        if (index < 0 || index >= count ||
            seg.OriginalOffset != checked((ulong)index *
                (ulong)AirFerry.Windows.Bundle.SegmentAssembler.SegmentRawBytes))
            throw new InvalidDataException("分段索引或偏移无效");
        ulong expectedCount = checked((rootSize - 1) /
            (ulong)AirFerry.Windows.Bundle.SegmentAssembler.SegmentRawBytes + 1);
        if ((ulong)count != expectedCount)
            throw new InvalidDataException("分段数量与根文件大小不一致");
        ulong expectedLength = Math.Min(
            (ulong)AirFerry.Windows.Bundle.SegmentAssembler.SegmentRawBytes,
            rootSize - seg.OriginalOffset);
        if (seg.OriginalSize == 0 || seg.OriginalSize != expectedLength ||
            seg.OriginalSize != (ulong)segBytes.LongLength)
            throw new InvalidDataException("分段实际长度与描述符不一致");
        if (seg.RawSha256.Length != 32)
            throw new InvalidDataException("分段描述符缺少 SHA-256");
        if (seg.CrcKnown && Crc32.Compute(segBytes) != seg.ExpectedCrc)
            throw new InvalidDataException($"分段 {index + 1}/{count} CRC32 校验失败");
        if (_resumeRootId is not null &&
            !string.Equals(rootId, _resumeRootId, StringComparison.Ordinal))
        {
            SwapReceiverForNextSegment(session, pool);
            return null;
        }

        // Reuse the active root so a long, sequential transfer does not reopen
        // the ledger and re-hash every earlier 8 MiB segment for each child.
        // Interleaved roots still open their own identity-bound assembler.
        var asm = _segAssembler is not null
                  && _segAssembler.Matches(lo, hi, count, (long)rootSize, displayName)
            ? _segAssembler
            : AirFerry.Windows.Bundle.SegmentAssembler.Open(
                lo, hi, count, (long)rootSize, displayName);
        _segAssembler = asm;

        // Crash recovery: all segments may already be durable while history
        // promotion was interrupted. Promotion is deliberately idempotent.
        if (asm.IsComplete())
            return ArchiveSegmentedTransfer(asm, displayName, rootSize);

        if (asm.HasSegment(index))
        {
            UpdateSegmentedProgress(asm);
            SwapReceiverForNextSegment(session, pool);
            return null;
        }
        // A failure leaves all earlier verified segments untouched. The outer
        // recovery boundary swaps in a fresh child receiver so this segment can
        // be scanned again immediately.
        asm.StoreSegment(index, segBytes, seg.RawSha256);

        if (!asm.IsComplete())
        {
            UpdateSegmentedProgress(asm);
            SwapReceiverForNextSegment(session, pool);
            return null;
        }

        return ArchiveSegmentedTransfer(asm, displayName, rootSize);
    }

    private RecoveryResult ArchiveSegmentedTransfer(
        AirFerry.Windows.Bundle.SegmentAssembler asm,
        string displayName,
        ulong rootSize)
    {
        string finalPath = asm.Finish()
            ?? throw new InvalidDataException(
                "分段账本已完成，但临时文件缺失或损坏");
        var put = AirFerry.Windows.Bundle.ContentStore.PutFile(
            displayName, finalPath,
            crcHex: "unknown", crcUnknown: true, kind: "file");
        asm.CommitArchived();
        _segAssembler = null;
        _resumeRootId = null;
        return new RecoveryResult(
            SingleFilePath: put.Path,
            SingleFileSize: rootSize,
            ExpectedCrc32: null,
            Crc32Known: false,
            ReceivedCrc32: null,
            Bundle: null,
            BundleDir: null,
            DisplayName: displayName);
    }

    private sealed record SegmentPayload(
        byte[] Bytes,
        uint SegmentIndex,
        uint SegmentCount,
        ulong RootOriginalSize,
        ulong RootLo,
        ulong RootHi,
        string FileName,
        ulong OriginalSize,
        ulong OriginalOffset,
        byte[] RawSha256,
        ulong ExpectedCrc,
        bool CrcKnown);

    private void UpdateSegmentedProgress(AirFerry.Windows.Bundle.SegmentAssembler asm)
    {
        StatusText = $"分段 {asm.ReceivedCount()}/{asm.SegmentCount()} 已收，继续扫描下一段…";
    }

    /// <summary>Swap to a fresh receiver for the next segment.</summary>
    private void SwapReceiverForNextSegment(ReceiverSession session, QrDecodePool pool)
    {
        lock (_lifecycleGate)
        {
            if (!ReferenceEquals(session, _session) || !ReferenceEquals(pool, _pool))
                return;
            pool.RunExclusive(() =>
            {
                session.Destroy();
                _session = new ReceiverSession();
                Interlocked.Exchange(ref _recoveryStarted, 0);
                pool.IngestStopped = false;
            });
        }
    }

    private bool ResetReceiverAfterRecoveryFailure(
        ReceiverSession session, QrDecodePool pool, int epoch)
    {
        lock (_lifecycleGate)
        {
            if (epoch != Volatile.Read(ref _sessionEpoch) ||
                !ReferenceEquals(session, _session) ||
                !ReferenceEquals(pool, _pool))
                return false;
            pool.RunExclusive(() =>
            {
                session.Destroy();
                _session = new ReceiverSession();
                Interlocked.Exchange(ref _recoveryStarted, 0);
                pool.IngestStopped = false;
            });
            return true;
        }
    }

    private RecoveryResult StageSingleFile(byte[] bytes, string displayName,
        ulong originalSize, ulong expectedCrc, bool crcKnown, ulong receivedCrc)
    {
        string finalName = string.IsNullOrEmpty(displayName) ? "received_file" : displayName;
        string crcHex = crcKnown ? expectedCrc.ToString("x") : "unknown";
        ContentStore.PutResult put = ContentStore.PutBytes(
            finalName, bytes, crcHex, crcUnknown: !crcKnown, kind: "file");
        return new RecoveryResult(
            SingleFilePath: put.Path,
            SingleFileSize: originalSize > 0 ? originalSize : (ulong)bytes.Length,
            ExpectedCrc32: expectedCrc,
            Crc32Known: crcKnown,
            ReceivedCrc32: receivedCrc,
            Bundle: null,
            BundleDir: null,
            DisplayName: finalName);
    }

    /// <summary>
    /// Stage a pure ETTEXTv1 message: store UTF-8 body under the descriptor
    /// filename (user-chosen on sender; default "文字消息.txt").
    /// </summary>
    private RecoveryResult StageEtText(string text, string displayName,
        ulong expectedCrc, bool crcKnown, ulong receivedCrc)
    {
        // Store the UTF-8 body (without magic), while retaining transport CRC
        // fields so corruption is not hidden by recomputing a different hash.
        string finalName = string.IsNullOrEmpty(displayName)
            ? "文字消息.txt"
            : (displayName.Contains('.') ? displayName : displayName + ".txt");
        byte[] contentBytes = Encoding.UTF8.GetBytes(text);
        ulong contentCrc = Crc32.Compute(contentBytes);
        string crcHex = contentCrc.ToString("x");
        ContentStore.PutResult put = ContentStore.PutBytes(
            finalName, contentBytes, crcHex, crcUnknown: false, kind: "text");
        return new RecoveryResult(
            SingleFilePath: put.Path,
            SingleFileSize: (ulong)contentBytes.Length,
            ExpectedCrc32: expectedCrc,
            Crc32Known: crcKnown,
            ReceivedCrc32: receivedCrc,
            Bundle: null,
            BundleDir: null,
            Text: text,
            DisplayName: finalName);
    }

    /// <summary>
    /// Stage a text-like single file into ContentStore and keep text for the copy UI.
    /// </summary>
    private RecoveryResult StageTextLikeFile(byte[] bytes, string displayName,
        ulong originalSize, ulong expectedCrc, bool crcKnown, ulong receivedCrc, string text)
    {
        string finalName = string.IsNullOrEmpty(displayName) ? "文字消息.txt" : displayName;
        ContentStore.PutResult put = ContentStore.PutBytes(
            finalName, bytes,
            crcHex: crcKnown ? expectedCrc.ToString("x") : "unknown",
            crcUnknown: !crcKnown, kind: "text");
        return new RecoveryResult(
            SingleFilePath: put.Path,
            SingleFileSize: originalSize > 0 ? originalSize : (ulong)bytes.Length,
            ExpectedCrc32: expectedCrc,
            Crc32Known: crcKnown,
            ReceivedCrc32: receivedCrc,
            Bundle: null,
            BundleDir: null,
            Text: text,
            DisplayName: finalName);
    }

    private RecoveryResult? StageBundle(byte[] bytes, ulong expectedCrc,
        bool crcKnown, ulong receivedCrc)
    {
        AirFerry.Windows.Bundle.Bundle? bundle = BundleParser.Parse(bytes);
        if (bundle is null || bundle.Files.Count == 0)
        {
            return null;
        }
        string bundleId = Guid.NewGuid().ToString("N");
        string bundleTitle = $"发送_{DateTime.Now:MMdd_HHmmss}";
        var staged = new List<BundleFile>(bundle.Files.Count);
        foreach (BundleFile f in bundle.Files)
        {
            ContentStore.PutResult put = ContentStore.PutBytes(
                f.Name, f.Data, kind: "file",
                bundleId: bundleId, bundleTitle: bundleTitle);
            // Keep in-memory bytes for the bundle UI; disk is content-addressed.
            staged.Add(new BundleFile(f.Name, f.Data));
            _ = put;
        }
        return new RecoveryResult(
            SingleFilePath: null,
            SingleFileSize: null,
            ExpectedCrc32: expectedCrc,
            Crc32Known: crcKnown,
            ReceivedCrc32: receivedCrc,
            Bundle: staged,
            BundleDir: null);
    }

    /// <summary>
    /// Periodically poll progress for the live UI (called by a timer at ~7 Hz).
    /// Keeps the hot ingest path allocation-free.
    /// </summary>
    public void RefreshProgress()
    {
        QrDecodePool? pool = _pool;
        ReceiverSession? session = _session;
        long now = Stopwatch.GetTimestamp();
        if (pool is not null)
        {
            ScanMetricsText = $"采集 {pool.CapturedFrames} 帧 · " +
                $"丢弃 {pool.DroppedFrames} 帧 · 解码 {pool.DecodedSymbols} 码";
        }
        if (pool is null || session is null)
        {
            return;
        }

        LiveSnapshot live = pool.RunExclusive(() =>
        {
            if (!session.IsInitialized)
            {
                return new LiveSnapshot(null, string.Empty, 0, 0, 0);
            }
            return new LiveSnapshot(
                session.Progress(),
                session.FileName(),
                session.FileSize(),
                session.SymbolSizeBytes,
                session.EstimatedTotalSymbols);
        });
        if (live.Progress is null)
        {
            return;
        }
        ProgressSnapshot p = live.Progress.Value;
        UpdateRates(now, pool.DecodedSymbols, p.ReceivedSymbols, live.SymbolSize, p.Complete);
        UpdateFileSummary(live, p);

        if (p.TotalSymbols > 0)
        {
            if (_transferStartTimestamp == 0)
            {
                _transferStartTimestamp = now;
            }
            Progress = p.Complete
                ? 100
                : Math.Clamp(p.ReceivedSymbols * 100.0 / p.TotalSymbols, 0, 100);
            TotalSymbolsText = p.TotalSymbols.ToString();
        }
        else if (p.ReceivedSymbols > 0)
        {
            Progress = live.EstimatedTotalSymbols > 0
                ? Math.Clamp(p.ReceivedSymbols * 100.0 / live.EstimatedTotalSymbols, 0, 15)
                : 0;
        }
        ReceivedSymbolsText = p.ReceivedSymbols.ToString();
        LossRatioText = $"{p.LossRatio * 100:F1}%";

        if (!IsRecovering)
        {
            StatusText = p.Complete
                ? "✓ 文件恢复完成"
                : !p.MetaConfirmed && p.ReceivedSymbols > 0
                    ? $"正在同步…已缓存 {p.ReceivedSymbols} 个符号"
                    : p.TotalSymbols == 0
                        ? "等待二维码…"
                        : p.ReceivedSymbols > 0 && p.DecodedBlocks == 0
                            ? $"接收中… {p.ReceivedSymbols}/{p.TotalSymbols}（等待解码）"
                            : $"恢复中… {Progress:F0}%";
        }
    }

    private void UpdateRates(long now, long decoded, long received, uint symbolSize, bool complete)
    {
        if (complete)
        {
            _rateSamples.Clear();
            _decodePerSecond = 0;
            _recentWireBytesPerSecond = 0;
        }
        else if (decoded > 0 || received > 0)
        {
            _rateSamples.Enqueue(new RateSample(now, decoded, received));
            long cutoff = now - Stopwatch.Frequency * RateWindowSeconds;
            while (_rateSamples.Count > 1 && _rateSamples.Peek().Timestamp < cutoff)
            {
                _rateSamples.Dequeue();
            }
            if (_rateSamples.Count >= 2)
            {
                RateSample oldest = _rateSamples.Peek();
                RateSample newest = _rateSamples.Last();
                long elapsedTicks = newest.Timestamp - oldest.Timestamp;
                if (elapsedTicks >= Stopwatch.Frequency * RateMinMilliseconds / 1000)
                {
                    long decodedDelta = Math.Max(0, newest.DecodedSymbols - oldest.DecodedSymbols);
                    long receivedDelta = Math.Max(0, newest.ReceivedSymbols - oldest.ReceivedSymbols);
                    _decodePerSecond = (long)Math.Min(long.MaxValue,
                        decodedDelta * (double)Stopwatch.Frequency / elapsedTicks);
                    _recentWireBytesPerSecond = (long)Math.Min(long.MaxValue,
                        receivedDelta * (double)symbolSize * Stopwatch.Frequency / elapsedTicks);
                }
            }
        }

        TimeSpan elapsed = _transferStartTimestamp == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((now - _transferStartTimestamp) /
                (double)Stopwatch.Frequency);
        TransferMetricsText = $"解码 {_decodePerSecond} 符号/秒 · " +
            $"有效 {FormatBytes((ulong)Math.Max(0, _recentWireBytesPerSecond))}/s · " +
            $"用时 {FormatDuration(elapsed)}";
    }

    private void UpdateFileSummary(LiveSnapshot live, ProgressSnapshot progress)
    {
        if (string.IsNullOrWhiteSpace(live.FileName))
        {
            FileSummaryText = "等待描述符…";
            return;
        }
        string original = live.FileSize > 0 ? FormatBytes(live.FileSize) : "大小未知";
        ulong wireBytes = progress.TotalSymbols > 0
            ? (ulong)progress.TotalSymbols * live.SymbolSize
            : 0;
        FileSummaryText = wireBytes > 0
            ? $"{live.FileName} · {original} → 传输 {FormatBytes(wireBytes)}"
            : $"{live.FileName} · {original}";
    }

    private void ResetLiveMetrics()
    {
        ScanMetricsText = "采集 0 帧 · 丢弃 0 帧 · 解码 0 码";
        FileSummaryText = "等待描述符…";
        TransferMetricsText = "解码 0 符号/秒 · 有效 0 B/s · 用时 00:00";
        _rateSamples.Clear();
        _transferStartTimestamp = 0;
        _decodePerSecond = 0;
        _recentWireBytesPerSecond = 0;
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
        : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

    /// <summary>
    /// Ensure <paramref name="sourcePath"/> is in ContentStore (idempotent if already a blob).
    /// Returns the canonical blob path.
    /// </summary>
    public static string ArchiveSingleFile(string sourcePath, string displayName)
    {
        if (File.Exists(sourcePath) &&
            sourcePath.StartsWith(ContentStore.RootDir, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }
        byte[] bytes = File.Exists(sourcePath) ? File.ReadAllBytes(sourcePath) : [];
        return ContentStore.PutBytes(displayName, bytes).Path;
    }

    /// <summary>Archive a bundle into ContentStore (content-addressed members).</summary>
    public static string ArchiveBundle(IReadOnlyList<BundleFile> files)
    {
        string bundleId = Guid.NewGuid().ToString("N");
        string bundleTitle = $"发送_{DateTime.Now:MMdd_HHmmss}";
        string? first = null;
        foreach (BundleFile f in files)
        {
            var put = ContentStore.PutBytes(
                f.Name, f.Data, kind: "file",
                bundleId: bundleId, bundleTitle: bundleTitle);
            first ??= put.Path;
        }
        return first ?? ContentStore.RootDir;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopScan();
    }
}
