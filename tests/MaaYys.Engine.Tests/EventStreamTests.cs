using MaaYys.Engine.Session;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>事件流（原生回调 → IObservable）测试。</summary>
public sealed class EventStreamTests
{
    [Fact]
    public void Subscribe_ShouldReceivePublishedValues()
    {
        var stream = new EventStream<int>();
        var received = new List<int>();

        using IDisposable subscription = stream.Subscribe(received.Add);
        stream.Publish(1);
        stream.Publish(2);

        Assert.Equal(new[] { 1, 2 }, received);
    }

    [Fact]
    public void DisposedSubscription_ShouldStopReceiving()
    {
        var stream = new EventStream<string>();
        var received = new List<string>();

        IDisposable subscription = stream.Subscribe(received.Add);
        stream.Publish("a");
        subscription.Dispose();
        stream.Publish("b");

        Assert.Equal(new[] { "a" }, received);
    }

    [Fact]
    public void MultipleSubscribers_ShouldAllReceive()
    {
        var stream = new EventStream<int>();
        int first = 0, second = 0;

        using IDisposable a = stream.Subscribe(_ => first++);
        using IDisposable b = stream.Subscribe(_ => second++);
        stream.Publish(42);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void ThrowingSubscriber_ShouldNotBreakOthers()
    {
        var stream = new EventStream<int>();
        Exception? captured = null;
        int received = 0;

        using IDisposable bad = stream.Subscribe(_ => throw new InvalidOperationException("boom"), ex => captured = ex);
        using IDisposable good = stream.Subscribe(_ => received++);

        stream.Publish(1);

        Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal(1, received);
    }

    [Fact]
    public void Where_ShouldFilterValues()
    {
        var stream = new EventStream<int>();
        var received = new List<int>();

        using IDisposable subscription = stream.Where(v => v % 2 == 0).Subscribe(received.Add);
        foreach (int value in new[] { 1, 2, 3, 4 })
        {
            stream.Publish(value);
        }

        Assert.Equal(new[] { 2, 4 }, received);
    }

    [Fact]
    public void Complete_ShouldNotifyAndClearSubscribers()
    {
        var stream = new EventStream<int>();
        bool completed = false;
        int received = 0;

        using IDisposable subscription = stream.Subscribe(_ => received++, onCompleted: () => completed = true);
        stream.Complete();
        stream.Publish(1);

        Assert.True(completed);
        Assert.Equal(0, received);
    }
}
