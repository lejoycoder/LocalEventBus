using System.Threading.Channels;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 事件通道管理器
/// </summary>
public sealed class EventChannelManager : IDisposable, IAsyncDisposable
{
    private readonly Channel<EventEnvelope>[] _channels;
    private readonly long[] _channelLoads;
    private readonly int[] _explicitChannelSubscriberCounts;
    private readonly EventBusOptions _options;

    /// <summary>
    /// 初始化事件通道管理器
    /// </summary>
    /// <param name="options">事件总线选项</param>
    public EventChannelManager(EventBusOptions options)
    {
        _options = options ?? new EventBusOptions();

        if (_options.PartitionCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.PartitionCount),
                _options.PartitionCount,
                "PartitionCount 最小值为 2（0号为未指定通道，1..N-1为显式通道）。");
        }

        ChannelCount = _options.PartitionCount;
        _channels = new Channel<EventEnvelope>[ChannelCount];
        _channelLoads = new long[ChannelCount];
        _explicitChannelSubscriberCounts = new int[ChannelCount];

        for (int i = 0; i < ChannelCount; i++)
        {
            _channels[i] = CreateChannel();
        }
    }

    /// <summary>
    /// 通道数量（由 PartitionCount 决定）
    /// </summary>
    public int ChannelCount { get; }

    /// <summary>
    /// 根据通道编号获取通道
    /// </summary>
    public Channel<EventEnvelope> GetChannel(int channelId)
    {
        ValidateChannelId(channelId);
        return _channels[channelId];
    }

    /// <summary>
    /// 校验通道编号
    /// </summary>
    public void ValidateChannelId(int channelId)
    {
        if (channelId < 0 || channelId >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelId),
                channelId,
                $"ChannelId 必须在 [0, {ChannelCount - 1}] 范围内。");
        }
    }

    /// <summary>
    /// 校验显式通道编号（1..N-1）
    /// </summary>
    public void ValidateExplicitChannelId(int channelId)
    {
        if (channelId <= 0 || channelId >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelId),
                channelId,
                $"显式 ChannelId 必须在 [1, {ChannelCount - 1}] 范围内。0 号保留为未指定通道。");
        }
    }

    /// <summary>
    /// 记录显式通道订阅者
    /// </summary>
    public void RegisterExplicitSubscriber(int channelId)
    {
        ValidateExplicitChannelId(channelId);
        Interlocked.Increment(ref _explicitChannelSubscriberCounts[channelId]);
    }

    /// <summary>
    /// 取消记录显式通道订阅者
    /// </summary>
    public void UnregisterExplicitSubscriber(int channelId)
    {
        ValidateExplicitChannelId(channelId);
        var updated = Interlocked.Decrement(ref _explicitChannelSubscriberCounts[channelId]);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _explicitChannelSubscriberCounts[channelId], 0);
        }
    }

    /// <summary>
    /// 入队成功后增加通道负载
    /// </summary>
    public void IncrementLoad(int channelId)
    {
        ValidateChannelId(channelId);
        Interlocked.Increment(ref _channelLoads[channelId]);
    }

    /// <summary>
    /// 事件处理完成后减少通道负载
    /// </summary>
    public void DecrementLoad(int channelId)
    {
        ValidateChannelId(channelId);
        var updated = Interlocked.Decrement(ref _channelLoads[channelId]);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _channelLoads[channelId], 0);
        }
    }

    /// <summary>
    /// 获取待处理事件数量（估算值，基于负载计数）
    /// </summary>
    public int GetPendingCount()
    {
        long total = 0;
        for (int i = 0; i < _channelLoads.Length; i++)
        {
            var load = Volatile.Read(ref _channelLoads[i]);
            if (load > 0)
            {
                total += load;
            }
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// 获取指定通道的当前负载
    /// </summary>
    public long GetChannelLoad(int channelId)
    {
        ValidateChannelId(channelId);
        var load = Volatile.Read(ref _channelLoads[channelId]);
        return load < 0 ? 0 : load;
    }

    /// <summary>
    /// 获取所有通道（用于独立处理每个通道）
    /// </summary>
    public IReadOnlyList<Channel<EventEnvelope>> GetShardedChannels()
    {
        return _channels;
    }

    /// <summary>
    /// 在候选通道集合中选择最空闲通道（并列随机）
    /// </summary>
    public int SelectLeastLoadedChannelId(IReadOnlyList<int> candidateChannelIds)
    {
        if (candidateChannelIds is null || candidateChannelIds.Count == 0)
        {
            throw new ArgumentException("候选通道不能为空。", nameof(candidateChannelIds));
        }

        long minLoad = long.MaxValue;
        int selected = candidateChannelIds[0];
        int tieCount = 0;

        for (int i = 0; i < candidateChannelIds.Count; i++)
        {
            var channelId = candidateChannelIds[i];
            ValidateChannelId(channelId);

            var load = Volatile.Read(ref _channelLoads[channelId]);
            if (load < minLoad)
            {
                minLoad = load;
                selected = channelId;
                tieCount = 1;
                continue;
            }

            if (load == minLoad)
            {
                tieCount++;
                // 蓄水池采样：在负载并列时随机打散
                if (Random.Shared.Next(tieCount) == 0)
                {
                    selected = channelId;
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// 在未指定通道池中选择最空闲通道
    /// 规则：始终包含 0 号通道；1..N-1 中仅包含当前没有显式订阅者占用的通道
    /// </summary>
    public int SelectLeastLoadedUnspecifiedChannelId()
    {
        long minLoad = long.MaxValue;
        int selected = 0;
        int tieCount = 0;

        for (int channelId = 0; channelId < ChannelCount; channelId++)
        {
            if (channelId > 0 && Volatile.Read(ref _explicitChannelSubscriberCounts[channelId]) > 0)
            {
                continue;
            }

            var load = Volatile.Read(ref _channelLoads[channelId]);
            if (load < minLoad)
            {
                minLoad = load;
                selected = channelId;
                tieCount = 1;
                continue;
            }

            if (load == minLoad)
            {
                tieCount++;
                if (Random.Shared.Next(tieCount) == 0)
                {
                    selected = channelId;
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// 创建新通道
    /// </summary>
    private Channel<EventEnvelope> CreateChannel()
    {
        if (_options.ChannelCapacity > 0)
        {
            return Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode = _options.ChannelFullMode,
                SingleReader = true,
                SingleWriter = false
            });
        }

        return Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        foreach (var channel in _channels)
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
