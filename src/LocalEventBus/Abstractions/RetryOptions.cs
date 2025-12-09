namespace LocalEventBus.Abstractions;

/// <summary>
/// 重试策略选项
/// </summary>
public sealed class RetryOptions
{
    /// <summary>
    /// 最大重试次数（默认 1 次）
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 1;

    /// <summary>
    /// 初始重试延迟（默认 0.1 秒）
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// 最大重试延迟（默认 3 秒）
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 延迟策略
    /// </summary>
    public RetryDelayStrategy DelayStrategy { get; set; } = RetryDelayStrategy.ExponentialBackoff;

    /// <summary>
    /// 自定义判断是否应该重试的委托
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; set; }
}

/// <summary>
/// 重试延迟策略
/// </summary>
public enum RetryDelayStrategy
{
    /// <summary>
    /// 固定延迟
    /// </summary>
    Fixed,

    /// <summary>
    /// 线性增长
    /// </summary>
    Linear,

    /// <summary>
    /// 指数退避
    /// </summary>
    ExponentialBackoff
}
