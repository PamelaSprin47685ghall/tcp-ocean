/// Process-level parts shared by client and server: clock, entropy, options, logging.
module Tbt.Cli.Common

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Security.Cryptography
open Tbt.Contracts

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

/// Options of the form --name value.
[<Sealed>]
type Options(args: string[]) =
    let values = Dictionary<string, string>()
    do
        let mutable i = 0
        while i < args.Length do
            let a = args.[i]
            if not (a.StartsWith "--") || i + 1 >= args.Length then failwith ("bad option: " + a)
            values.[a.Substring 2] <- args.[i + 1]
            i <- i + 2
    member _.Text(name: string) =
        match values.TryGetValue name with
        | true, v -> v
        | _ -> failwith ("missing option --" + name)
    member _.Int(name: string, fallback: int) =
        match values.TryGetValue name with
        | true, v -> Int32.Parse v
        | _ -> fallback
    member _.Float(name: string, fallback: float) =
        match values.TryGetValue name with
        | true, v -> Double.Parse(v, Globalization.CultureInfo.InvariantCulture)
        | _ -> fallback
    member _.Has(name: string) = values.ContainsKey name

/// host:port; a host name resolves to its first address.
let endpoint (text: string) : IPEndPoint =
    match IPEndPoint.TryParse text with
    | true, ep when not (isNull ep) -> nonNull ep
    | _ ->
        let at = text.LastIndexOf ':'
        if at <= 0 then failwith ("bad endpoint: " + text)
        let addresses = Dns.GetHostAddresses(text.Substring(0, at))
        IPEndPoint(addresses.[0], Int32.Parse(text.Substring(at + 1)))

/// PSK file: one key per line, 64 hex digits; '#' starts a comment.
let readPsks (path: string) =
    File.ReadAllLines path
    |> Array.map (fun l -> (match l.IndexOf '#' with -1 -> l | i -> l.Substring(0, i)).Trim())
    |> Array.filter (fun l -> l.Length > 0)
    |> Array.map (fun l ->
        let k = Convert.FromHexString l
        if k.Length <> 32 then failwith "a PSK has 32 bytes (64 hex digits)"
        k)

/// Resident set size in KiB (Linux), or -1.
let rssKiB () =
    try
        File.ReadAllLines "/proc/self/status"
        |> Array.tryFind (fun l -> l.StartsWith "VmRSS:")
        |> Option.map (fun l -> Int64.Parse(l.Substring(6).Trim().Split(' ').[0]))
        |> Option.defaultValue -1L
    with :? IOException -> -1L

/// Accept from a non-blocking listener; null when nothing waits.
let tryAccept (listener: System.Net.Sockets.Socket) : System.Net.Sockets.Socket | null =
    try listener.Accept() with :? System.Net.Sockets.SocketException -> null
