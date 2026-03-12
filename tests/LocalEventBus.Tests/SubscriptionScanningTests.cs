using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

public sealed class SubscriptionScanningTests
{
    private sealed class MultiAttributeTopicHandler
    {
        public int CallCount { get; private set; }

        [Subscribe("orders/created")]
        [Subscribe("orders/cancelled")]
        public void Handle()
        {
            CallCount++;
        }
    }

    private abstract class BaseOrderHandler
    {
        public List<int> ReceivedOrderIds { get; } = [];

        [Subscribe]
        public void OnOrderCreated(OrderCreatedEvent @event)
        {
            ReceivedOrderIds.Add(@event.OrderId);
        }
    }

    private sealed class DerivedOrderHandler : BaseOrderHandler
    {
        public int PingCount { get; private set; }

        [Subscribe("system/ping")]
        public void OnPing()
        {
            PingCount++;
        }
    }

    private sealed class InvalidReturnTypeHandler
    {
        [Subscribe]
        public int Handle(MessageEvent evt)
        {
            return evt.Message.Length;
        }
    }

    private sealed class TooManyParametersHandler
    {
        [Subscribe]
        public void Handle(MessageEvent evt, int extra, CancellationToken ct)
        {
        }
    }

    [Fact]
    public async Task Subscribe_Object_With_Multiple_Subscribe_Attributes_On_Same_Method_Should_Register_All_Topics()
    {
        using var bus = EventBusFactory.Create();
        var handler = new MultiAttributeTopicHandler();

        bus.Subscribe(handler);

        await bus.InvokeByTopicAsync("orders/created");
        await bus.InvokeByTopicAsync("orders/cancelled");

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Subscribe_Object_Should_Include_Base_Class_Subscribe_Methods()
    {
        using var bus = EventBusFactory.Create();
        var handler = new DerivedOrderHandler();

        bus.Subscribe(handler);

        await bus.InvokeAsync(new OrderCreatedEvent(7, "Alice", 50m));
        await bus.InvokeByTopicAsync("system/ping");

        Assert.Equal([7], handler.ReceivedOrderIds);
        Assert.Equal(1, handler.PingCount);
    }

    [Fact]
    public void Subscribe_Object_With_Invalid_Return_Type_Should_Throw()
    {
        using var bus = EventBusFactory.Create();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            bus.Subscribe(new InvalidReturnTypeHandler()));

        Assert.Contains("返回类型必须是 void、Task 或 ValueTask", exception.Message);
    }

    [Fact]
    public void Subscribe_Object_With_Too_Many_Parameters_Should_Throw()
    {
        using var bus = EventBusFactory.Create();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            bus.Subscribe(new TooManyParametersHandler()));

        Assert.Contains("最多有 2 个参数", exception.Message);
    }

    [Fact]
    public void Subscribe_With_UIThread_Option_Without_SynchronizationContext_Should_Throw()
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var bus = EventBusFactory.Create();
                bus.Subscribe<MessageEvent>(
                    (_, _) => ValueTask.CompletedTask,
                    new SubscribeOptions { ThreadOption = ThreadOption.UIThread });
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.Start();
        thread.Join();

        var exception = Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("SynchronizationContext", exception.Message);
    }
}
