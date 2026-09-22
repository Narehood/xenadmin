using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class ConsolePasteViewModel : ViewModelBase, IDisposable
{
    private readonly ConsolePasteTarget _target;
    private readonly Func<Task<string?>> _readClipboard;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private bool _hasSendResult;

    public ConsolePasteViewModel(ConsolePasteTarget target, Func<Task<string?>> readClipboard)
    {
        _target = target;
        _readClipboard = readClipboard;
    }

    public string Destination => _target.Label;
    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value.Length > ConsolePasteText.MaxLength)
            {
                RejectOversizedEdit();
                OnPropertyChanged();
                return;
            }
            if (SetProperty(ref _text, value))
            {
                _hasSendResult = false;
                AllowEnterAndTab = false;
                OnPropertyChanged(nameof(Summary));
                RefreshValidation();
            }
        }
    }
    [ObservableProperty] private bool _showText;
    [ObservableProperty] private bool _allowEnterAndTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    public string Validation => ConsolePasteText.GetError(Text, AllowEnterAndTab) ?? "Ready. No extra Enter key will be added.";
    public string Summary
    {
        get
        {
            var normalized = ConsolePasteText.Normalize(Text);
            return $"{normalized.Length:N0} characters; {normalized.Count(ch => ch == '\n')} Enter keys; {normalized.Count(ch => ch == '\t')} Tab keys.";
        }
    }
    public bool CanSend => !_disposed && !IsBusy && _target.IsCurrent && ConsolePasteText.GetError(Text, AllowEnterAndTab) == null;
    public bool CanLoad => !_disposed && !IsBusy;

    public void RejectOversizedEdit()
        => Status = $"The edit exceeds {ConsolePasteText.MaxLength:N0} characters. The draft was kept unchanged.";
    partial void OnAllowEnterAndTabChanged(bool value) => RefreshValidation();
    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        LoadClipboardCommand.NotifyCanExecuteChanged();
    }
    private void RefreshValidation()
    {
        OnPropertyChanged(nameof(Validation));
        SendCommand.NotifyCanExecuteChanged();
    }

    public void RefreshTarget()
    {
        if (_disposed) return;
        if (!_target.IsCurrent && !IsBusy && !_hasSendResult)
            Status = "The console changed or disconnected. Close this dialog and reopen Paste text for the intended destination.";
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadClipboard() => ReadClipboard(null, null);

    public Task<int?> PasteClipboardIntoDraft(int selectionStart, int selectionEnd)
        => ReadClipboard(selectionStart, selectionEnd);

    private async Task<int?> ReadClipboard(int? selectionStart, int? selectionEnd)
    {
        if (!CanLoad) return null;
        _hasSendResult = false;
        var draft = Text;
        var start = Math.Clamp(Math.Min(selectionStart ?? 0, selectionEnd ?? 0), 0, draft.Length);
        var end = Math.Clamp(Math.Max(selectionStart ?? 0, selectionEnd ?? 0), 0, draft.Length);
        IsBusy = true;
        Status = "Reading clipboard...";
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = reading;
        try
        {
            var text = await _readClipboard().WaitAsync(reading.Token);
            if (_disposed) return null;
            if (text?.Length > ConsolePasteText.MaxLength)
                Status = $"Clipboard text exceeds {ConsolePasteText.MaxLength:N0} characters. The draft was kept unchanged.";
            else if (selectionStart.HasValue)
            {
                // Intercept native editor paste before TextBox can truncate it.
                if (Text != draft)
                    Status = "The draft changed while reading the clipboard. Nothing was inserted.";
                else if (string.IsNullOrEmpty(text))
                    Status = "The clipboard contains no text. The draft was kept unchanged.";
                else if (draft.Length - (end - start) + text.Length > ConsolePasteText.MaxLength)
                    RejectOversizedEdit();
                else
                {
                    Text = draft.Remove(start, end - start).Insert(start, text);
                    AllowEnterAndTab = false;
                    Status = "Clipboard text inserted. Review the draft before sending.";
                    return start + text.Length;
                }
            }
            else
            {
                Text = text ?? "";
                ShowText = false;
                AllowEnterAndTab = false;
                Status = Text.Length == 0 ? "The clipboard contains no text." : "Clipboard loaded. Text is hidden until you choose to show it.";
            }
        }
        catch (OperationCanceledException) { Status = "Clipboard read stopped. The draft was kept unchanged."; }
        catch
        {
            // Clipboard providers and transports can include sensitive data in exceptions.
            Status = "Could not read clipboard text. The draft was kept unchanged.";
        }
        finally { _operation = null; IsBusy = false; }
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (!CanSend) return;
        IsBusy = true;
        Status = "Sending text...";
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = sending;
        var delivery = new ConsolePasteDelivery();
        var completed = false;
        var progress = new Progress<int>(count =>
        {
            if (!_disposed && ReferenceEquals(_operation, sending))
                Status = $"Sent {count:N0} characters. Stop cannot undo text already sent.";
        });
        try
        {
            await _target.SendAsync(Text, AllowEnterAndTab, progress, sending.Token, delivery);
            completed = true;
            Status = "Text sent. Check the console before continuing; no extra Enter was added.";
        }
        catch (OperationCanceledException)
        {
            Status = SendFailureStatus("Paste stopped", delivery);
        }
        catch
        {
            Status = SendFailureStatus("Paste failed or the console changed", delivery);
        }
        finally
        {
            _operation = null;
            if (completed || delivery.MayHaveSentText || _disposed)
            {
                Text = "";
                ShowText = false;
                AllowEnterAndTab = false;
            }
            _hasSendResult = true;
            IsBusy = false;
        }
    }

    private static string SendFailureStatus(string reason, ConsolePasteDelivery delivery)
    {
        if (!delivery.MayHaveSentText)
            return $"{reason}. Nothing was sent. The draft was kept unchanged.";
        var uncertain = delivery.UnconfirmedWrite ? " The next character may also have reached the console." : "";
        return $"{reason}. Sent {delivery.SentCharacters:N0} characters.{uncertain} Check the console before retrying.";
    }

    [RelayCommand] private void Stop() => _operation?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Text = "";
        ShowText = false;
        AllowEnterAndTab = false;
    }
}
