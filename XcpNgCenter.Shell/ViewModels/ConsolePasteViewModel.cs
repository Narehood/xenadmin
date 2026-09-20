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

    public ConsolePasteViewModel(ConsolePasteTarget target, Func<Task<string?>> readClipboard)
    {
        _target = target;
        _readClipboard = readClipboard;
    }

    public string Destination => _target.Label;
    [ObservableProperty] private string _text = "";
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

    partial void OnTextChanged(string value)
    {
        AllowEnterAndTab = false;
        OnPropertyChanged(nameof(Summary));
        RefreshValidation();
    }
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
        if (!_target.IsCurrent && !IsBusy)
            Status = "The console changed or disconnected. Close this dialog and reopen Paste text for the intended destination.";
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadClipboard()
    {
        if (!CanLoad) return;
        IsBusy = true;
        Text = "";
        ShowText = false;
        AllowEnterAndTab = false;
        Status = "Reading clipboard...";
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = reading;
        try
        {
            var text = await _readClipboard().WaitAsync(reading.Token);
            if (_disposed) return;
            if (text?.Length > ConsolePasteText.MaxLength)
                Status = $"Clipboard text exceeds {ConsolePasteText.MaxLength:N0} characters. Nothing was loaded.";
            else
            {
                Text = text ?? "";
                Status = Text.Length == 0 ? "The clipboard contains no text." : "Clipboard loaded. Text is hidden until you choose to show it.";
            }
        }
        catch (OperationCanceledException) { Status = "Clipboard read stopped."; }
        catch
        {
            // Clipboard providers and transports can include sensitive data in exceptions.
            Status = "Could not read clipboard text. Try again or enter text below.";
        }
        finally { _operation = null; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (!CanSend) return;
        IsBusy = true;
        Status = "Sending text...";
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = sending;
        var progress = new Progress<int>(count =>
        {
            if (!_disposed && ReferenceEquals(_operation, sending))
                Status = $"Sent {count:N0} characters. Stop cannot undo text already sent.";
        });
        try
        {
            await _target.SendAsync(Text, AllowEnterAndTab, progress, sending.Token);
            Status = "Text sent. Check the console before continuing; no extra Enter was added.";
        }
        catch (OperationCanceledException)
        {
            Status = "Paste stopped. Some text may already have reached the console. Check it before retrying.";
        }
        catch
        {
            Status = "Paste failed or the console changed. Some text may already have been sent. Check the console before retrying.";
        }
        finally
        {
            _operation = null;
            Text = "";
            ShowText = false;
            AllowEnterAndTab = false;
            IsBusy = false;
        }
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
