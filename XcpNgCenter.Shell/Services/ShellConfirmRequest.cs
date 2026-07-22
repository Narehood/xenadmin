namespace XcpNgCenter.Shell.Services;

public sealed class ShellConfirmRequest
{
    public string Title { get; init; } = "Confirm";
    public string Message { get; init; } = string.Empty;
    public string AcceptLabel { get; init; } = "OK";
    public string CancelLabel { get; init; } = "Cancel";
    public bool ShowCancel { get; init; } = true;
}
