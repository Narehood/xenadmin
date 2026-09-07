using System;
using System.IO;
using System.Threading;

namespace XenOvf
{
    /// <summary>Allows cancellation while an archive reader consumes skipped file data.</summary>
    internal sealed class CancellableReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _token;

        public CancellableReadStream(Stream inner, CancellationToken token)
        {
            _inner = inner;
            _token = token;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set { _token.ThrowIfCancellationRequested(); _inner.Position = value; }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();
            return _inner.Read(buffer, offset, count);
        }

        public override int ReadByte()
        {
            _token.ThrowIfCancellationRequested();
            return _inner.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _token.ThrowIfCancellationRequested();
            return _inner.Seek(offset, origin);
        }

        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
