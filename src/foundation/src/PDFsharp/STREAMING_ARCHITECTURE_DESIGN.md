# PDFsharp Streaming Architecture - Implementation Design

## Executive Summary

Transform PDFsharp from a DOM-based in-memory model to an append-only streaming architecture with:
- **O(1) memory complexity** relative to document size
- **Incremental object writing** to FileStream
- **Direct PDF operator emission** without DOM retention
- **Buffered compression pipeline** with DeflateStream
- **Forward reference support** via deferred object resolution
- **Page lifecycle management** with automatic disposal after flushing

---

## Architecture Layers

### Layer 1: Graphics API (User-Facing)
**Preserve:** XGraphics-like ergonomic API
**Change:** Backend dispatch to streaming instead of DOM

```csharp
// User code remains unchanged
using (var gfx = XGraphics.FromPdfPage(page))
{
    gfx.DrawString("Hello", font, XBrushes.Black, rect);
    gfx.DrawLine(pen, 100, 100, 500, 100);
    gfx.DrawRectangle(XBrushes.White, rect);
}
```

**Internally dispatches to:**
- `IStreamingGraphicsBackend` (interface)
- `StreamingXGraphicsRenderer` (streaming implementation)
- Direct PDF operator emission

---

### Layer 2: Streaming Graphics Renderer
**New class:** `StreamingXGraphicsRenderer`

Responsibilities:
1. Generate PDF operators directly (no StringBuilder)
2. Write operators immediately to content stream buffer
3. Track page resources (fonts, colors, images)
4. Emit resource dictionary references
5. Manage graphics state stack

```csharp
public class StreamingXGraphicsRenderer : IDisposable
{
    private IBufferWriter<byte> _outputBuffer;
    private StreamingPageResources _resources;
    private Stack<GraphicsState> _stateStack;
    
    public void DrawString(string text, XFont font, XBrush brush, XRect rect)
    {
        // Emit PDF operators directly:
        // BT /F1 10 Tf 50 700 Td (text) Tj ET
        // NO StringBuilder accumulation
    }
    
    public void Flush()
    {
        // Write buffered operators to underlying stream
    }
}
```

---

### Layer 3: Streaming Page Model
**New classes:** `StreamingPdfPage`, `StreamingPageContentStream`, `StreamingPageResources`

Responsibilities:
1. Represent a single page (no document reference)
2. Manage page content stream (append-only)
3. Track page resources (fonts, images)
4. Support async page flushing
5. Enable page disposal after flushing

```csharp
public class StreamingPdfPage : IAsyncDisposable
{
    private PdfDictionary _pageDictionary;
    private StreamingPageContentStream _contentStream;
    private StreamingPageResources _resources;
    private bool _flushed;
    
    public async Task FlushAsync(StreamingPdfWriter writer)
    {
        // 1. Write page resources dictionary
        // 2. Write compressed content stream object
        // 3. Write page dictionary object
        // 4. Return page object reference
        _flushed = true;
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!_flushed)
            await FlushAsync(_writer);
        
        // Release all page resources
        // Clear content buffer
    }
}
```

---

### Layer 4: Streaming Content Stream
**New class:** `StreamingPageContentStream`

Responsibilities:
1. Buffer page PDF operators
2. Compress on demand
3. Track stream length
4. Support direct byte writing

```csharp
public class StreamingPageContentStream
{
    private BufferWriter<byte> _uncompressedBuffer;
    private byte[] _compressedBytes;
    private int _uncompressedLength;
    
    public void WriteOperator(ReadOnlySpan<byte> operatorBytes)
    {
        _uncompressedBuffer.Write(operatorBytes);
    }
    
    public byte[] CompressAndGetBytes()
    {
        // Compress using DeflateStream
        // Store compressed result
        // Return byte array
    }
    
    public int CompressedLength => _compressedBytes.Length;
    public int UncompressedLength => _uncompressedLength;
}
```

---

### Layer 5: Streaming PDF Writer
**New class:** `StreamingPdfWriter`

Core responsibilities:
1. Write PDF objects directly to stream
2. Track object offsets for xref generation
3. Manage forward references
4. Buffer writes with BufferedStream
5. Generate xref table at finalization

```csharp
public class StreamingPdfWriter : IAsyncDisposable
{
    private BufferedStream _bufferedStream;
    private FileStream _fileStream;
    private Dictionary<int, long> _objectOffsets;
    private Queue<PendingForwardReference> _forwardReferences;
    private int _nextObjectId = 1;
    
    public async Task WriteObjectAsync(int objNum, PdfObject obj)
    {
        long offset = _bufferedStream.Position;
        _objectOffsets[objNum] = offset;
        
        // Write: "10 0 obj\n"
        // Write: <<...>>\n
        // Write: "endobj\n"
        
        // Resolve any forward references to this object
        ResolveForwardReferences(objNum);
    }
    
    public void RegisterForwardReference(int objNum, ForwardReferenceType type)
    {
        _forwardReferences.Enqueue(new PendingForwardReference(objNum, type));
    }
    
    public async Task FinalizeAsync()
    {
        // 1. Write Pages tree root
        // 2. Write Catalog
        // 3. Write Info dictionary
        // 4. Flush BufferedStream
        // 5. Calculate xref
        // 6. Write xref table
        // 7. Write trailer
        // 8. Write EOF
    }
}
```

---

### Layer 6: Object Offset Tracking
**New class:** `StreamingXrefTable`

Responsibilities:
1. Track object ID → byte offset mappings
2. Generate xref table at finalization
3. Support deferred xref calculation
4. Generate trailer dictionary

```csharp
public class StreamingXrefTable
{
    private SortedDictionary<int, long> _objectOffsets;
    
    public void RegisterObject(int objNum, long offset)
    {
        _objectOffsets[objNum] = offset;
    }
    
    public byte[] GenerateXrefTable()
    {
        // Build xref subsections
        // Format: "xref\n0 N\n0000000000 65535 f\n..."
        // Return as bytes
    }
    
    public string GenerateTrailer(int objectCount, int rootObjNum, int infoObjNum)
    {
        // Generate trailer dictionary with /Size, /Root, /Info
        // Format: "trailer\n<< ... >>\nstartxref\n...\n%%EOF"
    }
}
```

---

### Layer 7: Resource Management
**New class:** `StreamingPageResources`

Responsibilities:
1. Deduplicate fonts across pages
2. Reuse image objects
3. Build resource dictionary incrementally
4. Generate `/Resources` entry

```csharp
public class StreamingPageResources
{
    private Dictionary<string, int> _fonts;        // Font name → object ID
    private Dictionary<string, int> _xobjects;     // Image name → object ID
    private Dictionary<string, int> _gsStates;     // Color space → object ID
    
    public int GetOrCreateFontObject(XFont font, StreamingPdfWriter writer)
    {
        string key = font.Name + "_" + font.Size;
        if (_fonts.TryGetValue(key, out int objId))
            return objId;
        
        // Create new font object, register, return ID
    }
    
    public PdfDictionary GenerateResourcesDictionary()
    {
        // Build /Resources << /Font << ... >> /XObject << ... >> ... >>
    }
}
```

---

### Layer 8: Buffered Streaming
**New class:** `StreamingBufferWriter`

Responsibilities:
1. Buffer PDF operators before writing
2. Batch writes to underlying stream
3. Support IBufferWriter<byte> interface
4. Reduce syscall overhead

```csharp
public class StreamingBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int BUFFER_SIZE = 65536; // 64 KB buffer
    private byte[] _buffer;
    private int _position;
    private Stream _underlying;
    
    public void Write(ReadOnlySpan<byte> data)
    {
        // Append to buffer, flush when full
    }
    
    public void Flush()
    {
        _underlying.Write(_buffer, 0, _position);
        _position = 0;
    }
}
```

---

## Data Flow: Streaming Rendering Pipeline

```
1. User calls: gfx.DrawString("Hello", font, brush, rect)
   └─> XGraphics delegates to IStreamingGraphicsBackend

2. StreamingXGraphicsRenderer.DrawString()
   └─> Generate PDF operators: "BT /F1 10 Tf 50 700 Td (Hello) Tj ET\n"
   └─> Write directly to StreamingPageContentStream buffer
   └─> NO StringBuilder (direct write)

3. User calls: gfx.Dispose() (when exiting using block)
   └─> StreamingXGraphicsRenderer.Close()
   └─> Flush buffered operators to content stream
   └─> Return control to StreamingPdfPage

4. User calls: page.FlushAsync(writer)  [NEW API]
   └─> StreamingPageContentStream.CompressAndGetBytes()
   └─> StreamingPdfWriter.WriteObjectAsync(contentObjNum, compressed)
   └─> StreamingPageResources.GenerateResourcesDictionary()
   └─> StreamingPdfWriter.WriteObjectAsync(resObjNum, resourcesDict)
   └─> StreamingPdfWriter.WriteObjectAsync(pageObjNum, pageDictionary)
   └─> Return page object reference

5. User calls: document.FinalizeAsync()
   └─> Write Pages tree root object
   └─> Write Catalog object
   └─> Flush BufferedStream
   └─> Calculate byte offsets → xref table
   └─> Write xref table to stream
   └─> Write trailer
   └─> Write EOF marker
```

---

## Memory Model

### Current (O(n) - Linear)
```
Document size: 1 GB
→ Memory usage: ~1 GB (all objects in IrefTable)
→ Maximum practical: 100k pages, 1-5 GB
```

### Target (O(1) - Constant)
```
Document size: 1 GB (or 10 GB)
→ Memory usage: ~50-100 MB
  - Buffered stream: 64 KB
  - Current page: ~1 MB
  - Resource cache: ~10 MB
  - Xref table: ~100 KB
→ Maximum practical: Unlimited (disk-bound)
```

---

## Implementation Sequence

### Phase 1: Infrastructure (Week 1)
- [ ] Create `StreamingPageContentStream` class
- [ ] Create `StreamingBufferWriter` class
- [ ] Create `StreamingXrefTable` class
- [ ] Create `StreamingPageResources` class

### Phase 2: Rendering Backend (Week 2)
- [ ] Create `StreamingXGraphicsRenderer` class
- [ ] Implement PDF operator generation (text, lines, shapes)
- [ ] Implement direct buffer writing
- [ ] Implement resource tracking

### Phase 3: Page Model (Week 2-3)
- [ ] Create `StreamingPdfPage` class
- [ ] Implement page flushing logic
- [ ] Implement async disposal
- [ ] Implement resource dictionary generation

### Phase 4: Core Writer (Week 3-4)
- [ ] Create `StreamingPdfWriter` class
- [ ] Implement incremental object writing
- [ ] Implement forward reference handling
- [ ] Implement offset tracking

### Phase 5: Document Integration (Week 4)
- [ ] Modify `PdfDocument` to support streaming mode
- [ ] Create `StreamingPdfDocument` facade class
- [ ] Implement `FinalizeAsync()` method
- [ ] Implement xref table generation

### Phase 6: Testing & Optimization (Week 5)
- [ ] Unit tests for each component
- [ ] Integration tests with large documents
- [ ] Memory profiling
- [ ] Performance benchmarking

---

## Key Design Decisions

### 1. Parallel APIs
- **Keep:** Existing DOM-based `PdfDocument` (backward compatible)
- **Add:** New `StreamingPdfDocument` for streaming mode
- **Allow:** Users to choose based on use case

### 2. Forward References
- **Allow:** `/Parent 2 0 R` before object 2 is written
- **Resolve:** During finalization when page tree is constructed
- **Track:** Via `_forwardReferences` queue

### 3. Compression Strategy
- **Compress:** Each page content stream independently
- **When:** During `FlushAsync()` (on-demand)
- **Algorithm:** DeflateStream (gzip compatible)
- **Output:** `/Filter /FlateDecode` in stream dictionary

### 4. Resource Deduplication
- **Global font cache:** Fonts written once, referenced by all pages
- **Image deduplication:** Same image object referenced from multiple pages
- **Color spaces:** Shared across pages

### 5. Buffering Strategy
- **Input buffering:** `StreamingPageContentStream` accumulates operators
- **Output buffering:** `StreamingBufferWriter` batches writes to FileStream
- **Purpose:** Reduce syscall overhead, improve throughput

### 6. PDF Validity
- **Requirement:** All generated PDFs must pass PDF specification validation
- **Testing:** Open in Adobe Reader, Chrome, Firefox, Edge, macOS Preview
- **Compliance:** PDF/UA accessibility support optional but architecture-ready

---

## Thread Safety Considerations

### Current Design (Single-threaded)
```csharp
// Document.SaveAsync() is single-threaded
// All writes serialize through StreamingPdfWriter
// No concurrent page flushing
```

### Future Multi-threaded Design
```csharp
// If implemented:
// 1. Each page has independent content stream
// 2. Multiple threads render pages concurrently
// 3. Main thread coordinates object numbering
// 4. Xref generation serialized at end
```

---

## Performance Expectations

### Memory Usage
| Scenario | Old Model | New Model | Improvement |
|----------|-----------|-----------|------------|
| 1k pages, 100KB each | 100 MB | 10 MB | 10x |
| 10k pages, 100KB each | 1000 MB | 10 MB | 100x |
| 100k pages, 100KB each | **CRASH** | 10 MB | ∞ |
| 1M pages, 100KB each | **CRASH** | 10 MB | ∞ |

### Throughput
| Scenario | Old Model | New Model | Note |
|----------|-----------|-----------|------|
| Large page content | ~100 MB/s | ~200 MB/s | Better buffering |
| Compression ratio | 4:1 typical | 4:1 typical | Same algorithm |
| Write to disk | Batched | Buffered | Better I/O pattern |

---

## PDF Structure Example

### Old Approach (All in Memory Then Write)
```
Save() → iterate all objects → write each → xref at end
Result: Linear scan of huge in-memory graph
```

### New Approach (Streaming Write with Deferred Finalization)
```
1. Render pages incrementally
2. Flush each page immediately (write objects to stream)
3. Track byte offsets as objects are written
4. At end: write xref table (calculated from tracked offsets)

Example PDF output:
%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources 6 0 R >>
endobj
4 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources 6 0 R >>
endobj
5 0 obj
<< /Length 234 /Filter /FlateDecode >>
stream
(compressed content)
endstream
endobj
6 0 obj
<< /Font << /F1 8 0 R >> >>
endobj
... more objects ...
xref
0 9
0000000000 65535 f
0000000015 00000 n
0000000076 00000 n
... offset mappings ...
trailer
<< /Size 9 /Root 1 0 R /Info 10 0 R >>
startxref
2847
%%EOF
```

---

## File Structure for New Implementation

```
src/PdfSharp/
├── Streaming/                                    [NEW]
│   ├── Core/
│   │   ├── StreamingPdfWriter.cs                [NEW]
│   │   ├── StreamingXrefTable.cs                [NEW]
│   │   ├── StreamingBufferWriter.cs             [NEW]
│   │   └── ForwardReferenceResolver.cs          [NEW]
│   ├── Pages/
│   │   ├── StreamingPdfPage.cs                  [NEW]
│   │   ├── StreamingPageContentStream.cs        [NEW]
│   │   └── StreamingPageResources.cs            [NEW]
│   ├── Graphics/
│   │   ├── StreamingXGraphicsRenderer.cs        [NEW]
│   │   ├── IStreamingGraphicsBackend.cs         [NEW]
│   │   └── StreamingGraphicsState.cs            [NEW]
│   ├── Compression/
│   │   ├── ContentStreamCompressor.cs           [NEW]
│   │   └── DeflateStreamWrapper.cs              [NEW]
│   └── Model/
│       ├── StreamingPdfDocument.cs              [NEW]
│       └── StreamingDocumentOptions.cs          [NEW]
├── Drawing/
│   └── XGraphics.cs                             [MODIFIED - add backend dispatch]
└── Pdf/
    └── PdfDocument.cs                           [MODIFIED - keep old, add streaming option]
```

---

## API Usage Example

```csharp
// NEW: Streaming approach for large documents
using (var writer = new StreamingPdfWriter("output.pdf"))
{
    // Render first page
    var page1 = new StreamingPdfPage(writer, 612, 792);
    using (var gfx = XGraphics.FromStreamingPage(page1))
    {
        gfx.DrawString("Page 1", font, XBrushes.Black, new XRect(50, 50, 500, 100));
        gfx.DrawLine(pen, 100, 200, 500, 200);
    }
    await page1.FlushAsync();
    
    // Render second page
    var page2 = new StreamingPdfPage(writer, 612, 792);
    using (var gfx = XGraphics.FromStreamingPage(page2))
    {
        gfx.DrawString("Page 2", font, XBrushes.Black, new XRect(50, 50, 500, 100));
    }
    await page2.FlushAsync();
    
    // ... potentially millions more pages ...
    
    // Finalize (writes xref, trailer, etc.)
    await writer.FinalizeAsync();
}

// Result: Constant memory usage (~10-50 MB) regardless of document size
```

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|-----------|
| Forward references break PDF spec | High | Test against PDF validator, include in xref resolution |
| Xref calculation errors | High | Implement robust offset tracking, validate before writing |
| Compression overhead | Medium | Compress asynchronously, benchmark against old approach |
| Thread safety issues | Medium | Document as single-threaded initially, add locks if needed |
| Large page resources | Medium | Implement resource eviction if cache grows >100MB |
| Buffering exceeds memory | Low | Set BufferedStream to 64KB, tune if needed |

---

## Success Criteria

1. ✅ **Memory**: O(1) usage regardless of document size
2. ✅ **Functionality**: All XGraphics drawing operations work
3. ✅ **PDF Validity**: Generated PDFs open in Adobe Reader, Chrome, Edge, Firefox, Preview
4. ✅ **Performance**: > 200 MB/s throughput on large documents
5. ✅ **Scalability**: Support 1M+ pages with constant memory
6. ✅ **Compression**: 4:1+ compression ratio on typical content
7. ✅ **Backward Compatibility**: Old API still works (optional, non-streaming)
8. ✅ **Linux/Docker**: Works on .NET Core on Linux (no Windows-only APIs)
