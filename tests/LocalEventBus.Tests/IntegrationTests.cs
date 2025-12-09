using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// 综合集成测试
/// </summary>
public class IntegrationTests
{
    private class MultiTopicHandler
    {
        public List<string> ExecutedTopics { get; } = new();

        [Subscribe("orders/created")]
        public void OnOrderCreated()
        {
            ExecutedTopics.Add("orders/created");
        }

        [Subscribe("orders/cancelled")]
        public void OnOrderCancelled()
        {
            ExecutedTopics.Add("orders/cancelled");
        }

        [Subscribe("payments/received")]
        public void OnPaymentReceived()
        {
            ExecutedTopics.Add("payments/received");
        }

        [Subscribe]
        public void OnOrderEvent(OrderCreatedEvent e)
        {
            ExecutedTopics.Add($"OrderEvent:{e.OrderId}");
        }
    }

    [Fact]
    public async Task Should_Support_Multiple_Topics_On_Same_Handler()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new MultiTopicHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeByTopicAsync("orders/created");
        await eventBus.InvokeByTopicAsync("orders/cancelled");
        await eventBus.InvokeByTopicAsync("payments/received");

        // Assert
        Assert.Equal(3, handler.ExecutedTopics.Count);
        Assert.Contains("orders/created", handler.ExecutedTopics);
        Assert.Contains("orders/cancelled", handler.ExecutedTopics);
        Assert.Contains("payments/received", handler.ExecutedTopics);
    }

    [Fact]
    public async Task Should_Support_Mixed_Topic_And_Typed_Subscriptions()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new MultiTopicHandler();
        eventBus.Subscribe(handler);

        // Act - 调用 Topic 订阅
        await eventBus.InvokeByTopicAsync("orders/created");
        
        // Act - 调用类型订阅
        await eventBus.InvokeAsync(new OrderCreatedEvent(123, "Customer", 100m));

        // Assert
        Assert.Equal(2, handler.ExecutedTopics.Count);
        Assert.Contains("orders/created", handler.ExecutedTopics);
        Assert.Contains("OrderEvent:123", handler.ExecutedTopics);
    }

    [Fact]
    public async Task PublishAsync_And_InvokeAsync_Should_Both_Work()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var receivedViaPublish = new List<OrderCreatedEvent>();
        var receivedViaInvoke = new List<PaymentCompletedEvent>();

        eventBus.Subscribe<OrderCreatedEvent>(e => receivedViaPublish.Add(e));
        eventBus.Subscribe<PaymentCompletedEvent>((e, ct) =>
        {
            receivedViaInvoke.Add(e);
            return ValueTask.CompletedTask;
        });

        // Act - 通过 PublishAsync（经过队列）
        await eventBus.PublishAsync(new OrderCreatedEvent(1, "Test", 100m));
        await Task.Delay(100); // 等待队列处理

        // Act - 通过 InvokeAsync（直接调用）
        await eventBus.InvokeAsync(new PaymentCompletedEvent(1, 100m, DateTime.UtcNow));

        // Assert
        Assert.Single(receivedViaPublish);
        Assert.Single(receivedViaInvoke);
    }

    [Fact]
    public async Task Unsubscribe_Should_Work_For_All_Subscription_Types()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new MultiTopicHandler();
        var subscription = eventBus.Subscribe(handler);

        // Act - 先验证订阅有效
        await eventBus.InvokeByTopicAsync("orders/created");
        Assert.Single(handler.ExecutedTopics);

        // Act - 取消订阅
        subscription.Dispose();
        handler.ExecutedTopics.Clear();

        // Act - 再次调用，不应该有反应
        await eventBus.InvokeByTopicAsync("orders/created");

        // Assert
        Assert.Empty(handler.ExecutedTopics);
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Invocations()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var counter = 0;
        var lockObj = new object();

        eventBus.Subscribe<MessageEvent>((e, ct) =>
        {
            lock (lockObj)
            {
                counter++;
            }
            return ValueTask.CompletedTask;
        });

        // Act - 并发调用
        var tasks = Enumerable.Range(0, 100)
            .Select(i => eventBus.InvokeAsync(new MessageEvent($"Message {i}")).AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(100, counter);
    }

    [Fact]
    public void EventBusFactory_Should_Create_Working_Instance()
    {
        // Act
        using var eventBus = EventBusFactory.Create();

        // Assert
        Assert.NotNull(eventBus);
        Assert.IsAssignableFrom<IEventBus>(eventBus);
        Assert.IsAssignableFrom<IEventPublisher>(eventBus);
        Assert.IsAssignableFrom<IEventSubscriber>(eventBus);
    }

    [Fact]
    public void EventBusFactory_Should_Accept_Options()
    {
        // Act
        using var eventBus = EventBusFactory.Create(options =>
        {
            options.ChannelCapacity = 500;
            options.DefaultTimeout = TimeSpan.FromSeconds(10);
        });

        // Assert
        Assert.NotNull(eventBus);
    }
}
