using System.Collections.Concurrent;
using LocalEventBus.Abstractions;
using LocalEventBus.Internal;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

public sealed class PipelineCoverageTests
{
    private static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(1.5);

    private sealed class PassThroughFilter : IEventFilter
    {
        public int Order => 0;

        public ValueTask<bool> ShouldProcessAsync<TEvent>(TEvent @event, CancellationToken ct)
            where TEvent : notnull
        {
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingInterceptor : IEventInterceptor
    {
        private readonly ConcurrentQueue<string> _events;
        private readonly TaskCompletionSource<bool>? _completion;
        private readonly string _name;

        public RecordingInterceptor(
            string name,
            int order,
            ConcurrentQueue<string> events,
            TaskCompletionSource<bool>? completion = null)
        {
            _name = name;
            Order = order;
            _events = events;
            _completion = completion;
        }

        public int Order { get; }

        public Exception? LastException { get; private set; }

        public ValueTask OnHandlingAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, CancellationToken ct)
            where TEvent : notnull
        {
            _events.Enqueue($"{_name}:handling");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnHandledAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, TimeSpan elapsed, CancellationToken ct)
            where TEvent : notnull
        {
            _events.Enqueue($"{_name}:handled");
            _completion?.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnHandlerFailedAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, Exception ex, CancellationToken ct)
            where TEvent : notnull
        {
            LastException = ex;
            _events.Enqueue($"{_name}:failed");
            _completion?.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task PublishAsync_Should_Invoke_Interceptors_In_Order()
    {
        await using var bus = (DefaultEventBus)EventBusFactory.Create(new EventBusOptions
        {
            RetryOptions = new RetryOptions
            {
                MaxRetryAttempts = 0
            }
        });

        var events = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.AddInterceptor(new RecordingInterceptor("second", 2, events, completed));
        bus.AddInterceptor(new RecordingInterceptor("first", 1, events));

        var handlerCalls = 0;
        bus.Subscribe<MessageEvent>((_, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new MessageEvent("ok"));

        await WaitForCompletionAsync(completed.Task, "PublishAsync 未触发拦截器完成路径");

        Assert.Equal(1, handlerCalls);
        Assert.Equal(
        [
            "first:handling",
            "second:handling",
            "first:handled",
            "second:handled"
        ],
        events.ToArray());
    }

    [Fact]
    public async Task PublishAsync_Should_Call_Failure_Interceptor_When_ShouldRetry_Returns_False()
    {
        await using var bus = (DefaultEventBus)EventBusFactory.Create(new EventBusOptions
        {
            RetryOptions = new RetryOptions
            {
                MaxRetryAttempts = 3,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                DelayStrategy = RetryDelayStrategy.Fixed,
                ShouldRetry = _ => false
            }
        });

        var events = new ConcurrentQueue<string>();
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new RecordingInterceptor("failure", 0, events, failed);
        bus.AddInterceptor(interceptor);

        var attempts = 0;
        bus.Subscribe<MessageEvent>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("boom");
        });

        await bus.PublishAsync(new MessageEvent("fail"));

        await WaitForCompletionAsync(failed.Task, "失败拦截器未被触发");

        Assert.Equal(1, attempts);
        Assert.IsType<InvalidOperationException>(interceptor.LastException);
        Assert.Equal(["failure:handling", "failure:failed"], events.ToArray());
    }

    [Fact]
    public async Task AddFilter_After_Dispatch_Has_Started_Should_Throw()
    {
        await using var bus = (DefaultEventBus)EventBusFactory.Create();

        await bus.PublishAsync(new MessageEvent("start"));

        var exception = Assert.Throws<InvalidOperationException>(() => bus.AddFilter(new PassThroughFilter()));
        Assert.Contains("事件分发已启动后不支持动态添加过滤器", exception.Message);
    }

    [Fact]
    public async Task AddInterceptor_After_Dispatch_Has_Started_Should_Throw()
    {
        await using var bus = (DefaultEventBus)EventBusFactory.Create();

        await bus.PublishAsync(new MessageEvent("start"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            bus.AddInterceptor(new RecordingInterceptor("late", 0, new ConcurrentQueue<string>())));
        Assert.Contains("事件分发已启动后不支持动态添加拦截器", exception.Message);
    }

    [Fact]
    public async Task Subscribe_Topic_Delegate_Should_Stop_Receiving_After_Dispose()
    {
        await using var bus = EventBusFactory.Create();
        var received = new List<object?>();
        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = bus.Subscribe("system/ping", payload =>
        {
            received.Add(payload);
            firstReceived.TrySetResult(true);
        });

        await bus.PublishByTopicAsync("system/ping", 42);
        await WaitForCompletionAsync(firstReceived.Task, "纯 Topic 委托订阅未收到首条消息");

        subscription.Dispose();
        await bus.PublishByTopicAsync("system/ping", 100);
        await Task.Delay(100);

        Assert.Single(received);
        Assert.Equal(42, received[0]);
    }

    [Fact]
    public async Task InvokeAsync_Should_Respect_DefaultTimeout()
    {
        await using var bus = EventBusFactory.Create(options =>
        {
            options.DefaultTimeout = TimeSpan.FromMilliseconds(50);
        });

        bus.Subscribe<MessageEvent>((_, ct) =>
            new ValueTask(Task.Delay(TimeSpan.FromSeconds(5), ct)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bus.InvokeAsync(new MessageEvent("slow")).AsTask());
    }

    private static async Task WaitForCompletionAsync(Task task, string failureMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(AssertionTimeout));
        Assert.True(completed == task, failureMessage);
        await task;
    }
}
