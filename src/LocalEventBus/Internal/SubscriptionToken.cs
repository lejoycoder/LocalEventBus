namespace LocalEventBus.Internal;

/// <summary>
/// 订阅令牌
/// </summary>
internal sealed class SubscriptionToken : IDisposable
{
    private readonly Action _unsubscribe;
    private int _disposed;

    public SubscriptionToken(Action unsubscribe)
    {
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _unsubscribe();
        }
    }
}
