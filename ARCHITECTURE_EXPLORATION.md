# PDFsharp Architecture Exploration - Complete Analysis

## Executive Summary

The current PDFsharp architecture is a **DOM-based (Document Object Model)** system that builds a complete in-memory representation of the PDF before serialization. All objects are created, stored, and only written to disk/stream during the final save operation. This creates a fundamental memory bottleneck when generating large PDFs.

---

## 1. XGraphics Class - Drawing API

### Location
**File**: [src/PdfSharp/Drawing/XGraphics.cs](src/PdfSharp/Drawing/XGraphics.cs)

### Purpose
Public API providing a platform-independent graphics drawing surface. Acts as a facade that delegates to platform-specific implementations (GDI+, WPF, WUI).

### Key Properties
```csharp
public sealed class XGraphics : IDisposable
{
    // Platform-specific graphics contexts
#if GDI
    Graphics? _gfx;  // GDI+ graphics object
#endif
#if WPF
    DrawingContext? _dc;  // WPF drawing context
#endif
    
    // Page/drawing context
    XSize _pageSize;
    XGraphicsUnit _pageUnit;
    XPageDirection _pageDirection;
    GraphicsStateStack _gsStack;
    
    // Rendering target
    XGraphicTargetContext TargetContext;  // GDI, PDF, or Bitmap
    
    // For PDF rendering
    IXGraphicsRenderer _renderer;  // Platform-specific renderer
    RenderEvents? _renderEvents;
}
```

### Current Memory Model
- **Constructor**: Creates platform-specific graphics object or cached measure context
- **State Management**: GraphicsStateStack manages transformation matrices, colors, fonts, etc.
- **Platform Abstraction**: Actual rendering delegated to XGraphicsPdfRenderer (for PDF) or native APIs

### Key Methods
- `FromPdfPage(PdfPage page)` - Creates XGraphics for PDF rendering
- Drawing methods: `DrawLine()`, `DrawBezier()`, `DrawRectangle()`, `DrawString()`, `DrawImage()`
- Transformation: `TranslateTransform()`, `RotateTransform()`, `ScaleTransform()`
- State: `Save()`, `Restore()` - Push/pop graphics state

### Data Flow to PDF
```
XGraphics.DrawString(text)
  → Delegates to _renderer (XGraphicsPdfRenderer)
  → XGraphicsPdfRenderer appends PDF operators to StringBuilder
  → StringBuilder accumulates operators for entire page
  → On XGraphics.Dispose() → GetContent() from renderer
  → Content string converted to bytes → PdfContent.CreateStream(bytes)
```

---

## 2. XGraphicsPdfRenderer - PDF Content Generation

### Location
**File**: [src/PdfSharp/Drawing.Pdf/XGraphicsPdfRenderer.cs](src/PdfSharp/Drawing.Pdf/XGraphicsPdfRenderer.cs)

### Purpose
Implements IXGraphicsRenderer interface to translate XGraphics drawing commands into PDF content stream operators (move, line, curve, text, image operations).

### Architecture
```csharp
class XGraphicsPdfRenderer : IXGraphicsRenderer, IPageContentRenderer
{
    StringBuilder _content;           // Accumulates PDF operators
    PdfPage? _page;                   // Page being rendered
    XForm? _form;                     // Or XForm if rendering to form
    PdfGraphicsState _gfxState;       // Current PDF graphics state
    XGraphics _gfx;                   // Reference to parent XGraphics
    XGraphicsPdfPageOptions _options; // Page options
    ColorMode _colorMode;             // From document options
}
```

### Critical Data Structure
The `_content` StringBuilder is the bottleneck:
```csharp
_content = new StringBuilder();

// Throughout rendering, PDF operators are appended:
_content.Append("S\n");  // Stroke
_content.Append("f\n");  // Fill
_content.Append("BT\n"); // Begin text
_content.AppendFormat("{0} Tj\n", encoded_text);  // Show text
_content.Append("ET\n"); // End text
```

### Key Methods & Operators

#### Drawing Operations
```csharp
DrawLines(XPen pen, XPoint[] points)
  → Append "m" (move) + "l" (line) + "S" (stroke)

DrawBeziers(XPen pen, XPoint[] points)
  → Append cubic Bezier "c" operators

DrawRectangle(XPen? pen, XBrush? brush, double x, y, width, height)
  → Append "re" (rectangle) operator

DrawString(string s, XFont font, XBrush brush, XRect rect, XStringFormat fmt)
  → RealizeFont() - Ensures font in page resources
  → AppendFormatArgs() - Append text matrix + text show operators
  → Optionally: underline/strikeout via DrawRectangle
```

#### Graphics State Operations
```csharp
Realize(XPen pen)
  → Append stroke color, width, line style operators
  → "RG/rg" - RGB color
  → "w" - line width
  → "d" - line dash pattern

Realize(XBrush brush)
  → Append fill color operator
  → "RG/rg" - RGB for solid color
  → "gs" - graphics state for transparency
```

#### Content Appending Methods
```csharp
// Low-level: directly append to StringBuilder
void Append(string value) 
  → _content.Append(value)

// Format helpers (apply coordinate transformation)
void AppendFormatPoint(string format, double x, double y)
  → XPoint transformed = WorldToView(x, y)
  → _content.AppendFormat(format, transformed.X, transformed.Y)

void AppendFormatRect(string format, double x, y, width, height)
  → Transform first point, keep dimensions
  → _content.AppendFormat(format, transformed.X, transformed.Y, width, height)

// Higher-level helpers for specific PDF operations
void AppendFormat3Points(string format, double x1, y1, x2, y2, x3, y3)
  → Transform 3 points
  → Used for Bezier curves
```

### Memory Model During Rendering
```
Page Rendering Session:
  Session Start:
    _content = new StringBuilder()  // Empty, ~16KB capacity
  
  Draw Operations:
    StringBuilder grows with each operation
    Typical: 10-50 bytes per primitive
    Large page (100s of operations): 5KB-500KB
    
  Session Close:
    Total content = _content.ToString()  // ~O(page_size)
    Bytes = PdfEncoders.RawEncoding.GetBytes(content)
    
Memory: O(page_size) - unbounded string accumulation
```

### Coordinate Transformation Pipeline
```
User coordinates (e.g., cm, inches)
  ↓ (XGraphics transform matrix)
Page coordinates (points, 72 dpi)
  ↓ (WorldToView in AppendFormatPoint)
PDF page space
  ↓ (Appended to _content)
PDF operators with absolute coordinates
```

### Current Bottleneck
- **Unbounded StringBuilder**: For very large pages, StringBuilder can consume significant memory
- **No streaming**: Cannot emit operators until GetContent() called (when XGraphics disposed)
- **String to bytes**: Full content string converted to bytes at once
- **No incremental compression**: Entire page compressed in WriteObject

---

## 3. PdfContent - Individual Content Stream

### Location
**File**: [src/PdfSharp/Pdf.Advanced/PdfContent.cs](src/PdfSharp/Pdf.Advanced/PdfContent.cs)

### Purpose
Represents a single content stream (PDF indirect object with stream dictionary). PDFsharp supports one primary content stream per page.

### Class Definition
```csharp
public sealed class PdfContent : PdfDictionary
{
    public PdfContent(PdfDocument document)
        : base(document, true)  // true = create indirect
    { }
    
    internal PdfContent(PdfPage page)
        : base(page?.Owner)
    { }
    
    internal PdfContent(PdfDictionary dict)
        : base(dict)
    {
        Decode();  // Uncompress if needed
    }
}
```

### Stream Storage
```csharp
// From PdfDictionary.PdfStream:
public sealed class PdfStream
{
    byte[]? _value;  // Raw stream bytes
    
    public byte[] Value
    {
        get => _value ??= [];
        set { _value = value; }
    }
    
    public int Length => _value?.Length ?? 0;
}

// PdfContent inherits Stream property from PdfDictionary
public PdfStream Stream { get; set; }
```

### Compression Strategy
```csharp
public bool Compressed
{
    set
    {
        if (value && Stream is not null)
        {
            byte[] bytes = Filtering.FlateDecode.Encode(
                Stream.Value, 
                Document.Options.FlateEncodeMode
            );
            Stream.Value = bytes;
            Elements.SetName(PdfStream.Keys.Filter, "/FlateDecode");
            Elements.SetInteger(PdfStream.Keys.Length, Stream.Length);
        }
    }
}
```

### Content Stream Creation
```csharp
public PdfStream CreateStream(byte[] value)
{
    // Called during XGraphicsPdfRenderer.Close()
    Stream = new PdfStream(value, this);
    return Stream;
}
```

### Serialization to PDF
```csharp
internal override void WriteObject(PdfWriter writer)
{
    if (_pdfRenderer != null)
    {
        // If renderer still active, close it
        if (_pdfRenderer is XGraphicsPdfRenderer xgfxRenderer)
            xgfxRenderer.Close();
        else
            throw new InvalidOperationException("Renderer still open");
    }

    if (Stream != null!)
    {
        // Apply compression if enabled AND not already compressed
        const int streamLengthCompressionThreshold = 32;
        if (Owner.Options.CompressContentStreams 
            && !Elements.HasValue(PdfStream.Keys.Filter)
            && Stream.Value.Length > streamLengthCompressionThreshold)
        {
            Stream.Value = Filtering.FlateDecode.Encode(
                Stream.Value, 
                Document.Options.FlateEncodeMode
            );
            Elements.SetName(PdfStream.Keys.Filter, "/FlateDecode");
        }
        Elements.SetInteger(PdfStream.Keys.Length, Stream.Length);
    }

    base.WriteObject(writer);  // Writes dictionary + stream
}
```

### Memory Model
- **Per-page**: One PdfContent object per rendered page
- **Storage**: Stream holds entire page content as byte array
- **Compression**: Applied in-place during WriteObject() call
- **Lifetime**: Kept in memory until Save() completes

---

## 4. PdfContents - Content Stream Array

### Location
**File**: [src/PdfSharp/Pdf.Advanced/PdfContents.cs](src/PdfSharp/Pdf.Advanced/PdfContents.cs)

### Purpose
Container for multiple content streams. A PDF page can have multiple content streams combined in order.

### Key Implementation
```csharp
public sealed class PdfContents : PdfArray
{
    // Inherits from PdfArray - stores array of PdfReference to PdfContent
    Elements.Add(content.RequiredReference);
}
```

### Operations
```csharp
public PdfContent AppendContent()
{
    PdfContent content = new PdfContent(Owner);
    Owner.IrefTable.Add(content);
    Elements.Add(content.Reference);
    return content;
}

public PdfContent PrependContent()
{
    // Same but insert at beginning
}

public PdfContent CreateSingleContent()
{
    // Merge all streams into one
    byte[] bytes = [];
    foreach (PdfItem iref in Elements)
    {
        // Concatenate all stream bytes
    }
    return new PdfContent(Owner) { Stream = merged_bytes };
}
```

### Relationship to PdfPage
```csharp
// In PdfPage:
public PdfContents Contents
{
    get
    {
        if (_contents == null)
        {
            // Lazy initialization
            var item = Elements.GetValue(Keys.Contents);
            if (item is PdfReference)
            {
                _contents = new PdfContents((PdfArray)iref.Value);
            }
            else
            {
                _contents = new PdfContents(Owner);
            }
        }
        return _contents;
    }
}
```

---

## 5. PdfPage - Page Object & Content Container

### Location
**File**: [src/PdfSharp/Pdf/PdfPage.cs](src/PdfSharp/Pdf/PdfPage.cs)

### Purpose
Represents a PDF page dictionary object. Stores page dimensions, content streams, resources, and rendering state.

### Page Metadata
```csharp
public sealed class PdfPage : PdfPageTreeBase, IContentStream
{
    // Page dimensions
    XUnit _width;
    XUnit _height;
    PageOrientation _orientation;
    
    // Boxes (MediaBox, CropBox, TrimBox, etc.)
    PdfRectangle MediaBox { get; set; }
    PdfRectangle CropBox { get; set; }
    
    // Content streams
    public PdfContents Contents { get; }  // Array of content streams
    PdfContents? _contents;
    
    // Currently rendering content stream
    internal PdfContent? RenderContent;
    
    // Page dictionary elements
    public PdfDictionary Elements { get; }  // Inherits from PdfDictionary
}
```

### Active Content Tracking
```csharp
// During XGraphics.FromPdfPage():
internal void PrepareForRender()
{
    RenderContent = new PdfContent(this.Owner);
    // Store in Contents array for later serialization
}

// During XGraphics.Dispose():
// XGraphicsPdfRenderer.Close() is called
// → content.CreateStream(bytes)  // Stores in PdfContent.Stream
// → RenderContent = null
```

### Memory Model for Page
```
PdfPage instance:
  - Metadata: O(1) - dimensions, boxes
  - Contents array: O(1) - references only
  - RenderContent (while rendering): O(page_size)
  - Resources dict: O(num_fonts + num_images + num_forms)
  
Total: O(page_size) while rendering, O(1) after rendering
```

### Resource Management
```csharp
// From implicit Pages object:
public PdfResources Resources { get; }

// Resources contains:
// - Font table entries
// - Image references
// - Graphics state dictionary
// - Pattern dictionary
// - Etc.
```

---

## 6. PdfDocument - Document Container & Save Orchestrator

### Location
**File**: [src/PdfSharp/Pdf/PdfDocument.cs](src/PdfSharp/Pdf/PdfDocument.cs)

### Purpose
Top-level PDF document object. Manages pages, resources, settings, and orchestrates save operation.

### Document Structure
```csharp
public sealed class PdfDocument : IDisposable
{
    // Core document objects
    public PdfCrossReferenceTable IrefTable { get; set; }
    public PdfPages Pages { get; }
    public PdfTrailer Trailer { get; set; }
    
    // Resource tables
    PdfFontTable _fontTable;
    PdfImageTable _imageTable;
    
    // Document settings
    public PdfDocumentOptions Options { get; }  // Compression, PDF version, etc.
    PdfSecuritySettings _securitySettings;
    
    // Optional output stream (for streaming saves)
    public Stream? OutStream { get; set; }
    
    // Document state
    DocumentState State;  // Created, Imported, Saved, Disposed
    int _version;  // PDF version (17 = 1.7, etc.)
}
```

### Save Process - Detailed Flow

#### Phase 1: Initialization
```csharp
public async Task SaveAsync(Stream stream, bool closeStream = false)
{
    if (IsPdfA)
        PrepareForPdfA();
    
    var effectiveSecurityHandler = SecuritySettings.EffectiveSecurityHandler;
    
    PdfWriter writer = new(stream, this, effectiveSecurityHandler);
}
```

#### Phase 2: Document Preparation
```csharp
async Task DoSaveAsync(PdfWriter writer)
{
    // Prepare signing components if needed
    if (DigitalSignatureHandler != null)
        await DigitalSignatureHandler.AddSignatureComponentsAsync();
    
    // Prepare for save
    PrepareForSave();
    
    // Key operations:
    // - Compact(): Remove unreachable objects from IrefTable
    // - Renumber(): Reassign object numbers sequentially
}

void PrepareForSave()
{
    int removed = IrefTable.Compact();  // O(objects)
    
    if (SecuritySettings != null && ...)
        IrefTable.Add(SecuritySettings.SecurityHandler);
    
    IrefTable.Renumber();  // Reassign all object IDs
}
```

#### Phase 3: Header + Objects Writing
```csharp
async Task DoSaveAsync(PdfWriter writer)
{
    writer.WriteFileHeader(this);  // %PDF-1.7\n
    
    // CRITICAL: Get ALL references
    var irefs = IrefTable.AllReferences;  // Sorted array
    int count = irefs.Length;
    
    // Write every single object
    for (int idx = 0; idx < count; idx++)
    {
        PdfReference iref = irefs[idx];
        iref.Position = writer.Position;  // Record object byte position
        
        var obj = iref.Value;
        effectiveSecurityHandler?.EnterObject(obj.ObjectID);
        
        obj.WriteObject(writer);  // <-- SERIALIZE EACH OBJECT HERE
    }
}
```

#### Phase 4: Cross-Reference Table
```csharp
async Task DoSaveAsync(PdfWriter writer)
{
    var startXRef = (SizeType)writer.Position;  // Remember xref byte position
    
    IrefTable.WriteObject(writer);  // Write xref section
    writer.WriteRaw("trailer\n");
    Trailer.Elements.SetInteger("/Size", count + 1);
    Trailer.WriteObject(writer);
    writer.WriteEof(this, startXRef);
}
```

### Memory Consumption During Save
```
Document.Save():
  Total memory = Sum of all objects in IrefTable
  
Breakdown:
  - All pages: O(pages * average_page_size)
  - All fonts: O(num_fonts * font_size)
  - All images: O(images_in_memory)
  - All indirect objects: O(num_objects)
  
Worst case: Large document with many images = gigabytes

Serialization strategy: Single pass (no seek-back required)
  - Objects written sequentially
  - Positions recorded before write
  - Cannot write in streaming fashion (must know all positions)
```

---

## 7. PdfCrossReferenceTable - Object Registry & Xref

### Location
**File**: [src/PdfSharp/Pdf.Advanced/PdfCrossReferenceTable.cs](src/PdfSharp/Pdf.Advanced/PdfCrossReferenceTable.cs)

### Purpose
Central registry of all indirect objects in the PDF. Maintains object numbering and serialization order.

### Data Structure
```csharp
sealed class PdfCrossReferenceTable
{
    PdfDocument document;
    
    // Main registry
    Dictionary<PdfObjectID, PdfReference> _objectTable;
    
    // Metadata
    int MaxObjectNumber;  // Highest object number used
    bool IsUnderConstruction;  // true while reading PDF
}

public struct PdfObjectID
{
    public int ObjectNumber;  // 1, 2, 3, ...
    public int GenerationNumber;  // Usually 0 for new objects
    
    public bool IsEmpty => ObjectNumber == 0;
}

public class PdfReference
{
    public PdfObjectID ObjectID;
    public PdfObject? Value;  // The actual object
    public int Position;  // Byte offset in PDF (set during save)
    public int GenerationNumber;
}
```

### Object Registration

#### Adding Objects
```csharp
public void Add(PdfObject obj)
{
    // Assign new object number if empty
    if (obj.ObjectID.IsEmpty)
    {
        obj.SetObjectID(GetNewObjectNumber(), 0);
    }

    // Register in table
    if (_objectTable.ContainsKey(obj.ObjectID))
        throw new InvalidOperationException("Object already in table.");
    
    _objectTable.Add(obj.ObjectID, obj.RequiredReference);
    MaxObjectNumber = Math.Max(MaxObjectNumber, obj.ObjectNumber);
}

public bool TryAdd(PdfObject obj)
{
    if (obj.ObjectID.IsEmpty || !_objectTable.ContainsKey(obj.ObjectID))
    {
        Add(obj);
        return true;
    }
    return false;
}
```

#### Object Retrieval
```csharp
public PdfReference? this[PdfObjectID objectID]
{
    get
    {
        _objectTable.TryGetValue(objectID, out var iref);
        return iref;
    }
}

public bool Contains(PdfObjectID objectID) 
    => _objectTable.ContainsKey(objectID);

public PdfReference[] AllReferences
{
    get
    {
        var list = new List<PdfReference>(_objectTable.Values);
        list.Sort(PdfReference.Comparer);  // Sort by object number
        return list.ToArray();
    }
}
```

### Object Lifecycle Management

#### Compaction (Remove Dead Objects)
```csharp
internal int Compact()
{
    int removed = _objectTable.Count;
    
    // Find all reachable objects via transitive closure from trailer
    PdfReference[] reachable = TransitiveClosure(document.Trailer);
    
    // Remove unreachable
    _objectTable.Clear();
    foreach (PdfReference iref in reachable)
        _objectTable.Add(iref.ObjectID, iref);
    
    return removed - _objectTable.Count;
}

// BFS/DFS to find all referenced objects
PdfReference[] TransitiveClosure(PdfObject root)
{
    // Start from trailer (catalog, pages, etc.)
    // Follow all references recursively
    // Return all visited references
}
```

#### Renumbering (Sequential Numbering)
```csharp
internal void Renumber()
{
    int newNumber = 1;
    var oldReferences = new List<PdfReference>(_objectTable.Values);
    oldReferences.Sort(PdfReference.Comparer);
    
    _objectTable.Clear();
    
    foreach (PdfReference oldIref in oldReferences)
    {
        oldIref.ObjectID = new(newNumber++, 0);
        _objectTable.Add(oldIref.ObjectID, oldIref);
    }
    
    MaxObjectNumber = newNumber - 1;
}
```

### Cross-Reference Section Writing
```csharp
internal void WriteObject(PdfWriter writer)
{
    writer.WriteRaw("xref\n");
    
    var iRefs = AllReferences;
    int count = iRefs.Length;
    
    // Xref header: "0 N" = start at object 0, N total entries
    writer.WriteRaw(Invariant($"0 {count + 1}\n"));
    
    // Object 0 (always free)
    writer.WriteRaw(Invariant($"{0:0000000000} {65535:00000} f \n"));
    
    // All objects in order
    for (int idx = 0; idx < count; idx++)
    {
        var iref = iRefs[idx];
        // Must be exactly 20 bytes per line:
        // nnnnnnnnnn ggggg n/f SP LF
        writer.WriteRaw(Invariant($"{iref.Position:0000000000} {iref.GenerationNumber:00000} n \n"));
    }
}
```

### Memory Model
```
IrefTable:
  - Dictionary: O(num_objects)
  - Each entry: ~100-200 bytes (reference metadata)
  - Large document (10,000 objects): ~1-2 MB for IrefTable itself
  - Plus: All actual objects stored elsewhere
```

---

## 8. PdfObject - Base Class for Serialization

### Location
**File**: [src/PdfSharp/Pdf/PdfObject.cs](src/PdfSharp/Pdf/PdfObject.cs)

### Purpose
Base class for all indirect PDF objects (PdfDictionary, PdfArray, PdfContent, etc.).

### Class Hierarchy
```
PdfObject (abstract)
  ├── PdfContainer
  │   ├── PdfDictionary
  │   │   ├── PdfContent
  │   │   ├── PdfStream
  │   │   └── ...
  │   └── PdfArray
  └── PdfPrimitiveObject
      ├── PdfBoolean
      ├── PdfInteger
      ├── PdfReal
      ├── PdfString
      └── ...
```

### Key Properties
```csharp
public abstract class PdfObject : PdfItem
{
    // Identity
    public PdfObjectID ObjectID { get; set; }
    public PdfReference? Reference { get; }  // If indirect
    
    // Ownership
    public PdfDocument Document { get; set; }
    
    // Parent relationship (for direct objects)
    public ParentInfo? ParentInfo { get; set; }
    
    // Flags
    public ItemFlags ItemFlags { get; set; }
}
```

### Serialization Pattern
```csharp
public abstract class PdfObject : PdfItem
{
    // Each derived class implements:
    internal virtual void WriteObject(PdfWriter writer)
    {
        // Write this object in PDF syntax
        // Examples:
        //   PdfInteger: "42"
        //   PdfDictionary: "<< /Key /Value >>"
        //   PdfContent: "<< /Length 1234 >> stream\n...bytes...\nendstream"
    }
}

// Example: PdfDictionary.WriteObject
internal override void WriteObject(PdfWriter writer)
{
    if (IsIndirect)
    {
        // Indirect object: write with object number
        writer.WriteRaw($"{ObjectID.ObjectNumber} {ObjectID.GenerationNumber} obj\n");
    }
    
    // Write dictionary
    writer.WriteRaw("<<\n");
    foreach (var kvp in Elements)
    {
        writer.WriteRaw($"/{kvp.Key} ");
        kvp.Value.WriteObject(writer);  // Recursive
    }
    writer.WriteRaw(">>\n");
    
    // If has stream, write stream section
    if (Stream != null)
    {
        writer.WriteRaw("stream\n");
        writer.WriteRaw(Stream.Value);
        writer.WriteRaw("\nendstream\n");
    }
    
    if (IsIndirect)
    {
        writer.WriteRaw("endobj\n");
    }
}
```

---

## Complete Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                      APPLICATION CODE                           │
│                                                                  │
│  var doc = new PdfDocument();                                   │
│  var page = doc.AddPage();                                      │
│  var gfx = XGraphics.FromPdfPage(page);                         │
│  gfx.DrawString("Hello", font, brush, rect);                   │
│  gfx.Dispose();                                                 │
│  doc.Save("output.pdf");                                        │
└─────────────────────────────────────────────────────────────────┘
           ↓
┌─────────────────────────────────────────────────────────────────┐
│              PHASE 1: PAGE CREATION & RENDERING                 │
│                                                                  │
│  1. AddPage() → PdfPage instance created                         │
│     - MediaBox set                                               │
│     - Added to Pages collection                                  │
│     - Registered in IrefTable with object number                │
│                                                                  │
│  2. FromPdfPage(page) → XGraphics created                        │
│     - XGraphicsPdfRenderer initialized                           │
│     - page.RenderContent = new PdfContent(doc)                  │
│     - _renderer._content = new StringBuilder()                   │
│                                                                  │
│  3. Drawing Operations                                           │
│     - gfx.DrawString() → XGraphicsPdfRenderer.DrawString()      │
│     - RealizeFont() - ensure font in page resources             │
│     - AppendFormatArgs() - PDF operators appended to _content   │
│     - _content accumulates: "BT /F1 12 Tf ... Tj ET\n"         │
│                                                                  │
│  4. gfx.Dispose()                                               │
│     - XGraphicsPdfRenderer.Close() called                        │
│     - contentString = _content.ToString()                        │
│     - bytes = RawEncoding.GetBytes(contentString)               │
│     - page.RenderContent.CreateStream(bytes)                    │
│     - PdfContent.Stream = bytes (O(page_size) in memory)       │
└─────────────────────────────────────────────────────────────────┘
           ↓
┌─────────────────────────────────────────────────────────────────┐
│                 PHASE 2: SAVE INITIALIZATION                    │
│                                                                  │
│  1. doc.Save(stream) → SaveAsync() → DoSaveAsync()              │
│                                                                  │
│  2. PrepareForSave()                                            │
│     - IrefTable.Compact() - remove unreachable objects          │
│     - IrefTable.Renumber() - sequential numbering 1,2,3,...    │
│                                                                  │
│  3. Write Security Settings if needed                           │
│     - Encrypt dictionary created and added to IrefTable         │
└─────────────────────────────────────────────────────────────────┘
           ↓
┌─────────────────────────────────────────────────────────────────┐
│                PHASE 3: SERIALIZATION (CRITICAL)                │
│                                                                  │
│  1. writer.WriteFileHeader() → "%PDF-1.7\n"                     │
│                                                                  │
│  2. FOR EACH PdfReference iref IN IrefTable.AllReferences:      │
│     (All objects: Pages, PdfPage, PdfContent, Fonts, etc.)     │
│                                                                  │
│     a. iref.Position = writer.Position (record byte offset)     │
│                                                                  │
│     b. obj = iref.Value                                         │
│                                                                  │
│     c. obj.WriteObject(writer)                                  │
│        - PdfDictionary.WriteObject()                            │
│          • Writes: "1 0 obj\n << /Type /Page ... >> endobj\n"  │
│        - PdfContent.WriteObject()                               │
│          • If CompressContentStreams:                           │
│            - Stream.Value = FlateDecode.Encode(Stream.Value)   │
│            - Elements.SetName(Filter, "/FlateDecode")          │
│          • Writes stream to writer                              │
│        - Other objects write their representations              │
│                                                                  │
│  MEMORY USAGE: All objects still in RAM during this phase      │
│                O(document_size)                                 │
└─────────────────────────────────────────────────────────────────┘
           ↓
┌─────────────────────────────────────────────────────────────────┐
│               PHASE 4: XREF TABLE & TRAILER                     │
│                                                                  │
│  1. startXRef = writer.Position (remember xref byte location)   │
│                                                                  │
│  2. IrefTable.WriteObject(writer)                               │
│     - Writes "xref\n"                                           │
│     - Writes "0 N\n" (object 0 to N)                           │
│     - FOR EACH iref:                                            │
│       • "0000000001 00000 n \n" (byte offset + generation)     │
│                                                                  │
│  3. writer.WriteRaw("trailer\n")                                │
│                                                                  │
│  4. Trailer.WriteObject(writer)                                 │
│     - Writes: << /Size N /Root catalog /Info info >>           │
│                                                                  │
│  5. writer.WriteEof(this, startXRef)                            │
│     - Writes: "startxref\nN\n%%EOF\n"                          │
│                                                                  │
│  COMPLETE PDF FILE WRITTEN                                      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Memory Consumption Breakdown

### Typical 100-Page Document with Images

```
Memory Component                  Size Estimate    Notes
─────────────────────────────────────────────────────────
PdfDocument + metadata            ~1 MB           Pages collection, catalogs, etc.
IrefTable (100 pages + objects)   ~2 MB           100+ objects × ~20KB avg
Page Metadata (100 pages)         ~500 KB         Dimensions, boxes, annotations
Font Cache                        ~5 MB           Embedded fonts (if embedded)
Image Data (in memory)            VARIABLE        Typically 1-100 MB per image
Content Streams (100 pages)       ~10 MB          Average 100 KB per page
Resource Dictionaries             ~2 MB           Font refs, image refs, etc.
Graphics State Objects            ~1 MB           Color spaces, graphics states
───────────────────────────────────────────────────────
TYPICAL TOTAL                     ~30-120 MB      + Image data
```

### Large Document (1000 pages, many images)

```
Memory Component                  Size Estimate
─────────────────────────────────────────────
Basic Document Objects            ~100 MB
Content Streams                   ~100 MB        1000 pages × 100 KB avg
Image Data (in memory)            ~500 MB - 1 GB (100 images × 5-10 MB each)
Resource Dictionaries             ~10 MB
───────────────────────────────────────────────
TOTAL                             ~700 MB - 1.1 GB
```

**Problem**: Cannot scale to multi-gigabyte documents (e.g., 10,000 pages or large image collections)

---

## Current Architecture Bottlenecks

### 1. **No Streaming Content Emission**
- PDF operators accumulated in StringBuilder during rendering
- Cannot write to stream until XGraphics.Dispose()
- Full page content converted to bytes at once

### 2. **All Objects in Memory During Save**
- IrefTable holds references to ALL objects
- Complete object graph traversed during serialization
- No object can be garbage collected until Save() completes

### 3. **Xref Requires All Positions Precomputed**
- Cross-reference table lists byte positions of all objects
- Positions only known AFTER each object serialized
- Cannot use streaming (would require forward references)

### 4. **Compression Applied Late**
- Content streams compressed during WriteObject()
- Happens during save phase, not during rendering
- Memory still holds uncompressed bytes

### 5. **No Page Disposal**
- Rendered pages remain in IrefTable indefinitely
- Page resources not freed after rendering completes
- Memory not released until Save()

### 6. **Single-Pass Sequential Numbering**
- Object IDs assigned during creation
- Renumbering required during save for clean numbering
- Cannot assign final IDs until all objects created

---

## Key Classes Summary Table

| Class | Location | Role | Memory | Key Method |
|-------|----------|------|--------|-----------|
| XGraphics | Drawing/ | Public drawing API | O(page) | DrawString() |
| XGraphicsPdfRenderer | Drawing.Pdf/ | PDF operator generation | O(page) | AppendFormatPoint() |
| PdfContent | Pdf.Advanced/ | Single content stream | O(page) | CreateStream() |
| PdfContents | Pdf.Advanced/ | Content stream array | O(1) | AppendContent() |
| PdfPage | Pdf/ | Page object + container | O(page) | Contents property |
| PdfDocument | Pdf/ | Document + orchestrator | O(all) | Save() |
| PdfCrossReferenceTable | Pdf.Advanced/ | Object registry | O(objects) | Add(), WriteObject() |
| PdfObject | Pdf/ | Serialization base | Variable | WriteObject() |

---

## Next Steps for Streaming Architecture

To transform this to a streaming model:

1. **Deferred Content Writing**: Write content stream bytes directly to output stream instead of StringBuilder
2. **Incremental Object Writing**: Allow objects to be written as soon as created, not at end
3. **Forward References**: Implement xref offset placeholders for forward references
4. **Page Lifecycle**: Dispose/flush pages after writing, before Save() completes
5. **Buffered Compression**: Compress content using streaming pipeline (DeflateStream)
6. **Adaptive Numbering**: Assign object numbers in output order rather than renumbering at save time

This analysis provides the foundation for the streaming refactor.
