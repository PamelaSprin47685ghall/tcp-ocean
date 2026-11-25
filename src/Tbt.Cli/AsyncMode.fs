/// Async mode switch for Server and Client.
///
/// Async (per-session coordinator + AsyncPollerGroup) is the default; only an
/// explicit `TBTUN_ASYNC_MODE=0|false|off|disabled` selects the synchronous loop.
module Tbt.Cli.AsyncMode

open System

/// Async is the default. Only an explicit disable keeps the synchronous loop.
/// True unless TBTUN_ASYNC_MODE is 0, false, off, or disabled (case-insensitive).
let isEnabled () =
    match Environment.GetEnvironmentVariable "TBTUN_ASYNC_MODE" with
    | null -> true
    | value ->
        match value.Trim().ToLowerInvariant() with
        | "0" | "false" | "off" | "disabled" -> false
        | _ -> true

/// One line for the startup log.
let describe () =
    if isEnabled () then "async (per-session poller)" else "sync"
