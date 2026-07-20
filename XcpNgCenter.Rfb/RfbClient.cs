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

/* Extracted from XenAdmin/VNC/VNCStream.cs for the Avalonia shell.
 * Graphics callbacks are buffer-oriented (IRfbFramebuffer) instead of System.Drawing.
 */

using System;
using System.Threading;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace XcpNgCenter.Rfb
{
    public sealed class RfbClient
    {
        private const int RAW_ENCODING = 0;
        private const int COPY_RECTANGLE_ENCODING = 1;
        private const int RRE_ENCODING = 2;
        private const int CORRE_ENCODING = 4;
        private const int HEXTILE_ENCODING = 5;
        private const int CURSOR_PSEUDO_ENCODING = -239;
        private const int DESKTOP_SIZE_PSEUDO_ENCODING = -223;
        private const int XENCENTER_ENCODING = -254;
        private const int QEMU_EXT_KEY_ENCODING = -258;

        private const int SET_PIXEL_FORMAT = 0;
        private const int SET_ENCODINGS = 2;
        private const int FRAMEBUFFER_UPDATE_REQUEST = 3;
        private const int KEY_EVENT = 4;
        private const int KEY_SCAN_EVENT = 254;
        private const int QEMU_MSG = 255;
        private const int POINTER_EVENT = 5;
        private const int CLIENT_CUT_TEXT = 6;

        private const int RAW_SUBENCODING = 1;
        private const int BACKGROUND_SPECIFIED_SUBENCODING = 2;
        private const int FOREGROUND_SPECIFIED_SUBENCODING = 4;
        private const int ANY_SUBRECTS_SUBENCODING = 8;
        private const int SUBRECTS_COLORED_SUBENCODING = 16;

        private const int FRAME_BUFFER_UPDATE = 0;
        private const int BELL = 2;
        private const int SERVER_CUT_TEXT = 3;

        private const int QEMU_EXT_KEY_EVENT = 0;

        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(RfbClient));

        private Thread? thread;

        #region Current color properties

        private int bitsPerPixel;
        private int bytesPerPixel;
        private int depth;
        private bool bigEndian;
        private bool trueColor;
        private int redMax;
        private int greenMax;
        private int blueMax;
        private int redMaxPlus1;
        private int greenMaxPlus1;
        private int blueMaxPlus1;
        private int redMaxOver2;
        private int greenMaxOver2;
        private int blueMaxOver2;
        private int redShift;
        private int greenShift;
        private int blueShift;
        private bool rgb565;

        #endregion

        /// <summary>
        /// This event will be fired when an error occurs.  The helper thread is guaranteed to be
        /// closing down at this point.
        /// </summary>
        public event Action<object,Exception>? ErrorOccurred;

        public event EventHandler? ConnectionSuccess;

        /// <summary>
        /// The encodings used.  Note that these are ordered: preferred encoding first.
        /// </summary>
        private static readonly int[] encodings = {
            CORRE_ENCODING,
            RRE_ENCODING,
	        COPY_RECTANGLE_ENCODING,
	        RAW_ENCODING,
	        CURSOR_PSEUDO_ENCODING,
	        DESKTOP_SIZE_PSEUDO_ENCODING,
            XENCENTER_ENCODING,
            QEMU_EXT_KEY_ENCODING
	    };

        private readonly IRfbFramebuffer client;

        private readonly RfbStream stream;

        private readonly object writeLock = new object();
        private readonly object pauseMonitor = new object();

        private volatile bool running = true;
        private bool paused;

        private int _width;
        private int _height;

        private bool _incremental;
        private bool qemu_ext_key_encoding;

        private byte[] _data = new byte[1228800]; //640*480*32bpp
        private byte[]? data_8bpp;

        private readonly long imageUpdateThreshold;

        public readonly object updateMonitor = new object(); 

        [System.Diagnostics.CodeAnalysis.SuppressMessage("csharpsquid",
            "S5547:Cipher algorithms should be robust",
            Justification = "Needed by the server side.")]
        private readonly DES des = CreateDes();

        private static DES CreateDes()
        {
            var algorithm = DES.Create();
            algorithm.Padding = PaddingMode.None;
            algorithm.Mode = CipherMode.ECB;
            return algorithm;
        }

        public RfbClient(IRfbFramebuffer client, Stream stream, bool startPaused)
        {
            this.client = client;
            this.stream = new RfbStream(stream);
            paused = startPaused;

            imageUpdateThreshold = System.Diagnostics.Stopwatch.Frequency / 3;
        }

        public void Connect(char[] password)
        {
            System.Diagnostics.Trace.Assert(thread == null);

            thread = new Thread(Run)
            {
                Name = $"VNC connection to {client.VmName} - {client.Uuid}",
                IsBackground = true
            };
            thread.Start(password);
        }

        private void CheckProtocolVersion()
        {
            byte[] buffer = new byte[12];
            stream.ReadFully(buffer, 0, 12);
            char[] chars = new char[12];
            Encoding.ASCII.GetDecoder().GetChars(buffer, 0, 12, chars, 0);
            String s = new String(chars);
            Regex regex = new Regex("RFB ([0-9]{3})\\.([0-9]{3})\n");
            Match match = regex.Match(s);
            if (!match.Success)
                throw new RfbException("Unexpected protocol version " + s);

            int major = Int32.Parse(match.Groups[1].Value);
            int minor = Int32.Parse(match.Groups[2].Value);

            if (major < 3)
                throw new RfbException($"Unsupported protocol version {major}.{minor}");
        }

        private void SendProtocolVersion()
        {
            lock (writeLock)
            {
                byte[] bytes = Encoding.ASCII.GetBytes("RFB 003.003\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private void ReadPixelFormat()
        {
            bitsPerPixel = stream.ReadCard8();
            depth = stream.ReadCard8();
            bigEndian = stream.ReadFlag();
            trueColor = stream.ReadFlag();
            redMax = stream.ReadCard16();
            greenMax = stream.ReadCard16();
            blueMax = stream.ReadCard16();
            redShift = stream.ReadCard8();
            greenShift = stream.ReadCard8();
            blueShift = stream.ReadCard8();
            stream.ReadPadding(3);
            Log.Debug("readPixelFormat " + bitsPerPixel + " " + depth);
        }

        private void WritePixelFormat()
        {
            Log.Debug("writePixelFormat " + bitsPerPixel + " " + depth);
            stream.WriteInt8(SET_PIXEL_FORMAT);
            stream.WritePadding(3);

            stream.WriteInt8(bitsPerPixel);
            stream.WriteInt8(depth);
            stream.WriteFlag(bigEndian);
            stream.WriteFlag(trueColor);
            stream.WriteInt16(redMax);
            stream.WriteInt16(greenMax);
            stream.WriteInt16(blueMax);
            stream.WriteInt8(redShift);
            stream.WriteInt8(greenShift);
            stream.WriteInt8(blueShift);

            stream.WritePadding(3);
        }

        private void Force32bpp()
        {
            Log.Debug("force32bpp()");

            bitsPerPixel = 32;
            depth = 24;
            trueColor = true;
            redMax = 255;
            greenMax = 255;
            blueMax = 255;
            redShift = 16;
            greenShift = 8;
            blueShift = 0;

            // Note that we keep the endian value from the server.

            SetupPixelFormat();

            lock (writeLock)
            {
                WritePixelFormat();
            }
        }


        private void SetupPixelFormat()
        {
            Log.Debug("setupPixelFormat(" + bitsPerPixel + ")");

            bytesPerPixel = bitsPerPixel >> 3;

            redMaxPlus1 = redMax + 1;
            greenMaxPlus1 = greenMax + 1;
            blueMaxPlus1 = blueMax + 1;

            redMaxOver2 = redMax >> 1;
            greenMaxOver2 = greenMax >> 1;
            blueMaxOver2 = blueMax >> 1;

            if (bitsPerPixel == 32 || bitsPerPixel == 8)
            {
                // Client always receives BGRA32.
            }
            else if (bitsPerPixel == 16)
            {
                rgb565 = redShift == 11;
            }
            else
            {
                throw new IOException("unexpected bits per pixel: " + bitsPerPixel);
            }
        }

        private void WriteSetEncodings()
        {
            Log.Debug("writeSetEncodings");
            stream.WriteInt8(SET_ENCODINGS);
            stream.WritePadding(1);
            stream.WriteInt16(encodings.Length);
            foreach (var encoding in encodings)
                stream.WriteInt32(encoding);
        }

        private void WriteFramebufferUpdateRequest(int x, int y, int width, int height, bool incremental)
        {
            stream.WriteInt8(FRAMEBUFFER_UPDATE_REQUEST);
            stream.WriteFlag(incremental);
            stream.WriteInt16(x);
            stream.WriteInt16(y);
            stream.WriteInt16(width);
            stream.WriteInt16(height);
        }

        private void AuthenticationExchange(char[] password)
        {
            Log.Debug("authenticationExchange");

            int scheme = stream.ReadCard32();

            switch (scheme)
            {
                case 0:
                    var reason = stream.ReadString();
                    throw new RfbException("connection failed: " + reason);
                case 1:
                    // no authentication needed
                    break;
                case 2:
                    PasswordAuthentication(password);
                    break;
                default:
                    throw new RfbException("unexpected authentication scheme: " + scheme);
            }
        }

        private void PasswordAuthentication(char[] password)
        {
            byte[] keyBytes = new byte[8];
            for (int i = 0; i < 8 && i < password.Length; ++i)
                keyBytes[i] = Reverse((byte)password[i]);

            ICryptoTransform cipher = des.CreateEncryptor(keyBytes, null);

            byte[] challenge = new byte[16];
            stream.ReadFully(challenge, 0, 16);

            byte[] response = cipher.TransformFinalBlock(challenge, 0, 16);

            stream.Write(response, 0, 16);
            stream.Flush();

            int status = stream.ReadCard32();

            switch (status)
            {
                case 0:
                    break;
                case 1:
                case 2:
                    throw new RfbAuthenticationException();
                default:
                    throw new RfbException("Bad Authentication Response");
            }
        }

        private static byte Reverse(byte v)
        {
            byte r = 0;
            if ((v & 0x01) != 0) r |= 0x80;
            if ((v & 0x02) != 0) r |= 0x40;
            if ((v & 0x04) != 0) r |= 0x20;
            if ((v & 0x08) != 0) r |= 0x10;
            if ((v & 0x10) != 0) r |= 0x08;
            if ((v & 0x20) != 0) r |= 0x04;
            if ((v & 0x40) != 0) r |= 0x02;
            if ((v & 0x80) != 0) r |= 0x01;
            return r;
        }

        private void InitializeClient()
        {
            Log.Debug("clientInitialisation");
            lock (writeLock)
            {
                stream.WriteFlag(true); // shared
                stream.Flush();
            }
        }

        private void InitializateServer()
        {
            Log.Debug("serverInitialisation");
            int width = stream.ReadCard16();
            int height = stream.ReadCard16();

            ReadPixelFormat();

            stream.ReadString(); /* The desktop name -- we don't care. */

            if (trueColor)
            {
                SetupPixelFormat();

                lock (writeLock)
                {
                    WritePixelFormat();
                }
            }
            else
            {
                Force32bpp();
            }

            DesktopSize(width, height);

            lock (writeLock)
            {
                WriteSetEncodings();
            }
        }

        /**
         * Expects to be lock on writeLock.
         */
        private void WriteKey(int command, bool down, int key)
        {
            stream.WriteInt8(command); //Send Scancodes
            stream.WriteFlag(down);
            stream.WritePadding(2);
            stream.WriteInt32(key);
        }

        private void WriteQemuExtKey(int command, bool down, int key, int sym)
        {
            stream.WriteInt8(command);
            stream.WriteInt8(QEMU_EXT_KEY_EVENT);
            stream.WritePadding(1);
            stream.WriteFlag(down);
            stream.WriteInt32(sym);
            stream.WriteInt32(key);
        }

        /**
         * use_qemu_ext_key_encoding: Dictates if we want to use QEMU_EXT_KEY encoding.
         *
         * XS6.2 doesn't properly support QEMU_EXT_KEY and XS6.5 supports QEMU_EXT_KEY encoding
         * only if XS65ESP1051 is applied, so restrict QEMU_EXT_KEY encoding to Inverness and above.
         */
        public void keyScanEvent(bool down, int key, int sym, bool use_qemu_ext_key_encoding)
        {
            lock (writeLock)
            {
                try
                {
                    if (qemu_ext_key_encoding && use_qemu_ext_key_encoding)
                    {
                        WriteQemuExtKey(QEMU_MSG, down, key, sym);
                    }
                    else
                    {
                        WriteKey(KEY_SCAN_EVENT, down, key);
                    }
                    stream.Flush();
                }
                catch (IOException e)
                {
                    Log.Warn(e, e);
                }
            }
        }

        public void keyCodeEvent(bool down, int key)
        {
            lock (writeLock)
            {
                try
                {
                    WriteKey(KEY_EVENT, down, key);
                    stream.Flush();
                }
                catch (IOException e)
                {
                    Log.Warn(e, e);
                }
            }
        }

        public void PointerEvent(int buttonMask, int x, int y)
        {
            if (x < 0)
            {
                x = 0;
            }
            else if (x >= _width)
            {
                x = _width - 1;
            }

            if (y < 0)
            {
                y = 0;
            }
            else if (y >= _height)
            {
                y = _height - 1;
            }

            lock (writeLock)
            {
                try
                {
                    PointerEvent_(buttonMask, x, y);
                    stream.Flush();
                }
                catch (IOException e)
                {
                    Log.Warn(e, e);
                }
            }
        }

        public void PointerWheelEvent(int x, int y, int r)
        {
            lock (writeLock)
            {
                try
                {
                    /*
                      The RFB protocol specifies a down-up pair for each
                      scroll of the wheel, on button 4 for scrolling up, and
                      button 5 for scrolling down.
                    */

                    int m;

                    if (r < 0)
                    {
                        r = -r;
                        m = 8;
                    }
                    else
                    {
                        m = 16;
                    }
                    for (int i = 0; i < r; i++)
                    {
                        PointerEvent_(m, x, y);
                        PointerEvent_(0, x, y);
                    }

                    stream.Flush();
                }
                catch (IOException e)
                {
                    Log.Warn(e, e);
                }
            }
        }

        private void PointerEvent_(int buttonMask, int x, int y)
        {
            stream.WriteInt8(POINTER_EVENT);
            stream.WriteInt8(buttonMask);
            stream.WriteInt16(x);
            stream.WriteInt16(y);
        }

        public void ClientCutText(string text)
        {
            Log.Debug("cutEvent");

            lock (writeLock)
            {
                try
                {
                    stream.WriteInt8(CLIENT_CUT_TEXT);
                    stream.WritePadding(3);
                    stream.WriteString(text);
                    stream.Flush();
                }
                catch (IOException e)
                {
                    Log.Warn(e, e);
                }
            }
        }

        /// <summary>
        /// Creates an image in place in this.data.  It expects the pixel data to already be in this.data.  
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="start">the start of image data in this.data</param>
        /// <param name="length">the length of image data in this data</param>
        /// <param name="maskLength">length of cursor mask (after the image in this.data), as specified by
        /// the RFB protocol specification for the Cursor pseudo-encoding (1-bpp, packed). If 0,
        /// the mask is assumed to be totally opaque (as used by normal "raw" packets). Masks are not
        /// supported for 8-bpp images.</param>
        /// <param name="cursor"></param>
        private void CreateImage(int width, int height, int x, int y, int start, int length, int maskLength, bool cursor)
        {
            if (width == 0 || height == 0)
                return;

            byte[] dataToRender;
            int stride;

            if (bitsPerPixel == 32)
            {
                stride = width * 4;
                dataToRender = _data;

                System.Diagnostics.Debug.Assert(length == height * stride);

                if (cursor)
                {
                    // for mask
                    int j = 0; // bit within the current byte (k)
                    int k = start + length; //byte
                    int m = 0; // bit within the current row

                    for (int i = start; i < start + length; i += 4)
                    {
                        bool mask = (_data[k] & (1 << (7 - j))) == 0;
                        _data[i + 3] = (byte)(mask ? 0 : 0xff);

                        j++;
                        m++;

                        if (m == width)
                        {
                            j = 0;
                            m = 0;
                            k++;
                        }
                        else if (j > 7)
                        {
                            j = 0;
                            k++;
                        }
                    }
                }
            }
            else if (bitsPerPixel == 16)
            {
                // Bitmap requires that stride is a multiple of 4, so we 
                // will have to expand the data if width is odd.
                bool expand_data = width % 2 == 1;
                int stride_correction = expand_data ? 2 : 0;
                stride = width * 2 + stride_correction;
                dataToRender = expand_data ? new byte[stride * height] : _data;

                System.Diagnostics.Debug.Assert(length == height * width * 2);

                if (cursor)
                {
                    int p = 0;      // Byte within the destination data_to_render.
                    // for mask
                    int j = 0; // bit within the current byte (k)
                    int k = start + length; //byte
                    int m = 0; // bit within the current row

                    for (int i = start; i < start + length; i += 2)
                    {
                        bool mask = (_data[k] & (1 << (7 - j))) == 0;
                        byte mask_bit = (byte)(mask ? 0 : 0x80);

                        if (rgb565)
                        {
                            // Convert the 565 data into 1555.
                            dataToRender[p] = (byte)((_data[i] & 0x1f) | ((_data[i] & 0xe0) >> 1));
                            dataToRender[p + 1] = (byte)(((_data[i + 1] & 0x7) >> 1) | (_data[i + 1] & 0x78) | mask_bit);
                        }
                        else
                        {
                            // Add the mask bit -- everything else is OK because it's already 555.
                            dataToRender[p] = _data[i];
                            dataToRender[p + 1] = (byte)(_data[i + 1] | mask_bit);
                        }

                        j++;
                        m++;
                        p += 2;

                        if (m == width)
                        {
                            j = 0;
                            m = 0;
                            k++;
                            p += stride_correction;
                        }
                        else if (j > 7)
                        {
                            j = 0;
                            k++;
                        }
                    }
                }
                else if (expand_data)
                {
                    int w2 = width * 2;
                    int i = start;  // Byte within the source data.
                    int p = 0;      // Byte within the destination data_to_render.

                    for (int m = 0; m < height; m++)
                    {
                        Array.Copy(_data, i, dataToRender, p, w2);
                        i += w2;
                        p += stride;
                    }
                }
            }
            else if (bitsPerPixel == 8)
            {
                stride = width * 4;
                var needed8 = Math.Max(stride * height, width * height * 4);
                if (data_8bpp == null || data_8bpp.Length < needed8)
                    data_8bpp = new byte[needed8];
                dataToRender = data_8bpp;

                System.Diagnostics.Debug.Assert(length == width * height);

                // for mask
                int j = 0; // bit within the current byte (k)
                int k = start + length; //byte
                int m = 0; // bit within the current row

                for (int i = start, n = 0; i < start + length; i++, n += 4)
                {
                    data_8bpp[n + 2] = (byte)(((((_data[i] >> redShift) & redMax) << 8) + redMaxOver2) / redMaxPlus1);
                    data_8bpp[n + 1] = (byte)(((((_data[i] >> greenShift) & greenMax) << 8) + greenMaxOver2) / greenMaxPlus1);
                    data_8bpp[n] = (byte)(((((_data[i] >> blueShift) & blueMax) << 8) + blueMaxOver2) / blueMaxPlus1);
                    if (cursor)
                    {
                        bool mask = (_data[k] & (1 << (7 - j))) == 0;
                        data_8bpp[n + 3] = (byte)(mask ? 0 : 0xff);

                        j++;
                        m++;

                        if (m == width)
                        {
                            j = 0;
                            m = 0;
                            k++;
                        }
                        else if (j > 7)
                        {
                            j = 0;
                            k++;
                        }
                    }
                    else
                    {
                        data_8bpp[n + 3] = 0;
                    }
                }
            }
            else
            {
                throw new RfbException("unexpected bits per pixel");
            }

            if (dataToRender == null)
                throw new RfbException("Decoded framebuffer buffer was null");
            BitmapToClient(width, height, x, y, start, stride, cursor, dataToRender);
        }

        private void BitmapToClient(int width, int height, int x, int y, int start, int stride, bool cursor, byte [] img)
        {
            try
            {
                var bgra = EnsureBgra32(img, start, stride, width, height);
                if (cursor)
                    client.SetCursor(bgra, 0, width * 4, x, y, width, height);
                else
                    client.DrawImage(bgra, 0, width * 4, x, y, width, height);
            }
            catch (ArgumentException exn)
            {
                Log.Error(exn, exn);
            }
        }

        /// <summary>
        /// Normalize decoded RFB pixels to BGRA32 for Avalonia WriteableBitmap.
        /// For 32bpp little-endian (B,G,R,X) this is already the right layout.
        /// </summary>
        private byte[] EnsureBgra32(byte[] img, int start, int stride, int width, int height)
        {
            var dstStride = width * 4;
            if (bitsPerPixel == 32 && stride == dstStride && start == 0 && img.Length >= dstStride * height)
            {
                // Ensure alpha is opaque for Format32bppRgb-style buffers.
                for (int i = 3; i < dstStride * height; i += 4)
                {
                    if (img[i] == 0)
                        img[i] = 0xFF;
                }
                return img;
            }

            var bgra = new byte[dstStride * height];
            if (bitsPerPixel == 32)
            {
                for (int row = 0; row < height; row++)
                {
                    Buffer.BlockCopy(img, start + row * stride, bgra, row * dstStride, dstStride);
                    for (int col = 0; col < width; col++)
                    {
                        var i = row * dstStride + col * 4 + 3;
                        if (bgra[i] == 0)
                            bgra[i] = 0xFF;
                    }
                }
                return bgra;
            }

            if (bitsPerPixel == 16)
            {
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        var src = start + row * stride + col * 2;
                        var pixel = (uint)(img[src] | (img[src + 1] << 8));
                        var di = row * dstStride + col * 4;
                        byte r, g, b;
                        if (rgb565)
                        {
                            r = (byte)((((pixel >> 11) & 0x1f) * 255 + 15) / 31);
                            g = (byte)((((pixel >> 5) & 0x3f) * 255 + 31) / 63);
                            b = (byte)(((pixel & 0x1f) * 255 + 15) / 31);
                        }
                        else
                        {
                            r = (byte)((((pixel >> 10) & 0x1f) * 255 + 15) / 31);
                            g = (byte)((((pixel >> 5) & 0x1f) * 255 + 15) / 31);
                            b = (byte)(((pixel & 0x1f) * 255 + 15) / 31);
                        }
                        bgra[di] = b;
                        bgra[di + 1] = g;
                        bgra[di + 2] = r;
                        bgra[di + 3] = 0xFF;
                    }
                }
                return bgra;
            }

            // 8bpp path already expands into data_8bpp as BGRA-ish.
            if (bitsPerPixel == 8)
            {
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(img, start + row * stride, bgra, row * dstStride, dstStride);
                return bgra;
            }

            throw new RfbException("unexpected bits per pixel for client blit");
        }

        private void ReadRawEncoding(int x, int y, int width, int height)
        {
            ReadRawEncoding_(0, x, y, width, height, false);
        }

        /**
         * @param mask If true, read a mask after the raw data, as used by the
         * Cursor pseudo-encoding.
         * @param start The position in this.data to start using
         */
        private void ReadRawEncoding_(int start, int x, int y, int width, int height, bool cursor)
        {
            if (width < 0 || height < 0)
            {
                throw new RfbException("Invalid size: " + width + " x " +
                                       height);
            }

            int length = width * height * bytesPerPixel;
            if (_data.Length < start + length)
                throw new RfbException("Server error: received rectangle bigger than desktop!");
            stream.ReadFully(_data, start, length);

            int maskLength = 0;
            if (cursor)
            {
                // 1 bit mask.
                int scanline = (width + 7) >> 3;
                maskLength = scanline * height;
                System.Diagnostics.Trace.Assert(_data.Length >= start + length + maskLength);
                stream.ReadFully(_data, start + length, maskLength);
            }

            CreateImage(width, height, x, y, start, length, maskLength, cursor);
        }

        private void ReadCopyRectangleEncoding(int dx, int dy, int width, int height)
        {
            int x = stream.ReadCard16();
            int y = stream.ReadCard16();
            client.CopyRectangle(x, y, width, height, dx, dy);
        }

        private RfbColor ReadColor()
        {
            byte[] color = ReadColorBytes();
            return RfbColor.FromRgb(color[2], color[1], color[0]);
        }

        private byte[] ReadColorBytes(byte[] color, int start)
        {
            uint pixel;
            switch (bitsPerPixel)
            {
                case 32:
                    pixel = (uint)(color[start] |
                                   color[start + 1] << 8 |
                                   color[start + 2] << 16 |
                                   color[start + 3] << 24);
                    break;
                case 16:
                    pixel = (uint)(color[start] |
                                   color[start + 1] << 8);
                    break;
                default:
                    pixel = color[start];
                    break;
            }

            byte[] newColor = new byte[4];

            //ARGB Encoding
            newColor[3] = 0xFF;
            newColor[2] = (byte)(((((pixel >> redShift) & redMax) << 8) + redMaxOver2) / redMaxPlus1);
            newColor[1] = (byte)(((((pixel >> greenShift) & greenMax) << 8) + greenMaxOver2) / greenMaxPlus1);
            newColor[0] = (byte)(((((pixel >> blueShift) & blueMax) << 8) + blueMaxOver2) / blueMaxPlus1);

            return newColor;
        }

        private byte[] ReadColorBytes()
        {
            int n = bitsPerPixel >> 3;
            byte[] color = new byte[n];

            stream.ReadFully(color, 0, n);

            return ReadColorBytes(color, 0);
        }

        private void ReadRREEncoding(int x, int y, int width, int height)
        {
            int n = stream.ReadCard32();
            RfbColor background = ReadColor();
            client.FillRectangle(x, y, width, height, background);
            for (int i = 0; i < n; ++i)
            {
                RfbColor foreground = ReadColor();
                int rx = stream.ReadCard16();
                int ry = stream.ReadCard16();
                int rw = stream.ReadCard16();
                int rh = stream.ReadCard16();
                client.FillRectangle(x + rx, y + ry, rw, rh, foreground);
            }
        }

        private void ReadCoRREEncoding(int x, int y, int width, int height)
        {
            int n = stream.ReadCard32();
            RfbColor background = ReadColor();
            client.FillRectangle(x, y, width, height, background);
            for (int i = 0; i < n; ++i)
            {
                RfbColor foreground = ReadColor();
                int rx = stream.ReadCard8();
                int ry = stream.ReadCard8();
                int rw = stream.ReadCard8();
                int rh = stream.ReadCard8();
                client.FillRectangle(x + rx, y + ry, rw, rh, foreground);
            }
        }

        private void ReadFillRectangles(int rx, int ry, int n)
        {
            int pixelSize = (bitsPerPixel + 7) >> 3;
            int length = n * (pixelSize + 2);
            stream.ReadFully(_data, 0, length);
            int index = 0;
            for (int i = 0; i < n; ++i)
            {
                RfbColor foreground = ReadColor();
                int sxy = _data[index++] & 0xff;
                int sx = sxy >> 4;
                int sy = sxy & 0xf;
                int swh = _data[index++] & 0xff;
                int sw = (swh >> 4) + 1;
                int sh = (swh & 0xf) + 1;
                client.FillRectangle(
                    rx + sx, ry + sy, sw, sh, foreground
                );
            }
        }

        private void ReadRectangles(int rx, int ry, int n, RfbColor foreground)
        {
            for (int i = 0; i < n; ++i)
            {
                int sxy = stream.ReadCard8();
                int sx = sxy >> 4;
                int sy = sxy & 0xf;
                int swh = stream.ReadCard8();
                int sw = (swh >> 4) + 1;
                int sh = (swh & 0xf) + 1;
                client.FillRectangle(
                    rx + sx, ry + sy, sw, sh, foreground
                );
            }
        }

        private void FillRectBytes(byte[] data, int stride, int x, int y, int width, int height, byte[] color)
        {
            // Fast path thin rectangles

            int p = (y * stride) + (x * 4);
            int skip;

            if (width == 1 && height == 1)
            {
                data[p + 0] = color[0];
                data[p + 1] = color[1];
                data[p + 2] = color[2];
                data[p + 3] = color[3];
            }
            else if (width == 1)
            {
                skip = stride - 4;

                for (int i = 0; i < height; i++)
                {
                    data[p + 0] = color[0];
                    data[p + 1] = color[1];
                    data[p + 2] = color[2];
                    data[p + 3] = color[3];

                    p += skip;
                }
            }
            else if (height == 1)
            {
                for (int i = 0; i < width; i++)
                {
                    data[p + 0] = color[0];
                    data[p + 1] = color[1];
                    data[p + 2] = color[2];
                    data[p + 3] = color[3];

                    p += 4;
                }
            }
            else
            {
                skip = stride - (width * 4);

                for (int j = 0; j < height; j++)
                {
                    for (int i = 0; i < width; i++)
                    {
                        data[p + 0] = color[0];
                        data[p + 1] = color[1];
                        data[p + 2] = color[2];
                        data[p + 3] = color[3];

                        p += 4;
                    }
                    p += skip;
                }
            }
        }

        private void ReadHextileEncoding(int x, int y, int width, int height)
        {
            /*
             * Basically, we have two ways of doing this.
             * 1. Draw the hextile to a byte buffer, convert to image
             *    and draw to client
             * 2. Draw the individual rectangles of the hex encoding
             *    directly to the client
             * 
             * I have found its faster to do 1) when size is > 64
             */
            //GraphicsUtils.startTime();
            if (width * height > 64)
            {
                byte[] background = { 0, 0, 0, 0xFF }; // Black
                byte[] foreground = { 0xFF, 0xFF, 0xFF, 0xFF }; // White

                byte[] buff = new byte[width * height * 4]; //assume 32 bpp
                int stride = width * 4;

                for (int sy = 0; sy < height; sy += 16)
                {
                    int sheight = Math.Min(16, height - sy);

                    for (int sx = 0; sx < width; sx += 16)
                    {
                        int swidth = Math.Min(16, width - sx);

                        int mask = stream.ReadCard8();

                        if ((mask & RAW_SUBENCODING) != 0)
                        {
                            int length = swidth * sheight * 4;
                            stream.ReadFully(_data, 0, length);

                            int index = 0;
                            int skip = stride - (swidth * 4);
                            int p = (sy * stride) + (sx * 4);

                            for (int i = 0; i < sheight; i++)
                            {
                                for (int j = 0; j < swidth; j++)
                                {
                                    byte[] color = ReadColorBytes(_data, index);

                                    index += 4; //assumed 32bpp here

                                    buff[p + 3] = color[3];
                                    buff[p + 2] = color[2];
                                    buff[p + 1] = color[1];
                                    buff[p + 0] = color[0];

                                    p += 4;
                                }

                                p += skip;
                            }
                        }
                        else
                        {
                            if ((mask & BACKGROUND_SPECIFIED_SUBENCODING) != 0)
                            {
                                background = ReadColorBytes();
                            }

                            FillRectBytes(buff, stride, sx, sy, swidth, sheight, background);

                            if ((mask & FOREGROUND_SPECIFIED_SUBENCODING) != 0)
                            {
                                foreground = ReadColorBytes();
                            }

                            if ((mask & ANY_SUBRECTS_SUBENCODING) != 0)
                            {
                                int n = stream.ReadCard8();
                                if ((mask & SUBRECTS_COLORED_SUBENCODING) != 0)
                                {
                                    int length = n * 6; //assume 32bpp
                                    stream.ReadFully(_data, 0, length);
                                    int index = 0;
                                    for (int i = 0; i < n; ++i)
                                    {
                                        byte[] color = new byte[4];
                                        uint pixel = (uint)(_data[index + 0] & 0xFF |
                                                            _data[index + 1] << 8 |
                                                            _data[index + 2] << 16 |
                                                            _data[index + 3] << 24);

                                        //ARGB Encoding
                                        color[3] = 0xFF;
                                        color[2] = (byte)((pixel >> redShift) & redMax);
                                        color[1] = (byte)((pixel >> greenShift) & greenMax);
                                        color[0] = (byte)((pixel >> blueShift) & blueMax);

                                        index += 4;
                                        int txy = _data[index++] & 0xff;
                                        int tx = txy >> 4;
                                        int ty = txy & 0xf;
                                        int twh = _data[index++] & 0xff;
                                        int tw = (twh >> 4) + 1;
                                        int th = (twh & 0xf) + 1;

                                        FillRectBytes(buff, stride, sx + tx, sy + ty, tw, th, color);
                                    }
                                }
                                else
                                {
                                    for (int i = 0; i < n; ++i)
                                    {
                                        int txy = stream.ReadCard8();
                                        int tx = txy >> 4;
                                        int ty = txy & 0xf;
                                        int twh = stream.ReadCard8();
                                        int tw = (twh >> 4) + 1;
                                        int th = (twh & 0xf) + 1;

                                        FillRectBytes(buff, stride, sx + tx, sy + ty, tw, th, foreground);
                                    }
                                }
                            }
                        }
                    }
                }

                // Ensure opaque alpha then blit BGRA32
                for (int i = 3; i < buff.Length; i += 4)
                {
                    if (buff[i] == 0)
                        buff[i] = 0xFF;
                }
                client.DrawImage(buff, 0, stride, x, y, width, height);
            }
            else
            {
                RfbColor foreground = RfbColor.White;
                RfbColor background = RfbColor.Black;

                int xCount = (width + 15) >> 4;
                int yCount = (height + 15) >> 4;
                for (int yi = 0; yi < yCount; ++yi)
                {
                    int ry = y + (yi << 4);
                    int rh = (yi == (yCount - 1)) ? height & 0xf : 16;
                    if (rh == 0)
                    {
                        rh = 16;
                    }
                    for (int xi = 0; xi < xCount; ++xi)
                    {
                        int rx = x + (xi << 4);
                        int rw = (xi == (xCount - 1)) ? width & 0xf : 16;
                        if (rw == 0)
                        {
                            rw = 16;
                        }
                        int mask = stream.ReadCard8();
                        if ((mask & RAW_SUBENCODING) != 0)
                        {
                            ReadRawEncoding(rx, ry, rw, rh);
                        }
                        else
                        {
                            if ((mask & BACKGROUND_SPECIFIED_SUBENCODING) != 0)
                            {
                                background = ReadColor();
                            }
                            client.FillRectangle(rx, ry, rw, rh, background);
                            if ((mask & FOREGROUND_SPECIFIED_SUBENCODING) != 0)
                            {
                                foreground = ReadColor();
                            }
                            if ((mask & ANY_SUBRECTS_SUBENCODING) != 0)
                            {
                                int n = stream.ReadCard8();
                                if ((mask & SUBRECTS_COLORED_SUBENCODING) != 0)
                                {
                                    ReadFillRectangles(rx, ry, n);
                                }
                                else
                                {
                                    ReadRectangles(rx, ry, n, foreground);
                                }
                            }
                        }
                    }
                }
            }
            /*double time = GraphicsUtils.endTime("hextile");
            int size = width * height;
            StatsEntry entry = new StatsEntry();
            entry.time = time;
            entry.size = size;

            this.client.stats.Add(entry);*/
        }

        private void ReadCursorPseudoEncoding(int x, int y, int width, int height)
        {
            ReadRawEncoding_(0, x, y, width, height, true);
        }

        private void ReadFrameBufferUpdate()
        {
            stream.ReadPadding(1);
            int n = stream.ReadCard16();
            Log.Debug("reading " + n + " rectangles");
            bool fb_updated = false;

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < n; ++i)
            {
                int x = stream.ReadCard16();
                int y = stream.ReadCard16();
                int width = stream.ReadCard16();
                int height = stream.ReadCard16();
                int encoding = stream.ReadCard32();
                Log.Debug("read " + x + " " + y + " " + width + " " + height + " " + encoding);
                switch (encoding)
                {
                    case RAW_ENCODING:
                        ReadRawEncoding(x, y, width, height);
                        break;
                    case RRE_ENCODING:
                        ReadRREEncoding(x, y, width, height);
                        break;
                    case CORRE_ENCODING:
                        ReadCoRREEncoding(x, y, width, height);
                        break;
                    case COPY_RECTANGLE_ENCODING:
                        ReadCopyRectangleEncoding(x, y, width, height);
                        break;
                    case HEXTILE_ENCODING:
                        ReadHextileEncoding(x, y, width, height);
                        break;

                    case CURSOR_PSEUDO_ENCODING:
                        ReadCursorPseudoEncoding(x, y, width, height);
                        break;

                    case DESKTOP_SIZE_PSEUDO_ENCODING:
                        DesktopSize(width, height);
                        // Since the desktop size has changed, we want a full buffer update next time
                        _incremental = false;
                        break;

                    case QEMU_EXT_KEY_ENCODING:
                        qemu_ext_key_encoding = true;
                        break;

                    default:
                        throw new RfbException("unimplemented encoding: " + encoding);
                }

                var end = System.Diagnostics.Stopwatch.GetTimestamp();
                if (end - start > imageUpdateThreshold)
                {
                    client.FrameBufferUpdate();
                    start = end;
                    fb_updated = true;
                }
                else
                {
                    fb_updated = false;
                }
            }

            if (!fb_updated)
                client.FrameBufferUpdate();
        }


        private void DesktopSize(int width, int height)
        {
            _width = width;
            _height = height;
            int neededBytes = width * height * bytesPerPixel;
            if (neededBytes > _data.Length)
            {
                _data = new byte[neededBytes];
            }
            if (bitsPerPixel == 8 && (data_8bpp == null || neededBytes * 4 > data_8bpp.Length))
            {
                data_8bpp = new byte[neededBytes * 4];
            }
            client.DesktopSize(width, height);
        }

        private void ReadServerCutText()
        {
            stream.ReadPadding(3);
            String text = stream.ReadString();
            client.CutText(text);
        }

        private void ReadServerMessage()
        {
            Log.Debug("readServerMessage");

            int type = stream.ReadCard8();

            switch (type)
            {
                case FRAME_BUFFER_UPDATE:
                    Log.Debug("Update");
                    ReadFrameBufferUpdate();
                    break;
                case BELL:
                    Log.Debug("Bell");
                    client.Bell();
                    break;
                case SERVER_CUT_TEXT:
                    Log.Debug("Cut text");
                    ReadServerCutText();
                    break;
                default:
                    throw new RfbException("unknown server message: " + type);
            }
        }

        private void Run(object? o)
        {
            char[] password = (char[])o!;

            try
            {
                CheckProtocolVersion();
                SendProtocolVersion();
                AuthenticationExchange(password);
                InitializeClient();
                InitializateServer();

                if (ConnectionSuccess != null)
                    ConnectionSuccess(this, EventArgs.Empty);

                // Request a full framebuffer update the first time
                _incremental = false;

                while (running)
                {
                    lock (writeLock)
                    {
                        WriteFramebufferUpdateRequest(0, 0, _width, _height, _incremental);
                        stream.Flush();
                    }

                    _incremental = true;

                    ReadServerMessage();

                    lock (pauseMonitor)
                    {
                        lock(updateMonitor)
                            Monitor.PulseAll(updateMonitor);

                        if (paused)
                            Monitor.Wait(pauseMonitor);
                    }
                }
            }
            catch (Exception e)
            {
                if (running && ErrorOccurred != null)
                    ErrorOccurred(this, e);
            }
        }

        public void Close()
        {
            if (!running)
                return;

            running = false;
            try
            {
                stream.Close();
                lock (pauseMonitor)
                    Monitor.PulseAll(pauseMonitor);
                thread?.Interrupt();
            }
            catch
            {
                // ignored
            }
        }

        public void Pause()
        {
            paused = true;
        }

        public void UnPause(bool fullupdate = false)
        {
            _incremental = !fullupdate;
            paused = false;
            lock (pauseMonitor)
                Monitor.PulseAll(pauseMonitor);
        }
    }
}
