using System.Collections.Concurrent;
using LocalEventBus.Abstractions;
using LocalEventBus.Internal;

namespace LocalEventBus.Tests;

public sealed class ThreadOptionTests
{
    private sealed record ThreadEvent(int Id);

    private sealed class UiThreadAttributeHandler
    {
        public int HandlerThreadId { get; private set; }

        [Subscribe(ThreadOption = ThreadOption.UIThread)]
        public void Handle(ThreadEvent evt)
        {
            HandlerThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class TestKeyProvider : IEventTypeKeyProvider
    {
        public string GetKey(Type eventType) => eventType.FullName ?? eventType.Name;
    }

    private sealed class DedicatedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _threadIdTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedSynchronizationContext()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "LocalEventBus-UIThread-Test"
            };
            _thread.Start();
        }

        public int ThreadId => _threadIdTcs.Task.GetAwaiter().GetResult();

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (_queue.IsAddingCompleted)
            {
                return;
            }

            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // 忽略释放后的并发 Post
            }
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            _threadIdTcs.TrySetResult(Environment.CurrentManagedThreadId);

            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work.Callback(work.State);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_With_UIThread_Option_Should_Run_On_Configured_Context_Thread()
    {
        using var uiContext = new DedicatedSynchronizationContext();
        await using var bus = new DefaultEventBus(
            new EventBusOptions(),
            new TestKeyProvider(),
            new DefaultEventMatcherProvider(),
            synchronizationContext: uiContext);

        int handlerThreadId = -1;

        using var _ = bus.Subscribe<ThreadEvent>(
            (evt, ct) =>
            {
                handlerThreadId = Environment.CurrentManagedThreadId;
                return ValueTask.CompletedTask;
            },
            new SubscribeOptions { ThreadOption = ThreadOption.UIThread });

        await bus.InvokeAsync(new ThreadEvent(1));

        Assert.Equal(uiContext.ThreadId, handlerThreadId);
    }

    [Fact]
    public async Task Subscribe_Object_With_UIThread_Attribute_Should_Run_On_Configured_Context_Thread()
    {
        using var uiContext = new DedicatedSynchronizationContext();
        await using var bus = new DefaultEventBus(
            new EventBusOptions(),
            new TestKeyProvider(),
            new DefaultEventMatcherProvider(),
            synchronizationContext: uiContext);

        var handler = new UiThreadAttributeHandler();
        using var _ = bus.Subscribe(handler);

        await bus.InvokeAsync(new ThreadEvent(2));

        Assert.Equal(uiContext.ThreadId, handler.HandlerThreadId);
    }

    [Fact]
    public async Task PublishAsync_With_UIThread_Option_Should_Run_On_Configured_Context_Thread()
    {
        using var uiContext = new DedicatedSynchronizationContext();
        await using var bus = new DefaultEventBus(
            new EventBusOptions(),
            new TestKeyProvider(),
            new DefaultEventMatcherProvider(),
            synchronizationContext: uiContext);

        var handlerThreadTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var _ = bus.Subscribe<ThreadEvent>(
            (evt, ct) =>
            {
                handlerThreadTcs.TrySetResult(Environment.CurrentManagedThreadId);
                return ValueTask.CompletedTask;
            },
            new SubscribeOptions { ThreadOption = ThreadOption.UIThread });

        await bus.PublishAsync(new ThreadEvent(3));

        var completed = await Task.WhenAny(handlerThreadTcs.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        Assert.True(completed == handlerThreadTcs.Task, "UIThread 订阅未在 PublishAsync 场景下触发");
        Assert.Equal(uiContext.ThreadId, await handlerThreadTcs.Task);
    }

    [Fact]
    public void InvokeAsync_With_BackgroundThread_Option_Should_Run_On_Publisher_Thread()
    {
        using var bus = EventBusFactory.Create();

        int publisherThreadId = -1;
        int handlerThreadId = -1;

        using var _ = bus.Subscribe<ThreadEvent>(
            (evt, ct) =>
            {
                handlerThreadId = Environment.CurrentManagedThreadId;
                return ValueTask.CompletedTask;
            },
            new SubscribeOptions { ThreadOption = ThreadOption.BackgroundThread });

        var publisherThread = new Thread(() =>
        {
            publisherThreadId = Environment.CurrentManagedThreadId;
            bus.InvokeAsync(new ThreadEvent(1)).AsTask().GetAwaiter().GetResult();
        });

        publisherThread.Start();
        publisherThread.Join();

        Assert.NotEqual(-1, handlerThreadId);
        Assert.Equal(publisherThreadId, handlerThreadId);
    }
}
