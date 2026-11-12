/// Fixed byte layout of one data frame (spec 6.1.1). All modules read offsets from here.
module Tbt.Contracts.Layout

/// Chunk header: three hex digits and CRLF, e.g. "580\r\n".
[<Literal>]
let ChunkHeaderLength = 5

[<Literal>]
let CellSeqOffset = 5

[<Literal>]
let CellSeqLength = 8

/// Start of the AEAD plaintext (meta followed by symbol).
[<Literal>]
let MetaOffset = 13

[<Literal>]
let MetaLength = 40

[<Literal>]
let SymbolOffset = 53

[<Literal>]
let TagLength = 16

[<Literal>]
let TrailerLength = 2

/// Frame overhead O = 5 + 8 + 40 + 16 + 2 (spec 3.1).
[<Literal>]
let FrameOverhead = 71

/// Source envelope at the start of every source symbol (spec 5.3).
[<Literal>]
let EnvelopeLength = 16

/// Bootstrap frame length: "438\r\n" + 1080 + "\r\n" (spec 6.5.2).
[<Literal>]
let BootFrameLength = 1087

[<Literal>]
let BootBodyLength = 1080

/// Smallest and largest negotiated symbol length (spec 6.1.2).
[<Literal>]
let MinSymbol = 1024

[<Literal>]
let MaxSymbol = 1344

/// Frame length for symbol length s: L = S + 71.
let inline frameLength (s: int) = s + FrameOverhead

/// Offset of the AEAD tag for symbol length s.
let inline tagOffset (s: int) = SymbolOffset + s

/// True when s is a legal wire symbol length: 1024..1344 in steps of 64.
let isLegalSymbol (s: int) = s >= MinSymbol && s <= MaxSymbol && s % 64 = 0
