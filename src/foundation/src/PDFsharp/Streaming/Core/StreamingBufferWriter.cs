// Copyright (c) 2005-2024 empira Software GmbH, Cologne, Germany
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included
// in all copies or substantial portions of the Software.

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;

namespace PdfSharp.Streaming.Core
{
    /// <summary>
    /// Provides buffered writing to an underlying stream with IBufferWriter interface.
    /// Reduces syscall overhead by batching writes to the underlying stream.
    /// </summary>
    public class StreamingBufferWriter : IBufferWriter<byte>, IAsyncDisposable
    {
        private const int DefaultBufferSize = 65536; // 64 KB

        private byte[] _buffer;
        private int _position;
        private readonly Stream _underlyingStream;
        private readonly int _bufferSize;
        private bool _disposed;
        private long _totalBytesWritten;

        /// <summary>
        /// Gets the current position in the underlying stream (accounting for buffered data).
        /// This is manually tracked and works with forward-only streams.
        /// </summary>
        public long Position 
        { 
            get 
            { 
                if (_disposed) throw new ObjectDisposedException(nameof(StreamingBufferWriter));
                return _totalBytesWritten + _position; 
            } 
        }

        /// <summary>
        /// Gets the number of bytes buffered and not yet flushed.
        /// </summary>
        public int BufferedCount => _position;

        /// <summary>
        /// Initializes a new instance of StreamingBufferWriter.
        /// </summary>
        /// <param name="underlyingStream">The underlying stream to write to.</param>
        /// <param name="bufferSize">Size of the buffer in bytes. Default is 64 KB.</param>
        public StreamingBufferWriter(Stream underlyingStream, int bufferSize = DefaultBufferSize)
        {
            if (!underlyingStream.CanWrite)
                throw new ArgumentException("Stream must be writable.", nameof(underlyingStream));

            _underlyingStream = underlyingStream;
            _bufferSize = bufferSize;
            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            _position = 0;
            _totalBytesWritten = 0;
        }

        /// <summary>
        /// Writes the specified bytes to the buffer, flushing if necessary.
        /// </summary>
        public void Write(ReadOnlySpan<byte> data)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingBufferWriter));

            int bytesToWrite = data.Length;
            int offset = 0;

            while (bytesToWrite > 0)
            {
                int spacesInBuffer = _bufferSize - _position;

                if (spacesInBuffer == 0)
                {
                    // Buffer is full, flush it
                    Flush();
                    spacesInBuffer = _bufferSize;
                }

                int bytesToCopyNow = Math.Min(bytesToWrite, spacesInBuffer);
                data.Slice(offset, bytesToCopyNow).CopyTo(new Span<byte>(_buffer, _position, bytesToCopyNow));

                _position += bytesToCopyNow;
                offset += bytesToCopyNow;
                bytesToWrite -= bytesToCopyNow;
            }
        }

        /// <summary>
        /// Flushes the buffer to the underlying stream.
        /// </summary>
        public void Flush()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingBufferWriter));

            if (_position > 0)
            {
                _underlyingStream.Write(_buffer, 0, _position);
                _totalBytesWritten += _position;
                _position = 0;
            }
        }

        /// <summary>
        /// Flushes the buffer to the underlying stream asynchronously.
        /// </summary>
        public async Task FlushAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingBufferWriter));

            if (_position > 0)
            {
                await _underlyingStream.WriteAsync(_buffer, 0, _position);
                _totalBytesWritten += _position;
                _position = 0;
            }
        }

        /// <summary>
        /// Advances the buffer position (for IBufferWriter interface).
        /// </summary>
        public void Advance(int count)
        {
            if (count < 0 || count > _bufferSize - _position)
                throw new ArgumentException("Invalid count.", nameof(count));

            _position += count;

            if (_position >= _bufferSize)
            {
                Flush();
            }
        }

        /// <summary>
        /// Gets a memory block for writing (for IBufferWriter interface).
        /// </summary>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingBufferWriter));

            int requiredSize = sizeHint > 0 ? sizeHint : _bufferSize;

            if (_position + requiredSize > _bufferSize)
            {
                Flush();
            }

            return new Memory<byte>(_buffer, _position, _bufferSize - _position);
        }

        /// <summary>
        /// Gets a span for writing (for IBufferWriter interface).
        /// </summary>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return GetMemory(sizeHint).Span;
        }

        /// <summary>
        /// Disposes the buffer writer and releases resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                await FlushAsync();
            }
            finally
            {
                if (_buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null!;
                }
                _disposed = true;
            }
        }

        /// <summary>
        /// Disposes the buffer writer and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Flush();
            }
            finally
            {
                if (_buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null!;
                }
                _disposed = true;
            }
        }
    }
}
