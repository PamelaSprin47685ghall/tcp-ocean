/// Read the local sender's Linux TCP_INFO. These counters do not change TCP.
namespace Tbt.Carrier

open System
open System.Net.Sockets

/// Rates are bytes per second; times are microseconds. A negative value means
/// that the running kernel did not supply the field. RtoUs is the timer interval,
/// not the remaining time until its next expiry.
///
/// The first block is the 200-byte prefix every kernel since 2.6 returns. The
/// second block is the diagnostic tail that Linux 6.1 added; a shorter record
/// leaves those fields negative, so a reader must treat "unknown" differently
/// from "zero".
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
      /// `tcpi_total_rto`: RTO expiries this socket has seen, SYN included.
      /// Linux 6.5 added it as `__u16` at byte 240, after `tcpi_snd_wnd`@228,
      /// `tcpi_rcv_wnd`@232 and `tcpi_rehash`@236. It is a real counter on the
      /// kernels this ships against; do not delete it as "undefined tail".
      TotalRto: int64
      CaState: int
      AppLimited: int64
      BytesAcked: int64
      DeliveryRate: int64
      PacingRate: int64
      BusyUs: int64
      RwndLimitedUs: int64
      SendBufferLimitedUs: int64
      /// Segments held in the SACK scoreboard, i.e. arrived but out of order.
      Sacked: int64
      /// Segments the sender has declared lost.
      Lost: int64
      /// Segments retransmitted in the current recovery episode.
      Retrans: int64
      /// Segments this socket has sent, the denominator of a retransmit ratio.
      SegsOut: int64
      /// The smallest RTT the kernel has measured. Comparing RttUs with this
      /// separates self-inflicted queueing from a genuine path change.
      MinRttUs: int64
      /// Delivery-rate samples, the BBR clock.
      Delivered: int64
      /// Data segments handed to the network, retransmissions excluded.
      DataSegsOut: int64
      BytesSent: int64
      BytesRetrans: int64
      /// Duplicate SACKs seen: the kernel had to infer a spurious retransmit.
      DsackDups: int64
      /// Reordering events seen: evidence for an adaptive RACK reordering window.
      ReordSeen: int64 }

/// Additive aliases kept for readers that name the segment counters by their
/// Linux spelling. Both spellings read the same snapshot field.
type KernelTcpInfo with
    member this.LostSegs = this.Lost
    member this.RetransSegs = this.Retrans

/// One reusable 280-byte read buffer per thread.
///
/// `struct tcp_info` is 248 bytes through `tcpi_total_rto_time` and 280 with the
/// kernel's tail padding, so a 256-byte buffer silently truncated the newest
/// fields. Allocating it per call cost 2048 arrays and 2048 `getsockopt` calls
/// per second on an idle 256-path server; a thread-local buffer makes the read
/// allocation-free without sharing a buffer across threads.
[<Sealed>]
type private TcpInfoScratch() =
    // Nullability is annotated because the repository enables `Nullable` with
    // warnings-as-errors: an uninitialized ThreadStatic field is null until the
    // first read on that thread.
    [<ThreadStatic; DefaultValue>]
    static val mutable private buffer: byte[] | null

    static member Get() : byte[] =
        match TcpInfoScratch.buffer with
        | null ->
            let fresh = Array.zeroCreate<byte> 280
            TcpInfoScratch.buffer <- fresh
            fresh
        | existing -> existing

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
                  // Byte 240, `__u16 tcpi_total_rto` (Linux 6.5+). A record that
                  // ends at 228 snd_wnd cannot carry it, so require 242 bytes.
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
                  SendBufferLimitedUs = u64 data 184
                  // Without these counters a path cannot be ranked: there is no
                  // min-RTT to separate self-queueing from a real path change, no
                  // per-path retransmit ratio, and no reordering evidence.
                  Sacked = u32 data 28
                  Lost = u32 data 32
                  Retrans = u32 data 36
                  SegsOut = u32 data 136
                  MinRttUs = u32 data 148
                  Delivered = u32 data 192
                  DataSegsOut = u32 data 156
                  BytesSent = u64 data 200
                  BytesRetrans = u64 data 208
                  DsackDups = u32 data 216
                  ReordSeen = u32 data 220 }

    /// Called by diagnostics and by the path health sampler, never by the send
    /// gate. Reading once gives all fields from the same socket snapshot.
    let tryRead (socket: Socket) =
        if not Platform.current.Value.HasLinuxTcpInfo then ValueNone
        else
            try
                let buffer = TcpInfoScratch.Get()
                let count = socket.GetRawSocketOption(6, 11, Span buffer)
                if count <= 0 then ValueNone
                else parse (ReadOnlySpan(buffer, 0, count))
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

/// A cached snapshot of one carrier socket, refreshed by the session thread at
/// most once per interval. This is the whole input of health-aware path
/// selection: no `getsockopt` on the frame path, no allocation, and no shared
/// state between the session thread and the IO thread that owns the socket.
///
/// An unsigned field reports 0 when the kernel did not supply it; `Valid` says
/// whether the sample is complete, so a ranking never mistakes "unknown" for
/// "perfectly healthy".
[<Struct>]
type PathHealth =
    { SampledNs: int64
      CaState: byte
      RttUs: uint32
      MinRttUs: uint32
      CwndBytes: uint32
      UnackedBytes: uint32
      Lost: uint32
      Retrans: uint32
      TotalRetrans: uint32
      TotalRto: uint16
      DeliveryRate: int64
      BytesSent: uint64
      BytesRetrans: uint64
      NotSentBytes: int
      /// Retransmitted segments divided by sent segments between two samples,
      /// clamped to [0, 1]. Zero until a second sample exists.
      RetransRate: float
      Valid: bool }

module PathHealth =
    let unknown =
        { SampledNs = 0L
          CaState = 0uy
          RttUs = 0u
          MinRttUs = 0u
          CwndBytes = 0u
          UnackedBytes = 0u
          Lost = 0u
          Retrans = 0u
          TotalRetrans = 0u
          TotalRto = 0us
          DeliveryRate = 0L
          BytesSent = 0UL
          BytesRetrans = 0UL
          NotSentBytes = 0
          RetransRate = 0.0
          Valid = false }

    let private u32 (value: int64) =
        if value <= 0L then 0u
        elif value > int64 UInt32.MaxValue then UInt32.MaxValue
        else uint32 value

    let private u64 (value: int64) =
        if value <= 0L then 0UL else uint64 value

    let ofInfo (sampledNs: int64) (info: KernelTcpInfo) (retransRate: float) =
        { SampledNs = sampledNs
          CaState = byte (max 0 (min 255 info.CaState))
          RttUs = u32 info.RttUs
          MinRttUs = u32 info.MinRttUs
          CwndBytes = u32 info.CwndBytes
          UnackedBytes = u32 info.UnackedBytes
          Lost = u32 info.Lost
          Retrans = u32 info.Retrans
          TotalRetrans = u32 info.TotalRetrans
          TotalRto = uint16 (max 0L (min 65535L info.TotalRto))
          DeliveryRate = max 0L info.DeliveryRate
          BytesSent = u64 info.BytesSent
          BytesRetrans = u64 info.BytesRetrans
          NotSentBytes = int (max 0L (min (int64 Int32.MaxValue) info.NotSentBytes))
          RetransRate = retransRate
          Valid = info.CwndBytes >= 0L && info.MinRttUs >= 0L && info.SegsOut >= 0L }
