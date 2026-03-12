using System.Threading.Channels;

namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件总线配置选项
/// </summary>
public sealed class EventBusOptions
{
    /// <summary>
    /// 通道容量（0 表示无界通道）
    /// </summary>
    public int ChannelCapacity { get; set; } = 0;

    /// <summary>
    /// 通道满时的行为
    /// </summary>
    public BoundedChannelFullMode ChannelFullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    /// 默认订阅者超时时间
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 通道数量（用于并发分流）
    /// <para>默认值: 4</para>
    /// <para>
    /// - 匹配现代 CPU 核心数（4-16 核）
    /// - 2 的幂次（2²），利于编译器优化
    /// 调整建议：
    /// - 低负载（&lt; 1K 事件/秒）：4-8
    /// - 中等负载（1K-10K 事件/秒）：16（默认）
    /// - 高负载（&gt; 10K 事件/秒）：32-64
    /// - 单机串行优先：2（最小值）
    /// </para>
    /// <para>最小值为 2：0 号为未指定通道，1..N-1 为显式通道</para>
    /// </summary>
    public int PartitionCount { get; set; } = 4;

    /// <summary>
    /// 重试选项
    /// </summary>
    public RetryOptions RetryOptions { get; set; } = new();
}
