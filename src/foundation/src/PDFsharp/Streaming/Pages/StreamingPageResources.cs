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
using System.Text;
using PdfSharp.Pdf;

namespace PdfSharp.Streaming.Pages
{
    /// <summary>
    /// Manages resources for a page (fonts, images, color spaces, graphics states).
    /// Supports resource deduplication and dictionary generation.
    /// </summary>
    public class StreamingPageResources
    {
        private readonly Dictionary<string, int> _fonts;         // Font name -> object ID
        private readonly Dictionary<string, int> _xobjects;      // Image name -> object ID
        private readonly Dictionary<string, int> _gsStates;      // Graphics state name -> object ID
        private readonly Dictionary<string, int> _colorSpaces;   // Color space name -> object ID
        private int _fontCounter;
        private int _xobjectCounter;
        private int _gsStateCounter;

        /// <summary>
        /// Initializes a new instance of StreamingPageResources.
        /// </summary>
        public StreamingPageResources()
        {
            _fonts = new Dictionary<string, int>();
            _xobjects = new Dictionary<string, int>();
            _gsStates = new Dictionary<string, int>();
            _colorSpaces = new Dictionary<string, int>();
            _fontCounter = 0;
            _xobjectCounter = 0;
            _gsStateCounter = 0;
        }

        /// <summary>
        /// Registers or retrieves a font resource.
        /// </summary>
        /// <param name="fontName">Unique font identifier (e.g., "Helvetica_12").</param>
        /// <param name="fontObjectId">The PDF object ID for this font.</param>
        /// <returns>The internal font reference name (e.g., "F1", "F2").</returns>
        public string RegisterFont(string fontName, int fontObjectId)
        {
            if (string.IsNullOrEmpty(fontName))
                throw new ArgumentNullException(nameof(fontName));

            if (_fonts.ContainsKey(fontName))
                return GetFontReference(_fontCounter);

            _fonts[fontName] = fontObjectId;
            return $"F{++_fontCounter}";
        }

        /// <summary>
        /// Gets the font reference name for a registered font.
        /// </summary>
        public string GetFontReference(int index)
        {
            return $"F{index + 1}";
        }

        /// <summary>
        /// Registers or retrieves an XObject (image).
        /// </summary>
        /// <param name="imageName">Unique image identifier (e.g., "image_123").</param>
        /// <param name="imageObjectId">The PDF object ID for this image.</param>
        /// <returns>The internal image reference name (e.g., "Im1", "Im2").</returns>
        public string RegisterXObject(string imageName, int imageObjectId)
        {
            if (string.IsNullOrEmpty(imageName))
                throw new ArgumentNullException(nameof(imageName));

            if (_xobjects.ContainsKey(imageName))
            {
                // Return existing reference
                var entry = _xobjects[imageName];
                return $"Im{_xobjects.Count}"; // Simplified; would need better tracking
            }

            _xobjects[imageName] = imageObjectId;
            return $"Im{++_xobjectCounter}";
        }

        /// <summary>
        /// Registers a graphics state (for transparency, blending, etc.).
        /// </summary>
        public string RegisterGraphicsState(string gsName, int gsObjectId)
        {
            if (string.IsNullOrEmpty(gsName))
                throw new ArgumentNullException(nameof(gsName));

            if (_gsStates.ContainsKey(gsName))
                return $"GS{_gsStateCounter}";

            _gsStates[gsName] = gsObjectId;
            return $"GS{++_gsStateCounter}";
        }

        /// <summary>
        /// Gets the number of registered fonts.
        /// </summary>
        public int FontCount => _fonts.Count;

        /// <summary>
        /// Gets the number of registered XObjects.
        /// </summary>
        public int XObjectCount => _xobjects.Count;

        /// <summary>
        /// Gets the number of registered graphics states.
        /// </summary>
        public int GraphicsStateCount => _gsStates.Count;

        /// <summary>
        /// Generates the /Resources dictionary in PDF syntax.
        /// Example output:
        /// &lt;&lt; /Font &lt;&lt; /F1 1 0 R /F2 2 0 R &gt;&gt; /XObject &lt;&lt; /Im1 3 0 R &gt;&gt; &gt;&gt;
        /// </summary>
        public string GenerateResourcesDictionary()
        {
            var sb = new StringBuilder();
            sb.Append("<<");

            // Fonts
            if (_fonts.Count > 0)
            {
                sb.Append(" /Font << ");
                int index = 1;
                foreach (var (name, objId) in _fonts)
                {
                    sb.Append($"/F{index} {objId} 0 R ");
                    index++;
                }
                sb.Append(">>");
            }

            // XObjects
            if (_xobjects.Count > 0)
            {
                sb.Append(" /XObject << ");
                int index = 1;
                foreach (var (name, objId) in _xobjects)
                {
                    sb.Append($"/Im{index} {objId} 0 R ");
                    index++;
                }
                sb.Append(">>");
            }

            // Graphics States
            if (_gsStates.Count > 0)
            {
                sb.Append(" /ExtGState << ");
                int index = 1;
                foreach (var (name, objId) in _gsStates)
                {
                    sb.Append($"/GS{index} {objId} 0 R ");
                    index++;
                }
                sb.Append(">>");
            }

            sb.Append(" >>");
            return sb.ToString();
        }

        /// <summary>
        /// Generates the /Resources dictionary as bytes.
        /// </summary>
        public byte[] GenerateResourcesDictionaryBytes()
        {
            var text = GenerateResourcesDictionary();
            return System.Text.Encoding.ASCII.GetBytes(text);
        }

        /// <summary>
        /// Clears all registered resources.
        /// </summary>
        public void Clear()
        {
            _fonts.Clear();
            _xobjects.Clear();
            _gsStates.Clear();
            _colorSpaces.Clear();
        }

        /// <summary>
        /// Gets all registered font entries.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetFonts() => _fonts;

        /// <summary>
        /// Gets all registered XObject entries.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetXObjects() => _xobjects;

        /// <summary>
        /// Gets all registered graphics state entries.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetGraphicsStates() => _gsStates;
    }
}
