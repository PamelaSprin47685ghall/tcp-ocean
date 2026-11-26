namespace Tbt.Session

open System
open Tbt.Wire

/// Holds one rendered parity across transport submission failures. The rendered
/// plaintext and metadata remain owned here until Commit or Cancel; the caller
/// must seal each send attempt with a fresh path counter.
[<Sealed>]
type WindowParityDispatch(store: WindowTxStore, symbolSize: int) =
    do if isNull (box store) then nullArg (nameof store)
    do if symbolSize <= 0 then invalidArg (nameof symbolSize) "must be positive"
    let symbol = Array.zeroCreate<byte> symbolSize
    let mutable held = false
    let mutable meta = Unchecked.defaultof<Meta>

    member _.HasCandidate = held

    /// Only one uncommitted candidate may exist. Rendering consumes its unique
    /// parity index in the store, so a failed submission must retry this buffer.
    member _.TryBegin(windowId: uint64, parityIndex: int) =
        if held then false
        else
            match store.TryRenderParity(windowId, parityIndex, Span symbol) with
            | ValueNone -> false
            | ValueSome m ->
                meta <- m
                held <- true
                true

    member _.TryCopy(target: Span<byte>, result: byref<Meta>) =
        if not held || target.Length <> symbol.Length then false
        else
            ReadOnlySpan<byte>(symbol).CopyTo target
            result <- meta
            true

    /// Call only after successful submission or when an authenticated ACK has
    /// made the candidate unnecessary. Cancelling never reuses its parity index.
    member _.Finish() =
        if not held then false
        else
            held <- false
            symbol.AsSpan().Clear()
            meta <- Unchecked.defaultof<Meta>
            true
