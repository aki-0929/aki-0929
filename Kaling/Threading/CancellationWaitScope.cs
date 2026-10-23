namespace Kaling.Threading;

/// <summary>
/// Links one or more <see cref="CancellationToken"/> values for a single wait, then maps
/// <see cref="OperationCanceledException"/> to a specific <see cref="Exception"/> using ordered rules.
/// </summary>
internal sealed class CancellationWaitScope : IDisposable
{
    private readonly Func<Exception?>[] _rules;
    private readonly CancellationTokenSource? _linked;
    private bool _disposed;

    private CancellationWaitScope(CancellationToken waitToken, CancellationTokenSource? linked, Func<Exception?>[] rules)
    {
        WaitToken = waitToken;
        _linked = linked;
        _rules = rules;
    }

    /// <summary>Token passed to <c>WaitAsync</c> / <c>Task.WaitAsync</c> for this scope.</summary>
    public CancellationToken WaitToken { get; }

    /// <summary>
    /// Creates a scope. <paramref name="waitTokens"/> are combined when more than one is supplied.
    /// <paramref name="rules"/> are tried in order; the first rule that returns non-<see langword="null"/>
    /// wins. An empty rule list yields <see cref="OperationCanceledException"/> from <see cref="ToException"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="waitTokens"/> is empty.</exception>
    public static CancellationWaitScope Create(
        ReadOnlySpan<CancellationToken> waitTokens,
        ReadOnlySpan<Func<Exception?>> rules)
    {
        if (waitTokens.IsEmpty)
            throw new ArgumentException("At least one wait token is required.", nameof(waitTokens));

        CancellationTokenSource? linked = null;
        CancellationToken waitToken;
        if (waitTokens.Length == 1)
        {
            waitToken = waitTokens[0];
        }
        else
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(waitTokens.ToArray());
            waitToken = linked.Token;
        }

        return new CancellationWaitScope(waitToken, linked, rules.ToArray());
    }

    /// <summary>
    /// Links <paramref name="lifetime"/> with <paramref name="userToken"/> when it can be canceled;
    /// otherwise waits on <paramref name="lifetime"/> only.
    /// </summary>
    public static CancellationWaitScope Create(
        CancellationToken userToken,
        CancellationToken lifetime,
        ReadOnlySpan<Func<Exception?>> rules)
    {
        return userToken.CanBeCanceled
            ? Create([userToken, lifetime], rules)
            : Create([lifetime], rules);
    }

    /// <summary>
    /// Resolves which exception to throw after a wait was canceled. Rules run in registration order.
    /// </summary>
    public Exception ToException()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var rule in _rules)
        {
            var ex = rule();
            if (ex is not null) return ex;
        }

        return new OperationCanceledException();
    }

    /// <summary>Disposes the linked source created for multiple wait tokens, if any.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _linked?.Dispose();
    }
}
