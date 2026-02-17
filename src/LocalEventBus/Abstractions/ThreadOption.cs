namespace LocalEventBus.Abstractions;

/// <summary>
/// 订阅者执行线程选项
/// </summary>
public enum ThreadOption
{
    /// <summary>
    /// 在线程池后台线程执行（默认）。
    /// </summary>
    BackgroundThread = 0,

    /// <summary>
    /// 在 UI/Main 线程（SynchronizationContext）执行。
    /// </summary>
    UIThread = 1
}
