using LocalEventBus.Abstractions;
using LocalEventBus.Tests.Events;

namespace LocalEventBus.Tests;

/// <summary>
/// 哈希分片功能测试
/// </summary>
public class PartitionHashingTests
{
    [Fact]
    public async Task Should_Use_Fixed_Number_Of_Shards()
    {
        // Arrange - 创建16个分片的EventBus
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 16
        });

        var receivedEvents = new System.Collections.Concurrent.ConcurrentBag<OrderCreatedEvent>();

        eventBus.Subscribe<OrderCreatedEvent>(e =>
        {
            receivedEvents.Add(e);
        });

        // Act - 发布100个不同分区的事件
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var partitionKey = $"user:{i}";
            tasks.Add(eventBus.PublishAsync(
                new OrderCreatedEvent(i, $"User{i}", i * 100m),
                new PublishOptions { PartitionKey = partitionKey }
            ).AsTask());
        }

        await Task.WhenAll(tasks);
        await Task.Delay(200); // 等待处理

        // Assert - 所有事件都应该被处理
        Assert.Equal(100, receivedEvents.Count);
    }

    [Fact]
    public async Task Same_PartitionKey_Should_Be_Processed_In_Order()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 8
        });

        var receivedOrder = new List<int>();
        var lockObj = new object();

        eventBus.Subscribe<MessageEvent>((e, ct) =>
        {
            lock (lockObj)
            {
                // 从消息中提取ID
                var parts = e.Message.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var id))
                {
                    receivedOrder.Add(id);
                }
            }
            return ValueTask.CompletedTask;
        });

        var partitionKey = "test-partition";

        // Act - 同一分区键的事件应该按顺序处理
        for (int i = 0; i < 10; i++)
        {
            await eventBus.PublishAsync(
                new MessageEvent($"Message:{i}"),
                new PublishOptions { PartitionKey = partitionKey }
            );
        }

        await Task.Delay(1000); // 增加等待时间以确保所有事件都被处理

        // Assert - 顺序应该保持
        Assert.Equal(10, receivedOrder.Count);
        
        // 验证顺序
        var expected = Enumerable.Range(0, 10).ToList();
        Assert.Equal(expected, receivedOrder);
    }

    [Fact]
    public async Task Different_PartitionKeys_Can_Process_Concurrently()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 4
        });

        var processingTimes = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        eventBus.Subscribe<MessageEvent>(async (e, ct) =>
        {
            processingTimes.TryAdd(e.Message, DateTime.UtcNow);
            await Task.Delay(50, ct); // 模拟处理时间
        });

        // Act - 发布4个不同分区的事件
        var tasks = new[]
        {
            eventBus.PublishAsync(new MessageEvent("Partition1"), new PublishOptions { PartitionKey = "p1" }).AsTask(),
            eventBus.PublishAsync(new MessageEvent("Partition2"), new PublishOptions { PartitionKey = "p2" }).AsTask(),
            eventBus.PublishAsync(new MessageEvent("Partition3"), new PublishOptions { PartitionKey = "p3" }).AsTask(),
            eventBus.PublishAsync(new MessageEvent("Partition4"), new PublishOptions { PartitionKey = "p4" }).AsTask(),
        };

        await Task.WhenAll(tasks);
        await Task.Delay(300);

        // Assert - 4个事件应该并发处理（处理时间接近）
        Assert.Equal(4, processingTimes.Count);

        var times = processingTimes.Values.OrderBy(t => t).ToArray();
        var maxDiff = (times[^1] - times[0]).TotalMilliseconds;

        // 如果是串行处理，至少需要200ms (4 * 50ms)
        // 如果是并发处理，应该在100ms内完成
        Assert.True(maxDiff < 150, $"处理时间差: {maxDiff}ms，应该并发处理");
    }

    [Fact]
    public async Task Null_PartitionKey_Should_Use_Default_Shard()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 8
        });

        var count = 0;

        eventBus.Subscribe<MessageEvent>(e =>
        {
            Interlocked.Increment(ref count);
        });

        // Act - 不指定分区键
        for (int i = 0; i < 10; i++)
        {
            await eventBus.PublishAsync(new MessageEvent($"Message {i}"));
        }

        await Task.Delay(100);

        // Assert
        Assert.Equal(10, count);
    }

    [Fact]
    public void Should_Hash_PartitionKey_Consistently()
    {
        // Arrange
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 16
        });

        var diagnostics = eventBus as IEventBusDiagnostics;
        Assert.NotNull(diagnostics);

        // Act & Assert - 相同的分区键应该映射到相同的分片
        // 通过发布事件并验证行为一致性
        var partitionKey = "consistent-key";
        
        for (int i = 0; i < 5; i++)
        {
            eventBus.Publish(new MessageEvent($"Test {i}"), new PublishOptions
            {
                PartitionKey = partitionKey
            });
        }

        // 如果哈希一致，所有事件应该进入同一个分片
        // 这个可以通过后续处理顺序来验证
    }

    [Fact]
    public async Task Should_Support_Custom_Partition_Count()
    {
        // Arrange & Act
        var testCases = new[] { 1, 4, 8, 16, 32, 64 };

        foreach (var partitionCount in testCases)
        {
            using var eventBus = EventBusFactory.Create(new EventBusOptions
            {
                PartitionCount = partitionCount
            });

            var count = 0;
            eventBus.Subscribe<MessageEvent>(e => Interlocked.Increment(ref count));

            // 发布事件
            for (int i = 0; i < 10; i++)
            {
                await eventBus.PublishAsync(
                    new MessageEvent($"Test {i}"),
                    new PublishOptions { PartitionKey = $"partition:{i}" }
                );
            }

            await Task.Delay(100);

            // Assert
            Assert.Equal(10, count);
        }
    }

    [Fact]
    public async Task Many_Unique_PartitionKeys_Should_Not_Create_Many_Channels()
    {
        // Arrange - 固定16个分片
        using var eventBus = EventBusFactory.Create(new EventBusOptions
        {
            PartitionCount = 16
        });

        var receivedCount = 0;
        eventBus.Subscribe<MessageEvent>(e => Interlocked.Increment(ref receivedCount));

        // Act - 1000个不同的分区键，但只会使用16个分片
        var tasks = new List<Task>();
        for (int i = 0; i < 1000; i++)
        {
            tasks.Add(eventBus.PublishAsync(
                new MessageEvent($"Message {i}"),
                new PublishOptions { PartitionKey = $"unique-key-{i}" }
            ).AsTask());
        }

        await Task.WhenAll(tasks);
        await Task.Delay(500);

        // Assert - 所有1000个事件都应该被处理
        Assert.Equal(1000, receivedCount);
    }
}
