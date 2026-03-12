using BenchmarkDotNet.Attributes;
using LocalEventBus;
using LocalEventBus.Abstractions;
using LocalEventBus.Internal;

namespace LocalEventBus.Benchmarks;

[MemoryDiagnoser]
public class EventBusBenchmarks
{
    private IEventBus _bus = default!;
    private TestEvent _event = new(1);

    [GlobalSetup]
    public void Setup()
    {
        var options = new EventBusOptions
        {
            PartitionCount = Math.Max(Environment.ProcessorCount, 1),
            ChannelCapacity = 0,
            RetryOptions = new RetryOptions
            {
                MaxRetryAttempts = 0
            }
        };

        _bus = EventBusFactory.Create(options);

        _bus.Subscribe<TestEvent>((_, _) => ValueTask.CompletedTask);
        _bus.Subscribe("topic/sample", (_, _) => ValueTask.CompletedTask);

        // 预热调度循环
        _bus.Publish(_event);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_bus is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _bus.Dispose();
        }
    }

    [Benchmark(Description = "Publish enqueue (typed)")]
    public void PublishEnqueue() => _bus.Publish(_event);

    [Benchmark(Description = "Publish async (typed)")]
    public Task PublishAsync() => _bus.PublishAsync(_event).AsTask();

    [Benchmark(Description = "Direct invoke (typed)")]
    public Task InvokeDirect() => _bus.InvokeAsync(_event).AsTask();

    [Benchmark(Description = "Publish to topic (string)")]
    public Task PublishTopicAsync() => _bus.PublishAsync("topic/sample", null).AsTask();

    private sealed record TestEvent(int Id);
}

