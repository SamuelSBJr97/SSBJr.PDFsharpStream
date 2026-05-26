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
using PdfSharp.Drawing;

namespace PdfSharp.Streaming.Graphics
{
    /// <summary>
    /// Interface for streaming-based graphics backend that generates PDF operators directly.
    /// Replaces the traditional StringBuilder-based XGraphicsPdfRenderer.
    /// </summary>
    public interface IStreamingGraphicsBackend : IDisposable
    {
        /// <summary>
        /// Draws a string at the specified location.
        /// </summary>
        void DrawString(string text, XFont font, XBrush brush, double x, double y);

        /// <summary>
        /// Draws a string within a rectangle.
        /// </summary>
        void DrawString(string text, XFont font, XBrush brush, XRect rect, XStringFormat format);

        /// <summary>
        /// Draws a line between two points.
        /// </summary>
        void DrawLine(XPen pen, double x1, double y1, double x2, double y2);

        /// <summary>
        /// Draws a rectangle.
        /// </summary>
        void DrawRectangle(XPen pen, XBrush brush, double x, double y, double width, double height);

        /// <summary>
        /// Draws a filled rectangle.
        /// </summary>
        void DrawFilledRectangle(XBrush brush, double x, double y, double width, double height);

        /// <summary>
        /// Draws an image at the specified location.
        /// </summary>
        void DrawImage(XImage image, double x, double y, double width, double height);

        /// <summary>
        /// Saves the current graphics state.
        /// </summary>
        void SaveState();

        /// <summary>
        /// Restores the previous graphics state.
        /// </summary>
        void RestoreState();

        /// <summary>
        /// Sets the current transformation matrix.
        /// </summary>
        void SetTransform(XMatrix matrix);

        /// <summary>
        /// Clears the entire page with the specified color.
        /// </summary>
        void Clear(XColor color);

        /// <summary>
        /// Flushes any buffered operations to the underlying stream.
        /// </summary>
        void Flush();
    }
}
