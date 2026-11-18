/// Async mode switch for Server and Client.
///
/// The environment variable is the only switch. Anything but an explicit
/// enable keeps the synchronous loop, so a typo cannot silently move a
/// production node onto the experimental path.
module Tbt.Cli.AsyncMode

open System

/// True when TBTUN_ASYNC_MODE is 1, true, on, or enabled (case-insensitive).
let isEnabled () =
    match Environment.GetEnvironmentVariable "TBTUN_ASYNC_MODE" with
    | null -> false
    | value ->
        match value.Trim().ToLowerInvariant() with
        | "1" | "true" | "on" | "enabled" -> true
        | _ -> false

/// One line for the startup log.
let describe () =
    if isEnabled () then "async (per-session poller)" else "sync"
