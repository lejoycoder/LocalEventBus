using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// InvokeAsync 直接调用测试
/// </summary>
public class InvokeAsyncTests
{
    private class TestHandler
    {
        public List<OrderCreatedEvent> ReceivedOrders { get; } = new();
        public List<PaymentCompletedEvent> ReceivedPayments { get; } = new();
        public int InvokeCount { get; private set; }

        [Subscribe]
        public void HandleOrder(OrderCreatedEvent e)
        {
            InvokeCount++;
            ReceivedOrders.Add(e);
        }

        [Subscribe]
        public async ValueTask HandlePaymentAsync(PaymentCompletedEvent e, CancellationToken ct)
        {
            InvokeCount++;
            await Task.Delay(10, ct); // 模拟异步操作
            ReceivedPayments.Add(e);
        }
    }

    private class MultiHandler
    {
        public List<string> Invoked { get; } = new();

        [Subscribe]
        public void First(MessageEvent e)
        {
            Invoked.Add("First");
        }

        [Subscribe]
        public void Second(MessageEvent e)
        {
            Invoked.Add("Second");
        }

        [Subscribe]
        public void Third(MessageEvent e)
        {
            Invoked.Add("Third");
        }
    }

    private class ThrowingHandler
    {
        [Subscribe]
        public void ThrowException(MessageEvent e)
        {
            throw new InvalidOperationException("Test exception");
        }
    }

    [Fact]
    public async Task InvokeAsync_Should_Call_Typed_Subscribers()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new TestHandler();
        eventBus.Subscribe(handler);

        var testEvent = new OrderCreatedEvent(1, "Test", 100m);

        // Act
        await eventBus.InvokeAsync(testEvent);

        // Assert
        Assert.Single(handler.ReceivedOrders);
        Assert.Equal(testEvent, handler.ReceivedOrders[0]);
    }

    [Fact]
    public async Task InvokeAsync_Should_Call_Async_Subscribers()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new TestHandler();
        eventBus.Subscribe(handler);

        var testEvent = new PaymentCompletedEvent(1, 100m, DateTime.UtcNow);

        // Act
        await eventBus.InvokeAsync(testEvent);

        // Assert
        Assert.Single(handler.ReceivedPayments);
        Assert.Equal(testEvent, handler.ReceivedPayments[0]);
    }

    [Fact]
    public async Task InvokeAsync_Should_Wait_For_All_Subscribers()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new TestHandler();
        eventBus.Subscribe(handler);

        var orderEvent = new OrderCreatedEvent(1, "Test", 100m);
        var paymentEvent = new PaymentCompletedEvent(1, 100m, DateTime.UtcNow);

        // Act
        await eventBus.InvokeAsync(orderEvent);
        await eventBus.InvokeAsync(paymentEvent);

        // Assert - 直接调用应该同步等待完成
        Assert.Equal(2, handler.InvokeCount);
    }

    [Fact]
    public async Task InvokeAsync_With_Multiple_Matching_Subscribers_Should_Throw_And_Not_Invoke_Any()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new MultiHandler();
        eventBus.Subscribe(handler);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => eventBus.InvokeAsync(new MessageEvent("Test")).AsTask());

        Assert.Contains("匹配数量: 3", exception.Message);
        Assert.Empty(handler.Invoked);
    }

    [Fact]
    public async Task InvokeAsync_Should_Throw_Exception_From_Handler()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ThrowingHandler();
        eventBus.Subscribe(handler);

        // Act & Assert - 直接调用模式下应该抛出异常
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => eventBus.InvokeAsync(new MessageEvent("Test")).AsTask());
        
        Assert.Equal("Test exception", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_With_No_Subscribers_Should_Throw()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();

        // Act & Assert - 没有订阅者时应抛出异常
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m)).AsTask());

        Assert.Contains("匹配数量: 0", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_Should_Support_Delegate_Subscription()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var receivedEvents = new List<OrderCreatedEvent>();

        eventBus.Subscribe<OrderCreatedEvent>((e, ct) =>
        {
            receivedEvents.Add(e);
            return ValueTask.CompletedTask;
        });

        var testEvent = new OrderCreatedEvent(1, "Test", 100m);

        // Act
        await eventBus.InvokeAsync(testEvent);

        // Assert
        Assert.Single(receivedEvents);
        Assert.Equal(testEvent, receivedEvents[0]);
    }

    [Fact]
    public async Task InvokeByTopicAsync_Should_Call_Topic_Subscribers()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var callCount = 0;

        eventBus.Subscribe<MessageEvent>((e, ct) =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "test/topic" });

        // Act
        await eventBus.InvokeByTopicAsync("test/topic", new MessageEvent("Hello"));

        // Assert
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task InvokeByTopicAsync_With_No_Matching_Topic_Should_Throw_And_Not_Call()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var callCount = 0;

        eventBus.Subscribe<MessageEvent>((e, ct) =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "other/topic" });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => eventBus.InvokeByTopicAsync("test/topic", new MessageEvent("Hello")).AsTask());

        Assert.Contains("test/topic", exception.Message);
        Assert.Contains("匹配数量: 0", exception.Message);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task InvokeByTopicAsync_Should_Pass_EventData_To_Subscribers()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        object? receivedData = null;

        eventBus.Subscribe<object>((e, ct) =>
        {
            receivedData = e;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "data/topic" });

        var testData = new { Name = "Test", Value = 123 };

        // Act
        await eventBus.InvokeByTopicAsync("data/topic", testData);

        // Assert
        Assert.NotNull(receivedData);
    }

    [Fact]
    public async Task InvokeByTopicAsync_With_Multiple_Matching_Subscribers_Should_Throw_And_Not_Invoke_Any()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var firstCount = 0;
        var secondCount = 0;

        eventBus.Subscribe<object>((_, _) =>
        {
            firstCount++;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "conflict/topic" });

        eventBus.Subscribe<object>((_, _) =>
        {
            secondCount++;
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "conflict/topic" });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => eventBus.InvokeByTopicAsync("conflict/topic", new { Payload = "test" }).AsTask());

        Assert.Contains("conflict/topic", exception.Message);
        Assert.Contains("匹配数量: 2", exception.Message);
        Assert.Equal(0, firstCount);
        Assert.Equal(0, secondCount);
    }
}
