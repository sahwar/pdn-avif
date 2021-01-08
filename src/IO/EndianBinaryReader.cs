﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-avif, a FileType plugin for Paint.NET
// that loads and saves AVIF images.
//
// Copyright (c) 2020, 2021 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AvifFileType
{
    // Adapted from 'Problem and Solution: The Terrible Inefficiency of FileStream and BinaryReader'
    // https://jacksondunstan.com/articles/3568

    internal sealed class EndianBinaryReader
        : IDisposable
    {
        private const int MaxBufferSize = 4096;

#pragma warning disable IDE0032 // Use auto property
        private Stream stream;
        private int readOffset;
        private int readLength;
        private bool disposed;
        private readonly byte[] buffer;
        private readonly int bufferSize;
        private readonly Endianess endianess;
        private readonly bool leaveOpen;
        private readonly IByteArrayPool arrayPool;
#pragma warning restore IDE0032 // Use auto property

        /// <summary>
        /// Initializes a new instance of the <see cref="EndianBinaryReader"/> class.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="byteOrder">The byte order of the stream.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is null.
        ///
        /// -or-
        ///
        /// <paramref name="arrayPool"/> is null.
        /// </exception>
        public EndianBinaryReader(Stream stream, Endianess byteOrder, IByteArrayPool arrayPool) : this(stream, byteOrder, false, arrayPool)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EndianBinaryReader"/> class.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="byteOrder">The byte order of the stream.</param>
        /// <param name="leaveOpen">
        /// <see langword="true"/> to leave the stream open after the EndianBinaryReader is disposed; otherwise, <see langword="false"/>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is null.
        ///
        /// -or-
        ///
        /// <paramref name="arrayPool"/> is null.
        /// </exception>
        public EndianBinaryReader(Stream stream, Endianess byteOrder, bool leaveOpen, IByteArrayPool arrayPool)
        {
            if (arrayPool is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(arrayPool));
            }

            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.bufferSize = (int)Math.Min(stream.Length, MaxBufferSize);
            this.buffer = arrayPool.Rent(this.bufferSize);
            this.endianess = byteOrder;
            this.leaveOpen = leaveOpen;
            this.arrayPool = arrayPool;

            this.readOffset = 0;
            this.readLength = 0;
            this.disposed = false;
        }

        public Endianess Endianess => this.endianess;

        /// <summary>
        /// Gets the length of the stream.
        /// </summary>
        /// <value>
        /// The length of the stream.
        /// </value>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public long Length
        {
            get
            {
                VerifyNotDisposed();

                return this.stream.Length;
            }
        }

        /// <summary>
        /// Gets or sets the position in the stream.
        /// </summary>
        /// <value>
        /// The position in the stream.
        /// </value>
        /// <exception cref="ArgumentOutOfRangeException">value is negative.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public long Position
        {
            get
            {
                VerifyNotDisposed();

                return this.stream.Position - this.readLength + this.readOffset;
            }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException("value");
                }
                VerifyNotDisposed();

                long current = this.Position;

                if (value != current)
                {
                    long bufferStartOffset = current - this.readOffset;
                    long bufferEndOffset = bufferStartOffset + this.readLength;

                    // Avoid reading from the stream if the offset is within the current buffer.
                    if (value >= bufferStartOffset && value <= bufferEndOffset)
                    {
                        this.readOffset = (int)(value - bufferStartOffset);
                    }
                    else
                    {
                        // Invalidate the existing buffer.
                        this.readOffset = 0;
                        this.readLength = 0;
                        this.stream.Seek(value, SeekOrigin.Begin);
                    }
                }
            }
        }

        /// <summary>
        /// Creates the segment.
        /// </summary>
        /// <param name="startOffset">The start offset.</param>
        /// <param name="length">The length.</param>
        /// <returns>The created segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="startOffset"/> is greater than the <see cref="EndianBinaryReader"/> length.
        /// -or-
        /// <paramref name="startOffset"/> plus <paramref name="length"/> is greater than the <see cref="EndianBinaryReader"/> length.
        /// </exception>
        public EndianBinaryReaderSegment CreateSegment(long startOffset, long length)
        {
            VerifyNotDisposed();

            long streamLength = this.stream.Length;

            if ((ulong)startOffset > (ulong)streamLength)
            {
                ExceptionUtil.ThrowArgumentOutOfRangeException(nameof(startOffset));
            }

            try
            {
                if (checked(startOffset + length) > streamLength)
                {
                    ExceptionUtil.ThrowArgumentOutOfRangeException(nameof(length));
                }
            }
            catch (OverflowException ex)
            {
                throw new ArgumentOutOfRangeException(nameof(length), ex);
            }

            return new EndianBinaryReaderSegment(this, startOffset, length);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (!this.disposed)
            {
                this.arrayPool.Return(this.buffer);

                if (this.stream != null)
                {
                    if (!this.leaveOpen)
                    {
                        this.stream.Dispose();
                    }
                    this.stream = null;
                }
            }
        }

        /// <summary>
        /// Reads the specified number of bytes from the stream, starting from a specified point in the byte array.
        /// </summary>
        /// <param name="bytes">The bytes.</param>
        /// <param name="offset">The starting offset in the array.</param>
        /// <param name="count">The count.</param>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public unsafe void ProperRead(byte[] bytes, int offset, int count)
        {
            if (bytes is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(bytes));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            VerifyNotDisposed();

            if (count == 0)
            {
                return;
            }

            int totalBytesRead = 0;

            while (totalBytesRead < count)
            {
                int bytesRead = ReadInternal(bytes, offset + totalBytesRead, count - totalBytesRead);

                if (bytesRead == 0)
                {
                    throw new EndOfStreamException();
                }

                totalBytesRead += bytesRead;
            }
        }

        /// <summary>
        /// Reads the specified number of bytes from the stream, starting from a specified point in the SafeBuffer.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="offset">The starting offset in the buffer.</param>
        /// <param name="count">The count.</param>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public unsafe void ProperRead(SafeBuffer buffer, ulong offset, ulong count)
        {
            if (buffer is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(buffer));
            }
            VerifyNotDisposed();

            if (count == 0)
            {
                return;
            }

            // The largest multiple of 4096 that is under the large object heap limit.
            const int MaxReadBufferSize = 81920;

            byte[] readBuffer = this.arrayPool.Rent((int)Math.Min(count, MaxReadBufferSize));

            byte* writePtr = null;
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                buffer.AcquirePointer(ref writePtr);

                fixed (byte* readPtr = readBuffer)
                {
                    ulong totalBytesRead = 0;

                    while (totalBytesRead < count)
                    {
                        ulong bytesRead = (ulong)ReadInternal(readBuffer, 0, (int)Math.Min(count - totalBytesRead, MaxReadBufferSize));

                        if (bytesRead == 0)
                        {
                            throw new EndOfStreamException();
                        }

                        Buffer.MemoryCopy(readPtr, writePtr + offset + totalBytesRead, bytesRead, bytesRead);

                        totalBytesRead += bytesRead;
                    }
                }
            }
            finally
            {
                if (writePtr != null)
                {
                    buffer.ReleasePointer();
                }
            }

            this.arrayPool.Return(readBuffer);
        }

        /// <summary>
        /// Reads the specified number of bytes from the stream, starting from a specified point in the byte array.
        /// </summary>
        /// <param name="bytes">The bytes.</param>
        /// <param name="offset">The starting offset in the array.</param>
        /// <param name="count">The count.</param>
        /// <returns>The number of bytes read from the stream.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public int Read(byte[] bytes, int offset, int count)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            VerifyNotDisposed();

            return ReadInternal(bytes, offset, count);
        }

        /// <summary>
        /// Reads a null-terminated UTF-8 string from the stream.
        /// </summary>
        /// <param name="endOffset">The offset that marks the end of the null-terminator search area.</param>
        /// <returns>The string.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="IOException">The string is longer than <see cref="int.MaxValue"/>.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public AvifContainer.BoxString ReadBoxString(long endOffset)
        {
            if (endOffset < 0)
            {
                ExceptionUtil.ThrowArgumentOutOfRangeException(nameof(endOffset));
            }

            VerifyNotDisposed();

            AvifContainer.BoxString result;

            int length = GetStringLength(endOffset, out bool hasNullTerminator);

            if (length == 0)
            {
                result = AvifContainer.BoxString.Empty;
            }
            else
            {
                EnsureBuffer(length);

                result = System.Text.Encoding.UTF8.GetString(this.buffer, this.readOffset, length);

                this.readOffset += length;
            }

            if (hasNullTerminator)
            {
                this.Position++; // Skip the null-terminator if one was found at the end of the string.
            }

            return result;
        }

        /// <summary>
        /// Reads the next byte from the current stream.
        /// </summary>
        /// <returns>The next byte read from the current stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public byte ReadByte()
        {
            VerifyNotDisposed();

            return ReadByteInternal();
        }

        /// <summary>
        /// Reads the specified number of bytes from the stream.
        /// </summary>
        /// <param name="count">The number of bytes to read..</param>
        /// <returns>An array containing the specified bytes.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            VerifyNotDisposed();

            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] bytes = new byte[count];

            if ((this.readOffset + count) <= this.readLength)
            {
                Buffer.BlockCopy(this.buffer, this.readOffset, bytes, 0, count);
                this.readOffset += count;
            }
            else
            {
                // Ensure that any bytes at the end of the current buffer are included.
                int bytesUnread = this.readLength - this.readOffset;

                if (bytesUnread > 0)
                {
                    Buffer.BlockCopy(this.buffer, this.readOffset, bytes, 0, bytesUnread);
                }

                int numBytesToRead = count - bytesUnread;
                int numBytesRead = bytesUnread;
                do
                {
                    int n = this.stream.Read(bytes, numBytesRead, numBytesToRead);

                    if (n == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    numBytesRead += n;
                    numBytesToRead -= n;

                } while (numBytesToRead > 0);

                // Invalidate the existing buffer.
                this.readOffset = 0;
                this.readLength = 0;
            }

            return bytes;
        }

        /// <summary>
        /// Reads a 8-byte floating point value.
        /// </summary>
        /// <returns>The 8-byte floating point value.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public unsafe double ReadDouble()
        {
            ulong temp = ReadUInt64();

            return *(double*)&temp;
        }

        /// <summary>
        /// Reads a four-character code from the stream.
        /// </summary>
        /// <returns>The four-character code.</returns>
        public AvifContainer.FourCC ReadFourCC()
        {
            return new AvifContainer.FourCC(ReadUInt32());
        }

        /// <summary>
        /// Reads a 2-byte signed integer.
        /// </summary>
        /// <returns>The 2-byte signed integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public short ReadInt16()
        {
            return (short)ReadUInt16();
        }

        /// <summary>
        /// Reads a 4-byte signed integer.
        /// </summary>
        /// <returns>The 4-byte signed integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public int ReadInt32()
        {
            return (int)ReadUInt32();
        }

        /// <summary>
        /// Reads a 8-byte signed integer.
        /// </summary>
        /// <returns>The 8-byte signed integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public long ReadInt64()
        {
            return (long)ReadUInt64();
        }

        /// <summary>
        /// Reads a 4-byte floating point value.
        /// </summary>
        /// <returns>The 4-byte floating point value.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public unsafe float ReadSingle()
        {
            uint temp = ReadUInt32();

            return *(float*)&temp;
        }

        /// <summary>
        /// Reads a 2-byte unsigned integer.
        /// </summary>
        /// <returns>The 2-byte unsigned integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public ushort ReadUInt16()
        {
            VerifyNotDisposed();

            EnsureBuffer(sizeof(ushort));

            ushort val;

            switch (this.endianess)
            {
                case Endianess.Big:
                    val = (ushort)((this.buffer[this.readOffset] << 8) | this.buffer[this.readOffset + 1]);
                    break;
                case Endianess.Little:
                    val = (ushort)(this.buffer[this.readOffset] | (this.buffer[this.readOffset + 1] << 8));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported byte order: " + this.endianess.ToString());
            }

            this.readOffset += sizeof(ushort);

            return val;
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer.
        /// </summary>
        /// <returns>The 4-byte unsigned integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public uint ReadUInt32()
        {
            VerifyNotDisposed();

            EnsureBuffer(sizeof(uint));

            uint val;

            switch (this.endianess)
            {
                case Endianess.Big:
                    val = (uint)((this.buffer[this.readOffset] << 24) | (this.buffer[this.readOffset + 1] << 16) | (this.buffer[this.readOffset + 2] << 8) | this.buffer[this.readOffset + 3]);
                    break;
                case Endianess.Little:
                    val = (uint)(this.buffer[this.readOffset] | (this.buffer[this.readOffset + 1] << 8) | (this.buffer[this.readOffset + 2] << 16) | (this.buffer[this.readOffset + 3] << 24));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported byte order: " + this.endianess.ToString());
            }

            this.readOffset += sizeof(uint);

            return val;
        }

        /// <summary>
        /// Reads a 8-byte unsigned integer.
        /// </summary>
        /// <returns>The 8-byte unsigned integer.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        public ulong ReadUInt64()
        {
            VerifyNotDisposed();

            EnsureBuffer(sizeof(ulong));

            uint hi;
            uint lo;

            switch (this.endianess)
            {
                case Endianess.Big:
                    hi = (uint)((this.buffer[this.readOffset] << 24) | (this.buffer[this.readOffset + 1] << 16) | (this.buffer[this.readOffset + 2] << 8) | this.buffer[this.readOffset + 3]);
                    lo = (uint)((this.buffer[this.readOffset + 4] << 24) | (this.buffer[this.readOffset + 5] << 16) | (this.buffer[this.readOffset + 6] << 8) | this.buffer[this.readOffset + 7]);
                    break;
                case Endianess.Little:
                    lo = (uint)(this.buffer[this.readOffset] | (this.buffer[this.readOffset + 1] << 8) | (this.buffer[this.readOffset + 2] << 16) | (this.buffer[this.readOffset + 3] << 24));
                    hi = (uint)(this.buffer[this.readOffset + 4] | (this.buffer[this.readOffset + 5] << 8) | (this.buffer[this.readOffset + 6] << 16) | (this.buffer[this.readOffset + 7] << 24));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported byte order: " + this.endianess.ToString());
            }

            this.readOffset += sizeof(ulong);

            return (((ulong)hi) << 32) | lo;
        }

        /// <summary>
        /// Ensures that the buffer contains at least the number of bytes requested.
        /// </summary>
        /// <param name="count">The minimum number of bytes the buffer should contain.</param>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        private void EnsureBuffer(int count)
        {
            if ((this.readOffset + count) > this.readLength)
            {
                FillBuffer(count);
            }
        }

        /// <summary>
        /// Fills the buffer with at least the number of bytes requested.
        /// </summary>
        /// <param name="minBytes">The minimum number of bytes to place in the buffer.</param>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        private void FillBuffer(int minBytes)
        {
            int bytesUnread = this.readLength - this.readOffset;

            if (bytesUnread > 0)
            {
                Buffer.BlockCopy(this.buffer, this.readOffset, this.buffer, 0, bytesUnread);
            }

            int numBytesToRead = this.bufferSize - bytesUnread;
            int numBytesRead = bytesUnread;
            do
            {
                int n = this.stream.Read(this.buffer, numBytesRead, numBytesToRead);

                if (n == 0)
                {
                    throw new EndOfStreamException();
                }

                numBytesRead += n;
                numBytesToRead -= n;

            } while (numBytesRead < minBytes);

            this.readOffset = 0;
            this.readLength = numBytesRead;
        }

        /// <summary>
        /// Gets the length of the string.
        /// </summary>
        /// <param name="endOffset">The offset that marks the end of the null-terminator search area.</param>
        /// <param name="hasNullTerminator"><c>true</c> if the string has a null terminator; otherwise, <c>false</c>.</param>
        /// <returns>The string length.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        /// <exception cref="IOException">The string is longer than <see cref="int.MaxValue"/>.</exception>
        private int GetStringLength(long endOffset, out bool hasNullTerminator)
        {
            hasNullTerminator = false;

            long oldPosition = this.Position;

            while (this.Position < endOffset)
            {
                if (ReadByteInternal() == 0)
                {
                    hasNullTerminator = true;
                    break;
                }
            }

            long length = this.Position - oldPosition;
            if (hasNullTerminator)
            {
                // Subtract the null terminator from the string length.
                length--;
            }

            this.Position = oldPosition;

            if (length > int.MaxValue)
            {
                throw new IOException($"The string is longer than { int.MaxValue }.");
            }

            return (int)length;
        }

        /// <summary>
        /// Reads the next byte from the current stream.
        /// </summary>
        /// <returns>The next byte read from the current stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        private byte ReadByteInternal()
        {
            EnsureBuffer(sizeof(byte));

            byte val = this.buffer[this.readOffset];
            this.readOffset += sizeof(byte);

            return val;
        }

        /// <summary>
        /// Reads the specified number of bytes from the stream, starting from a specified point in the byte array.
        /// </summary>
        /// <param name="bytes">The bytes.</param>
        /// <param name="offset">The starting offset in the array.</param>
        /// <param name="count">The count.</param>
        /// <returns>The number of bytes read from the stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream has been reached.</exception>
        private int ReadInternal(byte[] bytes, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            if ((this.readOffset + count) <= this.readLength)
            {
                Buffer.BlockCopy(this.buffer, this.readOffset, bytes, offset, count);
                this.readOffset += count;

                return count;
            }
            else
            {
                // Ensure that any bytes at the end of the current buffer are included.
                int bytesUnread = this.readLength - this.readOffset;

                if (bytesUnread > 0)
                {
                    Buffer.BlockCopy(this.buffer, this.readOffset, bytes, offset, bytesUnread);
                }

                // Invalidate the existing buffer.
                this.readOffset = 0;
                this.readLength = 0;

                int totalBytesRead = bytesUnread;

                totalBytesRead += this.stream.Read(bytes, offset + bytesUnread, count - bytesUnread);

                return totalBytesRead;
            }
        }

        /// <summary>
        /// Verifies that the <see cref="EndianBinaryReader"/> has not been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
        private void VerifyNotDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(EndianBinaryReader));
            }
        }
    }
}
