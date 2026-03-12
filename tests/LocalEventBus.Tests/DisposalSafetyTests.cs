using LocalEventBus.Internal;
using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

public sealed class DisposalSafetyTests
{
    private sealed class PassThroughFilter : IEventFilter
    {
        public int Order => 0;

        public ValueTask<bool> ShouldProcessAsync<TEvent>(TEvent @event, CancellationToken ct)
            where TEvent : notnull
        {
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task PublishAsync_After_DisposeAsync_Should_Throw_ObjectDisposedException()
    {
        var bus = EventBusFactory.Create();
        await bus.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => bus.PublishAsync(new MessageEvent("disposed")).AsTask());
    }

    [Fact]
    public void Subscribe_After_Dispose_Should_Throw_ObjectDisposedException()
    {
        var bus = EventBusFactory.Create();
        bus.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<MessageEvent>((_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void AddFilter_After_Dispose_Should_Throw_ObjectDisposedException()
    {
        var bus = (DefaultEventBus)EventBusFactory.Create();
        bus.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            bus.AddFilter(new PassThroughFilter()));
    }
}
