using LocalEventBus.Abstractions;
using LocalEventBus.Internal;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// 拦截器在直接调用模式下的测试
/// </summary>
public class InterceptorTests
{
    private class TestInterceptor : IEventInterceptor
    {
        public int Order { get; set; } = 0;
        public List<string> Logs { get; } = new();
        public Exception? LastException { get; private set; }

        public ValueTask OnHandlingAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, CancellationToken ct)
            where TEvent : notnull
        {
            Logs.Add($"OnHandling: {typeof(TEvent).Name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnHandledAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, TimeSpan elapsed, CancellationToken ct)
            where TEvent : notnull
        {
            Logs.Add($"OnHandled: {typeof(TEvent).Name}, Elapsed: {elapsed.TotalMilliseconds:F2}ms");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnHandlerFailedAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, Exception ex, CancellationToken ct)
            where TEvent : notnull
        {
            LastException = ex;
            Logs.Add($"OnFailed: {typeof(TEvent).Name}, Error: {ex.Message}");
            return ValueTask.CompletedTask;
        }
    }

    private class TestFilter : IEventFilter
    {
        public int Order { get; set; } = 0;
        public bool ShouldProcess { get; set; } = true;
        public int CallCount { get; private set; }

        public ValueTask<bool> ShouldProcessAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : notnull
        {
            CallCount++;
            return ValueTask.FromResult(ShouldProcess);
        }
    }

    private class SimpleHandler
    {
        public int CallCount { get; private set; }

        [Subscribe]
        public void Handle(OrderCreatedEvent e)
        {
            CallCount++;
        }
    }

    private class ThrowingHandler
    {
        [Subscribe]
        public void Handle(OrderCreatedEvent e)
        {
            throw new InvalidOperationException("Handler error");
        }
    }

    [Fact]
    public async Task InvokeAsync_Should_NOT_Call_Interceptor_OnHandling()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var interceptor = new TestInterceptor();
        eventBus.AddInterceptor(interceptor);
        
        var handler = new SimpleHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));

        // Assert - 直接调用不触发拦截器
        Assert.Empty(interceptor.Logs);
        Assert.Equal(1, handler.CallCount); // 但订阅者仍然被调用
    }

    [Fact]
    public async Task InvokeAsync_Should_NOT_Call_Interceptor_OnHandled()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var interceptor = new TestInterceptor();
        eventBus.AddInterceptor(interceptor);
        
        var handler = new SimpleHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));

        // Assert - 直接调用不触发拦截器
        Assert.DoesNotContain(interceptor.Logs, l => l.StartsWith("OnHandled:"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_Should_NOT_Call_Interceptor_OnFailed_When_Exception()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var interceptor = new TestInterceptor();
        eventBus.AddInterceptor(interceptor);
        
        var handler = new ThrowingHandler();
        eventBus.Subscribe(handler);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));
        });

        // 直接调用不触发拦截器，即使异常也不会
        Assert.Empty(interceptor.Logs);
        Assert.Null(interceptor.LastException);
    }

    [Fact]
    public async Task InvokeAsync_Should_Call_Filter()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var filter = new TestFilter { ShouldProcess = true };
        eventBus.AddFilter(filter);
        
        var handler = new SimpleHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));

        // Assert
        Assert.Equal(1, filter.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_Should_Skip_Handler_When_Filter_Returns_False()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var filter = new TestFilter { ShouldProcess = false };
        eventBus.AddFilter(filter);
        
        var handler = new SimpleHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));

        // Assert
        Assert.Equal(1, filter.CallCount);
        Assert.Equal(0, handler.CallCount); // 过滤器阻止了处理
    }

    [Fact]
    public async Task InvokeByTopicAsync_Should_NOT_Call_Interceptors()
    {
        // Arrange
        var eventBus = (DefaultEventBus)EventBusFactory.Create();
        var interceptor = new TestInterceptor();
        eventBus.AddInterceptor(interceptor);
        
        var callCount = 0;
        eventBus.Subscribe<MessageEvent>((e, ct) =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "test/topic" });

        // Act
        await eventBus.InvokeByTopicAsync("test/topic", new MessageEvent("Hello"));

        // Assert - 直接调用不触发拦截器
        Assert.Equal(1, callCount);
        Assert.Empty(interceptor.Logs);
    }
}
