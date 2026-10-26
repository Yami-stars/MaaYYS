namespace MaaYys.Engine.Session;

/// <summary>
/// 轻量 <see cref="IObservable{T}"/> 实现（不引入 System.Reactive 依赖）。
/// 用途：把 MaaFramework 的原生回调（框架工作线程）转成可组合的事件流，
/// UI 侧通过 <see cref="EventStreamExtensions.Subscribe{T}(IObservable{T}, Action{T})"/> 订阅即可。
/// 线程安全：Publish 与 Subscribe 可并发；回调异常被隔离，不会打断其他订阅者。
/// </summary>
public sealed class EventStream<T> : IObservable<T>
{
    private readonly object _gate = new();
    private IObserver<T>[] _observers = [];

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            var next = new IObserver<T>[_observers.Length + 1];
            _observers.CopyTo(next, 0);
            next[^1] = observer;
            _observers = next;
        }

        return new Subscription(this, observer);
    }

    /// <summary>向所有订阅者推送（单订阅者异常不影响其它订阅者）。</summary>
    public void Publish(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            snapshot = _observers;
        }

        foreach (IObserver<T> observer in snapshot)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (Exception ex)
            {
                // 订阅者异常只回传给该订阅者，不污染事件源
                try
                {
                    observer.OnError(ex);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>结束所有订阅（引擎 Dispose 时调用）。</summary>
    public void Complete()
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            snapshot = _observers;
            _observers = [];
        }

        foreach (IObserver<T> observer in snapshot)
        {
            try
            {
                observer.OnCompleted();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_observers, observer);
            if (index < 0)
            {
                return;
            }

            var next = new IObserver<T>[_observers.Length - 1];
            Array.Copy(_observers, 0, next, 0, index);
            Array.Copy(_observers, index + 1, next, index, _observers.Length - index - 1);
            _observers = next;
        }
    }

    private sealed class Subscription(EventStream<T> owner, IObserver<T> observer) : IDisposable
    {
        private EventStream<T>? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            current?.Unsubscribe(observer);
        }
    }
}

/// <summary>把 <see cref="Action{T}"/> 适配成 IObserver。</summary>
internal sealed class AnonymousObserver<T>(Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
    : IObserver<T>
{
    public void OnNext(T value) => onNext(value);

    public void OnError(Exception error) => onError?.Invoke(error);

    public void OnCompleted() => onCompleted?.Invoke();
}

/// <summary>事件流订阅辅助（LINQ 风格的轻量封装）。</summary>
public static class EventStreamExtensions
{
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext) =>
        source.Subscribe(new AnonymousObserver<T>(onNext));

    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, Action<Exception> onError) =>
        source.Subscribe(new AnonymousObserver<T>(onNext, onError));

    /// <summary>带完成回调的订阅（测试与生命周期收尾用）。</summary>
    public static IDisposable Subscribe<T>(
        this IObservable<T> source,
        Action<T> onNext,
        Action<Exception>? onError = null,
        Action? onCompleted = null) =>
        source.Subscribe(new AnonymousObserver<T>(onNext, onError, onCompleted));

    /// <summary>过滤：只接收满足条件的元素。</summary>
    public static IObservable<T> Where<T>(this IObservable<T> source, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);
        return new FilteredObservable<T>(source, predicate);
    }

    private sealed class FilteredObservable<T>(IObservable<T> source, Func<T, bool> predicate) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) =>
            source.Subscribe(new AnonymousObserver<T>(
                value =>
                {
                    if (predicate(value))
                    {
                        observer.OnNext(value);
                    }
                },
                observer.OnError,
                observer.OnCompleted));
    }
}
