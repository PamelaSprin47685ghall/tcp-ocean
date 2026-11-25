module Tbt.Cli.Program

open Tbt.Cli.Common

let private usage =
    "usage:\n"
    + "  tbtun client --server HOST:PORT --listen ADDR:PORT --psk-file FILE [--paths 4] [--mss 1448] [--window 1024] [--window-clamp 600000] [--uplink-pace 0] [--tuple-mbit 0] [--gate-pass2 262144] [--notsent-lowat 16384] [--tx-slots 16] [--io-threads min(4,cpus)] [--user-timeout-ms 30000] [--new-repair 0] [--suites chacha20] [--x25519]\n"
    + "  tbtun server --target HOST:PORT --psk-file FILE [--listen ADDR:PORT | --tun NAME --tun-listen ADDR:PORT] [--tun-txqueuelen 20000] [--tun-drain adaptive|full|main] [--tun-recv-budget 128] [--tun-recv-yield-us 2000] [--tun-sndbuf 2000000] [--max-paths 64] [--mss 1448] [--window 1024] [--window-clamp 600000] [--pace 0] [--gate-pass2 262144] [--notsent-lowat 16384] [--tx-slots 16] [--io-threads min(4,cpus)] [--user-timeout-ms 30000] [--new-repair 0] [--stats-s 0] [--suites chacha20] [--x25519]"

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        System.Console.Error.WriteLine usage
        2
    else
        let o = Options(argv.[1..])
        match System.Environment.GetEnvironmentVariable "TBT_TRACE" with
        | null | "" -> ()
        | prefix ->
            Tbt.Contracts.Trace.start (prefix + "." + argv.[0] + "." + string System.Environment.ProcessId)
            System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> Tbt.Contracts.Trace.flush ())
        try
            match argv.[0] with
            | "client" -> Client.run o
            | "server" -> Server.run o
            | _ ->
                System.Console.Error.WriteLine usage
                2
        with
        | UsageError message ->
            // A bad invocation is one line for the operator, not a stack trace. Every other
            // exception is a bug in tbtun and is left to the runtime: masking it as a tidy
            // one-liner would hide the stack the bug report needs.
            System.Console.Error.WriteLine("tbtun: " + message)
            2
