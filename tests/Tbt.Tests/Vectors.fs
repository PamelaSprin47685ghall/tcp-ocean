/// Access to the normative test vectors in tests/vectors.
module Tbt.Tests.Vectors

open System
open System.IO
open System.Text.Json

let private load (name: string) =
    let path = Path.Combine(AppContext.BaseDirectory, "vectors", name)
    JsonDocument.Parse(File.ReadAllText path).RootElement

let codec = lazy (load "tbt-sc2-vectors.json")
let joinAck = lazy (load "join-ack-v22-vectors.json")

let str (root: JsonElement) (key: string) : string =
    match root.GetProperty(key).GetString() with
    | null -> failwith ("vector has no string: " + key)
    | v -> v

let hex (s: string) = Convert.FromHexString s

let toHex (b: ReadOnlySpan<byte>) = Convert.ToHexString(b).ToLowerInvariant()
