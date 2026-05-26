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
using System.Text;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Streaming.Core;
using PdfSharp.Streaming.Graphics;

namespace PdfSharp.Streaming.Pages
{
    /// <summary>
    /// Represents a single page in streaming mode.
    /// Manages page content, resources, and handles page lifecycle (rendering and flushing).
    /// </summary>
    public class StreamingPdfPage : IAsyncDisposable
    {
        private readonly int _pageNumber;
        private readonly double _mediaBoxWidth;
        private readonly double _mediaBoxHeight;
        private StreamingPageContentStream? _contentStream;
        private StreamingPageResources? _resources;
        private StreamingXGraphicsRenderer? _renderer;
        private bool _flushed;
        private bool _disposed;
        private int _pageObjectId;
        private int _contentsObjectId;
        private int _resourcesObjectId;

        /// <summary>
        /// Gets the page number (1-based).
        /// </summary>
        public int PageNumber => _pageNumber;

        /// <summary>
        /// Gets the page width in user space units.
        /// </summary>
        public double Width => _mediaBoxWidth;

        /// <summary>
        /// Gets the page height in user space units.
        /// </summary>
        public double Height => _mediaBoxHeight;

        /// <summary>
        /// Gets the page content stream.
        /// </summary>
        public StreamingPageContentStream? ContentStream => _contentStream;

        /// <summary>
        /// Gets the page resources.
        /// </summary>
        public StreamingPageResources? Resources => _resources;

        /// <summary>
        /// Gets whether this page has been flushed to the PDF stream.
        /// </summary>
        public bool IsFlushed => _flushed;

        /// <summary>
        /// Initializes a new instance of StreamingPdfPage.
        /// </summary>
        /// <param name="pageNumber">The page number (1-based).</param>
        /// <param name="mediaBoxWidth">Page width in points (default 612 for letter).</param>
        /// <param name="mediaBoxHeight">Page height in points (default 792 for letter).</param>
        public StreamingPdfPage(int pageNumber, double mediaBoxWidth = 612, double mediaBoxHeight = 792)
        {
            if (pageNumber < 1)
                throw new ArgumentException("Page number must be >= 1.", nameof(pageNumber));
            if (mediaBoxWidth <= 0 || mediaBoxHeight <= 0)
                throw new ArgumentException("MediaBox dimensions must be positive.");

            _pageNumber = pageNumber;
            _mediaBoxWidth = mediaBoxWidth;
            _mediaBoxHeight = mediaBoxHeight;

            // Initialize resources
            _contentStream = new StreamingPageContentStream();
            _resources = new StreamingPageResources();
        }

        /// <summary>
        /// Creates a graphics context for drawing on this page.
        /// Allows user code like: using (var gfx = page.CreateGraphics()) { gfx.DrawString(...); }
        /// </summary>
        public IStreamingGraphicsBackend CreateGraphics()
        {
            ThrowIfDisposed();

            if (_contentStream == null || _resources == null)
                throw new InvalidOperationException("Page has been flushed.");

            _renderer = new StreamingXGraphicsRenderer(_contentStream, _resources);
            return _renderer;
        }

        /// <summary>
        /// Flushes the page to the PDF writer.
        /// This writes all page objects (contents, resources, page dict) to the stream.
        /// After flushing, the page content is committed and the page releases its buffers.
        /// </summary>
        public async Task FlushAsync(StreamingPdfWriter writer)
        {
            ThrowIfDisposed();

            if (_flushed)
                return; // Already flushed

            if (_contentStream == null || _resources == null)
                throw new InvalidOperationException("Page has no content stream or resources.");

            // 1. Close the graphics renderer if it's still open
            _renderer?.Dispose();

            // 2. Compress the content stream
            _contentStream.Compress();

            // 3. Write the content stream object
            _contentsObjectId = writer.AllocateObjectId();
            await writer.WriteContentStreamObjectAsync(_contentsObjectId, _contentStream.GetCompressedSpan(), _contentStream.UncompressedLength);

            // 4. Write the resources dictionary object
            _resourcesObjectId = writer.AllocateObjectId();
            string resourcesDict = _resources.GenerateResourcesDictionary();
            await writer.WriteRawObjectAsync(_resourcesObjectId, resourcesDict);

            // 5. Write the page dictionary object
            _pageObjectId = writer.AllocateObjectId();
            string pageDict = GeneratePageDictionary();
            await writer.WriteRawObjectAsync(_pageObjectId, pageDict);

            _flushed = true;

            // 6. Release content buffers
            _contentStream.Dispose();
            _contentStream = null;
        }

        /// <summary>
        /// Gets the page object ID (valid only after flushing).
        /// </summary>
        public int PageObjectId
        {
            get
            {
                if (!_flushed)
                    throw new InvalidOperationException("Page has not been flushed yet.");
                return _pageObjectId;
            }
        }

        /// <summary>
        /// Gets the page contents object ID (valid only after flushing).
        /// </summary>
        public int ContentsObjectId
        {
            get
            {
                if (!_flushed)
                    throw new InvalidOperationException("Page has not been flushed yet.");
                return _contentsObjectId;
            }
        }

        /// <summary>
        /// Gets the page resources object ID (valid only after flushing).
        /// </summary>
        public int ResourcesObjectId
        {
            get
            {
                if (!_flushed)
                    throw new InvalidOperationException("Page has not been flushed yet.");
                return _resourcesObjectId;
            }
        }

        /// <summary>
        /// Generates the page dictionary in PDF syntax.
        /// Example: &lt;&lt; /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Resources 4 0 R &gt;&gt;
        /// </summary>
        private string GeneratePageDictionary()
        {
            var sb = new StringBuilder();
            sb.Append("<<");
            sb.Append(" /Type /Page");
            sb.Append($" /MediaBox [0 0 {_mediaBoxWidth:F0} {_mediaBoxHeight:F0}]");
            sb.Append($" /Contents {_contentsObjectId} 0 R");
            sb.Append($" /Resources {_resourcesObjectId} 0 R");
            sb.Append(" >>");

            return sb.ToString();
        }

        /// <summary>
        /// Asynchronously disposes the page and releases resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                _renderer?.Dispose();

                if (_contentStream != null && !_flushed)
                {
                    _contentStream.Dispose();
                }

                _resources?.Clear();
            }
            finally
            {
                _disposed = true;
            }

            await ValueTask.CompletedTask;
        }

        /// <summary>
        /// Synchronously disposes the page.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _renderer?.Dispose();

                if (_contentStream != null && !_flushed)
                {
                    _contentStream.Dispose();
                }

                _resources?.Clear();
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Throws if the page has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingPdfPage));
        }
    }
}
