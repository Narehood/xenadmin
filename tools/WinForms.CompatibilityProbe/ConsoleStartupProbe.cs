using System.Reflection;

internal sealed partial class LifecycleDesignerProbe
{
    private void CheckConsoleStartup()
    {
        var settings = ClientType("Properties.Settings");
        var field = settings.GetField("_default", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        field.SetValue(null, Activator.CreateInstance(settings));
        try { CheckConsoleStartupCore(); }
        finally { field.SetValue(null, previous); }
    }

    private void CheckConsoleStartupCore()
    {
        using var parent = new ContainerControl();
        var type = ClientType("ConsoleView.VNCGraphicsClient");
        using var client = (Control)Activator.CreateInstance(type, parent)!;
        var vmType = type.GetProperty("SourceVM")!.PropertyType;
        var vm = Activator.CreateInstance(vmType)!;
        vmType.GetProperty("uuid")!.SetValue(vm, "synthetic-console-uuid");
        vmType.GetProperty("name_label")!.SetValue(vm, "Synthetic guest");
        type.GetProperty("SourceVM")!.SetValue(client, vm);

        var begin = type.GetMethod("BeginConnectionAttempt", InstanceMembers)!;
        var guard = (IDisposable)begin.Invoke(client, [TimeSpan.FromMilliseconds(150)])!;
        using var stream = new SilentConsoleStream();
        guard.GetType().GetMethod("AttachTransport")!.Invoke(guard, [stream]);
        Exception? failure = null;
        using var failed = new ManualResetEventSlim();
        Action<object, Exception> onError = (_, error) => { failure = error; failed.Set(); };
        type.GetEvent("ErrorOccurred")!.AddEventHandler(client, onError);
        type.GetMethod("Connect", InstanceMembers)!.Invoke(client, [stream, Array.Empty<char>()]);
        Require(stream.Reading.Wait(TimeSpan.FromSeconds(3)), "WinForms RFB handshake did not start.");
        Require(failed.Wait(TimeSpan.FromSeconds(3)), "WinForms silent RFB handshake did not time out.");
        Require(stream.Closed, "Timed-out WinForms handshake left its stream open.");
        Require(failure is IOException && failure.Message.Contains("handshake"), "WinForms timeout lost its phase-specific message.");

        // A fresh attempt can be cancelled even before transport handoff.
        var second = (IDisposable)begin.Invoke(client, [TimeSpan.FromSeconds(10)])!;
        Require(second != null, "WinForms failed attempt blocked the next startup.");
        using var pending = new SilentConsoleStream();
        second!.GetType().GetMethod("AttachTransport")!.Invoke(second, [pending]);
        var token = (CancellationToken)type.GetProperty("ConnectionToken", InstanceMembers)!.GetValue(client)!;
        type.GetMethod("DisconnectAndDispose")!.Invoke(client, null);
        Require(token.IsCancellationRequested && pending.Closed, "Disposing WinForms console did not cancel pending startup.");
        Require(begin.Invoke(client, [TimeSpan.FromSeconds(10)]) == null, "Disposed WinForms console accepted another attempt.");

        using var connectedClient = (Control)Activator.CreateInstance(type, parent)!;
        type.GetProperty("SourceVM")!.SetValue(connectedClient, vm);
        var connectedGuard = (IDisposable)begin.Invoke(connectedClient, [TimeSpan.FromMilliseconds(500)])!;
        var handshake = System.Text.Encoding.ASCII.GetBytes("RFB 003.003\n").Concat(new byte[]
        {
            0, 0, 0, 1, // Authentication: none.
            0, 2, 0, 2, // Desktop size.
            32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0, // Pixel format.
            0, 0, 0, 0 // Empty desktop name.
        }).ToArray();
        using var idleStream = new SilentConsoleStream(handshake);
        connectedGuard.GetType().GetMethod("AttachTransport")!.Invoke(connectedGuard, [idleStream]);
        var connected = false;
        Exception? connectedFailure = null;
        Action<object, Exception> onConnectedError = (_, error) => connectedFailure = error;
        type.GetEvent("ErrorOccurred")!.AddEventHandler(connectedClient, onConnectedError);
        EventHandler onConnected = (_, _) => connected = true;
        type.GetEvent("ConnectionSuccess")!.AddEventHandler(connectedClient, onConnected);
        type.GetMethod("Connect", InstanceMembers)!.Invoke(connectedClient, [idleStream, Array.Empty<char>()]);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!connected && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Require(connected, "WinForms successful RFB handshake did not reach its UI callback: " + connectedFailure);
        Thread.Sleep(650);
        Require(!idleStream.Closed, "WinForms startup deadline closed a connected idle console.");
        connectedClient.Dispose();
        Require(idleStream.Closed, "WinForms connected console disposal retained its stream.");
    }

    private sealed class SilentConsoleStream(byte[]? prefix = null) : Stream
    {
        private int position;
        private readonly ManualResetEventSlim closed = new();
        public readonly ManualResetEventSlim Reading = new();
        public bool Closed => closed.IsSet;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reading.Set();
            if (prefix != null && position < prefix.Length)
            {
                var available = Math.Min(count, prefix.Length - position);
                Array.Copy(prefix, position, buffer, offset, available);
                position += available;
                return available;
            }
            closed.Wait();
            return 0;
        }
        protected override void Dispose(bool disposing) { closed.Set(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
