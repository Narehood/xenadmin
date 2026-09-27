using System.Buffers.Binary;
using System.Text;
using XcpNgCenter.Rfb;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class RfbReconnectTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FragmentedPaddingConsumesEveryByte(int fragmentSize)
    {
        using var transport = new ScriptedTransport([0, 0, 0, 0x7a], fragmentSize);
        var stream = new RfbStream(transport);
        stream.ReadPadding(3);
        Assert.Equal(0x7a, stream.ReadCard8());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TruncatedPaddingReportsDisconnection(int availableBytes)
    {
        using var transport = new ScriptedTransport(new byte[availableBytes], 1);
        var stream = new RfbStream(transport);
        Assert.Throws<EndOfStreamException>(() => stream.ReadPadding(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(65536)]
    public async Task ReconnectedConsoleParsesFragmentedHandshakeResizeClipboardAndFrame(int fragmentSize)
    {
        // A new RFB stream after a guest restart: handshake, boot-resolution
        // change, a desktop-name/clipboard message and the first visible pixel.
        using var transport = new ScriptedTransport(RebootFrames(), fragmentSize);
        var framebuffer = new RecordingFramebuffer();
        var client = new RfbClient(framebuffer, transport, startPaused: false);
        var completed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;
        client.ConnectionSuccess += (_, _) => connected++;
        client.ErrorOccurred += (_, error) => completed.TrySetResult(error);
        try
        {
            client.Connect([]);
            var error = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsType<EndOfStreamException>(error);
            Assert.Equal(1, connected);
            Assert.Equal(new[] { (2, 2), (1, 1) }, framebuffer.Sizes);
            Assert.Equal("ready", framebuffer.Clipboard);
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0xff }, framebuffer.Pixel);
            Assert.Equal(2, framebuffer.Updates);

            // The resize must ask for a full frame at the new dimensions.
            var requests = DecodeUpdateRequests(transport.Written);
            Assert.Equal(new[] { (false, 2, 2), (false, 1, 1), (true, 1, 1), (true, 1, 1) }, requests);
        }
        finally
        {
            client.Close();
        }
        Assert.True(transport.Closed);
    }

    [Fact]
    public async Task ClosingBlockedConsoleReleasesReadWithoutASecondError()
    {
        using var transport = new ScriptedTransport(Handshake(), 1, blockAtEnd: true);
        var client = new RfbClient(new RecordingFramebuffer(), transport, startPaused: false);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        client.ConnectionSuccess += (_, _) => connected.TrySetResult();
        client.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);
        try
        {
            client.Connect([]);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Close();
            await transport.ReadReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(transport.Closed);
            Assert.Equal(0, Volatile.Read(ref errors));
        }
        finally
        {
            client.Close();
        }
    }

    private static byte[] Handshake()
    {
        using var script = new MemoryStream();
        script.Write(Encoding.ASCII.GetBytes("RFB 003.003\n"));
        Put32(script, 1); // No RFB authentication inside the hosted tunnel.
        Put16(script, 2);
        Put16(script, 2);
        script.Write([32, 24, 0, 1]);
        Put16(script, 255);
        Put16(script, 255);
        Put16(script, 255);
        script.Write([16, 8, 0, 0, 0, 0]); // Shifts and three padding bytes.
        PutText(script, "boot");
        return script.ToArray();
    }

    private static byte[] RebootFrames()
    {
        using var script = new MemoryStream();
        script.Write(Handshake());
        Rectangle(script, -223); // DesktopSize pseudo-encoding.
        script.Write([3, 0, 0, 0]); // ServerCutText plus padding.
        PutText(script, "ready");
        Rectangle(script, 0); // RAW at the new resolution.
        script.Write([0x12, 0x34, 0x56, 0]);
        return script.ToArray();
    }

    private static void Rectangle(Stream script, int encoding)
    {
        script.Write([0, 0]);
        Put16(script, 1);
        Put16(script, 0);
        Put16(script, 0);
        Put16(script, 1);
        Put16(script, 1);
        Put32(script, encoding);
    }

    private static void PutText(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Put32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void Put16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static void Put32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static List<(bool Incremental, int Width, int Height)> DecodeUpdateRequests(byte[] bytes)
    {
        Assert.Equal("RFB 003.003\n", Encoding.ASCII.GetString(bytes, 0, 12));
        Assert.Equal(1, bytes[12]);
        Assert.Equal(0, bytes[13]); // SetPixelFormat: 20 bytes.
        var offset = 33;
        Assert.Equal(2, bytes[offset]); // SetEncodings.
        offset += 4 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2)) * 4;
        var requests = new List<(bool, int, int)>();
        while (offset < bytes.Length)
        {
            Assert.Equal(3, bytes[offset]);
            requests.Add((bytes[offset + 1] == 1,
                BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 6, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 8, 2))));
            offset += 10;
        }
        Assert.Equal(bytes.Length, offset);
        return requests;
    }

    private sealed class RecordingFramebuffer : IRfbFramebuffer
    {
        public string VmName => "isolated-reboot-fixture";
        public string Uuid => "fixture";
        public List<(int, int)> Sizes { get; } = [];
        public string? Clipboard { get; private set; }
        public byte[]? Pixel { get; private set; }
        public int Updates { get; private set; }
        public void Bell() { }
        public void CopyRectangle(int x, int y, int width, int height, int dx, int dy) { }
        public void CutText(string text) => Clipboard = text;
        public void DesktopSize(int width, int height) => Sizes.Add((width, height));
        public void DrawImage(byte[] bgra, int offset, int stride, int x, int y, int width, int height)
            => Pixel = bgra.AsSpan(offset, 4).ToArray();
        public void FillRectangle(int x, int y, int width, int height, RfbColor color) { }
        public void FrameBufferUpdate() => Updates++;
        public void SetCursor(byte[] bgra, int offset, int stride, int hotspotX, int hotspotY, int width, int height) { }
    }

    private sealed class ScriptedTransport(byte[] input, int fragmentSize, bool blockAtEnd = false) : Stream
    {
        private readonly MemoryStream _input = new(input);
        private readonly MemoryStream _output = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed => _closed.Task.IsCompleted;
        public byte[] Written => _output.ToArray();
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => !Closed;
        public override bool CanWrite => !Closed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Closed, this);
            var read = _input.Read(buffer, offset, Math.Min(count, fragmentSize));
            if (read != 0 || !blockAtEnd)
                return read;
            ReadBlocked.TrySetResult();
            try
            {
                _closed.Task.GetAwaiter().GetResult();
                throw new ObjectDisposedException(nameof(ScriptedTransport));
            }
            finally
            {
                ReadReleased.TrySetResult();
            }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(Closed, this);
            _output.Write(buffer, offset, count);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) => _closed.TrySetResult();
    }
}
