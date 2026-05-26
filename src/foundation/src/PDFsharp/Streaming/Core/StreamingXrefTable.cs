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

namespace PdfSharp.Streaming.Core
{
    /// <summary>
    /// Tracks object IDs and their byte offsets in the PDF stream for xref table generation.
    /// Enables incremental PDF writing with deferred xref calculation.
    /// </summary>
    public class StreamingXrefTable
    {
        private readonly SortedDictionary<int, long> _objectOffsets;
        private int _nextObjectId = 1;
        private int _rootObjNum = 0;
        private int _infoDictObjNum = 0;
        private int _pageTreeRootObjNum = 0;

        /// <summary>
        /// Initializes a new instance of StreamingXrefTable.
        /// </summary>
        public StreamingXrefTable()
        {
            _objectOffsets = new SortedDictionary<int, long>();
        }

        /// <summary>
        /// Gets the next available object ID and increments the counter.
        /// </summary>
        public int GetNextObjectId() => _nextObjectId++;

        /// <summary>
        /// Registers an object's byte offset in the stream.
        /// </summary>
        /// <param name="objNum">Object ID (e.g., "10" in "10 0 obj").</param>
        /// <param name="offset">Byte offset in the PDF stream where the object starts.</param>
        public void RegisterObject(int objNum, long offset)
        {
            if (objNum <= 0)
                throw new ArgumentException("Object number must be positive.", nameof(objNum));
            if (offset < 0)
                throw new ArgumentException("Offset must be non-negative.", nameof(offset));

            _objectOffsets[objNum] = offset;
        }

        /// <summary>
        /// Sets the object ID for the document catalog (root).
        /// </summary>
        public void SetRootObjectId(int objNum) => _rootObjNum = objNum;

        /// <summary>
        /// Sets the object ID for the document info dictionary.
        /// </summary>
        public void SetInfoDictObjectId(int objNum) => _infoDictObjNum = objNum;

        /// <summary>
        /// Sets the object ID for the page tree root.
        /// </summary>
        public void SetPageTreeRootObjectId(int objNum) => _pageTreeRootObjNum = objNum;

        /// <summary>
        /// Gets the byte offset for a given object ID.
        /// </summary>
        public bool TryGetObjectOffset(int objNum, out long offset)
        {
            return _objectOffsets.TryGetValue(objNum, out offset);
        }

        /// <summary>
        /// Gets all registered object IDs.
        /// </summary>
        public IEnumerable<int> GetAllObjectIds() => _objectOffsets.Keys;

        /// <summary>
        /// Generates the xref table bytes for the PDF trailer.
        /// Format: "xref\n0 N\noffsets..."
        /// </summary>
        /// <returns>UTF-8 encoded xref table bytes.</returns>
        public byte[] GenerateXrefTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("xref");

            // Create subsections for consecutive object numbers
            var subsections = new List<(int start, int count)>();
            int currentStart = 0;
            int currentCount = 0;

            foreach (var objNum in _objectOffsets.Keys)
            {
                if (currentCount == 0)
                {
                    currentStart = objNum;
                    currentCount = 1;
                }
                else if (objNum == currentStart + currentCount)
                {
                    currentCount++;
                }
                else
                {
                    subsections.Add((currentStart, currentCount));
                    currentStart = objNum;
                    currentCount = 1;
                }
            }

            if (currentCount > 0)
            {
                subsections.Add((currentStart, currentCount));
            }

            // Add subsection for object 0 (always free)
            if (subsections.Count == 0 || subsections[0].start > 0)
            {
                subsections.Insert(0, (0, 1));
            }

            // Write subsections
            foreach (var (start, count) in subsections)
            {
                sb.AppendLine($"{start} {count}");

                if (start == 0)
                {
                    // Object 0 is always free
                    sb.AppendLine("0000000000 65535 f");
                }
                else
                {
                    // Write entries for this subsection
                    for (int i = 0; i < count; i++)
                    {
                        int objNum = start + i;
                        if (_objectOffsets.TryGetValue(objNum, out long offset))
                        {
                            // Format: "0000000123 00000 n"
                            sb.AppendLine($"{offset:D10} {0:D5} n");
                        }
                    }
                }
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Generates the trailer dictionary bytes for the PDF.
        /// </summary>
        /// <returns>Trailer dictionary as UTF-8 encoded string.</returns>
        public string GenerateTrailer()
        {
            int objectCount = _objectOffsets.Count + 1; // +1 for object 0
            
            var sb = new StringBuilder();
            sb.AppendLine("trailer");
            sb.AppendLine("<<");
            sb.AppendLine($"/Size {objectCount}");
            
            if (_rootObjNum > 0)
                sb.AppendLine($"/Root {_rootObjNum} 0 R");
            
            if (_infoDictObjNum > 0)
                sb.AppendLine($"/Info {_infoDictObjNum} 0 R");
            
            sb.AppendLine(">>");

            return sb.ToString();
        }

        /// <summary>
        /// Gets the total number of objects (excluding object 0).
        /// </summary>
        public int ObjectCount => _objectOffsets.Count;
    }
}
