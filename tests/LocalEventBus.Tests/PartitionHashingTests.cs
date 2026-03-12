using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// 订阅驱动通道路由测试
/// </summary>
public class PartitionHashingTests
{
    private sealed class InvalidChannelZeroAttributeHandler
    {
        [Subscribe(ChannelId = 0)]
        public void Handle(MessageEvent evt)
        {
        }
    }

    private sealed class InvalidChannelOutOfRangeAttributeHandler
    {
        [Subscribe(ChannelId = 99)]
        public void Handle(MessageEvent evt)
        {
        }
    }

    private sealed class ExplicitChannel2Handler
    {
        [Subscribe(Topic = "dummy/topic", ChannelId = 2)]
        public void Handle(MessageEvent evt)
        {
        }
    }

    [Fact]
    public async Task Publish_Should_Fanout_To_Explicit_And_Unspecified_Subscribers_Without_Duplicates()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 4
        });

        int explicit1Count = 0;
        int explicit2Count = 0;
        int unspecifiedCount = 0;
        var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        eventBus.Subscribe<MessageEvent>(
            (evt, ct) =>
            {
                if (Interlocked.Increment(ref explicit1Count) == 1 &&
                    Volatile.Read(ref explicit2Count) == 1 &&
                    Volatile.Read(ref unspecifiedCount) == 1)
                {
                    allReceived.TrySetResult(true);
                }
                return ValueTask.CompletedTask;
            },
            new SubscribeOptions { ChannelId = 1 });

        eventBus.Subscribe<MessageEvent>(
            (evt, ct) =>
            {
                if (Interlocked.Increment(ref explicit2Count) == 1 &&
                    Volatile.Read(ref explicit1Count) == 1 &&
                    Volatile.Read(ref unspecifiedCount) == 1)
                {
                    allReceived.TrySetResult(true);
                }
                return ValueTask.CompletedTask;
            },
            new SubscribeOptions { ChannelId = 2 });

        eventBus.Subscribe<MessageEvent>((evt, ct) =>
        {
            if (Interlocked.Increment(ref unspecifiedCount) == 1 &&
                Volatile.Read(ref explicit1Count) == 1 &&
                Volatile.Read(ref explicit2Count) == 1)
            {
                allReceived.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        });

        await eventBus.PublishAsync(new MessageEvent("fanout"));

        var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        Assert.True(completed == allReceived.Task, "显式/未指定订阅未全部触发");
        Assert.Equal(1, explicit1Count);
        Assert.Equal(1, explicit2Count);
        Assert.Equal(1, unspecifiedCount);
    }

    [Fact]
    public async Task Unspecified_Routing_Should_Use_Unspecified_Pool_And_Prefer_Less_Loaded_Channel()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 3
        });

        // 占用显式通道 1 和 2，使第一次未指定路由只能落到 0 号通道
        eventBus.Subscribe<MessageEvent>((_, _) => ValueTask.CompletedTask,
            new SubscribeOptions { Topic = "explicit/one", ChannelId = 1 });
        var explicitTwoToken = eventBus.Subscribe<MessageEvent>((_, _) => ValueTask.CompletedTask,
            new SubscribeOptions { Topic = "explicit/two", ChannelId = 2 });

        var holdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        eventBus.Subscribe<MessageEvent>(async (evt, ct) =>
        {
            if (evt.Message == "hold")
            {
                holdStarted.TrySetResult(true);
                await releaseHold.Task.WaitAsync(ct);
                return;
            }

            if (evt.Message == "probe")
            {
                probeHandled.TrySetResult(true);
            }
        });

        await eventBus.PublishAsync(new MessageEvent("hold"));
        var holdReady = await Task.WhenAny(holdStarted.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        Assert.True(holdReady == holdStarted.Task, "未能构造 0 号通道在途负载");

        // 释放通道2占用后，未指定池变为 {0,2}，且 0 号有负载，下一条应优先走 2 号
        explicitTwoToken.Dispose();
        await eventBus.PublishAsync(new MessageEvent("probe"));

        var probeReady = await Task.WhenAny(probeHandled.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        releaseHold.TrySetResult(true);

        Assert.True(probeReady == probeHandled.Task, "未指定路由未按最小负载选择可用通道");
    }

    [Fact]
    public async Task Unsubscribe_Object_Should_Release_Explicit_Channel_Occupancy()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 3
        });

        eventBus.Subscribe<MessageEvent>((_, _) => ValueTask.CompletedTask,
            new SubscribeOptions { Topic = "explicit/one", ChannelId = 1 });

        var handler = new ExplicitChannel2Handler();
        eventBus.Subscribe(handler);

        var holdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        eventBus.Subscribe<MessageEvent>(async (evt, ct) =>
        {
            if (evt.Message == "hold")
            {
                holdStarted.TrySetResult(true);
                await releaseHold.Task.WaitAsync(ct);
                return;
            }

            if (evt.Message == "probe")
            {
                probeHandled.TrySetResult(true);
            }
        });

        await eventBus.PublishAsync(new MessageEvent("hold"));
        var holdReady = await Task.WhenAny(holdStarted.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        Assert.True(holdReady == holdStarted.Task, "未能构造 0 号通道在途负载");

        eventBus.Unsubscribe(handler);
        await eventBus.PublishAsync(new MessageEvent("probe"));

        var probeReady = await Task.WhenAny(probeHandled.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        releaseHold.TrySetResult(true);

        Assert.True(probeReady == probeHandled.Task, "Unsubscribe(object) 后显式通道占用未被回收");
    }

    [Fact]
    public void PartitionCount_Less_Than_2_Should_Throw_ArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            EventBusFactory.Create(new EventBusOptions { PartitionCount = 1 }));

        Assert.Contains("PartitionCount", ex.Message);
    }

    [Fact]
    public void Subscribe_With_Invalid_Explicit_Channel_Should_Throw_ArgumentOutOfRangeException()
    {
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 2
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eventBus.Subscribe<MessageEvent>(
                (evt, ct) => ValueTask.CompletedTask,
                new SubscribeOptions { ChannelId = 0 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eventBus.Subscribe<MessageEvent>(
                (evt, ct) => ValueTask.CompletedTask,
                new SubscribeOptions { ChannelId = 2 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eventBus.Subscribe(new InvalidChannelZeroAttributeHandler()));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eventBus.Subscribe(new InvalidChannelOutOfRangeAttributeHandler()));
    }

    [Fact]
    public async Task Publish_With_No_Matching_Subscribers_Should_Not_Enqueue()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 2
        });

        var diagnostics = Assert.IsAssignableFrom<IEventBusDiagnostics>(eventBus);
        Assert.Equal(0, diagnostics.GetPendingEventCount());

        await eventBus.PublishAsync(new MessageEvent("no-subscriber"));
        await Task.Delay(50);

        Assert.Equal(0, diagnostics.GetPendingEventCount());
    }

    [Fact]
    public async Task Publish_Multiple_Events_Should_Keep_EventBus_Semantics_For_All_Matched_Subscribers()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 3
        });

        const int total = 100;
        int explicitCount = 0;
        int unspecifiedCount = 0;
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        eventBus.Subscribe<MessageEvent>((evt, ct) =>
        {
            if (Interlocked.Increment(ref explicitCount) == total &&
                Volatile.Read(ref unspecifiedCount) == total)
            {
                allDone.TrySetResult(true);
            }
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { ChannelId = 1 });

        eventBus.Subscribe<MessageEvent>((evt, ct) =>
        {
            if (Interlocked.Increment(ref unspecifiedCount) == total &&
                Volatile.Read(ref explicitCount) == total)
            {
                allDone.TrySetResult(true);
            }
            return ValueTask.CompletedTask;
        });

        foreach (var evt in Enumerable.Range(0, total).Select(i => new MessageEvent($"event-{i}")))
        {
            await eventBus.PublishAsync(evt);
        }

        var completed = await Task.WhenAny(allDone.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(completed == allDone.Task, "连续发布未满足所有匹配订阅者都处理一次的语义");
        Assert.Equal(total, explicitCount);
        Assert.Equal(total, unspecifiedCount);
    }

    [Fact]
    public async Task Sync_Publish_Should_Throw_When_Any_Target_Channel_Cannot_Enqueue()
    {
        await using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 2,
            ChannelCapacity = 1
        });

        var holdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        eventBus.Subscribe<MessageEvent>(async (evt, ct) =>
        {
            if (evt.Message == "hold")
            {
                holdStarted.TrySetResult(true);
                await releaseHold.Task.WaitAsync(ct);
            }
        }, new SubscribeOptions { ChannelId = 1 });

        await eventBus.PublishAsync(new MessageEvent("hold"));
        var holdReady = await Task.WhenAny(holdStarted.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        Assert.True(holdReady == holdStarted.Task, "未能构造阻塞中的显式通道");

        // 第二条消息占满队列容量
        await eventBus.PublishAsync(new MessageEvent("queued"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            eventBus.Publish(new MessageEvent("overflow")));

        releaseHold.TrySetResult(true);

        Assert.Contains("无法将事件写入通道", ex.Message);
    }
}
