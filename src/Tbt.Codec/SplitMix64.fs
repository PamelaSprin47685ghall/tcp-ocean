/// SplitMix64 and seed derivation (spec 5.5). Both ends compute the same streams.
module Tbt.Codec.SplitMix64

[<Literal>]
let Golden = 0x9E3779B97F4A7C15UL

/// Domain tags keep the parity and BOOSTER streams apart.
[<Literal>]
let DomParity = 0x5041UL

[<Literal>]
let DomBoost = 0x424FUL

let inline mix (z0: uint64) =
    let z = (z0 ^^^ (z0 >>> 30)) * 0xBF58476D1CE4E5B9UL
    let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
    z ^^^ (z >>> 31)

/// SplitMix64 stream. Next adds Golden, then mixes.
[<Struct>]
type Rng =
    val mutable State: uint64
    new(state) = { State = state }

    member r.Next() =
        r.State <- r.State + Golden
        mix r.State

/// One step of seed derivation: h' = mix((h + Golden) xor f).
let inline seedStep (h: uint64) (f: uint64) = mix ((h + Golden) ^^^ f)

/// seed_of(g, f1, f2, f3, f4): h = g; for each field f: h = mix((h + Golden) xor f).
let seedOf4 (g: uint64) f1 f2 f3 f4 = seedStep (seedStep (seedStep (seedStep g f1) f2) f3) f4
