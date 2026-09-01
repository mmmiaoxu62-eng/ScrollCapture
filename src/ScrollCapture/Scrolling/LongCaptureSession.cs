using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Capture;
using ScrollCapture.Stitching;
using ScrollCapture.Utils;
using ScrollCapture.Vision;

namespace ScrollCapture.Scrolling;

public enum SessionStopReason
{
    ReachedBottom,
    LimitReached,
    Cancelled,
    Error,
    Unstable,
}

public sealed record SessionResult(
    SessionStopReason Reason,
    int FrameCount,
    string FramesDirectory,
    Exception? Error = null)
{
    /// <summary>Estimated scroll delta (px) between consecutive frames (null when unavailable).</summary>
    public IReadOnlyList<double?> EstimatedScrollDeltas { get; init; } = Array.Empty<double?>();

    /// <summary>Number of steps that got the auto-step-down (4->2->1) treatment.</summary>
    public int DegradedSteps { get; init; }

    /// <summary>Long image stitched incrementally during the session (null when empty).</summary>
    public BitmapSource? StitchedImage { get; init; }

    /// <summary>Per-frame stitch report (mirrors IncrementalStitcher.Steps).</summary>
    public IReadOnlyList<Stitching.StitchStepReport>? StitchSteps { get; init; }

    /// <summary>Stitcher warnings (fallbacks used, limits hit).</summary>
    public IReadOnlyList<string>? StitchWarnings { get; init; }

    /// <summary>True when height/memory limits cut the image short.</summary>
    public bool StitchTruncated { get; init; }
}

public sealed record ScrollOptions
{
    // Larger step = faster, but smaller overlap between consecutive frames.
    public int WheelStep { get; init; } = 4;
    public int DelayPerScrollMs { get; init; } = 400;
    public int IdenticalFramesToStop { get; init; } = 2;

    /// <summary>Estimated movement (px) below which we declare the page essentially static.</summary>
    public int StaticDeltaPx { get; init; } = 15;

    /// <summary>Poll interval for the stability probe (before the real frame is taken).</summary>
    public int StabilityProbeIntervalMs { get; init; } = 60;

    /// <summary>Stability timeout = DelayPerScrollMs * StabilityTimeoutFactor (smooth-scroll pages can run ~1s).</summary>
    public double StabilityTimeoutFactor { get; init; } = 3.0;

    /// <summary>Grace period after the picture stabilizes (DWM compositor lag).</summary>
    public int StabilityGraceMs { get; init; } = 60;
}

/// <summary>
/// Phase 4 loop: capture -> probe offset -> scroll -> wait -> capture ...
/// Stop/handling rules, strongest to weakest:
///  - user cancel / frame limit         -> stop
///  - offset probe (+ scrollbar/UIA)    -> delta < StaticDeltaPx twice => ReachedBottom
///  - offset delta == 0 twice           -> auto step-down (4->2->1); still 0 => bottom
///  - vision (frames nearly identical)  -> identical twice => bottom (fallback for unprobeable apps)
/// </summary>
public sealed class LongCaptureSession : IDisposable
{
    private readonly Int32Rect _region;
    private readonly ScrollOptions _options;
    private readonly int _maxFrames;
    private readonly string _framesDirectory;
    private Func<Int32Rect, BitmapSource> _capture;
    private readonly Action _scrollOnce;
    private readonly Func<OffsetSnapshot?>? _probeGetter;
    private readonly CancellationToken _token;
    private readonly ScrollController? _ownController;
    private readonly IncrementalStitcher _stitcher;
    private CaptureEngine? _engine;
    private readonly object _saveGate = new();
    private readonly List<Task> _saveTasks = new();
    private int _capturedCount;

    public event EventHandler<int>? FrameCaptured;

    public LongCaptureSession(
        Int32Rect region,
        ScrollOptions? options = null,
        int maxFrames = 100,
        string? framesDirectory = null,
        Func<Int32Rect, BitmapSource>? capture = null,
        Action? scrollOnce = null,
        Func<OffsetSnapshot?>? probeGetter = null,
        CancellationToken token = default,
        int maxImageHeight = 30000)
    {
        _region = region;
        _stitcher = new IncrementalStitcher(maxImageHeight);
        _options = options ?? new ScrollOptions();
        _maxFrames = Math.Max(1, maxFrames);
        _framesDirectory = framesDirectory ?? Path.Combine(AppPaths.DataDir, "temp", $"session_{DateTime.Now:yyyyMMdd_HHmmss}");
        _capture = capture ?? ScreenCaptureService.Capture; // replaced by the engine (below) in real mode
        _token = token;

        _baseWheelStep = options?.WheelStep ?? 4;

        if (scrollOnce != null)
        {
            _scrollOnce = scrollOnce;   // injected (tests) — no cursor management
            _probeGetter = probeGetter; // still usable in tests
        }
        else
        {
            _ownController = new ScrollController(_options.WheelStep);
            _scrollOnce = _ownController.ScrollOnce;
            _probeGetter = probeGetter ?? (() =>
            {
                IntPtr hwnd = _ownController.TargetRootHwnd;
                return hwnd == IntPtr.Zero ? null : ScrollOffsetProbe.Probe(hwnd);
            });
        }
    }

    public async Task<SessionResult> RunAsync(Action<int>? onProgress = null)
    {
        var deltas = new List<double?>();
        var stopwatch = new Stopwatch();
        int degraded = 0;

        Logger.Info($"Session start region={_region}");
        try
        {
            _ownController?.Prepare(_region);
            if (_ownController != null && _ownController.TargetRootHwnd != IntPtr.Zero)
            {
                // BitBlt primary: PrintWindow(RENDERFULLCONTENT) returns a FROZEN frame for
                // Chromium/RDP (verified hard), which kills live scrolling. WGC (Phase 5)
                // will be the eventual live, cursor-free channel.
                _engine = new CaptureEngine(_ownController.TargetRootHwnd, CaptureChannelKind.BitBlt);
                _capture = region => _engine.Capture(region);
                Logger.Info($"Capture channel armed (BitBlt) for hwnd=0x{_ownController.TargetRootHwnd.ToInt64():X}");
            }

            OffsetSnapshot? previousSnapshot = null;
            int smallMoveCount = 0;
            int zeroMoveCount = 0;
            int unchanged = 0;
            int visionFailCount = 0;
            int visionSuccessStreak = 0;
            double waitScale = 1.0;
            BitmapSource? previous = null;

            for (int i = 0; ; i++)
            {
                if (_token.IsCancellationRequested)
                {
                    return await FinishAsync(SessionStopReason.Cancelled, deltas, degraded);
                }
                if (i >= _maxFrames)
                {
                    return await FinishAsync(SessionStopReason.LimitReached, deltas, degraded);
                }

                BitmapSource frame = _capture(_region);
                _capturedCount++;
                ScheduleSave(frame, _capturedCount);
                onProgress?.Invoke(_capturedCount);
                FrameCaptured?.Invoke(this, _capturedCount);

                // probe the scroll position *after* the content moved (before next scroll)
                OffsetSnapshot? snapshot = _probeGetter?.Invoke();
                double? delta = previousSnapshot != null && snapshot != null && snapshot.IsUsable
                    ? previousSnapshot.EstimateDeltaPx(snapshot)
                    : null;
                deltas.Add(delta);
                _deltaLog.Add(delta);

                if (delta != null)
                {
                    if (delta < _options.StaticDeltaPx)
                    {
                        smallMoveCount++;
                        if (smallMoveCount >= 2)
                        {
                            return await FinishAsync(SessionStopReason.ReachedBottom, deltas, degraded);
                        }
                    }
                    else
                    {
                        smallMoveCount = 0;
                    }

                    if (delta <= 0.5)
                    {
                        zeroMoveCount++;
                        if (zeroMoveCount >= 2 && _ownController != null && _ownController.WheelStep > 1 && degraded < 2)
                        {
                            _ownController.WheelStep = Math.Max(1, _ownController.WheelStep / 2);
                            degraded++;
                            zeroMoveCount = 0; // give the halved wheel-step a fair chance
                        }
                        else if (zeroMoveCount >= 3)
                        {
                            return await FinishAsync(SessionStopReason.ReachedBottom, deltas, degraded);
                        }
                    }
                    else
                    {
                        zeroMoveCount = 0;
                    }
                }

                if (previous != null && FrameSimilarity.IsNearlyIdentical(frame, previous))
                {
                    unchanged++;
                    if (unchanged >= _options.IdenticalFramesToStop)
                    {
                        return await FinishAsync(SessionStopReason.ReachedBottom, deltas, degraded);
                    }
                }
                else
                {
                    unchanged = 0;
                }

                // incremental stitch (also feeds the vision ladder below)
                double? priorDelta = deltas.Count > 0 ? deltas[^1] : null;
                if (previous == null)
                {
                    _stitcher.Start(frame);
                }
                else
                {
                    _stitcher.Add(frame, priorDelta);
                }

                visionFailCount = EvaluateVision(frame, visionFailCount,
                    ref visionSuccessStreak, ref waitScale, ref degraded);

                // Single failure usually is a transient (new chat line, animation frame):
                // wait, re-capture the same spot, and try the detector once more.
                if (visionFailCount == 1 && previous != null && _ownController != null)
                {
                    await Task.Delay(Math.Max(300, _options.DelayPerScrollMs), _token).ConfigureAwait(false);
                    BitmapSource retryFrame = _capture(_region);
                    ScheduleSave(retryFrame, _capturedCount + 500);
                    double? priorD = deltas.Count > 0 ? deltas[^1] : null;
                    _stitcher.Add(retryFrame, priorD);
                    visionFailCount = EvaluateVision(retryFrame, visionFailCount,
                        ref visionSuccessStreak, ref waitScale, ref degraded);
                }

                if (visionFailCount >= 2 && i >= 2)
                {
                    return await FinishAsync(SessionStopReason.Unstable, deltas, degraded);
                }
                previous = frame;
                previousSnapshot = snapshot;

                stopwatch.Restart();
                _scrollOnce();
                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                await WaitForStabilityAsync(Math.Max(0, _options.DelayPerScrollMs - elapsed) * waitScale);
            }
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(SessionStopReason.Cancelled, deltas, degraded);
        }
        catch (Exception ex)
        {
            Logger.Error("Auto capture session failed", ex);
            return await FinishAsync(SessionStopReason.Error, deltas, degraded, ex);
        }
        finally
        {
            _ownController?.Restore();
        }
    }

    private async Task<SessionResult> FinishAsync(SessionStopReason reason, IReadOnlyList<double?> deltas,
        int degraded, Exception? error = null)
    {
        try
        {
            await Task.WhenAll(_saveTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // frame files are best-effort
        }
        Logger.Info($"Session finished reason={reason} frames={_capturedCount} degraded={degraded} channel={_engine?.LastChannel.ToString() ?? "none"}");
        return new SessionResult(reason, _capturedCount, _framesDirectory, error)
        {
            EstimatedScrollDeltas = deltas,
            DegradedSteps = degraded,
            StitchedImage = _stitcher.Finish(),
            StitchSteps = _stitcher.Steps,
            StitchWarnings = _stitcher.Warnings,
            StitchTruncated = _stitcher.Truncated,
        };
    }

    /// <summary>
    /// Vision gate fed by the stitcher's last step (no extra detection):
    ///  - success streak >= 3 restores the wheel step (recover speed)
    ///  - failure: halve wheel step + double wait (degraded)
    ///  - 2 consecutive failures: caller aborts with Unstable
    /// Sub-resolution frames (unit tests) are skipped — no penalty.
    /// </summary>
    private int EvaluateVision(BitmapSource current,
        int visionFailCount, ref int visionSuccessStreak, ref double waitScale, ref int degraded)
    {
        if (current.PixelHeight < 240 || _stitcher.Steps.Count == 0)
        {
            return 0; // tiny synthetic frames — not a real vision target
        }
        StitchStepReport last = _stitcher.Steps[^1];
        if (last.Skipped)
        {
            return visionFailCount; // no content change — handled by the identical-stop logic
        }

        if (!last.UsedFallback)
        {
            visionFailCount = 0;
            visionSuccessStreak++;
            if (visionSuccessStreak >= 3 && waitScale > 1.0)
            {
                waitScale = Math.Max(1.0, waitScale / 2);
                visionSuccessStreak = 0;
            }
            if (visionSuccessStreak >= 3 && _ownController != null && degraded > 0 &&
                _ownController.WheelStep < _baseWheelStep)
            {
                _ownController.WheelStep = Math.Min(_baseWheelStep, _ownController.WheelStep * 2);
                degraded--;
            }
        }
        else
        {
            visionFailCount++;
            visionSuccessStreak = 0;
            if (_ownController != null && _ownController.WheelStep > 1 && waitScale < 2.0)
            {
                _ownController.WheelStep = Math.Max(1, _ownController.WheelStep / 2);
                waitScale = 2.0;
                degraded++;
            }
        }
        return visionFailCount;
    }

    /// <summary>Latest estimated scroll delta (px) — used as the visual prior next round.</summary>
    private double? LastDeltaEstimate
    {
        get
        {
            for (int i = _deltaLog.Count - 1; i >= 0; i--)
            {
                if (_deltaLog[i] is double d) return d;
            }
            return null;
        }
    }

    private readonly List<double?> _deltaLog = new();
    private readonly int _baseWheelStep;

    /// <summary>
    /// Scroll animation detector: poll tiny a/b comparisons until the picture stops
    /// changing (or timeout). Prevents capturing mid-smooth-scroll frames.
    /// </summary>
    private async Task WaitForStabilityAsync(double scaledDelayMs)
    {
        if (scaledDelayMs < 30)
        {
            return;
        }
        double timeoutMs = _options.DelayPerScrollMs * _options.StabilityTimeoutFactor;
        var sw = Stopwatch.StartNew();
        BitmapSource? previousProbe = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            BitmapSource probe = _capture(_region);
            if (previousProbe != null && FrameSimilarity.IsNearlyIdentical(previousProbe, probe))
            {
                if (_options.StabilityGraceMs > 0)
                {
                    await Task.Delay(_options.StabilityGraceMs, _token).ConfigureAwait(false);
                }
                return; // picture still => proceed to the real frame
            }
            previousProbe = probe;
            await Task.Delay(_options.StabilityProbeIntervalMs, _token).ConfigureAwait(false);
        }
        // timed out: assume settled (or nearly), the vision ladder will catch problems
    }

    /// <summary>
    /// Queues PNG persistence off the hot path (capture loop keeps the cadence).
    /// Best effort; drained with a timeout in FinishAsync.
    /// </summary>
    private void ScheduleSave(BitmapSource frame, int index)
    {
        lock (_saveGate)
        {
            if (_saveTasks.Count > 30)
            {
                // don't let the queue grow without bound
                return;
            }
            _saveTasks.Add(Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(_framesDirectory);
                    string path = Path.Combine(_framesDirectory, $"frame_{index:D4}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                    encoder.Save(stream);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to save frame {index}: {ex.Message}");
                }
            }));
        }
    }

    public void Dispose()
    {
        _ownController?.Dispose();
    }
}
