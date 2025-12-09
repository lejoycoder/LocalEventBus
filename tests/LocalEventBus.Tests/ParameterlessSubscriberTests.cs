using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// 无参订阅者方法测试
/// </summary>
public class ParameterlessSubscriberTests
{
    /// <summary>
    /// 带 Topic 的无参订阅者处理器
    /// </summary>
    private class ParameterlessHandler
    {
        public int ShutdownCallCount { get; private set; }
        public int RefreshCallCount { get; private set; }
        public int OrderCallCount { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        [Subscribe("system/shutdown")]
        public void OnSystemShutdown()
        {
            ShutdownCallCount++;
        }

        [Subscribe("system/refresh")]
        public async ValueTask OnRefreshAsync(CancellationToken ct)
        {
            LastCancellationToken = ct;
            RefreshCallCount++;
            await Task.CompletedTask;
        }

        [Subscribe]
        public void HandleOrder(OrderCreatedEvent e)
        {
            OrderCallCount++;
        }
    }

    /// <summary>
    /// 无效的无参订阅者（无 Topic）
    /// </summary>
    private class InvalidParameterlessHandler
    {
        [Subscribe] // 无参方法必须有 Topic
        public void InvalidMethod()
        {
        }
    }

    [Fact]
    public void Subscribe_ParameterlessMethod_With_Topic_Should_Work()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ParameterlessHandler();

        // Act - 注册应该成功
        var subscription = eventBus.Subscribe(handler);

        // Assert
        Assert.NotNull(subscription);
        subscription.Dispose();
    }

    [Fact]
    public void Subscribe_ParameterlessMethod_Without_Topic_Should_Throw()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new InvalidParameterlessHandler();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => eventBus.Subscribe(handler));
        Assert.Contains("必须指定 Topic", exception.Message);
    }

    [Fact]
    public async Task InvokeByTopic_Should_Call_Parameterless_Method()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ParameterlessHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeByTopicAsync("system/shutdown");

        // Assert
        Assert.Equal(1, handler.ShutdownCallCount);
    }

    [Fact]
    public async Task InvokeByTopic_Should_Pass_CancellationToken()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ParameterlessHandler();
        eventBus.Subscribe(handler);
        using var cts = new CancellationTokenSource();

        // Act
        await eventBus.InvokeByTopicAsync("system/refresh", null, cts.Token);

        // Assert
        Assert.Equal(1, handler.RefreshCallCount);
        Assert.False(handler.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InvokeByTopic_Multiple_Times_Should_Work()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ParameterlessHandler();
        eventBus.Subscribe(handler);

        // Act
        await eventBus.InvokeByTopicAsync("system/shutdown");
        await eventBus.InvokeByTopicAsync("system/shutdown");
        await eventBus.InvokeByTopicAsync("system/shutdown");

        // Assert
        Assert.Equal(3, handler.ShutdownCallCount);
    }

    [Fact]
    public async Task Mixed_Parameterless_And_Typed_Subscribers_Should_Work()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create();
        var handler = new ParameterlessHandler();
        eventBus.Subscribe(handler);

        // Act - 调用无参方法
        await eventBus.InvokeByTopicAsync("system/shutdown");
        
        // Act - 调用带参方法
        await eventBus.InvokeAsync(new OrderCreatedEvent(1, "Test", 100m));

        // Assert
        Assert.Equal(1, handler.ShutdownCallCount);
        Assert.Equal(1, handler.OrderCallCount);
    }
}
