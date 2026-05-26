# PDFsharp Architecture - Implementation Details & Code References

## Component Implementation Reference

This document provides line-by-line implementation details and code references for each key component.

---

## 1. XGraphics - Drawing Surface (Public API)

**File**: `src/PdfSharp/Drawing/XGraphics.cs`  
**Type**: `public sealed class XGraphics : IDisposable`  
**Lines**: ~2000+

### Constructor Selection
```csharp
// Line ~85-120
#if GDI
XGraphics(Graphics? gfx, XSize size, XGraphicsUnit pageUnit, XPageDirection pageDirection, RenderEvents? renderEvents = null)
{
    _gsStack = new GraphicsStateStack(this);  // Line ~102
    TargetContext = XGraphicTargetContext.GDI;
    _gfx = gfx;
    _drawGraphics = true;
    _pageSize = size;
    _pageUnit = pageUnit;
    // ... unit conversions ...
    _pageDirection = pageDirection;
    Initialize();  // Line ~134
}
#endif
```

### FromPdfPage Factory Method
```csharp
// ~Line 300-350
public static XGraphics FromPdfPage(PdfPage page)
{
    return FromPdfPage(page, XGraphicsUnit.Point, XPageDirection.Downwards);
}

public static XGraphics FromPdfPage(PdfPage page, XGraphicsUnit pageUnit, XPageDirection pageDirection)
{
    return FromPdfPage(page, pageUnit, pageDirection, null);
}

public static XGraphics FromPdfPage(PdfPage page, XGraphicsUnit pageUnit, 
    XPageDirection pageDirection, RenderEvents? renderEvents)
{
    // Create PDF renderer
    var xgfxRenderer = new XGraphicsPdfRenderer(page, ... );  // Instantiate renderer
    
    // Create XGraphics without underlying platform graphics object
    var xgfx = new XGraphics(null, page.Size, pageUnit, pageDirection, renderEvents);
    xgfx._renderer = xgfxRenderer;
    xgfx.TargetContext = XGraphicTargetContext.Pdf;
    
    return xgfx;
}
```

### Drawing Methods Delegation
```csharp
// Line ~400-500
public void DrawString(string s, XFont font, XBrush brush, double x, double y, XStringFormat? format)
{
    if (_renderer is IXGraphicsRenderer renderer)
    {
        renderer.DrawString(s, font, brush, new XRect(x, y, 0, 0), format);  // Delegate
    }
}

public void DrawLine(XPen pen, double x1, double y1, double x2, double y2)
{
    if (_renderer is IXGraphicsRenderer renderer)
    {
        renderer.DrawLine(pen, x1, y1, x2, y2);  // Delegate
    }
}
```

### Dispose Pattern
```csharp
// Line ~700-750
public void Dispose()
{
    Dispose(true);
    GC.SuppressFinalize(this);
}

void Dispose(bool disposing)
{
    if (disposing)
    {
        if (_renderer is IXGraphicsRenderer renderer)
        {
            renderer.Close();  // Calls XGraphicsPdfRenderer.Close() for PDF
        }
    }
}
```

**Key Properties**:
- `_renderer` (IXGraphicsRenderer): Delegates actual drawing
- `_gsStack` (GraphicsStateStack): Maintains graphics state stack
- `TargetContext`: Indicates PDF, GDI, or WPF rendering
- `_pageSize`, `_pageUnit`, `_pageDirection`: Page configuration

---

## 2. XGraphicsPdfRenderer - PDF Operator Generation

**File**: `src/PdfSharp/Drawing.Pdf/XGraphicsPdfRenderer.cs`  
**Type**: `class XGraphicsPdfRenderer : IXGraphicsRenderer, IPageContentRenderer`  
**Lines**: ~2000+

### Constructor & Initialization
```csharp
// Line ~40-70
public XGraphicsPdfRenderer(PdfPage page, XGraphics gfx, XGraphicsPdfPageOptions options)
{
    _page = page;                                          // Line ~41
    _colorMode = page.Document.Options.ColorMode;         // Line ~42
    _options = options;                                    // Line ~43
    _gfx = gfx;                                           // Line ~44
    _content = new StringBuilder();                        // Line ~45 - CRITICAL: PDF operators accumulate here
    page.RenderContent!.SetRenderer(this);                // Line ~46 - Register as active renderer
    _gfxState = new PdfGraphicsState(this);               // Line ~47 - Graphics state tracker
}

public XGraphicsPdfRenderer(XForm form, XGraphics gfx)
{
    _form = form;                                          // Line ~52
    _colorMode = form.Owner.Options.ColorMode;           // Line ~53
    _gfx = gfx;                                           // Line ~54
    _content = new StringBuilder();                        // Line ~55 - Same accumulation
    form.PdfRenderer = this;                              // Line ~56
    _gfxState = new PdfGraphicsState(this);               // Line ~57
}
```

### GetContent() - Finalizes Content
```csharp
// Line ~80-85
string GetContent()
{
    EndPage();                                             // Close any open text objects
    return _content.ToString();                            // Convert StringBuilder to string
}
```

### Close() - Serializes Content to Stream
```csharp
// Line ~90-115
public void Close()
{
    if (_page != null!)
    {
        var renderer = _page.RenderContent?.Renderer;     // Line ~93
        if (!ReferenceEquals(renderer, this))
            throw new InvalidOperationException("...");    // Line ~97 - Sanity check
        
        var content = _page.RenderContent;                 // Line ~99
        Debug.Assert(content != null);
        
        var contentString = GetContent();                  // Line ~101 - Gets accumulated operators
        
        // Create stream with content bytes
        content.CreateStream(                              // Line ~103 - CRITICAL CALL
            PdfEncoders.RawEncoding.GetBytes(contentString)
        );
        
        _gfx = null!;                                      // Line ~106 - Clear reference
        content.SetRenderer(null);                         // Line ~107 - Unregister renderer
        _page.RenderContent = null;                        // Line ~108 - Clear active content
        
        // Remove empty stream if page has multiple
        if (contentString.Length == 0 && _page.Contents.Elements.Count > 1)
        {
            _page.Contents.Elements.Remove(content);       // Line ~112
        }
    }
    else if (_form != null!)
    {
        // Similar for forms
        _form._pdfForm!.CreateStream(
            PdfEncoders.RawEncoding.GetBytes(GetContent())
        );
    }
}
```

### Content Appending Methods - Core Engine
```csharp
// Line ~1875-1910 (Append methods at end of file)

void Append(string value)
{
    _content.Append(value);                                // Line ~1878 - Direct append
}

void AppendFormatArgs(string format, params object[] args)
{
    _content.AppendFormat(                                 // Line ~1883
        CultureInfo.InvariantCulture, format, args
    );
}

void AppendFormatPoint(string format, double x, double y)
{
    XPoint result = WorldToView(new XPoint(x, y));        // Line ~1911 - Apply coordinate transform
    _content.AppendFormat(                                 // Line ~1912
        CultureInfo.InvariantCulture, format, result.X, result.Y
    );
}

void AppendFormatRect(string format, double x, double y, double width, double height)
{
    XPoint point1 = WorldToView(new XPoint(x, y));        // Line ~1917
    _content.AppendFormat(                                 // Line ~1918
        CultureInfo.InvariantCulture, format, 
        point1.X, point1.Y, width, height
    );
}
```

### DrawString Implementation - Text Rendering
```csharp
// Line ~475-870 (Main DrawString, plus supporting methods)

public void DrawString(string s, XFont font, XBrush brush, XRect rect, XStringFormat format)
{
    // Events
    if (Owner.UAManager != null)                           // Line ~479
        Owner.Events.OnPageGraphicsAction(...);
    
    // Prepare text rendering
    RealizeFont(font);                                     // Line ~560 - Ensure font in resources
    RealizeBrush(brush);                                   // Line ~561 - Set fill color
    
    // Build text positioning operator
    // Using various formatting strings depending on italic simulation
    // Append: "BT ... Tm ... Tj ET\n"
    
    AppendFormatArgs(s_format, m.M11, m.M12, m.M21, m.M22, // Lines ~800-850
        m.OffsetX, m.OffsetY, encoded_text);
}
```

### DrawBeziers Implementation - Path Rendering
```csharp
// Line ~200-210
public void DrawBeziers(XPen pen, XPoint[] points)
{
    Realize(pen);                                          // Line ~207 - Set pen state
    
    const string format = DefaultNumberFormat4;            // Line ~209
    AppendFormatPoint("{0:" + format + "} {1:" + format + "} m\n",  // Move
        points[0].X, points[0].Y);
    
    for (int idx = 1; idx < count; idx += 3)
    {
        AppendFormat3Points(                               // Cubic Bezier curve
            "{0:" + format + "} ... {5:" + format + "} c\n",
            points[idx].X, points[idx].Y, ...
        );
    }
    _content.Append("S\n");                                // Stroke
}
```

### Realize Methods - Graphics State Setup
```csharp
// Line ~1500+ (Realize methods)

void Realize(XPen pen)
{
    // Set stroke color
    if (pen.Color != _gfxState.StrokeColor)
    {
        _gfxState.StrokeColor = pen.Color;
        
        // Format color in PDF: "R G B RG" (RGB space)
        XColor color = ConvertColor(pen.Color);
        AppendFormat4("{0:" + format + "} {1:" + format + "} {2:" + format + "} RG\n",
            color.R, color.G, color.B);
    }
    
    // Set line width
    if (Math.Abs(pen.Width - _gfxState.LineWidth) > 0.0001)
    {
        _gfxState.LineWidth = pen.Width;
        AppendFormatDouble("w {0:" + format + "} w\n", pen.Width);
    }
    
    // ... dash pattern, line cap, line join ...
}

void Realize(XBrush brush)
{
    if (brush is XSolidBrush solidBrush)
    {
        if (solidBrush.Color != _gfxState.FillColor)
        {
            _gfxState.FillColor = solidBrush.Color;
            
            // Format fill color: "r g b rg" (lowercase for non-stroking)
            XColor color = ConvertColor(solidBrush.Color);
            AppendFormat4("{0:" + format + "} {1:" + format + "} {2:" + format + "} rg\n",
                color.R, color.G, color.B);
        }
    }
}
```

### Memory Growth During Rendering

```
Initial state:
  _content = new StringBuilder()  // ~16 KB default capacity

After first operation (e.g., DrawString):
  _content.Capacity: 16 KB
  _content.Length: ~50 bytes
  Used: ~0.3%

After 1000 operations:
  _content.Capacity: May expand: 16 KB → 32 KB → 64 KB
  _content.Length: ~50,000 bytes
  Used: ~78% at 64 KB

StringBuilder grows by doubling capacity when needed:
  16 KB → 32 KB → 64 KB → 128 KB → 256 KB → ...

For a complex page: Final length typically 100 KB - 500 KB
```

**Key Members**:
- `_content` (StringBuilder): PDF operators accumulation point
- `_page` (PdfPage): Reference to page being rendered
- `_gfxState` (PdfGraphicsState): Current font, colors, transforms
- `_gfx` (XGraphics): Parent graphics object

---

## 3. PdfContent - Content Stream Object

**File**: `src/PdfSharp/Pdf.Advanced/PdfContent.cs`  
**Type**: `public sealed class PdfContent : PdfDictionary`  
**Lines**: ~250

### Constructor & Stream Creation
```csharp
// Line ~19-22
public PdfContent(PdfDocument document)
    : base(document, true)  // true = createIndirect
{
}

// Line ~35-39
internal PdfContent(PdfDictionary dict)
    : base(dict)
{
    Decode();  // Uncompress if stream was compressed
}
```

### CreateStream Method
```csharp
// Inherited from PdfDictionary
// Defined in PdfDictionary.cs line ~2545+

public PdfStream CreateStream(byte[] value)
{
    return Stream = new PdfStream(value, this);
}
```

### PdfStream Class (nested in PdfDictionary)
```csharp
// Line ~2545-2650 in PdfDictionary.cs

public sealed class PdfStream
{
    internal PdfStream(PdfDictionary ownerDictionary)
    {
        _ownerDictionary = ownerDictionary;               // Line ~2553
        if (ownerDictionary.IsIndirect is false)
        {
            ownerDictionary.SetMustBeIndirect();          // Line ~2557
        }
    }

    internal PdfStream(byte[] value, PdfDictionary owner)
        : this(owner)
    {
        _value = value;                                   // Line ~2568 - Store bytes
    }

    public byte[] Value
    {
        get => _value ??= [];
        set
        {
            if (value == null!)
                throw new ArgumentNullException(nameof(value));
            _value = value;                               // Line ~2591 - Update stored bytes
            _ownerDictionary.Elements.SetInteger(       // Line ~2592 - Update /Length
                "/Length", value.Length
            );
        }
    }
    byte[]? _value;                                       // Line ~2595 - Actual data
}
```

### WriteObject - Serialization with Compression
```csharp
// Line ~90-135

internal override void WriteObject(PdfWriter writer)
{
    if (_pdfRenderer != null)                              // Line ~91 - Check if still rendering
    {
        if (_pdfRenderer is XGraphicsPdfRenderer xgfxRenderer)
        {
            xgfxRenderer.Close();                          // Line ~95 - Close active renderer
        }
        else
        {
            throw new InvalidOperationException("...");    // Line ~100
        }
        Debug.Assert(_pdfRenderer == null);                // Line ~102
    }

    if (Stream != null!)                                   // Line ~105 - Stream exists
    {
        const int streamLengthCompressionThreshold = 32;   // Line ~107 - Min length to compress
        
        // Check if should compress
        if (Owner.Options.CompressContentStreams           // Line ~110
            && !Elements.HasValue(PdfStream.Keys.Filter)   // Line ~111 - Not already filtered
            && Stream.Value.Length > streamLengthCompressionThreshold)  // Line ~112 - Large enough
        {
            // COMPRESSION HAPPENS HERE
            Stream.Value = Filtering.FlateDecode.Encode(   // Line ~114 - Compress
                Stream.Value, 
                Document.Options.FlateEncodeMode
            );
            Elements.SetName(                              // Line ~117 - Set filter name
                PdfStream.Keys.Filter, "/FlateDecode"
            );
        }
        Elements.SetInteger(                              // Line ~120 - Update length
            PdfStream.Keys.Length, Stream.Length
        );
    }

    base.WriteObject(writer);                             // Line ~123 - Call PdfDictionary.WriteObject
}
```

### Compression Detail
```csharp
// Filtering.FlateDecode.Encode located in Pdf.Filters/PdfFlateEncodeFilter.cs

public static byte[] Encode(byte[] input, PdfFlateEncodeMode mode)
{
    using (var output = new MemoryStream())
    {
        using (var stream = new DeflateStream(output, CompressionMode.Compress))
        {
            stream.Write(input, 0, input.Length);
        }
        return output.ToArray();  // Returns compressed bytes
    }
}
```

**Key Properties**:
- `Stream` (PdfStream): Holds byte array of content
- `Elements` (Dictionary): Metadata (/Type, /Length, /Filter, etc.)
- `_pdfRenderer`: Reference to active XGraphicsPdfRenderer

---

## 4. PdfPage - Page Container

**File**: `src/PdfSharp/Pdf/PdfPage.cs`  
**Type**: `public sealed class PdfPage : PdfPageTreeBase, IContentStream`  
**Lines**: ~1500+

### Initialization
```csharp
// Line ~22-45
public PdfPage()
{
    Elements.SetName(Keys.Type, "/Page");                  // Line ~24
    Initialize(false);                                     // Line ~25
}

public PdfPage(PdfDocument document)
    : base(document)
{
    Elements.SetName(Keys.Type, "/Page");                  // Line ~31
    Elements[Keys.Parent] = document.Pages.RequiredReference;  // Line ~32
    Initialize(false);                                     // Line ~33
}

internal void Initialize(bool setupSizeFromMediaBox)
{
    if (setupSizeFromMediaBox)                             // Line ~48
    {
        var rectangle = Elements.GetRectangle(           // Line ~50
            InheritablePageKeys.MediaBox, false
        );
        _width = XUnit.FromPoint(rectangle.X2 - rectangle.X1);
        _height = XUnit.FromPoint(rectangle.Y2 - rectangle.Y1);
    }
    else
    {
        Size = RegionInfo.CurrentRegion.IsMetric           // Line ~61 - A4 or Letter
            ? PageSize.A4 : PageSize.Letter;
    }
}
```

### Contents Property - Lazy Initialization
```csharp
// Line ~520-572

public PdfContents Contents
{
    get
    {
        if (_contents == null)                             // Line ~524 - Lazy load
        {
            var item = Elements.GetValue(Keys.Contents);   // Line ~529
            
            if (item == null)                              // Line ~531 - New page
            {
                _contents = new PdfContents(Owner);        // Line ~532 - Create empty
            }
            else if (item is PdfArray)                     // Line ~535 - Existing array
            {
                var array = (PdfArray)item;
                _contents = new PdfContents(array);        // Line ~550 - Wrap existing
            }
            else if (item is PdfReference)                 // Line ~551 - Single stream
            {
                var iref = (PdfReference)item;
                if (iref.Value is PdfArray array)
                {
                    _contents = new PdfContents(array);    // Line ~556
                }
                else
                {
                    _contents = new PdfContents(Owner);    // Line ~558 - Convert to array
                    _contents.Elements.Add(iref);          // Line ~559
                }
            }
        }
        return _contents;                                  // Line ~569
    }
}
PdfContents? _contents;                                    // Line ~572 - Cache
```

### RenderContent - Active Rendering Track
```csharp
// Line ~515
internal PdfContent? RenderContent;

// Used during rendering to track active content stream:
// PdfContent renderContent = page.RenderContent;
// renderContent.CreateStream(bytes);
// page.RenderContent = null;  // Mark rendering complete
```

**Key Properties**:
- `Contents` (PdfContents): Array of content streams
- `RenderContent` (PdfContent): Currently rendering stream
- `MediaBox`, `CropBox`: Page dimensions
- `Resources`: Font and image dictionaries

---

## 5. PdfDocument - Document & Save Orchestrator

**File**: `src/PdfSharp/Pdf/PdfDocument.cs`  
**Type**: `public sealed class PdfDocument : IDisposable`  
**Lines**: ~1500+

### Initialization
```csharp
// Line ~40-50
public PdfDocument()
{
    PdfSharpLogHost.Logger.PdfDocumentCreated(Name);       // Line ~40
    CreationDate = DateTimeOffset.Now;                     // Line ~41
    State = DocumentState.Created;                         // Line ~42
    _version = 17;  // 1.7                                 // Line ~43
    Initialize();                                          // Line ~44
    Info.CreationDate = CreationDate;                      // Line ~45
}

void Initialize()
{
    _fontTable = new PdfFontTable(this);                   // Line ~88
    _imageTable = new PdfImageTable(this);                 // Line ~89
    IrefTable = new PdfCrossReferenceTable(this);          // Line ~90 - CRITICAL
    Trailer = new PdfTrailer(this);                        // Line ~91
    Trailer.CreateNewDocumentIDs();                        // Line ~92
}
```

### Save Method - Entry Point
```csharp
// Line ~237-280

public void Save(string path)
{
    SaveAsync(path).GetAwaiter().GetResult();              // Line ~239 - Call async
}

public async Task SaveAsync(string path)
{
    EnsureNotYetSaved();                                   // Line ~245
    
    using var stream = new FileStream(path, FileMode.Create, ...);
    await SaveAsync(stream).ConfigureAwait(false);         // Line ~251
}

public void Save(Stream stream, bool closeStream = false)
{
    SaveAsync(stream, closeStream).GetAwaiter().GetResult();  // Line ~268
}

public async Task SaveAsync(Stream stream, bool closeStream = false)
{
    EnsureNotYetSaved();                                   // Line ~273
    
    if (IsPdfA)
        PrepareForPdfA();                                  // Line ~279
    
    var effectiveSecurityHandler = 
        SecuritySettings.EffectiveSecurityHandler;         // Line ~283
    
    PdfWriter? writer = null;
    try
    {
        writer = new(stream, this, effectiveSecurityHandler);  // Line ~286
        await DoSaveAsync(writer).ConfigureAwait(false);   // Line ~287 - Main work here
    }
    finally
    {
        writer?.Close(closeStream);                        // Line ~297
    }
}
```

### DoSaveAsync - Core Save Logic
```csharp
// Line ~325-410

async Task DoSaveAsync(PdfWriter writer)
{
    PdfSharpLogHost.Logger.PdfDocumentSaved(Name);         // Line ~327
    
    // Validation
    if (_pages == null || _pages.Count == 0)               // Line ~329
        throw new InvalidOperationException("...");
    
    // Add signature components if needed
    if (DigitalSignatureHandler != null)                   // Line ~337
        await DigitalSignatureHandler.AddSignatureComponentsAsync();
    
    // Prepare structure
    if (Trailer is PdfCrossReferenceStream crossReferenceStream)
        Trailer = new PdfTrailer(crossReferenceStream);    // Line ~343
    
    // Add encryption if needed
    var effectiveSecurityHandler = _securitySettings?.EffectiveSecurityHandler;
    if (effectiveSecurityHandler != null)                  // Line ~345
    {
        if (effectiveSecurityHandler.Reference == null)
            IrefTable.Add(effectiveSecurityHandler);       // Line ~348 - Register security
        Trailer.Elements[PdfTrailer.Keys.Encrypt] = 
            _securitySettings!.SecurityHandler.RequiredReference;
    }
    
    // PREPARATION PHASE
    PrepareForSave();                                      // Line ~356 - Compact + renumber
    
    effectiveSecurityHandler?.PrepareForWriting();         // Line ~358
    
    // SERIALIZATION PHASE
    writer.WriteFileHeader(this);                          // Line ~360 - "%PDF-1.7\n"
    
    var irefs = IrefTable.AllReferences;                   // Line ~361 - GET ALL OBJECTS
    int count = irefs.Length;
    
    // Write each object
    for (int idx = 0; idx < count; idx++)                  // Line ~362 - CRITICAL LOOP
    {
        PdfReference iref = irefs[idx];
        iref.Position = writer.Position;                   // Line ~365 - Record position
        
        var obj = iref.Value;                              // Line ~369
        
        effectiveSecurityHandler?.EnterObject(obj.ObjectID);  // Line ~372
        
        obj.WriteObject(writer);                           // Line ~375 - WRITE EACH OBJECT
    }
    
    effectiveSecurityHandler?.LeaveObject();               // Line ~378
    
    // XREF & TRAILER PHASE
    var startXRef = (SizeType)writer.Position;             // Line ~381 - Remember xref location
    
    IrefTable.WriteObject(writer);                         // Line ~383 - Write xref section
    writer.WriteRaw("trailer\n");                          // Line ~384
    
    Trailer.Elements.SetInteger("/Size", count + 1);       // Line ~385
    Trailer.WriteObject(writer);                           // Line ~386
    
    writer.WriteEof(this, startXRef);                      // Line ~387 - Write EOF
    
    // Signing
    if (DigitalSignatureHandler != null)                   // Line ~391
        await DigitalSignatureHandler.ComputeSignatureAndRange(writer);
}
finally
{
    writer.Stream.Flush();                                 // Line ~398
    State |= DocumentState.Saved;                         // Line ~400
}
```

### PrepareForSave - Cleanup & Renumbering
```csharp
// Line ~516-535

void PrepareForSave()
{
    int removed = IrefTable.Compact();                     // Line ~519 - Remove dead objects
    
    IrefTable.Renumber();                                  // Line ~532 - Sequential numbering
}
```

**Key Members**:
- `IrefTable` (PdfCrossReferenceTable): Registry of all objects
- `Pages` (PdfPages): Page collection
- `Trailer` (PdfTrailer): PDF trailer dictionary
- `_fontTable`, `_imageTable`: Resource tables
- `State`: Document state enum

---

## 6. PdfCrossReferenceTable - Object Registry

**File**: `src/PdfSharp/Pdf.Advanced/PdfCrossReferenceTable.cs`  
**Type**: `sealed class PdfCrossReferenceTable`  
**Lines**: ~600

### Constructor & Registration
```csharp
// Line ~20-30
sealed class PdfCrossReferenceTable(PdfDocument document)
{
    PdfDocument document = document;                       // Property
    
    Dictionary<PdfObjectID, PdfReference> _objectTable = new();  // Main registry
    
    int MaxObjectNumber { get; set; }                      // Highest object number
    
    bool IsUnderConstruction { get; set; }                 // true while reading
}

// Line ~43-85
public void Add(PdfObject obj)
{
    if (obj.ObjectID.IsEmpty)                              // Line ~56 - Assign new ID
    {
        obj.SetObjectID(GetNewObjectNumber(), 0);          // Line ~59
    }

    if (_objectTable.ContainsKey(obj.ObjectID))            // Line ~62
        throw new InvalidOperationException("...");

    _objectTable.Add(obj.ObjectID, obj.RequiredReference); // Line ~68 - Register
    
    MaxObjectNumber = Math.Max(MaxObjectNumber,            // Line ~71 - Update max
        obj.ObjectNumber);
}

// Line ~87-95
public bool TryAdd(PdfObject obj)
{
    if (obj.ObjectID.IsEmpty || 
        !_objectTable.ContainsKey(obj.ObjectID))
    {
        Add(obj);
        return true;
    }
    return false;
}
```

### AllReferences - Sorted Array for Serialization
```csharp
// Line ~130-150

internal PdfReference[] AllReferences
{
    get
    {
        var collection = _objectTable.Values;              // Line ~133
        var list = new List<PdfReference>(collection);     // Line ~134
        list.Sort(PdfReference.Comparer);                  // Line ~135 - Sort by object number
        var iRefs = new PdfReference[collection.Count];    // Line ~136
        list.CopyTo(iRefs, 0);                             // Line ~137
        return iRefs;                                      // Line ~138 - Return sorted array
    }
}
```

### Compact - Remove Dead Objects
```csharp
// Line ~200-260

internal int Compact()
{
    int removed = _objectTable.Count;                      // Line ~202
    
    // Find all reachable objects starting from trailer
    PdfReference[] irefs = TransitiveClosure(             // Line ~204
        document.Trailer
    );
    
    // Clear table and re-add only reachable
    _objectTable.Clear();                                  // Line ~208
    foreach (PdfReference iref in irefs)                  // Line ~210
    {
        _objectTable.Add(iref.ObjectID, iref);            // Line ~212
    }
    
    return removed - _objectTable.Count;                   // Line ~215 - Removed count
}

// BFS/DFS traversal
PdfReference[] TransitiveClosure(PdfObject root)
{
    // Line ~290+ - Implements breadth-first search
    var stack = new Stack<PdfObject>();
    var visited = new HashSet<PdfReference>();
    
    stack.Push(root);
    
    while (stack.Count > 0)
    {
        var obj = stack.Pop();
        var iref = obj.Reference;
        
        if (iref != null && visited.Add(iref))
        {
            // Get all objects referenced by this one
            var referenced = obj.GetReferences();
            foreach (var refObj in referenced)
                stack.Push(refObj);
        }
    }
    
    return visited.ToArray();
}
```

### Renumber - Reassign Object Numbers
```csharp
// Line ~320-345

internal void Renumber()
{
    int newNumber = 1;                                     // Line ~322
    var oldReferences = new List<PdfReference>(          // Line ~323
        _objectTable.Values
    );
    oldReferences.Sort(PdfReference.Comparer);             // Line ~324 - Sort first
    
    _objectTable.Clear();                                  // Line ~326 - Empty table
    
    foreach (PdfReference oldIref in oldReferences)        // Line ~328
    {
        // Reassign ID
        oldIref.ObjectID = new(newNumber++, 0);            // Line ~330 - NEW NUMBER
        _objectTable.Add(oldIref.ObjectID, oldIref);       // Line ~331 - Re-register
    }
    
    MaxObjectNumber = newNumber - 1;                       // Line ~334 - Update max
}
```

### WriteObject - Xref Section
```csharp
// Line ~170-190

internal void WriteObject(PdfWriter writer)
{
    writer.WriteRaw("xref\n");                             // Line ~172
    
    var iRefs = AllReferences;                             // Line ~174
    int count = iRefs.Length;
    
    // Xref subsection header
    writer.WriteRaw(                                       // Line ~177
        Invariant($"0 {count + 1}\n")
    );
    
    // Object 0 (always free)
    writer.WriteRaw(                                       // Line ~179
        Invariant($"{0:0000000000} {65535:00000} f \n")
    );
    
    // All objects in sequence
    for (int idx = 0; idx < count; idx++)                  // Line ~182
    {
        var iref = iRefs[idx];
        
        // Exactly 20 bytes per entry: "nnnnnnnnnn ggggg n LF"
        writer.WriteRaw(                                   // Line ~187
            Invariant($"{iref.Position:0000000000} " +
                      $"{iref.GenerationNumber:00000} n \n")
        );
    }
}
```

**Key Methods**:
- `Add(PdfObject obj)`: Register new object with ID assignment
- `AllReferences`: Returns sorted array for serialization
- `Compact()`: Remove unreachable objects
- `Renumber()`: Reassign sequential numbering
- `WriteObject(PdfWriter writer)`: Serialize xref table

---

## 7. PdfObject - Serialization Base

**File**: `src/PdfSharp/Pdf/PdfObject.cs`  
**Type**: `public abstract class PdfObject : PdfItem`  
**Lines**: ~200

### Class Definition & ID Management
```csharp
// Line ~10-40
public abstract class PdfObject : PdfItem
{
    /// <summary>
    /// Initializes a new indirect object.
    /// </summary>
    protected PdfObject(PdfDocument document, bool createIndirect = false)
    {
        Document = document;                               // Line ~23
        ItemFlags = ItemFlags.IsCompoundObject;            // Line ~24
        if (createIndirect)
            document.IrefTable.Add(this);                  // Line ~26 - Auto-register
    }

    // Object identity
    public PdfObjectID ObjectID { get; set; }              // Line ~100+ - Assigned on creation
    
    public PdfReference? Reference 
    {
        get
        {
            if (_iref != null)
                return _iref;
            return null;
        }
    }
    PdfReference? _iref;                                   // Line ~150 - Cached reference
    
    // Ownership
    public PdfDocument Document { get; set; }              // Line ~180 - Owner document
    
    // Parent relationship (for direct objects)
    public ParentInfo? ParentInfo { get; set; }            // Line ~195 - Parent info
}
```

### WriteObject Contract
```csharp
// Line ~250+
internal virtual void WriteObject(PdfWriter writer)
{
    // Each subclass implements this:
    // PdfDictionary.WriteObject() → writes << ... >>
    // PdfArray.WriteObject() → writes [ ... ]
    // PdfContent.WriteObject() → writes stream dictionary + stream
    // PdfString.WriteObject() → writes string
    // etc.
}
```

### Reference Access
```csharp
public PdfReference RequiredReference
{
    get
    {
        if (_iref == null)
            throw new InvalidOperationException("...");
        return _iref;
    }
}
```

**Key Properties**:
- `ObjectID`: Unique identifier (number, generation)
- `Reference`: Self-reference for indirect objects
- `Document`: Owner document

---

## Memory Profiling During Save

```
At start of Save:
  Total Heap: ~30 MB (100 pages)
  IrefTable: ~2 MB
  Page objects: ~10 MB
  Content streams: ~10 MB
  Fonts: ~5 MB

During serialization loop (line 362-375 in DoSaveAsync):
  For each object:
    iref.Position = computed
    obj.WriteObject(writer)
    Bytes written to stream
  
  Heap remains constant:
    Objects not freed during iteration
    All in memory simultaneously

After compression (in PdfContent.WriteObject):
  Stream.Value = FlateDecode.Encode(Stream.Value)  // Line 114
  Creates temporary byte[] for compressed data
  Original bytes freed
  New bytes stored in Stream.Value
  
  Peak memory: During FlateDecode.Encode() when both
  original and compressed bytes exist momentarily

After save completes:
  Objects can be garbage collected
  Heap drops back to baseline
```

---

## Complete Object Dependency Graph

```
PdfDocument (root)
├─ Trailer (PdfTrailer)
│  ├─ Catalog (PdfCatalog)
│  │  └─ Pages (PdfPages)
│  │     └─ PdfPage[] (collection)
│  │        ├─ Contents (PdfContents)
│  │        │  └─ PdfContent[] (array)
│  │        │     └─ Stream (PdfStream)
│  │        │        └─ byte[] _value
│  │        └─ Resources (PdfResources)
│  │           ├─ FontTable (references)
│  │           ├─ ImageTable (references)
│  │           └─ ...
│  └─ Info (PdfDocumentInformation)
│
├─ IrefTable (PdfCrossReferenceTable)
│  └─ _objectTable
│     └─ Dictionary of all objects
│
├─ FontTable (PdfFontTable)
│  └─ Font objects
│
└─ ImageTable (PdfImageTable)
   └─ Image objects

Total objects in large document: 10,000 - 100,000+
All must fit in RAM during Save()
```

This concludes the implementation reference guide.
