/// Read the local sender's Linux TCP_INFO. These counters do not change TCP.
namespace Tbt.Carrier

open System
open System.Net.Sockets

/// Rates are bytes per second; times are microseconds. A negative value means
/// that the running kernel did not supply the field. RtoUs is the timer interval,
/// not the remaining time until its next expiry.
[<Struct>]
type KernelTcpInfo =
    { CwndBytes: int64
      SsthreshBytes: int64
      UnackedBytes: int64
      SendWindowBytes: int64
      NotSentBytes: int64
      RttUs: int64
      RtoUs: int64
      TotalRetrans: int64
      TotalRto: int64
      CaState: int
      AppLimited: int64
      BytesAcked: int64
      DeliveryRate: int64
      PacingRate: int64
      BusyUs: int64
      RwndLimitedUs: int64
      SendBufferLimitedUs: int64 }

module KernelTcpInfo =
    let private u32 (data: ReadOnlySpan<byte>) offset =
        if data.Length < offset + 4 then -1L
        else int64 (BitConverter.ToUInt32(data.Slice(offset, 4)))

    let private u64 (data: ReadOnlySpan<byte>) offset =
        if data.Length < offset + 8 then -1L
        else
            let value = BitConverter.ToUInt64(data.Slice(offset, 8))
            // Linux uses all bits set for an unlimited pacing rate.
            if value > uint64 Int64.MaxValue then -1L else int64 value

    /// The stable prefix and optional tails from include/uapi/linux/tcp.h.
    /// Check each field's end: old kernels return a shorter TCP_INFO record.
    let parse (data: ReadOnlySpan<byte>) =
        if data.Length < 104 then ValueNone
        else
            let mss = u32 data 16
            ValueSome
                { CwndBytes = u32 data 80 * mss
                  SsthreshBytes = u32 data 76 * mss
                  // This is unacked segments times MSS, not exact wire inflight.
                  UnackedBytes = u32 data 24 * mss
                  SendWindowBytes = u32 data 228
                  NotSentBytes = u32 data 144
                  RttUs = u32 data 68
                  RtoUs = u32 data 8
                  TotalRetrans = u32 data 100
                  TotalRto =
                    if data.Length < 242 then -1L
                    else int64 (BitConverter.ToUInt16(data.Slice(240, 2)))
                  CaState = int data.[1]
                  AppLimited = if data.Length < 168 then -1L else int64 (data.[7] &&& 1uy)
                  BytesAcked = u64 data 120
                  DeliveryRate = u64 data 160
                  PacingRate = u64 data 104
                  BusyUs = u64 data 168
                  RwndLimitedUs = u64 data 176
                  SendBufferLimitedUs = u64 data 184 }

    /// Called by diagnostics, never by the send gate. Reading once gives all
    /// fields from the same socket snapshot and replaces the old ACK-only read.
    let tryRead (socket: Socket) =
        if not Platform.current.Value.HasLinuxTcpInfo then ValueNone
        else
            try
                let buffer = Array.zeroCreate<byte> 256
                let count = socket.GetRawSocketOption(6, 11, Span buffer)
                parse (ReadOnlySpan(buffer, 0, count))
            with
            | :? SocketException -> ValueNone
            | :? ObjectDisposedException -> ValueNone

    let private number value = if value < 0L then "-" else string value

    /// Fixed order, documented in docs/cases/uplink-observation.md.
    let describePath (info: KernelTcpInfo) =
        [| info.CwndBytes; info.SsthreshBytes; info.UnackedBytes
           info.SendWindowBytes; info.NotSentBytes; info.RttUs; info.RtoUs
           info.TotalRetrans; info.TotalRto; int64 info.CaState
           info.AppLimited; info.DeliveryRate; info.PacingRate |]
        |> Array.map number
        |> String.concat "/"

/// One aggregate snapshot. Any missing member makes that sum unknown; a known
/// partial sum would hide an old kernel or a path that closed during the read.
[<Sealed>]
type KernelTcpTotals() =
    let values = Array.zeroCreate<int64> 13
    let mutable count = 0
    let add index value =
        if value < 0L || values.[index] < 0L || Int64.MaxValue - values.[index] < value then values.[index] <- -1L
        else values.[index] <- values.[index] + value

    member _.Count = count
    member _.BytesAcked = values.[0]

    member _.Add(info: KernelTcpInfo voption) =
        count <- count + 1
        match info with
        | ValueNone -> Array.Fill(values, -1L)
        | ValueSome info ->
            add 0 info.BytesAcked
            add 1 info.CwndBytes
            add 2 info.UnackedBytes
            add 3 info.NotSentBytes
            add 4 info.DeliveryRate
            add 5 info.PacingRate
            add 6 info.TotalRetrans
            add 7 info.TotalRto
            add 8 info.BusyUs
            add 9 info.RwndLimitedUs
            add 10 info.SendBufferLimitedUs
            add 11 info.SendWindowBytes
            add 12 info.AppLimited

    member _.Describe() =
        let number index = if values.[index] < 0L then "-" else string values.[index]
        let rate index =
            if values.[index] < 0L then "-"
            else (float values.[index] * 8.0 / 1_000_000.0).ToString("F3", Globalization.CultureInfo.InvariantCulture)
        " kernel_paths=" + string count
        + " kernel_cwnd_b=" + number 1 + " kernel_unacked_b=" + number 2
        + " kernel_notsent_b=" + number 3 + " kernel_delivery_mbit=" + rate 4
        + " kernel_pacing_mbit=" + rate 5 + " kernel_retrans=" + number 6
        + " kernel_rto=" + number 7 + " kernel_busy_us=" + number 8
        + " kernel_rwnd_us=" + number 9 + " kernel_sndbuf_us=" + number 10
        + " kernel_send_window_b=" + number 11 + " kernel_app_limited=" + number 12
