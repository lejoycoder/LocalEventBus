namespace LocalEventBus.Internal;

/// <summary>
/// 订阅令牌
/// </summary>
internal sealed class SubscriptionToken : IDisposable
{
    private Action? _unsubscribe;

    public SubscriptionToken(Action unsubscribe)
    {
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
    }

    public void Dispose()
    {
        var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
        unsubscribe?.Invoke();
    }
}
