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

    public Task SendAsync(string text, bool allowEnterAndTab, IProgress<int>? progress, CancellationToken token)
        => SendAsync(text, allowEnterAndTab, progress, token, new ConsolePasteDelivery());

    internal async Task SendAsync(string text, bool allowEnterAndTab, IProgress<int>? progress,
        CancellationToken token, ConsolePasteDelivery delivery)
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
                    delivery.UnconfirmedWrite = true;
                    try
                    {
                        _sendKey(ConsolePasteText.KeySym(normalized[i]));
                    }
                    catch (ConsolePasteUnavailableException)
                    {
                        // The session can change between IsCurrent and its own guard.
                        // That guard can certify that it did not attempt this write.
                        delivery.UnconfirmedWrite = false;
                        throw;
                    }
                    delivery.SentCharacters = i + 1;
                    delivery.UnconfirmedWrite = false;
                    progress?.Report(i + 1);
                    if (i + 1 < normalized.Length)
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

// Written synchronously by the sender and read only after awaiting SendAsync.
// UI progress callbacks are queued and cannot establish whether a write occurred.
internal sealed class ConsolePasteDelivery
{
    public int SentCharacters { get; set; }
    public bool UnconfirmedWrite { get; set; }
    public bool MayHaveSentText => SentCharacters > 0 || UnconfirmedWrite;
}

internal sealed class ConsolePasteUnavailableException : InvalidOperationException
{
    public ConsolePasteUnavailableException() : base("Console changed or disconnected before the write.") { }
}
