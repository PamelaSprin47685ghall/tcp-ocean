/// Thread-safe gateways over the real codec and the real source API.
///
/// The decoder and the session source path are single-owner objects: several
/// caller threads may use these gateways, but every call still runs under one
/// lock, so the decoder row list and the encoder fill slot never race. No work
/// is invented here; the gateways only serialise the calls the session would
/// make itself.
namespace Tbt.Async

open System
open System.Threading
open Tbt.Codec
open Tbt.Contracts
open Tbt.Session

/// Serialises calls into one Decoder (spec 5.9).
[<Sealed>]
type AsyncDecoder(decoder: Decoder) =
    let gate = obj()

    member _.SymbolSize = decoder.SymbolSize

    member _.Frontier =
        Monitor.Enter gate
        try decoder.Frontier
        finally Monitor.Exit gate

    member _.BaseStep =
        Monitor.Enter gate
        try decoder.BaseStep
        finally Monitor.Exit gate

    member _.PendingRows =
        Monitor.Enter gate
        try decoder.PendingRows
        finally Monitor.Exit gate

    member _.KConflicts =
        Monitor.Enter gate
        try decoder.KConflicts
        finally Monitor.Exit gate

    /// A DATA cell: step t, slot, k of the step, symbol.
    member _.OfferSource(t: uint64, slot: int, k: int, symbol: ReadOnlySpan<byte>) =
        Monitor.Enter gate
        try decoder.OfferSource(t, slot, k, symbol)
        finally Monitor.Exit gate

    /// Record the peer's step_used k. False on a conflicting k.
    member _.NoteStepUsed(t: uint64, k: int) =
        Monitor.Enter gate
        try decoder.NoteStepUsed(t, k)
        finally Monitor.Exit gate

    /// A PARITY cell. The caller records k of t and its history steps first.
    member _.OfferParity(t: uint64, p: int, field: CodeField, urgent: bool, k: int, payload: ReadOnlySpan<byte>) =
        Monitor.Enter gate
        try decoder.OfferParity(t, p, field, urgent, k, payload)
        finally Monitor.Exit gate

    /// A BOOSTER cell (tLo, span, seq).
    member _.OfferBooster(tLo: uint64, span: int, seq: int, payload: ReadOnlySpan<byte>) =
        Monitor.Enter gate
        try decoder.OfferBooster(tLo, span, seq, payload)
        finally Monitor.Exit gate

    /// Take the next newly known source. Copies the byref results out of the
    /// lock so no byref is captured by a closure.
    member _.TryTakeKnown(t: byref<uint64>, slot: byref<int>, recovered: byref<bool>) =
        Monitor.Enter gate
        try decoder.TryTakeKnown(&t, &slot, &recovered)
        finally Monitor.Exit gate

    member _.StepUsed(t: uint64) =
        Monitor.Enter gate
        try decoder.StepUsed t
        finally Monitor.Exit gate

    member _.Release(below: uint64) =
        Monitor.Enter gate
        try decoder.Release below
        finally Monitor.Exit gate

/// Serialises the session's source API (TryBeginSourceFor / CommitSource / Flush).
/// In async mode the session loop and any helper thread must both come through
/// here, otherwise the encoder fill slot can be committed twice.
[<Sealed>]
type AsyncEncoder(session: Session) =
    let gate = obj()

    /// Write one source of slot from data, then commit it. Returns false when the
    /// window or the peer grant is full; data beyond the symbol is truncated.
    member _.WriteSource(slot: int, data: ReadOnlySpan<byte>, fin: bool) =
        Monitor.Enter gate
        try
            let mutable span = Span<byte>.Empty
            let ok = session.TryBeginSourceFor(slot, &span)
            if ok then
                let written = min data.Length span.Length
                if written > 0 then data.Slice(0, written).CopyTo span
                session.CommitSource(slot, written, fin)
            ok
        finally Monitor.Exit gate

    member _.CommitSource(slot: int, length: int, fin: bool) =
        Monitor.Enter gate
        try session.CommitSource(slot, length, fin)
        finally Monitor.Exit gate

    /// Seal a partly filled step (0-wait flush, spec 16.3).
    member _.Flush() =
        Monitor.Enter gate
        try session.Flush()
        finally Monitor.Exit gate

/// Real gateways, named for the async coordinator.
module AsyncCodec =
    let wrapDecoder (decoder: Decoder) = new AsyncDecoder(decoder)
    let wrapEncoder (session: Session) = new AsyncEncoder(session)
