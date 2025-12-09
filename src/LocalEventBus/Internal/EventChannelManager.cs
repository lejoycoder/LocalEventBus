using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 事件通道管理器
/// </summary>
public sealed class EventChannelManager : IDisposable, IAsyncDisposable
{
    private readonly Channel<EventEnvelope>[] _shardedChannels;
    private readonly EventBusOptions _options;
    private readonly int _shardCount;
    private readonly ConcurrentDictionary<string, int> _partitionKeyToShardIndex;
    private int _nextShardIndex = 0;
    private readonly object _assignLock = new object();

    /// <summary>
    /// 初始化事件通道管理器
    /// </summary>
    /// <param name="options">事件总线选项</param>
    public EventChannelManager(EventBusOptions options)
    {
        _options = options ?? new EventBusOptions();

        // 确定分片数量（至少1个）
        _shardCount = Math.Max(1, _options.PartitionCount);

        // 预先创建固定数量的分片通道
        _shardedChannels = new Channel<EventEnvelope>[_shardCount];
        for (int i = 0; i < _shardCount; i++)
        {
            _shardedChannels[i] = CreateChannel();
        }

        // 初始化分区键映射字典
        _partitionKeyToShardIndex = new ConcurrentDictionary<string, int>(Environment.ProcessorCount, _shardCount);
    }

    /// <summary>
    /// 获取通道（按分区键智能分片）
    /// </summary>
    /// <remarks>
    /// 当分区键数量不超过分片数量时，直接映射到固定分片；
    /// 当分区键数量超过分片数量时，使用哈希分区策略。
    /// </remarks>
    public Channel<EventEnvelope> GetChannel(string? partitionKey)
    {
        // 如果没有分区键,使用第一个分片
        if (string.IsNullOrEmpty(partitionKey) || _shardCount == 1)
        {
            return _shardedChannels[0];
        }

        // 如果已经有映射,直接返回
        if (_partitionKeyToShardIndex.TryGetValue(partitionKey, out var existingShardIndex))
        {
            return _shardedChannels[existingShardIndex];
        }

        // 当分区键数量未超过分片数量时,直接分配新的分片
        if (_partitionKeyToShardIndex.Count < _shardCount)
        {
            lock (_assignLock)
            {
                // 双重检查,避免并发时重复分配
                if (_partitionKeyToShardIndex.TryGetValue(partitionKey, out existingShardIndex))
                {
                    return _shardedChannels[existingShardIndex];
                }

                // 再次检查是否仍在独立分配阶段
                if (_partitionKeyToShardIndex.Count < _shardCount)
                {
                    // 分配下一个可用的分片索引
                    var shardIndex = _nextShardIndex;
                    _nextShardIndex = (_nextShardIndex + 1) % _shardCount;

                    _partitionKeyToShardIndex[partitionKey] = shardIndex;
                    return _shardedChannels[shardIndex];
                }
            }
        }

        // 分区键数量超过分片数量,使用哈希分区
        var hashCode = partitionKey.GetHashCode();
        var hashBasedShardIndex = Math.Abs(hashCode % _shardCount);

        return _shardedChannels[hashBasedShardIndex];
    }

    /// <summary>
    /// 获取待处理事件数量
    /// </summary>
    public int GetPendingCount()
    {
        int count = 0;
        foreach (var channel in _shardedChannels)
        {
            count += channel.Reader.Count;
        }
        return count;
    }

    /// <summary>
    /// 创建新通道
    /// </summary>
    private Channel<EventEnvelope> CreateChannel()
    {
        if (_options.ChannelCapacity > 0)
        {
            // 有界通道
            return Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode = _options.ChannelFullMode,
                SingleReader = true,
                SingleWriter = false
            });
        }
        else
        {
            // 无界通道
            return Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }
    }

    /// <summary>
    /// 读取所有通道的事件（合并所有分片）
    /// </summary>
    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readers = _shardedChannels.Select(c => c.Reader).ToArray();

        while (!cancellationToken.IsCancellationRequested)
        {
            bool anyRead = false;

            // 轮询所有分片尝试读取
            foreach (var reader in readers)
            {
                while (reader.TryRead(out var envelope))
                {
                    yield return envelope;
                    anyRead = true;
                }
            }

            if (!anyRead)
            {
                // 等待任意分片有数据
                var tasks = readers.Select(r => r.WaitToReadAsync(cancellationToken).AsTask()).ToArray();
                try
                {
                    await Task.WhenAny(tasks);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// 获取所有分片通道（用于独立处理每个分片）
    /// </summary>
    public IReadOnlyList<Channel<EventEnvelope>> GetShardedChannels()
    {
        return _shardedChannels;
    }

    /// <summary>
    /// 获取当前已分配的分区键数量
    /// </summary>
    public int GetPartitionKeyCount()
    {
        return _partitionKeyToShardIndex.Count;
    }

    /// <summary>
    /// 获取分区键的分片索引（如果存在）
    /// </summary>
    /// <param name="partitionKey">分区键</param>
    /// <returns>分片索引，如果不存在则返回 null</returns>
    public int? GetShardIndexForPartitionKey(string partitionKey)
    {
        return _partitionKeyToShardIndex.TryGetValue(partitionKey, out var index) ? index : null;
    }

    /// <summary>
    /// 获取每个分片的分区键分布情况
    /// </summary>
    /// <returns>分片索引到分区键列表的映射</returns>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> GetPartitionKeyDistribution()
    {
        return _partitionKeyToShardIndex
            .GroupBy(kvp => kvp.Value, kvp => kvp.Key)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.ToList());
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        foreach (var channel in _shardedChannels)
        {
            channel.Writer.Complete();
        }
    }

    /// <summary>
    /// 异步释放资源
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
