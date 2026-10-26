using System.Diagnostics;
using MaaYys.Engine.Session;

namespace MaaYys.Engine.Devices;

/// <summary>
/// 实时截图采集泵（P2.3）：按目标帧率循环抓帧，帧数据经 <see cref="Frames"/> 推送。
/// 采集在后台线程串行执行（控制器截图本身是阻塞式 Post+Wait），因此不会与识别并发抢占。
/// 帧率可运行时调整（1–60）；实际帧率按 1 秒窗口统计。
/// </summary>
public sealed class FramePump : IDisposable
{
    private readonly object _gate = new();
    private readonly EventStream<FrameData> _frames = new();
    private readonly SynchronizationContext? _dispatchContext;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _fpsGate = new();
    private readonly Queue<long> _recentFrameTicks = new();
    private int _fps = 5;
    private double _actualFps;
    private long _frameCount;
    private bool _disposed;

    /// <param name="dispatchContext">
    /// 帧回调派发上下文（WPF 传 SynchronizationContext.Current）；null 表示在采集线程直接回调。
    /// </param>
    public FramePump(SynchronizationContext? dispatchContext = null) => _dispatchContext = dispatchContext;

    public IObservable<FrameData> Frames => _frames;

    public int TargetFps => Volatile.Read(ref _fps);

    public double ActualFps => _actualFps;

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is { IsCompleted: false } && _cts is { IsCancellationRequested: false };
            }
        }
    }

    /// <summary>开始采集。重复调用会先停止上一轮。</summary>
    public void Start(Func<FrameData?> capture, int fps = 5)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ThrowIfDisposed();
        Stop();

        int clamped = Math.Clamp(fps, 1, 60);
        Volatile.Write(ref _fps, clamped);
        lock (_fpsGate)
        {
            _recentFrameTicks.Clear();
            _actualFps = 0;
        }

        Interlocked.Exchange(ref _frameCount, 0);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _loop = Task.Run(() => RunAsync(capture, cts.Token));
        }
    }

    /// <summary>运行时调整帧率（下一帧生效）。</summary>
    public void SetFps(int fps) => Volatile.Write(ref _fps, Math.Clamp(fps, 1, 60));

    /// <summary>
    /// 停止采集。等待采集线程退出的上限 <paramref name="waitMilliseconds"/> 不宜大：
    /// 本方法可能在 UI 线程被调用（窗口最小化暂停预览），而截图是阻塞调用，
    /// 等太久会让界面卡住——那正是「点最小化/恢复时窗口像卡死」的来源。
    /// </summary>
    public void Stop(int waitMilliseconds = 300)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            loop?.Wait(TimeSpan.FromMilliseconds(Math.Max(0, waitMilliseconds)));
        }
        catch (AggregateException)
        {
            // 采集异常已在循环内吞掉，这里只是收尾
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunAsync(Func<FrameData?> capture, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Stopwatch tick = Stopwatch.StartNew();
            FrameData? frame = null;
            try
            {
                frame = capture();
            }
            catch (Exception)
            {
                // 单帧失败（窗口最小化/掉线）不应终止采集，下一轮继续尝试
            }

            if (frame is not null)
            {
                Interlocked.Increment(ref _frameCount);
                UpdateActualFps();
                Publish(frame);
            }

            int targetIntervalMs = Math.Max(1, 1000 / Math.Max(1, Volatile.Read(ref _fps)));
            int delay = (int)Math.Max(1, targetIntervalMs - tick.ElapsedMilliseconds);
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>实际帧率 = 最近 1 秒内到达的帧数（滑动窗口，逐帧更新，避免 1 秒空窗显示 0）。</summary>
    private void UpdateActualFps()
    {
        long now = Environment.TickCount64;
        lock (_fpsGate)
        {
            _recentFrameTicks.Enqueue(now);
            while (_recentFrameTicks.Count > 0 && now - _recentFrameTicks.Peek() > 1000)
            {
                _recentFrameTicks.Dequeue();
            }

            _actualFps = _recentFrameTicks.Count;
        }
    }

    private void Publish(FrameData frame)
    {
        SynchronizationContext? context = _dispatchContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            _frames.Publish(frame);
            return;
        }

        context.Post(_ => _frames.Publish(frame), null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _frames.Complete();
    }
}
