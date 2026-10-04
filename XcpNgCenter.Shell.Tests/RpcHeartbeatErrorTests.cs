using System.Net;
using System.Reflection;
using XenAdmin.Network;
using XenAPI;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class RpcHeartbeatErrorTests
{
    [Theory]
    [InlineData(HttpRequestError.Unknown, WebExceptionStatus.UnknownError)]
    [InlineData(HttpRequestError.ProxyTunnelError, WebExceptionStatus.RequestProhibitedByProxy)]
    [InlineData(HttpRequestError.UserAuthenticationError, WebExceptionStatus.UnknownError)]
    [InlineData(HttpRequestError.InvalidResponse, WebExceptionStatus.ServerProtocolViolation)]
    [InlineData(HttpRequestError.VersionNegotiationError, WebExceptionStatus.ServerProtocolViolation)]
    public void TransientTransportErrorsGetOneHeartbeatRetry(HttpRequestError kind, WebExceptionStatus expected)
    {
        var error = new HttpRequestException(kind, "Synthetic transport failure", null);
        var translate = typeof(JsonRpcClient).GetMethod("TranslateTransportError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mapped = Assert.IsType<WebException>(translate.Invoke(null, [error]));
        Assert.Equal(expected, mapped.Status);
        var probe = new HeartbeatConnection();
        var heartbeat = new Heartbeat(probe, 5000);
        var fail = typeof(Heartbeat).GetMethod("HandleConnectionLoss", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            fail.Invoke(heartbeat, [mapped]);
            Assert.Equal(0, probe.Interrupts);
            fail.Invoke(heartbeat, [mapped]);
            Assert.Equal(1, probe.Interrupts);
        }
        finally { heartbeat.Stop(); }
    }

    private sealed class HeartbeatConnection : IXenConnection
    {
        public int Interrupts;
        public void Interrupt() { Interrupts++; IsConnected = false; }
        public bool IsConnected { get; private set; } = true;
        public bool ExpectDisruption { get; set; }
        public string Hostname { get; set; } = "synthetic";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool ExpectPasswordIsCorrect { get; set; }
        public int Port { get; set; }
        public string FriendlyName { get; set; } = "synthetic";
        public bool CacheIsPopulated => false;
        public bool SuppressErrors { get; set; }
        public bool PreventResettingPasswordPrompt { get; set; }
        public bool CoordinatorMayChange { get; set; }
        public bool SaveDisconnected { get; set; }
        public string HostnameWithPort => Hostname;
        public bool InProgress => false;
        public string Name => Hostname;
        public TimeSpan ServerTimeOffset { get; set; }
        public Session Session => throw new NotSupportedException();
        public ICache Cache => throw new NotSupportedException();
        public string UriScheme => "https";
        public NetworkCredential NetworkCredential { get; set; } = new();
        public List<string> PoolMembers { get; set; } = [];
        public Session DuplicateSession() => throw new NotSupportedException();
        public Session DuplicateSession(int timeout) => throw new NotSupportedException();
        public Session ElevatedSession(string username, string password) => throw new NotSupportedException();
        public T TryResolveWithTimeout<T>(XenRef<T> reference) where T : XenObject<T> => throw new NotSupportedException();
        public T Resolve<T>(XenRef<T> reference) where T : XenObject<T> => throw new NotSupportedException();
        public List<T> ResolveAll<T>(IEnumerable<XenRef<T>> references) where T : XenObject<T> => throw new NotSupportedException();
        public List<VDI> ResolveAllShownXenModelObjects(List<XenRef<VDI>> references, bool hidden) => throw new NotSupportedException();
        public T WaitForCache<T>(XenRef<T> reference) where T : XenObject<T> => throw new NotSupportedException();
        public T WaitForCache<T>(XenRef<T> reference, Func<bool> cancelling) where T : XenObject<T> => throw new NotSupportedException();
        public void WaitFor(Func<bool> predicate, Func<bool> cancelling) => throw new NotSupportedException();
        public void EndConnect(bool resetState = true, bool exiting = false) { }
        public void Logout() { }
        public void Logout(Session session, bool exiting = false) { }
        public void Dispose() { }
        public int CompareTo(IXenConnection? other) => 0;
#pragma warning disable CS0067 // The probe implements events but never connects or schedules reconnection.
        public event Action<IXenConnection>? CachePopulated;
        public event Action<IXenConnection>? ClearingCache;
        public event Action<IXenConnection>? BeforeConnectionEnd;
        public event Action<IXenConnection>? ConnectionClosed;
        public event Action<IXenConnection>? ConnectionLost;
        public event Action<IXenConnection>? ConnectionReconnecting;
        public event EventHandler<ConnectionResultEventArgs>? ConnectionResult;
        public event Action<IXenConnection>? ConnectionStateChanged;
        public event Action<IXenConnection, string>? ConnectionMessageChanged;
        public event Action<IXenConnection, bool>? BeforeMajorChange;
        public event Action<IXenConnection, bool>? AfterMajorChange;
        public event EventHandler<EventArgs>? XenObjectsUpdated;
#pragma warning restore CS0067
    }
}
