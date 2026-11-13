module Tbt.Cli.Program

open Tbt.Cli.Common

let private usage =
    "usage:\n"
    + "  tbtun client --server HOST:PORT --listen ADDR:PORT --psk-file FILE [--paths 4] [--mss 1448] [--window 1024] [--window-clamp 600000]\n"
    + "  tbtun server --listen ADDR:PORT --target HOST:PORT --psk-file FILE [--max-paths 64] [--mss 1448] [--window 1024] [--window-clamp 600000] [--stats-s 0]"

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
        match argv.[0] with
        | "client" -> Client.run o
        | "server" -> Server.run o
        | _ ->
            System.Console.Error.WriteLine usage
            2
