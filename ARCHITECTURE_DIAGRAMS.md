# PDFsharp Architecture - Visual Diagrams & Flowcharts

## 1. Component Interaction Diagram

```
┌────────────────────────────────────────────────────────────────────────────┐
│                          Application Layer                                │
│                                                                           │
│  XGraphics (Public Drawing API)                                         │
│  - DrawString(), DrawLine(), DrawRectangle(), DrawImage()               │
│  - Save/Restore graphics state                                          │
└────────────────────────────────────────────────────────────────────────────┘
                                    ↓
┌────────────────────────────────────────────────────────────────────────────┐
│                          Rendering Layer                                  │
│                                                                           │
│  Platform Selection (compile-time):                                      │
│  ├─ GDI (Windows)     ─→ GDI Graphics.FromHwnd()                        │
│  ├─ WPF              ─→ DrawingContext                                   │
│  └─ PDF              ─→ XGraphicsPdfRenderer [TARGET]                   │
│                                                                           │
│  XGraphicsPdfRenderer:                                                   │
│  ├─ _content (StringBuilder)         ← PDF operators accumulate here     │
│  ├─ _page (PdfPage reference)        ← Currently rendering page         │
│  ├─ _gfxState (PdfGraphicsState)     ← Current font, color, transform   │
│  └─ Append* methods                  ← Format + append to _content      │
└────────────────────────────────────────────────────────────────────────────┘
                                    ↓
┌────────────────────────────────────────────────────────────────────────────┐
│                       Content Stream Layer                                │
│                                                                           │
│  PdfContent (PDF indirect object):                                       │
│  ├─ Stream (PdfStream)              ← Raw bytes of content               │
│  ├─ Elements (Dictionary)           ← /Type /Stream /Length /Filter     │
│  └─ _pdfRenderer                    ← Reference to active renderer      │
│                                                                           │
│  CreateStream(bytes):                                                    │
│  └─ Converts StringBuilder content to byte array                         │
│                                                                           │
│  WriteObject(writer):                                                    │
│  ├─ Check if compression enabled                                         │
│  ├─ If yes: FlateDecode.Encode(Stream.Value)                           │
│  ├─ Write dictionary: << /Length N /Filter /FlateDecode >>              │
│  └─ Write stream: stream\n<bytes>\nendstream                            │
└────────────────────────────────────────────────────────────────────────────┘
                                    ↓
┌────────────────────────────────────────────────────────────────────────────┐
│                          Document Layer                                   │
│                                                                           │
│  PdfPage:                            PdfDocument:                        │
│  ├─ MediaBox                         ├─ Pages (collection)               │
│  ├─ Contents                         ├─ IrefTable                        │
│  │  └─ PdfContents (array)          ├─ Trailer                          │
│  │      └─ PdfContent refs          ├─ FontTable                        │
│  ├─ RenderContent (during render)    ├─ ImageTable                      │
│  └─ Resources (fonts, images)        └─ Options                         │
│                                                                           │
│  PdfContents:                        IrefTable:                          │
│  └─ Array of PdfContent references   ├─ Dictionary<ObjectID, Reference> │
│                                       ├─ MaxObjectNumber                  │
│                                       └─ AllReferences (sorted array)    │
└────────────────────────────────────────────────────────────────────────────┘
                                    ↓
┌────────────────────────────────────────────────────────────────────────────┐
│                        Serialization Layer                                │
│                                                                           │
│  PdfObject (abstract base):                                              │
│  └─ WriteObject(PdfWriter writer)   ← Each object serializes itself     │
│                                                                           │
│  All PDF objects override WriteObject:                                   │
│  ├─ PdfDictionary       ─→ << /Key value >>                             │
│  ├─ PdfArray            ─→ [ element1 element2 ]                        │
│  ├─ PdfContent          ─→ stream dict + stream bytes                   │
│  ├─ PdfPage             ─→ page dictionary                              │
│  └─ Primitives          ─→ 42, "string", /Name, true                    │
└────────────────────────────────────────────────────────────────────────────┘
                                    ↓
┌────────────────────────────────────────────────────────────────────────────┐
│                         Output Layer                                      │
│                                                                           │
│  PdfWriter:                         Output Stream:                       │
│  ├─ Stream                          ├─ FileStream                        │
│  ├─ Encoder                         ├─ MemoryStream                      │
│  └─ Position tracking               └─ Network stream                    │
│                                                                           │
│  PDF file format:                                                        │
│  %PDF-1.7\n                                                              │
│  1 0 obj ... endobj                                                      │
│  2 0 obj ... endobj                                                      │
│  ... (all objects)                                                       │
│  xref                                                                     │
│  0 N                                                                      │
│  0000000000 65535 f                                                      │
│  0000000009 00000 n                                                      │
│  ... (positions)                                                         │
│  trailer << /Size N /Root 1 0 R >>                                       │
│  startxref\n<xref_position>\n%%EOF                                       │
└────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Memory Layout During Page Rendering

```
┌─────────────────────────────────────────────────────────────────────┐
│                     RAM Memory Layout                              │
│                                                                    │
│  At Page Start:                                                   │
│  ┌──────────────────────────────────┐                             │
│  │ XGraphics instance               │ ~1 KB                       │
│  │ ├─ Page reference                │                             │
│  │ ├─ Graphics state stack          │ ~10 KB                      │
│  │ └─ Transform matrices            │                             │
│  └──────────────────────────────────┘                             │
│                  ↓                                                 │
│  ┌──────────────────────────────────┐                             │
│  │ XGraphicsPdfRenderer             │ ~1 KB initial               │
│  │ ├─ PdfPage reference             │                             │
│  │ ├─ PdfGraphicsState              │ ~20 KB                      │
│  │ └─ _content StringBuilder         │ STARTS AT ~16 KB            │
│  └──────────────────────────────────┘                             │
│         Growth During Rendering:                                 │
│         drawString() ─→ +20 bytes                                 │
│         drawLine()   ─→ +30 bytes                                 │
│         drawBezier() ─→ +40 bytes                                 │
│         ...                                                       │
│         Final: ~100 KB - 500 KB (typical page)                   │
│                                                                    │
│  At Close:                                                        │
│  ┌──────────────────────────────────┐                             │
│  │ PdfContent                       │ ~1 KB metadata              │
│  │ ├─ Stream (PdfStream)            │                             │
│  │ │  └─ byte[] _value              │ ~100 KB - 500 KB            │
│  │ └─ Elements (Dictionary)         │ ~1 KB                       │
│  └──────────────────────────────────┘                             │
│                                                                    │
│  In IrefTable:                                                    │
│  ┌──────────────────────────────────┐                             │
│  │ PdfReference (for PdfContent)    │ ~100 bytes                  │
│  │ └─ Value → PdfContent object     │ points above                │
│  └──────────────────────────────────┘                             │
│                                                                    │
│  Total per page: 100 KB - 500 KB                                 │
│  For 100 pages:  10 MB - 50 MB                                   │
│  For 1000 pages: 100 MB - 500 MB                                 │
│                                                                    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. Object Registration & Numbering Flow

```
┌──────────────────────────────────────────────────────────────────┐
│                    Object Lifecycle                              │
│                                                                  │
│  1. Creation Phase:                                             │
│     ┌─────────────────────────────────────────┐                │
│     │ new PdfPage()                           │                │
│     │ └─ Document = doc                       │                │
│     │ └─ createIndirect = true                │                │
│     └─────────────────────────────────────────┘                │
│                 ↓                                               │
│     ┌─────────────────────────────────────────┐                │
│     │ doc.IrefTable.Add(pdfPage)              │                │
│     │ ├─ pdfPage.SetObjectID(n, 0)           │ n = 1, 2, 3... │
│     │ ├─ _objectTable[ObjectID] = reference  │                │
│     │ └─ MaxObjectNumber = n                  │                │
│     └─────────────────────────────────────────┘                │
│                 ↓                                               │
│     ┌─────────────────────────────────────────┐                │
│     │ Object lives in:                        │                │
│     │ - _objectTable (dictionary)             │                │
│     │ - IrefTable (for iteration)             │                │
│     │ - Potentially referenced by other obj   │                │
│     └─────────────────────────────────────────┘                │
│                                                                  │
│  2. Save Phase:                                                 │
│     ┌─────────────────────────────────────────┐                │
│     │ doc.Save()                              │                │
│     │ └─ PrepareForSave()                     │                │
│     │    ├─ IrefTable.Compact()               │ Remove dead obj │
│     │    └─ IrefTable.Renumber()              │ 1,2,3,... again │
│     └─────────────────────────────────────────┘                │
│                 ↓                                               │
│     ┌─────────────────────────────────────────┐                │
│     │ FOR EACH PdfReference IN AllReferences: │                │
│     │ ├─ iref.Position = writer.Position      │ Record byte # │
│     │ ├─ obj = iref.Value                     │                │
│     │ └─ obj.WriteObject(writer)              │ Serialize it  │
│     └─────────────────────────────────────────┘                │
│                 ↓                                               │
│     ┌─────────────────────────────────────────┐                │
│     │ IrefTable.WriteObject(writer)           │                │
│     │ ├─ Write "xref" header                  │                │
│     │ ├─ FOR EACH iref:                       │                │
│     │ │  └─ Write position: "0000000042 n"    │ Pre-computed! │
│     │ └─ Write trailer with size              │                │
│     └─────────────────────────────────────────┘                │
│                                                                  │
│  OUTPUT: Complete PDF file                                      │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

---

## 4. Content Stream Generation Pipeline

```
                  XGraphics Drawing Commands
                              ↓
              ┌───────────────────────────────┐
              │ DrawString(text, font, brush) │
              └───────────────────────────────┘
                              ↓
              ┌───────────────────────────────────────┐
              │ XGraphicsPdfRenderer.DrawString()     │
              │ 1. RealizeFont(font)                  │
              │    ├─ Get/create font object         │
              │    ├─ Add to page resources          │
              │    └─ Get font resource name (/F1)   │
              │ 2. RealizeBrush(brush)                │
              │    └─ Set fill color in PDF state    │
              └───────────────────────────────────────┘
                              ↓
              ┌───────────────────────────────────────┐
              │ AppendFormatArgs(format, args)        │
              │ 1. Encode text string to PDF hex      │
              │ 2. Append to _content StringBuilder    │
              └───────────────────────────────────────┘
                              ↓
              ┌───────────────────────────────────────┐
              │ _content accumulates:                 │
              │                                       │
              │ "BT\n"                                │
              │ "/F1 12 Tf\n"                         │
              │ "100 700 Td\n"                        │
              │ "(Hello) Tj\n"                        │
              │ "ET\n"                                │
              │ ...                                   │
              │                                       │
              │ StringBuilder size: 16 KB → N KB      │
              └───────────────────────────────────────┘
                              ↓
                (Many more drawing operations)
                              ↓
              ┌───────────────────────────────────────┐
              │ XGraphics.Dispose()                   │
              │ 1. XGraphicsPdfRenderer.Close()       │
              │ 2. contentString = GetContent()       │
              │    └─ _content.ToString() [O(page)]  │
              │ 3. bytes = RawEncoding.GetBytes(...)  │
              │ 4. RenderContent.CreateStream(bytes)  │
              └───────────────────────────────────────┘
                              ↓
              ┌───────────────────────────────────────┐
              │ PdfContent.CreateStream(byte[])       │
              │ Stream = new PdfStream(bytes, this)   │
              │ _value = bytes (stored)               │
              └───────────────────────────────────────┘
                              ↓
              ┌───────────────────────────────────────┐
              │ page.RenderContent = null             │
              │ Page rendering complete              │
              │ PdfContent ready for Save phase       │
              └───────────────────────────────────────┘
```

---

## 5. Save Serialization Order

```
doc.Save() workflow:

┌─────────────────────────────────────────────────────────┐
│ PrepareForSave():                                       │
│ 1. IrefTable.Compact()      - Remove unreachable objs  │
│ 2. IrefTable.Renumber()     - Reassign 1,2,3,...      │
└─────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────┐
│ Write File Header:                                      │
│ %PDF-1.7                                               │
└─────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────┐
│ Serialize All Objects (IrefTable.AllReferences):        │
│                                                         │
│ Object 1: Catalog                                       │
│   1 0 obj                                               │
│   << /Type /Catalog /Pages 2 0 R >>                    │
│   endobj                                                │
│   Byte offset: 9                                        │
│                                                         │
│ Object 2: Pages                                         │
│   2 0 obj                                               │
│   << /Type /Pages /Kids [3 0 R] /Count 1 >>            │
│   endobj                                                │
│   Byte offset: 74                                       │
│                                                         │
│ Object 3: Page                                          │
│   3 0 obj                                               │
│   << /Type /Page /Parent 2 0 R /Contents 4 0 R >>      │
│   endobj                                                │
│   Byte offset: 142                                      │
│                                                         │
│ Object 4: Content Stream                                │
│   4 0 obj                                               │
│   << /Length 256 /Filter /FlateDecode >>               │
│   stream                                                │
│   <compressed bytes>                                    │
│   endstream                                             │
│   endobj                                                │
│   Byte offset: 221                                      │
│                                                         │
│ Object 5: Font                                          │
│   5 0 obj                                               │
│   << /Type /Font /Subtype /Type1 ...>>                 │
│   endobj                                                │
│   Byte offset: 489                                      │
│                                                         │
│ ... (all other objects)                                │
│                                                         │
│ *** POSITIONS NOW KNOWN ***                            │
│                                                         │
└─────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────┐
│ Write Cross-Reference Table:                            │
│                                                         │
│ xref                                                    │
│ 0 6                                                     │
│ 0000000000 65535 f                                      │
│ 0000000009 00000 n   ← Object 1 at byte 9             │
│ 0000000074 00000 n   ← Object 2 at byte 74            │
│ 0000000142 00000 n   ← Object 3 at byte 142           │
│ 0000000221 00000 n   ← Object 4 at byte 221           │
│ 0000000489 00000 n   ← Object 5 at byte 489           │
│                                                         │
└─────────────────────────────────────────────────────────┘
            ↓
┌─────────────────────────────────────────────────────────┐
│ Write Trailer:                                          │
│                                                         │
│ trailer                                                 │
│ << /Size 6                                              │
│    /Root 1 0 R                                          │
│    /Info 5 0 R                                          │
│ >>                                                      │
│ startxref                                               │
│ 612                                  ← Byte offset of xref
│ %%EOF                                                   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

---

## 6. Compression Flow

```
Content Stream Compression:

┌────────────────────────────────────┐
│ XGraphicsPdfRenderer.Close()        │
│ contentString = "BT /F1 12 Tf ..."  │ Uncompressed
│ (size: 256 bytes)                   │
└────────────────────────────────────┘
            ↓
┌────────────────────────────────────┐
│ PdfContent.CreateStream(bytes)      │
│ Stream.Value = bytes                │ Still uncompressed
│ (stored in memory)                  │
└────────────────────────────────────┘
            ↓
┌────────────────────────────────────┐
│ Document.Save()                     │
│ ... writes all objects ...          │
└────────────────────────────────────┘
            ↓
┌────────────────────────────────────┐
│ PdfContent.WriteObject(writer)      │
│                                     │
│ IF CompressContentStreams:          │
│ ├─ Check stream length > 32 bytes   │
│ ├─ IF true:                         │
│ │  ├─ FlateDecode.Encode()          │
│ │  ├─ Stream.Value = compressed     │ ~60-80 bytes (4:1)
│ │  └─ Set /Filter /FlateDecode      │
│ └─ Set /Length = compressed size    │
│                                     │
│ Write to PDF:                        │
│ 4 0 obj                              │
│ << /Length 60 /Filter /FlateDecode >>
│ stream                               │
│ <60 bytes of compressed data>        │
│ endstream                            │
│ endobj                               │
│                                     │
└────────────────────────────────────┘
            ↓
┌────────────────────────────────────┐
│ Result:                              │
│ - Original: 256 bytes                │
│ - Compressed: 60 bytes               │
│ - Ratio: 77% reduction               │
│ - Memory savings: ~196 bytes per page│
│ - For 1000 pages: ~196 KB savings    │
│   (Not huge for DOM architecture)    │
└────────────────────────────────────┘
```

---

## 7. IrefTable Structure

```
PdfCrossReferenceTable Internal State:

┌─────────────────────────────────────────────────────────────┐
│ _objectTable: Dictionary<PdfObjectID, PdfReference>         │
│                                                              │
│ PdfObjectID → PdfReference mapping:                         │
│                                                              │
│ PdfObjectID(1, 0) → PdfReference                            │
│                    ├─ ObjectID: (1, 0)                      │
│                    ├─ Value: PdfCatalog instance            │
│                    ├─ Position: 9 (set during save)         │
│                    └─ GenerationNumber: 0                   │
│                                                              │
│ PdfObjectID(2, 0) → PdfReference                            │
│                    ├─ ObjectID: (2, 0)                      │
│                    ├─ Value: PdfPages instance              │
│                    ├─ Position: 74                          │
│                    └─ GenerationNumber: 0                   │
│                                                              │
│ PdfObjectID(3, 0) → PdfReference                            │
│                    ├─ ObjectID: (3, 0)                      │
│                    ├─ Value: PdfPage instance               │
│                    ├─ Position: 142                         │
│                    └─ GenerationNumber: 0                   │
│                                                              │
│ PdfObjectID(4, 0) → PdfReference                            │
│                    ├─ ObjectID: (4, 0)                      │
│                    ├─ Value: PdfContent instance            │
│                    ├─ Position: 221                         │
│                    └─ GenerationNumber: 0                   │
│                                                              │
│ PdfObjectID(5, 0) → PdfReference                            │
│                    ├─ ObjectID: (5, 0)                      │
│                    ├─ Value: PdfFont instance               │
│                    ├─ Position: 489                         │
│                    └─ GenerationNumber: 0                   │
│                                                              │
│ ... (all other objects)                                     │
│                                                              │
└─────────────────────────────────────────────────────────────┘

AllReferences property returns sorted array:

[PdfReference(1,0), PdfReference(2,0), PdfReference(3,0), ...]

Sorted by:
 - Primary: ObjectNumber (1, 2, 3, ...)
 - Secondary: GenerationNumber (usually all 0)

Used for:
 - Iterating all objects during serialization
 - Writing xref table in sequential order
 - Compacting (removing unreachable objects)
```

---

## 8. Bottleneck Points - Visual Summary

```
┌──────────────────────────────────────────────────────────────────┐
│              MEMORY ACCUMULATION BOTTLENECKS                     │
│                                                                  │
│  During Page Rendering:                                         │
│  ┌────────────────────────────────────────────┐                │
│  │ StringBuilder _content                     │                │
│  │ Growing: 16 KB → 100 KB → 500 KB           │ Can't release  │
│  │ until Dispose()                            │ until complete │
│  └────────────────────────────────────────────┘                │
│                                                                  │
│  During Save:                                                   │
│  ┌────────────────────────────────────────────┐                │
│  │ IrefTable._objectTable                     │                │
│  │ Contains: ALL objects in document          │ All in RAM     │
│  │ Cannot be freed until Save() completes     │ until Save     │
│  └────────────────────────────────────────────┘                │
│                                                                  │
│  Compression Stage:                                             │
│  ┌────────────────────────────────────────────┐                │
│  │ PdfContent.Stream.Value                    │                │
│  │ Holds uncompressed bytes                   │ Late compress  │
│  │ Compressed in-place during WriteObject()   │ during save    │
│  └────────────────────────────────────────────┘                │
│                                                                  │
│  Xref Table:                                                    │
│  ┌────────────────────────────────────────────┐                │
│  │ Cannot write positions until all objects   │ Must serialize │
│  │ serialized - single-pass limitation        │ sequentially   │
│  └────────────────────────────────────────────┘                │
│                                                                  │
│  Numbering:                                                     │
│  ┌────────────────────────────────────────────┐                │
│  │ Must renumber all objects during save      │ Extra pass     │
│  │ Cannot use streaming with fixed numbering  │                │
│  └────────────────────────────────────────────┘                │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘

Key Statistics:

Object Count vs Memory:
┌──────────────┬──────────────────┬────────────────┐
│ Page Count   │ Memory per Page   │ Total Memory   │
├──────────────┼──────────────────┼────────────────┤
│ 10           │ 100-500 KB       │ 1-5 MB         │
│ 100          │ 100-500 KB       │ 10-50 MB       │
│ 1,000        │ 100-500 KB       │ 100-500 MB     │
│ 10,000       │ 100-500 KB       │ 1-5 GB (limit!)│
└──────────────┴──────────────────┴────────────────┘

Breaking point: 10,000+ pages or very large images
```

---

## Reference Map - Quick Navigation

```
Core Rendering Path:
  XGraphics.DrawString()
    ↓ delegates to
  XGraphicsPdfRenderer.DrawString()
    ↓ uses
  AppendFormatArgs() → _content.Append()
    ↓ creates
  StringBuilder with PDF operators

Content Storage:
  PdfContent ← PdfPage.RenderContent
    ↓ contains
  PdfStream (byte[])
    ↓ referenced in
  PdfContents (array)
    ↓ property of
  PdfPage

Serialization:
  PdfDocument.Save()
    ↓ calls
  DoSaveAsync()
    ↓ uses
  IrefTable.AllReferences (all objects)
    ↓ iterates and calls
  obj.WriteObject(writer)
    ↓ writes to
  Output stream

Object Registry:
  PdfCrossReferenceTable
    ├─ Maintains _objectTable
    ├─ Assigns ObjectIDs
    ├─ Tracks positions
    └─ Serializes xref section
```

This completes the visual architecture documentation.
