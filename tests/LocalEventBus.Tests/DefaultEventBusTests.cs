using LocalEventBus;
using LocalEventBus.Abstractions;
using LocalEventBus.Internal;
using LocalEventBus.Matchers;
using Xunit;

namespace LocalEventBus.Tests;

public sealed class DefaultEventBusTests
{
    private static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(1.5);

    [Fact]
    public async Task PublishAsync_Should_Invoke_Typed_Subscriber()
    {
        await using var bus = EventBusFactory.Create();
        var tcs = new TaskCompletionSource<TestEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var _ = bus.Subscribe<TestEvent>((evt, _) =>
        {
            tcs.TrySetResult(evt);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(1));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(AssertionTimeout));
        Assert.True(completed == tcs.Task, "事件未到达订阅者");
        var result = await tcs.Task;
        Assert.Equal(1, result.Id);
    }

    [Fact]
    public async Task PublishByTopicAsync_Should_Invoke_Matching_Subscriber()
    {
        await using var bus = EventBusFactory.Create();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var _ = bus.Subscribe<string>((message, _) =>
        {
            tcs.TrySetResult(message);
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "orders.created" });

        await bus.PublishByTopicAsync("orders.created", "payload");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(AssertionTimeout));
        Assert.True(completed == tcs.Task, "Topic 订阅未触发");
        var payload = await tcs.Task;
        Assert.Equal("payload", payload);
    }

    [Fact]
    public async Task Wildcard_Matcher_Should_Handle_Pattern_Subscription()
    {
        var matcherProvider = new DefaultEventMatcherProvider(new IEventMatcher[]
        {
            new WildcardEventMatcher(ignoreCase: true)
        });
        await using var bus = new DefaultEventBus(new EventBusOptions(), new TestKeyProvider(), matcherProvider);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = bus.Subscribe<TestEvent>((_, _) =>
        {
            tcs.TrySetResult(true);
            return ValueTask.CompletedTask;
        }, new SubscribeOptions { Topic = "orders.created" });

        await bus.PublishAsync(new TestEvent(9), "orders.*");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(AssertionTimeout));
        Assert.True(completed == tcs.Task, "通配符匹配未生效");
    }

    [Fact]
    public async Task Retry_Should_Reexecute_When_Handler_Fails()
    {
        var options = new EventBusOptions
        {
            RetryOptions = new RetryOptions
            {
                MaxRetryAttempts = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                DelayStrategy = RetryDelayStrategy.Fixed
            }
        };

        await using var bus = EventBusFactory.Create(options);

        var attempts = 0;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var _ = bus.Subscribe<TestEvent>((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("fail once");
            }

            tcs.TrySetResult(attempts);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(42));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(AssertionTimeout));
        Assert.True(completed == tcs.Task, "重试未成功执行");
        var attemptsResult = await tcs.Task;
        Assert.Equal(2, attemptsResult);
    }

    [Fact]
    public async Task Unsubscribe_Should_Stop_Receiving_Events()
    {
        await using var bus = EventBusFactory.Create();
        var counter = 0;
        var firstReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var token = bus.Subscribe<TestEvent>((_, _) =>
        {
            counter++;
            if (counter == 1)
            {
                firstReceive.TrySetResult(true);
            }
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(1));
        await Task.WhenAny(firstReceive.Task, Task.Delay(AssertionTimeout));

        token.Dispose();
        await bus.PublishAsync(new TestEvent(2));
        await Task.Delay(100); // 等待可能的异步调度

        Assert.Equal(1, counter);
    }

    private sealed record TestEvent(int Id);

    private sealed class TestKeyProvider : IEventTypeKeyProvider
    {
        public string GetKey(Type eventType) => eventType.FullName ?? eventType.Name;
    }
}

