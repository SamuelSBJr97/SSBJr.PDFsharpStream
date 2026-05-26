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
using System.Text;
using System.Threading.Tasks;
using PdfSharp.Streaming.Core;

namespace PdfSharp.Streaming.Model
{
    /// <summary>
    /// Core PDF writer that manages streaming PDF generation with incremental object writing.
    /// Writes objects directly to a file stream with buffering for performance.
    /// Handles offset tracking for xref table generation.
    /// </summary>
    public class StreamingPdfWriter : IAsyncDisposable
    {
        private readonly Stream _outputStream;
        private StreamingBufferWriter? _bufferedWriter;
        private StreamingXrefTable _xrefTable;
        private List<int> _pageObjectIds;
        private int _catalogObjectId;
        private int _pagesRootObjectId;
        private int _infoDictObjectId;
        private bool _finalized;
        private bool _disposed;
        private bool _ownsStream;

        /// <summary>
        /// Gets the number of objects written so far.
        /// </summary>
        public int ObjectCount => _xrefTable.ObjectCount;

        /// <summary>
        /// Gets whether the PDF has been finalized.
        /// </summary>
        public bool IsFinalized => _finalized;

        /// <summary>
        /// Initializes a new instance of StreamingPdfWriter with a file path.
        /// </summary>
        /// <param name="filePath">The file path where the PDF will be written.</param>
        public StreamingPdfWriter(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            _outputStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            _ownsStream = true;
            _bufferedWriter = new StreamingBufferWriter(_outputStream, 65536);
            _xrefTable = new StreamingXrefTable();
            _pageObjectIds = new List<int>();

            WriteFileHeader();
        }

        /// <summary>
        /// Initializes a new instance of StreamingPdfWriter with a stream.
        /// Works with forward-only streams (like HTTP response streams).
        /// </summary>
        /// <param name="stream">The stream where the PDF will be written. Must be writable.</param>
        /// <param name="ownsStream">If true, the stream will be disposed when the writer is disposed.</param>
        public StreamingPdfWriter(Stream stream, bool ownsStream = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("Stream must be writable.", nameof(stream));

            _outputStream = stream;
            _ownsStream = ownsStream;
            _bufferedWriter = new StreamingBufferWriter(stream, 65536);
            _xrefTable = new StreamingXrefTable();
            _pageObjectIds = new List<int>();

            WriteFileHeader();
        }

        /// <summary>
        /// Allocates a new object ID for the next object.
        /// </summary>
        public int AllocateObjectId()
        {
            ThrowIfDisposed();
            return _xrefTable.GetNextObjectId();
        }

        /// <summary>
        /// Writes a content stream object (compressed page content).
        /// </summary>
        public async Task WriteContentStreamObjectAsync(int objNum, ReadOnlySpan<byte> compressedContent, int uncompressedLength)
        {
            ThrowIfDisposed();

            long offset = _bufferedWriter!.Position;
            _xrefTable.RegisterObject(objNum, offset);

            // Write object header
            var header = $"{objNum} 0 obj\n".AsSpan();
            _bufferedWriter.Write(header);

            // Write stream dictionary
            var dictStart = Encoding.ASCII.GetBytes($"<< /Length {compressedContent.Length} /Filter /FlateDecode >>\nstream\n");
            _bufferedWriter.Write(dictStart);

            // Write compressed content
            _bufferedWriter.Write(compressedContent);

            // Write stream footer
            var dictEnd = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            _bufferedWriter.Write(dictEnd);

            await _bufferedWriter.FlushAsync();
        }

        /// <summary>
        /// Writes a raw PDF object (dictionary, array, etc.).
        /// </summary>
        public async Task WriteRawObjectAsync(int objNum, string content)
        {
            ThrowIfDisposed();

            long offset = _bufferedWriter!.Position;
            _xrefTable.RegisterObject(objNum, offset);

            // Write object header
            var header = $"{objNum} 0 obj\n".AsSpan();
            _bufferedWriter.Write(header);

            // Write content
            var contentBytes = Encoding.ASCII.GetBytes(content);
            _bufferedWriter.Write(contentBytes);

            // Write object footer
            var footer = Encoding.ASCII.GetBytes("\nendobj\n");
            _bufferedWriter.Write(footer);

            await _bufferedWriter.FlushAsync();
        }

        /// <summary>
        /// Registers a page object ID.
        /// </summary>
        public void RegisterPageObject(int pageObjectId)
        {
            _pageObjectIds.Add(pageObjectId);
        }

        /// <summary>
        /// Finalizes the PDF document.
        /// Writes the pages tree, catalog, xref table, and trailer.
        /// Must be called before the document is complete.
        /// </summary>
        public async Task FinalizeAsync()
        {
            ThrowIfDisposed();

            if (_finalized)
                throw new InvalidOperationException("Document already finalized.");

            // 1. Write Pages tree root object
            _pagesRootObjectId = AllocateObjectId();
            string pagesDict = GeneratePagesTree();
            await WriteRawObjectAsync(_pagesRootObjectId, pagesDict);
            _xrefTable.SetPageTreeRootObjectId(_pagesRootObjectId);

            // 2. Write Catalog (root) object
            _catalogObjectId = AllocateObjectId();
            string catalogDict = GenerateCatalog();
            await WriteRawObjectAsync(_catalogObjectId, catalogDict);
            _xrefTable.SetRootObjectId(_catalogObjectId);

            // 3. Write Info dictionary object (optional but recommended)
            _infoDictObjectId = AllocateObjectId();
            string infoDict = GenerateInfoDictionary();
            await WriteRawObjectAsync(_infoDictObjectId, infoDict);
            _xrefTable.SetInfoDictObjectId(_infoDictObjectId);

            // 4. Flush the buffer
            await _bufferedWriter!.FlushAsync();

            // 5. Calculate and write xref table
            byte[] xrefBytes = _xrefTable.GenerateXrefTable();
            long xrefOffset = _bufferedWriter.Position;  // Use buffered writer's position (works with forward-only streams)
            _bufferedWriter.Write(xrefBytes);

            // 6. Write trailer
            string trailer = _xrefTable.GenerateTrailer();
            var trailerBytes = Encoding.ASCII.GetBytes(trailer + "\n");
            _bufferedWriter.Write(trailerBytes);

            // 7. Write startxref and EOF
            string startxref = $"startxref\n{xrefOffset}\n%%EOF\n";
            var startxrefBytes = Encoding.ASCII.GetBytes(startxref);
            _bufferedWriter.Write(startxrefBytes);

            // 8. Final flush to ensure all data is written
            await _bufferedWriter.FlushAsync();

            _finalized = true;
        }

        /// <summary>
        /// Generates the Pages tree dictionary.
        /// References all page objects.
        /// </summary>
        private string GeneratePagesTree()
        {
            var sb = new StringBuilder();
            sb.Append("<<");
            sb.Append(" /Type /Pages");
            sb.Append($" /Count {_pageObjectIds.Count}");
            sb.Append(" /Kids [");

            foreach (int pageObjId in _pageObjectIds)
            {
                sb.Append($"{pageObjId} 0 R ");
            }

            sb.Append("]");
            sb.Append(" >>");

            return sb.ToString();
        }

        /// <summary>
        /// Generates the Catalog (root) dictionary.
        /// References the Pages tree.
        /// </summary>
        private string GenerateCatalog()
        {
            return $"<< /Type /Catalog /Pages {_pagesRootObjectId} 0 R >>";
        }

        /// <summary>
        /// Generates the Info dictionary (document metadata).
        /// </summary>
        private string GenerateInfoDictionary()
        {
            var sb = new StringBuilder();
            sb.Append("<<");
            sb.Append($" /Producer (PDFsharp Streaming {PdfSharp.Pdf.PdfDocument.VersionString})");
            sb.Append($" /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss})");
            sb.Append(" >>");

            return sb.ToString();
        }

        /// <summary>
        /// Writes the PDF file header.
        /// </summary>
        private void WriteFileHeader()
        {
            var header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
            _bufferedWriter!.Write(header);

            // Write binary comment (helps with binary file detection)
            var binaryComment = new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' };
            _bufferedWriter.Write(binaryComment);
        }

        /// <summary>
        /// Asynchronously disposes the writer and closes the underlying stream if owned.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                if (!_finalized)
                {
                    await FinalizeAsync();
                }

                if (_bufferedWriter != null)
                {
                    await _bufferedWriter.DisposeAsync();
                    _bufferedWriter = null;
                }

                if (_ownsStream)
                {
                    _outputStream?.Dispose();
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Synchronously disposes the writer.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                if (!_finalized)
                {
                    FinalizeAsync().GetAwaiter().GetResult();
                }

                _bufferedWriter?.Dispose();

                if (_ownsStream)
                {
                    _outputStream?.Dispose();
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Throws if the writer has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingPdfWriter));
        }
    }
}
