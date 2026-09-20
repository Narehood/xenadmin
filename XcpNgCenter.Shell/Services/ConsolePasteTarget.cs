namespace XcpNgCenter.Shell.Services;

/// <summary>A paste is bound to one transport generation, never the current selection.</summary>
public sealed class ConsolePasteTarget
{
    private readonly Func<bool> _isCurrent;
    private readonly Func<bool> _begin;
    private readonly Action _end;
    private readonly Action<int> _sendKey;
    private readonly CancellationToken _connectionToken;

    internal ConsolePasteTarget(string label, Func<bool> isCurrent, Func<bool> begin,
        Action end, Action<int> sendKey, CancellationToken connectionToken)
    {
        Label = label;
        _isCurrent = isCurrent;
        _begin = begin;
        _end = end;
        _sendKey = sendKey;
        _connectionToken = connectionToken;
    }

    public string Label { get; }
    public bool IsCurrent => !_connectionToken.IsCancellationRequested && _isCurrent();

    public async Task SendAsync(string text, bool allowEnterAndTab, IProgress<int>? progress, CancellationToken token)
    {
        var error = ConsolePasteText.GetError(text, allowEnterAndTab);
        if (error != null)
            throw new InvalidOperationException(error);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _connectionToken);
        linked.Token.ThrowIfCancellationRequested();
        if (!IsCurrent || !_begin())
            throw new InvalidOperationException("The console changed, disconnected, or is already receiving text. Reopen Paste text.");
        try
        {
            // Network writes must not block the desktop. A down/up pair is indivisible;
            // cancellation takes effect between characters and never retries a prefix.
            await Task.Run(async () =>
            {
                var normalized = ConsolePasteText.Normalize(text);
                for (var i = 0; i < normalized.Length; i++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!IsCurrent)
                        throw new InvalidOperationException("The console changed or disconnected.");
                    _sendKey(ConsolePasteText.KeySym(normalized[i]));
                    progress?.Report(i + 1);
                    await Task.Delay(10, linked.Token).ConfigureAwait(false);
                }
            }, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _end();
        }
    }
}
