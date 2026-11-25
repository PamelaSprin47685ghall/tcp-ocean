/// Process-level parts shared by client and server: clock, entropy, options, logging.
module Tbt.Cli.Common

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Security.Cryptography
open Tbt.Contracts
open Tbt.Crypto.Bootstrap

[<Sealed>]
type SystemClock() =
    static let factor = 1_000_000_000.0 / float Stopwatch.Frequency
    interface IClock with
        member _.NowNs() = int64 (float (Stopwatch.GetTimestamp()) * factor)
        member this.FreshNs() = (this :> IClock).NowNs()

[<Sealed>]
type SystemEntropy() =
    interface IEntropy with
        member _.Fill(d) = RandomNumberGenerator.Fill d

let unixMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let log (text: string) =
    Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text)

/// A rejected invocation: the message is written for the operator, not for a stack trace.
/// Raised while options are being resolved, before any socket exists, so `Program.main` can
/// turn it into one line of stderr and exit 2 instead of an unhandled-exception dump.
exception UsageError of message: string

let failUsage (message: string) : 'T = raise (UsageError message)

/// Options of the form --name value.
[<Sealed>]
type Options(args: string[]) =
    let values = Dictionary<string, string>()
    do
        let mutable i = 0
        while i < args.Length do
            let a = args.[i]
            if a = "-h" || a = "--help" then
                values.["help"] <- "1"
                i <- i + 1
            elif not (a.StartsWith "--") || a.Length < 3 then failUsage ("bad option: " + a)
            else
                let name = a.Substring 2
                // A bare flag is allowed when it is last, or the next token is another --option.
                if i + 1 < args.Length && not (args.[i + 1].StartsWith "--") then
                    values.[name] <- args.[i + 1]
                    i <- i + 2
                else
                    values.[name] <- "1"
                    i <- i + 1
    member _.Text(name: string) =
        match values.TryGetValue name with
        | true, v -> v
        | _ -> failUsage ("missing option --" + name)
    member _.TextOr(name: string, fallback: string) =
        match values.TryGetValue name with
        | true, v -> v
        | _ -> fallback
    member _.Int(name: string, fallback: int) =
        match values.TryGetValue name with
        | true, v ->
            // A typo'd number is the operator's mistake, not a bug: report it as one.
            match Int32.TryParse v with
            | true, n -> n
            | _ -> failUsage ("option --" + name + " needs an integer, got: " + v)
        | _ -> fallback
    member _.Float(name: string, fallback: float) =
        match values.TryGetValue name with
        | true, v ->
            match Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, n -> n
            | _ -> failUsage ("option --" + name + " needs a number, got: " + v)
        | _ -> fallback
    member _.Has(name: string) = values.ContainsKey name

/// CLI offer flags. Default (no --suites / --x25519): suite 0x02 only, no X25519.
/// --suites is a comma-separated list (aegis, chacha8, chacha20). --x25519 is a
/// bare flag, or --x25519 0 to turn it off explicitly.
/// Per-flow receive credit (what this end lets the peer have in flight per flow).
/// --flow-window-mib caps one flow's window; --credit-pool-mib is the session's
/// receive budget, which includes C0 for all 256 slots (16 MiB). Defaults are the
/// spec values (8 / 32 MiB). The window only binds when in-order delivery lags
/// receipt by window / rate: 8 MiB at 60 Mbit/s is ~1.1 s, which the lossy CN->JP
/// uplink (kernel retransmits on single paths) reaches.
let creditFromOptions (o: Options) =
    let d = Tbt.Session.CreditPolicy.defaults
    let win = max 1 (o.Int("flow-window-mib", d.FlowWindowMax >>> 20)) <<< 20
    let pool = max ((256 * Tbt.Session.CreditPolicy.C0) + win) (o.Int("credit-pool-mib", d.PoolBytes >>> 20) <<< 20)
    { d with FlowWindowMax = win; PoolBytes = pool }

let offerFromOptions (o: Options) =
    let struct (aegis, chacha8, chacha20) =
        if o.Has "suites" then Suite.parseOfferList (o.TextOr("suites", ""))
        else struct (false, false, true)
    let x25519 =
        if not (o.Has "x25519") then false
        else
            match o.TextOr("x25519", "1").Trim().ToLowerInvariant() with
            | "0" | "false" | "no" | "off" -> false
            | _ -> true
    struct (aegis, chacha8, chacha20, x25519)

let describeOffer (suite: byte) (caps: uint16) =
    let dh = if (caps &&& Caps.X25519) <> 0us then "1" else "0"
    "suite=0x" + suite.ToString("x2") + "(" + Suite.name suite + ") x25519=" + dh

/// host:port; a host name resolves to its first address.
let endpoint (text: string) : IPEndPoint =
    match IPEndPoint.TryParse text with
    | true, ep when not (isNull ep) -> nonNull ep
    | _ ->
        let at = text.LastIndexOf ':'
        if at <= 0 then failUsage ("bad endpoint: " + text)
        let addresses = Dns.GetHostAddresses(text.Substring(0, at))
        IPEndPoint(addresses.[0], Int32.Parse(text.Substring(at + 1)))

/// PSK file: one key per line, 64 hex digits; '#' starts a comment.
/// Every failure here is the operator's typing (path, permissions, digits), so each one is a
/// `UsageError` naming the file — never the key material, and never a raw exception.
let readPsks (path: string) =
    let lines =
        try File.ReadAllLines path
        with
        | :? UnauthorizedAccessException -> failUsage ("psk-file is not readable: " + path)
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> failUsage ("psk-file not found: " + path)
        | :? IOException as e -> failUsage ("psk-file cannot be read: " + path + ": " + e.Message)
    let keys =
        lines
        |> Array.map (fun l -> (match l.IndexOf '#' with -1 -> l | i -> l.Substring(0, i)).Trim())
        |> Array.filter (fun l -> l.Length > 0)
        |> Array.map (fun l ->
            let k =
                try Convert.FromHexString l
                with :? FormatException -> failUsage ("a PSK has 32 bytes (64 hex digits): " + path)
            if k.Length <> 32 then failUsage ("a PSK has 32 bytes (64 hex digits): " + path)
            k)
    if keys.Length = 0 then failUsage ("psk-file has no keys: " + path)
    keys

/// Resident set size in KiB. Linux uses /proc/self/status; elsewhere WorkingSet64. -1 if unknown.
let rssKiB () =
    try
        File.ReadAllLines "/proc/self/status"
        |> Array.tryFind (fun l -> l.StartsWith "VmRSS:")
        |> Option.map (fun l -> Int64.Parse(l.Substring(6).Trim().Split(' ').[0]))
        |> Option.defaultValue -1L
    with :? IOException ->
        try Process.GetCurrentProcess().WorkingSet64 / 1024L
        with _ -> -1L

/// Register SIGINT/SIGTERM on POSIX, or Console.CancelKeyPress on Windows.
/// The callback must be safe to run from a signal / console handler.
let onInterrupt (action: unit -> unit) : IDisposable =
    let mutable posix = true
    try
        let handle (c: Runtime.InteropServices.PosixSignalContext) =
            c.Cancel <- true
            action ()
        let a = Runtime.InteropServices.PosixSignalRegistration.Create(Runtime.InteropServices.PosixSignal.SIGINT, handle)
        let b = Runtime.InteropServices.PosixSignalRegistration.Create(Runtime.InteropServices.PosixSignal.SIGTERM, handle)
        { new IDisposable with
            member _.Dispose() = a.Dispose(); b.Dispose() }
    with :? PlatformNotSupportedException ->
        posix <- false
        let handler = ConsoleCancelEventHandler(fun _ ev -> ev.Cancel <- true; action ())
        Console.CancelKeyPress.AddHandler handler
        { new IDisposable with
            member _.Dispose() = Console.CancelKeyPress.RemoveHandler handler }

/// Accept from a non-blocking listener; null when nothing waits.
let tryAccept (listener: System.Net.Sockets.Socket) : System.Net.Sockets.Socket | null =
    try listener.Accept() with :? System.Net.Sockets.SocketException -> null
