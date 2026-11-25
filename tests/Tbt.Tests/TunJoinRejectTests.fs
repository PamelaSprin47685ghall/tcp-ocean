module Tbt.Tests.TunJoinRejectTests

// What a TUN *path* does when a reliable payload is written and the path is then aborted.
//
// Scope, because the name of a test is a contract: these two tests drive `TunPath.SendReliable`
// and `TunPath.Abort` directly. They do NOT call `Server.decideJoin`, do not build a JOIN frame,
// and do not run a socket. What `decideJoin` does with this primitive is covered separately, at
// the glue level, by `ServerJoinRealTests` (a refusal replies the MAC'd ack and closes the path);
// the protocol rule itself by `JoinTests`. End-to-end delivery of a JOIN reject across a real TUN
// pair is covered nowhere yet.
//
// What is asserted is only the sender's side of spec §6.5.7 rule 6 — the reply is written in full
// and the connection is then closed — plus Abort's own terminal contract. Nothing here asserts how
// many times a segment may be carried: a payload written once and dropped is a property of the
// current `Close = closing <- true; Abort()` (`Abort` emits RST and sets `state <- Closed`, and
// the retransmit/repair paths require `state = Established`), so "never resent" is an observation
// about today's code, not a rule. Asserting it would veto a correct flush-then-close fix; the gap
// itself is recorded as [OPEN] in the design record (§5.8).
//
// Kernel carrier is not covered here: it uses `kernelReply` (`Queue`/`Send`) and whether queued
// bytes survive a close is the socket's behaviour, which this file neither tests nor claims.

open System
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

[<Fact>]
let ``SendReliable then Abort writes the whole payload before the RST`` () =
    // The payload is a stand-in for the MAC'd JOIN_ACK a refusal writes; nothing here parses it.
    //
    // These assertions are what spec §6.5.7 rule 6 requires *of the emitting end*: the reply is
    // written, in full, and the connection is then closed. They must keep holding under any
    // delivery fix (a flush-then-close would still write the payload before the RST), so they do
    // not veto one.
    let f = Fixture 2
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let before = f.Out.Packets.Count
    let ack = Array.init 64 (fun i -> byte (i + 1))
    f.Path.SendReliable(ReadOnlySpan ack)
    f.Path.Abort()
    let emitted = [ for i in before .. f.Out.Packets.Count - 1 -> parse f.Out.Packets.[i] ]
    // 1. the payload left as one segment, carrying every byte handed to SendReliable
    let ackSegs = emitted |> List.filter (fun q -> q.PayloadLength > 0)
    Assert.Equal(1, ackSegs.Length)
    Assert.Equal(ack.Length, (List.head ackSegs).PayloadLength)
    let ackSeq = (List.head ackSegs).Seq
    // 2. the RST follows it, with a non-decreasing sequence number
    let rsts = emitted |> List.filter (fun q -> q.Flags &&& Packet.Rst <> 0uy)
    Assert.Equal(1, rsts.Length)
    Assert.True((List.head rsts).Seq >= ackSeq)
    Assert.True(f.Path.Closed)

[<Fact>]
let ``an aborted TUN path emits no further segment at all`` () =
    // Abort's own contract, and only that: once it returns, the path emits nothing more. This is
    // about `Abort` itself, not about a graceful close — an implementation that flushes before
    // resetting would not call `Abort` directly, so this assertion does not veto such a fix.
    let f = Fixture 2
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let mutable frame = Span<byte>.Empty
    for _ in 1 .. 3 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        if ip.TryBeginFrame(&frame) then
            frame.Fill 0x55uy
            ip.CommitFrame()
    f.Path.Abort()
    let n = f.Out.Packets.Count
    let mutable more = false
    for _ in 1 .. 3000 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        f.Path.Service f.Clock.Now
        if f.Out.Packets.Count > n then more <- true
    Assert.False(more, "an aborted TUN path must not emit anything else")
