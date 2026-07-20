/* Copyright (c) Cloud Software Group, Inc.
 *
 * Redistribution and use in source and binary forms,
 * with or without modification, are permitted provided
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer in the documentation and/or other
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System.Text;

namespace XcpNgCenter.Rfb;

/// <summary>Buffered big-endian helper over an RFB TCP/HTTP CONNECT stream.</summary>
public sealed class RfbStream
{
    private const int MaxStringLength = 1 << 16;
    private const int MaxClipboardSize = 1024 * 1024;

    private readonly Stream _inStream;
    private readonly Stream _outStream;
    private readonly byte[] _readbuf = new byte[4];
    private readonly byte[] _writebuf = new byte[4];
    private readonly byte[] _zerobuf = new byte[4];

    public RfbStream(Stream stream)
    {
        _outStream = new BufferedStream(stream, 1024);
        _inStream = new BufferedStream(stream, 65536);
    }

    public void WritePadding(int n) => _outStream.Write(_zerobuf, 0, n);

    public void WriteFlag(bool v) => _outStream.WriteByte(v ? (byte)1 : (byte)0);

    public void WriteInt8(int v) => _outStream.WriteByte((byte)v);

    public void WriteInt16(int v)
    {
        _writebuf[0] = (byte)((v >> 8) & 0xff);
        _writebuf[1] = (byte)(v & 0xff);
        _outStream.Write(_writebuf, 0, 2);
    }

    public void WriteInt32(int v)
    {
        _writebuf[0] = (byte)((v >> 24) & 0xff);
        _writebuf[1] = (byte)((v >> 16) & 0xff);
        _writebuf[2] = (byte)((v >> 8) & 0xff);
        _writebuf[3] = (byte)(v & 0xff);
        _outStream.Write(_writebuf, 0, 4);
    }

    public void WriteString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var size = Math.Min(bytes.Length, MaxClipboardSize);
        WriteInt32(size);
        _outStream.Write(bytes, 0, size);
    }

    public void ReadPadding(int n) => _ = _inStream.Read(_readbuf, 0, n);

    public void ReadFully(byte[] b, int off, int len)
    {
        var n = 0;
        while (n < len)
        {
            var count = _inStream.Read(b, off + n, len - n);
            if (count <= 0)
                throw new EndOfStreamException();
            n += count;
        }
    }

    public bool ReadFlag() => ReadCard8() != 0;

    public int ReadCard8()
    {
        var v = _inStream.ReadByte();
        if (v < 0)
            throw new EndOfStreamException();
        return v;
    }

    public int ReadCard16()
    {
        var b1 = ReadCard8();
        var b0 = ReadCard8();
        return (short)((b1 << 8) | b0);
    }

    public int ReadCard32()
    {
        var b3 = ReadCard8();
        var b2 = ReadCard8();
        var b1 = ReadCard8();
        var b0 = ReadCard8();
        return (b3 << 24) | (b2 << 16) | (b1 << 8) | b0;
    }

    public string ReadString()
    {
        var length = ReadCard32();
        if (length < 0 || length >= MaxStringLength)
            throw new IOException("Invalid string length: " + length);
        var buffer = new byte[length];
        ReadFully(buffer, 0, length);
        var chars = new char[length];
        Encoding.ASCII.GetDecoder().GetChars(buffer, 0, length, chars, 0);
        return new string(chars);
    }

    public void Flush() => _outStream.Flush();

    public void Write(byte[] data, int offset, int count) => _outStream.Write(data, offset, count);

    public void Close() => _outStream.Close();
}
