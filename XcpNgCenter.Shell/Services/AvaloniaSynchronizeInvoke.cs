using System.ComponentModel;
using Avalonia.Threading;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Bridges XenModel's <see cref="ISynchronizeInvoke"/> expectation to Avalonia's UI dispatcher.
/// </summary>
public sealed class AvaloniaSynchronizeInvoke : ISynchronizeInvoke
{
    private readonly Dispatcher _dispatcher;

    public AvaloniaSynchronizeInvoke(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool InvokeRequired => !_dispatcher.CheckAccess();

    public IAsyncResult BeginInvoke(Delegate method, object?[]? args)
    {
        var result = new DispatcherAsyncResult();
        _dispatcher.Post(() =>
        {
            try
            {
                result.ReturnValue = method.DynamicInvoke(args);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }
            finally
            {
                result.Complete();
            }
        });
        return result;
    }

    public object? EndInvoke(IAsyncResult result)
    {
        if (result is not DispatcherAsyncResult asyncResult)
            throw new ArgumentException("Invalid async result.", nameof(result));

        asyncResult.AsyncWaitHandle.WaitOne();
        if (asyncResult.Error != null)
            throw asyncResult.Error;
        return asyncResult.ReturnValue;
    }

    public object? Invoke(Delegate method, object?[]? args)
    {
        if (!InvokeRequired)
            return method.DynamicInvoke(args);

        object? returnValue = null;
        Exception? error = null;
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                returnValue = method.DynamicInvoke(args);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }).Wait();

        if (error != null)
            throw error;
        return returnValue;
    }

    private sealed class DispatcherAsyncResult : IAsyncResult
    {
        private readonly ManualResetEventSlim _done = new(false);

        public object? ReturnValue { get; set; }
        public Exception? Error { get; set; }
        public object? AsyncState => null;
        public WaitHandle AsyncWaitHandle => _done.WaitHandle;
        public bool CompletedSynchronously => false;
        public bool IsCompleted => _done.IsSet;

        public void Complete() => _done.Set();
    }
}
