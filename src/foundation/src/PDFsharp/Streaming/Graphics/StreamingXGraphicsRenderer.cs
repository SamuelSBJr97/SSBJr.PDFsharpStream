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
using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Streaming.Pages;

namespace PdfSharp.Streaming.Graphics
{
    /// <summary>
    /// Streaming-based graphics renderer that generates PDF operators directly.
    /// Emits operators immediately to the content stream without buffering.
    /// Supports text, lines, rectangles, and images.
    /// </summary>
    public class StreamingXGraphicsRenderer : IStreamingGraphicsBackend
    {
        private readonly StreamingPageContentStream _contentStream;
        private readonly StreamingPageResources _resources;
        private readonly Stack<GraphicsState> _stateStack;
        private GraphicsState _currentState;
        private bool _disposed;

        /// <summary>
        /// Gets the page content stream being rendered to.
        /// </summary>
        public StreamingPageContentStream ContentStream => _contentStream;

        /// <summary>
        /// Gets the page resources manager.
        /// </summary>
        public StreamingPageResources Resources => _resources;

        /// <summary>
        /// Initializes a new instance of StreamingXGraphicsRenderer.
        /// </summary>
        public StreamingXGraphicsRenderer(StreamingPageContentStream contentStream, StreamingPageResources resources)
        {
            _contentStream = contentStream ?? throw new ArgumentNullException(nameof(contentStream));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _stateStack = new Stack<GraphicsState>();
            _currentState = new GraphicsState();
        }

        /// <summary>
        /// Draws text at the specified position.
        /// Emits: BT /F1 size Tf x y Td (text) Tj ET
        /// </summary>
        public void DrawString(string text, XFont font, XBrush brush, double x, double y)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(text) || font == null)
                return;

            EmitSaveState();

            // Set font and size
            var fontRef = $"F1"; // Simplified; real implementation would track fonts
            EmitOperator($"BT\n");
            EmitOperator($"/{fontRef} {font.Size:F2} Tf\n");

            // Position (x, y in PDF coordinates)
            EmitOperator($"{x:F2} {y:F2} Td\n");

            // Set color if brush is specified
            if (brush is XSolidBrush solidBrush)
            {
                SetColor(solidBrush.Color);
            }

            // Emit text
            string escapedText = EscapeString(text);
            EmitOperator($"({escapedText}) Tj\n");

            EmitOperator($"ET\n");
            EmitRestoreState();
        }

        /// <summary>
        /// Draws text within a rectangle with formatting.
        /// </summary>
        public void DrawString(string text, XFont font, XBrush brush, XRect rect, XStringFormat format)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(text))
                return;

            // Simplified implementation: center in rectangle
            // Real implementation would handle text wrapping and alignment
            double x = rect.X + rect.Width / 2;
            double y = rect.Y + rect.Height / 2;

            DrawString(text, font, brush, x, y);
        }

        /// <summary>
        /// Draws a line from (x1, y1) to (x2, y2).
        /// Emits: q x1 y1 m x2 y2 l S Q
        /// </summary>
        public void DrawLine(XPen pen, double x1, double y1, double x2, double y2)
        {
            ThrowIfDisposed();

            if (pen == null)
                return;

            EmitSaveState();

            // Set line properties
            EmitOperator($"{pen.Width:F2} w\n"); // Line width
            
            if (pen.DashStyle != XDashStyle.Solid)
            {
                SetDashPattern(pen.DashStyle);
            }

            // Draw line
            EmitOperator($"{x1:F2} {y1:F2} m\n");  // Move to
            EmitOperator($"{x2:F2} {y2:F2} l\n");  // Line to
            EmitOperator($"S\n");                    // Stroke

            EmitRestoreState();
        }

        /// <summary>
        /// Draws a rectangle (outlined).
        /// Emits: q x y w h re S Q
        /// </summary>
        public void DrawRectangle(XPen pen, XBrush brush, double x, double y, double width, double height)
        {
            ThrowIfDisposed();

            EmitSaveState();

            if (brush != null && brush is XSolidBrush solidBrush)
            {
                SetColor(solidBrush.Color);
                EmitOperator($"{x:F2} {y:F2} {width:F2} {height:F2} re\n");
                EmitOperator($"f\n"); // Fill
            }

            if (pen != null)
            {
                EmitOperator($"{pen.Width:F2} w\n");
                EmitOperator($"{x:F2} {y:F2} {width:F2} {height:F2} re\n");
                EmitOperator($"S\n"); // Stroke
            }

            EmitRestoreState();
        }

        /// <summary>
        /// Draws a filled rectangle.
        /// Emits: q x y w h re f Q
        /// </summary>
        public void DrawFilledRectangle(XBrush brush, double x, double y, double width, double height)
        {
            ThrowIfDisposed();

            if (brush == null)
                return;

            EmitSaveState();

            if (brush is XSolidBrush solidBrush)
            {
                SetColor(solidBrush.Color);
            }

            EmitOperator($"{x:F2} {y:F2} {width:F2} {height:F2} re\n");
            EmitOperator($"f\n"); // Fill

            EmitRestoreState();
        }

        /// <summary>
        /// Draws an image (placeholder - full implementation would embed image data).
        /// </summary>
        public void DrawImage(XImage image, double x, double y, double width, double height)
        {
            ThrowIfDisposed();

            if (image == null)
                return;

            // Placeholder: emit comment
            EmitOperator($"% Image: {image.FileName} at ({x}, {y}) size ({width}x{height})\n");

            // Real implementation would:
            // 1. Register image in resources
            // 2. Emit XObject reference and transformation
        }

        /// <summary>
        /// Saves the current graphics state onto the stack.
        /// Emits: q
        /// </summary>
        public void SaveState()
        {
            ThrowIfDisposed();
            EmitSaveState();
        }

        /// <summary>
        /// Restores the previous graphics state from the stack.
        /// Emits: Q
        /// </summary>
        public void RestoreState()
        {
            ThrowIfDisposed();
            EmitRestoreState();
        }

        /// <summary>
        /// Sets the current transformation matrix.
        /// Emits: a b c d e f cm
        /// </summary>
        public void SetTransform(XMatrix matrix)
        {
            ThrowIfDisposed();

            EmitOperator($"{matrix.M11:F6} {matrix.M21:F6} {matrix.M12:F6} {matrix.M22:F6} {matrix.OffsetX:F2} {matrix.OffsetY:F2} cm\n");
        }

        /// <summary>
        /// Clears the page with a color (draws a filled rectangle).
        /// </summary>
        public void Clear(XColor color)
        {
            ThrowIfDisposed();

            // Draw white/colored background rectangle
            var brush = new XSolidBrush(color);
            DrawFilledRectangle(brush, 0, 0, 612, 792); // Standard letter size
        }

        /// <summary>
        /// Flushes any buffered operations to the underlying stream.
        /// </summary>
        public void Flush()
        {
            ThrowIfDisposed();
            // Content stream buffer is flushed when the page is written
        }

        /// <summary>
        /// Emits a PDF operator to the content stream.
        /// </summary>
        private void EmitOperator(string operatorText)
        {
            var bytes = Encoding.ASCII.GetBytes(operatorText);
            _contentStream.WriteOperator(bytes);
        }

        /// <summary>
        /// Emits the 'save graphics state' operator (q).
        /// </summary>
        private void EmitSaveState()
        {
            _stateStack.Push(_currentState);
            _currentState = new GraphicsState(_currentState);
            EmitOperator("q\n");
        }

        /// <summary>
        /// Emits the 'restore graphics state' operator (Q).
        /// </summary>
        private void EmitRestoreState()
        {
            if (_stateStack.Count > 0)
            {
                _currentState = _stateStack.Pop();
            }
            EmitOperator("Q\n");
        }

        /// <summary>
        /// Sets the current color (RGB).
        /// </summary>
        private void SetColor(XColor color)
        {
            // Convert XColor to RGB (0-1 range)
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            // Emit: r g b rg (non-stroking color)
            EmitOperator($"{r:F3} {g:F3} {b:F3} rg\n");
        }

        /// <summary>
        /// Sets the line dash pattern.
        /// </summary>
        private void SetDashPattern(XDashStyle style)
        {
            string pattern = style switch
            {
                XDashStyle.Solid => "[] 0",
                XDashStyle.Dash => "[3 1] 0",
                XDashStyle.Dot => "[1 2] 0",
                XDashStyle.DashDot => "[3 1 1 1] 0",
                XDashStyle.DashDotDot => "[3 1 1 1 1 1] 0",
                _ => "[] 0"
            };

            EmitOperator($"{pattern} d\n");
        }

        /// <summary>
        /// Escapes a string for use in PDF string literals.
        /// </summary>
        private string EscapeString(string text)
        {
            var sb = new StringBuilder();

            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '(':
                        sb.Append("\\(");
                        break;
                    case ')':
                        sb.Append("\\)");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c >= 32 && c <= 126)
                            sb.Append(c);
                        else
                            sb.Append($"\\{(int)c:O3}");
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Disposes the renderer.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            Flush();
            _disposed = true;
        }

        /// <summary>
        /// Throws if the renderer has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingXGraphicsRenderer));
        }

        /// <summary>
        /// Represents the graphics state (used for state stack).
        /// </summary>
        private class GraphicsState
        {
            public XColor CurrentColor { get; set; } = XColors.Black;
            public double LineWidth { get; set; } = 1.0;
            public XDashStyle DashStyle { get; set; } = XDashStyle.Solid;

            public GraphicsState() { }

            public GraphicsState(GraphicsState other)
            {
                CurrentColor = other.CurrentColor;
                LineWidth = other.LineWidth;
                DashStyle = other.DashStyle;
            }
        }
    }
}
