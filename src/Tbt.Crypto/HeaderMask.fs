/// Header protection of cell_seq (spec 6.3). Removable decorator: without HP_MASK the
/// zero mask leaves cell_seq in clear and nothing else changes.
namespace Tbt.Crypto

open System
open Tbt.Contracts

type IHeaderMask =
    /// Write the 8-byte mask for a 16-byte sample.
    abstract Compute: sample: ReadOnlySpan<byte> * mask: Span<byte> -> unit

/// HP for the ChaCha20-Poly1305 suite: first 8 key stream bytes of
/// ChaCha20(K_hp, counter = LE32(sample[0..4]), nonce = sample[4..16]).
[<Sealed>]
type ChaChaHeaderMask(hpKey: ReadOnlySpan<byte>) =
    let words = ChaCha20.keyWords hpKey

    interface IHeaderMask with
        member _.Compute(sample, mask) =
            let counter = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian sample
            ChaCha20.block words counter (sample.Slice(4, 12)) (mask.Slice(0, Layout.CellSeqLength))

/// HP_MASK not negotiated: cell_seq goes in clear.
[<Sealed>]
type NoHeaderMask() =
    interface IHeaderMask with
        member _.Compute(_, mask) = mask.Slice(0, Layout.CellSeqLength).Clear()
