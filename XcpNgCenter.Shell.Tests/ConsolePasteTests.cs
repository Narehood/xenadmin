using System.Buffers.Binary;
using System.Reflection;
using XcpNgCenter.Rfb;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ConsolePasteTests
{
    [Theory]
    [InlineData("hello\0world")]
    [InlineData("hello\u001b[31m")]
    [InlineData("hello\bworld")]
    [InlineData("hello\u007fworld")]
    [InlineData("hello\u0085world")]
    [InlineData("hello\u202eworld")]
    [InlineData("hello\u200bworld")]
    [InlineData("caf\u00e9")]
    [InlineData("hello\ud800")]
    public async Task InvalidTextIsRejectedBeforeAnyTransmission(string text)
    {
        var fixture = new PasteFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.SendAsync(text, true, null, default));
        Assert.Empty(fixture.Keys);
        Assert.Equal(0, fixture.Begins);
    }

    [Fact]
    public void SizeLimitRejectsInsteadOfTruncatingAndAllPrintableAsciiIsAccepted()
    {
        Assert.Null(ConsolePasteText.GetError(new string('x', ConsolePasteText.MaxLength), false));
        Assert.NotNull(ConsolePasteText.GetError(new string('x', ConsolePasteText.MaxLength + 1), true));
        Assert.NotNull(ConsolePasteText.GetError("", false));
        Assert.Null(ConsolePasteText.GetError(new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray()), false));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    [InlineData("command\n")]
    public async Task EnterAndTabRequireExplicitConsent(string text)
    {
        var fixture = new PasteFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.SendAsync(text, false, null, default));
        Assert.Empty(fixture.Keys);
        await fixture.Target.SendAsync(text, true, null, default);
        Assert.Equal(ConsolePasteText.Normalize(text).Select(ConsolePasteText.KeySym), fixture.Keys);
    }

    [Fact]
    public async Task NormalizesLineEndingsWithoutAddingEnter()
    {
        var fixture = new PasteFixture();
        await fixture.Target.SendAsync("A!\r\nb\rc\td", true, null, default);
        Assert.Equal(new[] { 65, 33, 0xff0d, 98, 0xff0d, 99, 0xff09, 100 }, fixture.Keys);
        Assert.Equal(1, fixture.Ends);
    }

    [Fact]
    public async Task CancellationStopsAfterCompleteCharacterAndDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new PasteFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Target.SendAsync("secret", false,
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal(new[] { (int)'s' }, fixture.Keys);
        Assert.Equal(1, fixture.Ends);
    }

    [Fact]
    public async Task TargetChangeStopsRemainingText()
    {
        var fixture = new PasteFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.SendAsync("secret", false,
            new InlineProgress(_ => fixture.Current = false), default));
        Assert.Equal(new[] { (int)'s' }, fixture.Keys);
        Assert.Equal(1, fixture.Ends);
    }

    [Fact]
    public async Task StaleTargetAndCancelledConnectionSendNothing()
    {
        var fixture = new PasteFixture { Current = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.SendAsync("secret", false, null, default));
        Assert.Empty(fixture.Keys);
        using var connection = new CancellationTokenSource();
        var target = new ConsolePasteTarget("VM", () => true, () => true, () => { }, _ => throw new Exception("Sent"), connection.Token);
        connection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.SendAsync("secret", false, null, default));
    }

    [Fact]
    public async Task ConcurrentPasteIsRejected()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new PasteFixture();
        var first = fixture.Target.SendAsync(new string('x', 100), false, null, cancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Target.SendAsync("second", false, null, default));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, fixture.Ends);
    }

    [Fact]
    public void WireFormatHasPairedKeysAndModifierReleaseButNoClipboardOrExtraEnter()
    {
        using var stream = new MemoryStream();
        var client = new RfbClient(new Framebuffer(), stream, false);
        client.SendTextKey('A', releaseModifiers: true);
        client.SendTextKey('!');
        var events = DecodeKeys(stream.ToArray());
        Assert.Equal(12, events.Count);
        Assert.All(events.Take(8), key => Assert.False(key.Down));
        Assert.Equal(new[] { (true, 65), (false, 65), (true, 33), (false, 33) }, events.Skip(8));
    }

    [Fact]
    public void WireWriterPropagatesFailuresAndRejectsClosedClient()
    {
        var failing = new FailingStream();
        var client = new RfbClient(new Framebuffer(), failing, false);
        Assert.Throws<IOException>(() => client.SendTextKey('a'));
        Assert.False(failing.CanWrite);
        client.Close();
        Assert.Equal(1, failing.WriteAttempts);
        Assert.Throws<IOException>(() => client.SendTextKey('b'));
        Assert.Equal(1, failing.WriteAttempts);
        using var stream = new MemoryStream();
        var closed = new RfbClient(new Framebuffer(), stream, false);
        closed.Close();
        Assert.Throws<IOException>(() => closed.SendTextKey('a'));
        Assert.Throws<ArgumentOutOfRangeException>(() => closed.SendTextKey(0xff1b));
    }

    [Fact]
    public async Task HostedSessionNeverRetargetsCapturedPasteAfterStopAndReconnect()
    {
        using var session = new HostedConsoleSession();
        using var oldStream = new MemoryStream();
        Attach(session, oldStream, secure: true, generation: 1);
        var target = Assert.IsType<ConsolePasteTarget>(session.CapturePasteTarget());
        session.Stop();
        using var replacement = new MemoryStream();
        Attach(session, replacement, secure: true, generation: 3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.SendAsync("secret", false, null, default));
        Assert.Empty(oldStream.ToArray());
        Assert.Empty(replacement.ToArray());
        await session.CapturePasteTarget()!.SendAsync("ok", false, null, default);
        Assert.Equal(new[] { (true, 111), (false, 111), (true, 107), (false, 107) }, DecodeKeys(replacement.ToArray()).Skip(8));
    }

    [Fact]
    public void HostedSessionRequiresSecureConnectedTransportAndIgnoresStaleStatus()
    {
        using var session = new HostedConsoleSession();
        Assert.Null(session.CapturePasteTarget());
        using var stream = new MemoryStream();
        Attach(session, stream, secure: false, generation: 7);
        Assert.False(session.CanPaste);
        Assert.Null(session.CapturePasteTarget());
        typeof(HostedConsoleSession).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [6, "stale", false]);
        Assert.True(session.IsConnected);
        session.Stop();
        typeof(HostedConsoleSession).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [7, "stale", true]);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task HostedSessionSuppressesOtherInputDuringPaste()
    {
        using var session = new HostedConsoleSession();
        using var stream = new MemoryStream();
        Attach(session, stream, secure: true, generation: 1);
        await session.CapturePasteTarget()!.SendAsync("ab", false, new InlineProgress(_ =>
        {
            Assert.False(session.CanPaste);
            Assert.Null(session.CapturePasteTarget());
            session.SendKey(true, 'x');
            session.SendPointer(1, 0, 0);
            session.SendPointerWheel(0, 0, 1);
        }), default);
        Assert.True(session.CanPaste);
        Assert.Equal(new[] { (true, 97), (false, 97), (true, 98), (false, 98) }, DecodeKeys(stream.ToArray()).Skip(8));
    }

    [Fact]
    public async Task HostedSessionDropsTransportOnWriteFailure()
    {
        using var session = new HostedConsoleSession();
        Attach(session, new FailingStream(), secure: true, generation: 1);
        await Assert.ThrowsAsync<IOException>(() => session.CapturePasteTarget()!.SendAsync("secret", false, null, default));
        Assert.False(session.IsConnected);
        Assert.Null(session.CapturePasteTarget());
    }

    [Fact]
    public async Task ClipboardReadsAreExplicitAndSendingUsesReviewedSnapshotThenClearsDraft()
    {
        var reads = 0;
        var clipboard = "abc";
        var fixture = new PasteFixture();
        using var vm = new ConsolePasteViewModel(fixture.Target, () => { reads++; return Task.FromResult<string?>(clipboard); });
        Assert.Equal(0, reads);
        await vm.LoadClipboardCommand.ExecuteAsync(null);
        Assert.False(vm.ShowText);
        Assert.Equal("abc", vm.Text);
        clipboard = "different";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(1, reads);
        Assert.Equal(new[] { 97, 98, 99 }, fixture.Keys);
        Assert.Equal("", vm.Text);
        Assert.False(vm.ShowText);
        Assert.False(vm.AllowEnterAndTab);
        Assert.Contains("Text sent", vm.Status);
    }

    [Fact]
    public async Task DraftChangesResetAcknowledgementAndOversizedClipboardIsNotTruncated()
    {
        var fixture = new PasteFixture();
        using var vm = new ConsolePasteViewModel(fixture.Target, () => Task.FromResult<string?>(new string('s', 4097)));
        vm.Text = "command\n";
        Assert.False(vm.CanSend);
        vm.AllowEnterAndTab = true;
        Assert.True(vm.CanSend);
        vm.Text = "different\n";
        Assert.False(vm.CanSend);
        await vm.LoadClipboardCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Text);
        Assert.Contains("exceeds", vm.Status);
        Assert.Empty(fixture.Keys);
    }

    [Fact]
    public async Task ClosedDialogDoesNotKeepLateClipboardResult()
    {
        var clipboard = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new PasteFixture();
        var vm = new ConsolePasteViewModel(fixture.Target, () => clipboard.Task);
        var read = vm.LoadClipboardCommand.ExecuteAsync(null);
        vm.Dispose();
        clipboard.TrySetResult("secret");
        await read;
        Assert.Equal("", vm.Text);
        Assert.False(vm.CanSend);
        Assert.False(vm.CanLoad);
    }

    [Fact]
    public async Task StopCancelsPendingClipboardRead()
    {
        var clipboard = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new ConsolePasteViewModel(new PasteFixture().Target, () => clipboard.Task);
        var read = vm.LoadClipboardCommand.ExecuteAsync(null);
        vm.StopCommand.Execute(null);
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        clipboard.SetResult("late secret");
        Assert.Equal("", vm.Text);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ClosingWhileSendingCancelsAndClearsTheDraft()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new List<int>();
        var target = new ConsolePasteTarget("VM", () => true, () => true, () => { }, key =>
        {
            keys.Add(key);
            sent.TrySetResult();
        }, default);
        var vm = new ConsolePasteViewModel(target, () => Task.FromResult<string?>(null));
        vm.Text = new string('s', 4096);
        var sending = vm.SendCommand.ExecuteAsync(null);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(keys.Count, 1, 4095);
        Assert.Equal("", vm.Text);
        Assert.False(vm.CanSend);
        Assert.Contains("stopped", vm.Status);
    }

    [Fact]
    public async Task ExceptionsDoNotExposeClipboardOrTransportData()
    {
        const string secret = "sensitive-clipboard-value";
        var target = new ConsolePasteTarget("VM", () => true, () => true, () => { }, _ => throw new IOException(secret), default);
        using var vm = new ConsolePasteViewModel(target, () => throw new IOException(secret));
        await vm.LoadClipboardCommand.ExecuteAsync(null);
        Assert.DoesNotContain(secret, vm.Status);
        vm.Text = secret;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.DoesNotContain(secret, vm.Status);
        Assert.Equal("", vm.Text);
        Assert.Contains("Paste failed", vm.Status);
    }

    private sealed class PasteFixture
    {
        public bool Current = true;
        private bool _busy;
        public int Begins;
        public int Ends;
        public List<int> Keys { get; } = [];
        public ConsolePasteTarget Target { get; }
        public PasteFixture() => Target = new ConsolePasteTarget("VM on server", () => Current,
            () => { if (_busy) return false; _busy = true; Begins++; return true; },
            () => { _busy = false; Ends++; }, Keys.Add, default);
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static void Attach(HostedConsoleSession session, Stream stream, bool secure, int generation)
    {
        void Set(string name, object value) => typeof(HostedConsoleSession)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, value);
        Set("_client", new RfbClient(new Framebuffer(), stream, false));
        Set("_cts", new CancellationTokenSource());
        Set("_secureTransport", secure);
        Set("_generation", generation);
        typeof(HostedConsoleSession).GetProperty(nameof(HostedConsoleSession.IsConnected))!.SetValue(session, true);
    }

    private static List<(bool Down, int Key)> DecodeKeys(byte[] wire)
    {
        Assert.Equal(0, wire.Length % 8);
        var keys = new List<(bool, int)>();
        for (var i = 0; i < wire.Length; i += 8)
        {
            Assert.Equal(4, wire[i]);
            Assert.Equal(0, wire[i + 2]);
            Assert.Equal(0, wire[i + 3]);
            keys.Add((wire[i + 1] == 1, BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(i + 4, 4))));
        }
        return keys;
    }

    private sealed class FailingStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            throw new IOException("write failed");
        }
    }

    private sealed class Framebuffer : IRfbFramebuffer
    {
        public string VmName => "test";
        public string Uuid => "test";
        public void Bell() { }
        public void CopyRectangle(int x, int y, int width, int height, int dx, int dy) { }
        public void CutText(string text) => throw new Exception("Unexpected clipboard synchronization");
        public void DesktopSize(int width, int height) { }
        public void DrawImage(byte[] bgra, int offset, int stride, int x, int y, int width, int height) { }
        public void FillRectangle(int x, int y, int width, int height, RfbColor color) { }
        public void FrameBufferUpdate() { }
        public void SetCursor(byte[] bgra, int offset, int stride, int hotspotX, int hotspotY, int width, int height) { }
    }
}
