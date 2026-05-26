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
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Streaming.Model;
using PdfSharp.Streaming.Pages;

namespace PdfSharp.Streaming
{
    /// <summary>
    /// High-level facade for streaming PDF document generation.
    /// Provides a simple API for creating multi-page PDFs with constant memory usage.
    /// </summary>
    public class StreamingPdfDocument : IAsyncDisposable
    {
        private readonly string? _filePath;
        private readonly Stream? _outputStream;
        private readonly bool _ownsStream;
        private StreamingPdfWriter? _writer;
        private List<StreamingPdfPage> _pages;
        private int _pageCount;
        private bool _disposed;

        /// <summary>
        /// Gets the file path where the PDF will be written (if using file-based constructor).
        /// </summary>
        public string? FilePath => _filePath;

        /// <summary>
        /// Gets the number of pages in the document.
        /// </summary>
        public int PageCount => _pageCount;

        /// <summary>
        /// Gets the underlying writer (advanced usage).
        /// </summary>
        public StreamingPdfWriter? Writer => _writer;

        /// <summary>
        /// Initializes a new instance of StreamingPdfDocument with a file path.
        /// </summary>
        /// <param name="filePath">The output file path for the PDF.</param>
        public StreamingPdfDocument(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            _filePath = filePath;
            _outputStream = null;
            _ownsStream = false;
            _writer = new StreamingPdfWriter(filePath);
            _pages = new List<StreamingPdfPage>();
            _pageCount = 0;
        }

        /// <summary>
        /// Initializes a new instance of StreamingPdfDocument with a stream.
        /// Works with forward-only streams (like HTTP response streams).
        /// </summary>
        /// <param name="stream">The stream where the PDF will be written. Must be writable.</param>
        /// <param name="ownsStream">If true, the stream will be disposed when the document is disposed.</param>
        public StreamingPdfDocument(Stream stream, bool ownsStream = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("Stream must be writable.", nameof(stream));

            _filePath = null;
            _outputStream = stream;
            _ownsStream = ownsStream;
            _writer = new StreamingPdfWriter(stream, ownsStream);
            _pages = new List<StreamingPdfPage>();
            _pageCount = 0;
        }

        /// <summary>
        /// Creates a new page and adds it to the document.
        /// </summary>
        /// <param name="width">Page width in points (default 612 for letter).</param>
        /// <param name="height">Page height in points (default 792 for letter).</param>
        /// <returns>A new StreamingPdfPage ready for drawing.</returns>
        public StreamingPdfPage AddPage(double width = 612, double height = 792)
        {
            ThrowIfDisposed();

            if (_writer == null)
                throw new InvalidOperationException("Document has been finalized.");

            _pageCount++;
            var page = new StreamingPdfPage(_pageCount, width, height);
            _pages.Add(page);

            return page;
        }

        /// <summary>
        /// Flushes a page to the PDF writer and releases its buffers.
        /// Call this after drawing on a page to commit it to disk.
        /// </summary>
        /// <param name="page">The page to flush.</param>
        public async Task FlushPageAsync(StreamingPdfPage page)
        {
            ThrowIfDisposed();

            if (_writer == null)
                throw new InvalidOperationException("Writer is not available.");

            await page.FlushAsync(_writer);
            _writer.RegisterPageObject(page.PageObjectId);
        }

        /// <summary>
        /// Flushes all pages that haven't been flushed yet.
        /// </summary>
        public async Task FlushAllPagesAsync()
        {
            ThrowIfDisposed();

            foreach (var page in _pages)
            {
                if (!page.IsFlushed)
                {
                    await FlushPageAsync(page);
                }
            }
        }

        /// <summary>
        /// Finalizes the PDF document and writes it to disk.
        /// Must be called before disposing to complete the PDF.
        /// </summary>
        public async Task FinalizeAsync()
        {
            ThrowIfDisposed();

            if (_writer == null)
                throw new InvalidOperationException("Document already finalized.");

            // Flush any remaining pages
            await FlushAllPagesAsync();

            // Finalize the writer
            await _writer.FinalizeAsync();
        }

        /// <summary>
        /// Asynchronously disposes the document.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                // Finalize if not already done
                if (_writer != null && !_writer.IsFinalized)
                {
                    await FinalizeAsync();
                }

                // Dispose pages
                foreach (var page in _pages)
                {
                    await page.DisposeAsync();
                }

                // Dispose writer
                if (_writer != null)
                {
                    await _writer.DisposeAsync();
                    _writer = null;
                }

                _pages.Clear();
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Synchronously disposes the document.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Finalize if not already done
                if (_writer != null && !_writer.IsFinalized)
                {
                    FinalizeAsync().GetAwaiter().GetResult();
                }

                // Dispose pages
                foreach (var page in _pages)
                {
                    page.Dispose();
                }

                // Dispose writer
                if (_writer != null)
                {
                    _writer.Dispose();
                    _writer = null;
                }

                _pages.Clear();
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Throws if the document has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingPdfDocument));
        }
    }
}
