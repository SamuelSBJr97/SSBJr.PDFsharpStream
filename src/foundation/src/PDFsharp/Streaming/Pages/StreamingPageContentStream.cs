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
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PdfSharp.Streaming.Pages
{
    /// <summary>
    /// Manages a single page's content stream with buffering and compression.
    /// Accumulates PDF operators and compresses them on demand using DEFLATE (FlateDecode).
    /// </summary>
    public class StreamingPageContentStream : IDisposable
    {
        private readonly MemoryPool<byte> _memoryPool;
        private MemoryPool<byte>.MemoryPoolBuffer? _uncompressedBuffer;
        private byte[]? _compressedBuffer;
        private int _uncompressedLength;
        private bool _compressed;
        private bool _disposed;

        /// <summary>
        /// Gets the uncompressed content length in bytes.
        /// </summary>
        public int UncompressedLength => _uncompressedLength;

        /// <summary>
        /// Gets the compressed content length in bytes (or 0 if not yet compressed).
        /// </summary>
        public int CompressedLength => _compressedBuffer?.Length ?? 0;

        /// <summary>
        /// Gets whether the content has been compressed.
        /// </summary>
        public bool IsCompressed => _compressed;

        /// <summary>
        /// Initializes a new instance of StreamingPageContentStream.
        /// </summary>
        /// <param name="initialCapacity">Initial capacity in bytes. Default is 64 KB.</param>
        public StreamingPageContentStream(int initialCapacity = 65536)
        {
            _memoryPool = MemoryPool<byte>.Shared;
            _uncompressedBuffer = _memoryPool.Rent(initialCapacity);
            _uncompressedLength = 0;
            _compressed = false;
        }

        /// <summary>
        /// Writes PDF operator bytes directly to the content stream buffer.
        /// </summary>
        /// <param name="operatorBytes">The bytes to write.</param>
        public void WriteOperator(ReadOnlySpan<byte> operatorBytes)
        {
            ThrowIfDisposed();

            if (_compressed)
                throw new InvalidOperationException("Cannot write to a compressed content stream.");

            // Ensure buffer has space
            if (_uncompressedBuffer == null)
                throw new InvalidOperationException("Buffer was released.");

            if (_uncompressedLength + operatorBytes.Length > _uncompressedBuffer.Memory.Length)
            {
                // Resize buffer
                var newBuffer = _memoryPool.Rent(_uncompressedBuffer.Memory.Length * 2 + operatorBytes.Length);
                _uncompressedBuffer.Memory.Span.CopyTo(newBuffer.Memory.Span);
                _uncompressedBuffer.Dispose();
                _uncompressedBuffer = newBuffer;
            }

            // Write operator bytes
            operatorBytes.CopyTo(_uncompressedBuffer.Memory.Span[_uncompressedLength..]);
            _uncompressedLength += operatorBytes.Length;
        }

        /// <summary>
        /// Writes a string as PDF operators to the content stream buffer.
        /// </summary>
        /// <param name="operatorString">The operator string to write.</param>
        public void WriteOperator(string operatorString)
        {
            var bytes = Encoding.ASCII.GetBytes(operatorString);
            WriteOperator(new ReadOnlySpan<byte>(bytes));
        }

        /// <summary>
        /// Compresses the content stream using DEFLATE (FlateDecode) algorithm.
        /// This should be called before retrieving the content for serialization.
        /// </summary>
        public void Compress()
        {
            ThrowIfDisposed();

            if (_compressed)
                return; // Already compressed

            if (_uncompressedBuffer == null || _uncompressedLength == 0)
            {
                _compressedBuffer = Array.Empty<byte>();
                _compressed = true;
                return;
            }

            // Compress using DEFLATE
            using (var compressedStream = new MemoryStream())
            {
                using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Compress, leaveOpen: true))
                {
                    deflateStream.Write(_uncompressedBuffer.Memory.Span[.._uncompressedLength]);
                }

                _compressedBuffer = compressedStream.ToArray();
            }

            _compressed = true;

            // Release uncompressed buffer
            if (_uncompressedBuffer != null)
            {
                _uncompressedBuffer.Dispose();
                _uncompressedBuffer = null;
            }
        }

        /// <summary>
        /// Gets the uncompressed content bytes.
        /// Throws if content has been compressed.
        /// </summary>
        public byte[] GetUncompressedBytes()
        {
            ThrowIfDisposed();

            if (_compressed)
                throw new InvalidOperationException("Content has been compressed. Cannot retrieve uncompressed bytes.");

            if (_uncompressedBuffer == null)
                throw new InvalidOperationException("Buffer was released.");

            var result = new byte[_uncompressedLength];
            _uncompressedBuffer.Memory.Span[.._uncompressedLength].CopyTo(result);
            return result;
        }

        /// <summary>
        /// Gets the compressed content bytes.
        /// Must call Compress() first.
        /// </summary>
        public byte[] GetCompressedBytes()
        {
            ThrowIfDisposed();

            if (!_compressed)
                throw new InvalidOperationException("Content has not been compressed. Call Compress() first.");

            return _compressedBuffer ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Gets the compressed content as a span.
        /// </summary>
        public ReadOnlySpan<byte> GetCompressedSpan()
        {
            ThrowIfDisposed();

            if (!_compressed)
                throw new InvalidOperationException("Content has not been compressed. Call Compress() first.");

            return _compressedBuffer ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Disposes the content stream and releases buffers.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (_uncompressedBuffer != null)
            {
                _uncompressedBuffer.Dispose();
                _uncompressedBuffer = null;
            }

            _compressedBuffer = null;
            _disposed = true;
        }

        /// <summary>
        /// Throws if the object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingPageContentStream));
        }
    }
}
