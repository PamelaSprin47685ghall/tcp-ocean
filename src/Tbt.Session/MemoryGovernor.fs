/// Memory governor and mem_tier (spec 9.6, caps bits 4–5): watermarks that only
/// restrict future input. In-flight custody is never dropped.
namespace Tbt.Session

open Tbt.Crypto.Bootstrap

/// caps bit4..5: 0 = 1024 MiB, 1 = 384 MiB. Other values are unknown and must be refused.
module MemTier =
    [<Literal>]
    let Ti1024 = 0us
    [<Literal>]
    let Ti384 = 1us

    let ofCaps (caps: uint16) = (caps &&& Caps.MemTierMask) >>> 4
    let isKnown (tier: uint16) = tier = Ti1024 || tier = Ti384
    let set (caps: uint16) (tier: uint16) =
        (caps &&& ~~~Caps.MemTierMask) ||| ((tier &&& 0x3us) <<< 4)
    let budgetBytes (tier: uint16) =
        if tier = Ti384 then 384L <<< 20 else 1024L <<< 20
    /// Bulk paths the server must cap at for this tier (lane extension is separate).
    let maxBulkPaths (tier: uint16) = if tier = Ti384 then 96 else 256
    let maxLanes (tier: uint16) = if tier = Ti384 then 16 else 32

/// Watermark band (spec 9.6). Each band is inclusive of its lower bound except RejectFlows,
/// which is strictly above 95%.
type GovernorLevel =
    | Normal = 0
    | StopIntake = 1
    | ShrinkWindow = 2
    | HoleCap = 3
    | RejectFlows = 4

/// Actions for the current band. All of them apply only to future input.
[<Struct>]
type GovernorState =
    { Level: GovernorLevel
      /// 70%+: stop new bulk intake (repair / control / close / interactive still run).
      StopIntake: bool
      /// 80%+: shrink newly opened windows; do not revoke a grant already given.
      ShrinkNewWindows: bool
      /// 80%+: the server should halve future downlink source rate (not a RateGroup retune).
      DownlinkHalf: bool
      /// 90%+: enable the after-hole byte cap (CAP-1). Off in the normal band.
      EnableHoleCap: bool
      /// >95%: refuse new flows; existing flows continue.
      RejectNewFlows: bool }

/// Single owner of each reported byte. A byte counted in two modules is a defect.
/// Credit promises, grant limits, and unsent kernel buffers are not physical occupancy.
module GovernorModules =
    [<Literal>]
    let EncoderRing = 1uy
    [<Literal>]
    let DecoderRing = 2uy
    [<Literal>]
    let DecoderRows = 3uy
    [<Literal>]
    let FlowRxBuffers = 4uy
    [<Literal>]
    let ExternalRxScratch = 5uy

[<Sealed>]
type MemoryGovernor(budgetBytes: int64) =
    let usage: int64[] = Array.zeroCreate 256
    let mutable total = 0L
    let mutable level = GovernorLevel.Normal
    let mutable pauseCount = 0L

    let refresh () =
        let prev = level
        let q = if budgetBytes <= 0L then 0.0 else float total / float budgetBytes
        level <-
            if q > 0.95 then GovernorLevel.RejectFlows
            elif q >= 0.90 then GovernorLevel.HoleCap
            elif q >= 0.80 then GovernorLevel.ShrinkWindow
            elif q >= 0.70 then GovernorLevel.StopIntake
            else GovernorLevel.Normal
        if level >= GovernorLevel.StopIntake && prev < GovernorLevel.StopIntake then
            pauseCount <- pauseCount + 1L

    member _.Budget = budgetBytes
    member _.Used = total
    member _.Level = level
    /// Times the governor entered a pressure band from Normal (spec M-GOV governor_pause).
    member _.PauseCount = pauseCount

    /// Replace module id's reported occupancy. Bytes already held stay in the total until
    /// the owner reports a lower value: the governor never discards custody itself.
    member _.Report(moduleId: byte, bytes: int64) =
        let i = int moduleId
        let n = max 0L bytes
        total <- total - usage.[i] + n
        if total < 0L then total <- 0L
        usage.[i] <- n
        refresh ()

    member this.ReportTotal(bytes: int64) = this.Report(0uy, bytes)

    /// Carrier-owned receive scratch. Session never owns these bytes; it only relays the count.
    member this.NoteExternalRxScratch(bytes: int64) = this.Report(GovernorModules.ExternalRxScratch, bytes)

    member _.Order =
        { Level = level
          StopIntake = level >= GovernorLevel.StopIntake
          ShrinkNewWindows = level >= GovernorLevel.ShrinkWindow
          DownlinkHalf = level >= GovernorLevel.ShrinkWindow
          EnableHoleCap = level >= GovernorLevel.HoleCap
          RejectNewFlows = level >= GovernorLevel.RejectFlows }
