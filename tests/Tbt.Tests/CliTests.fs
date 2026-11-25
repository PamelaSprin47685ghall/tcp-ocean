module Tbt.Tests.CliTests

open System
open Xunit
open Tbt.Crypto.Bootstrap
open Tbt.Cli.Common

[<Fact>]
let ``omitting --suites and --x25519 keeps the historical default offer`` () =
    let o = Options([| "--psk-file"; "x"; "--paths"; "4" |])
    let struct (aegis, chacha8, chacha20, x25519) = offerFromOptions o
    Assert.False(aegis)
    Assert.False(chacha8)
    Assert.True(chacha20)
    Assert.False(x25519)
    Assert.Equal(Suite.ChaCha20Poly1305Bit, Suite.advertisedBits chacha8 aegis chacha20)

[<Fact>]
let ``--suites lists the offered suites and --x25519 is a bare flag`` () =
    let o = Options([| "--suites"; "aegis,chacha8,chacha20"; "--x25519" |])
    let struct (aegis, chacha8, chacha20, x25519) = offerFromOptions o
    Assert.True(aegis)
    Assert.True(chacha8)
    Assert.True(chacha20)
    Assert.True(x25519)
    let off = Options([| "--suites"; "chacha20"; "--x25519"; "0" |])
    let struct (_, _, c20, x) = offerFromOptions off
    Assert.True(c20)
    Assert.False(x)

[<Fact>]
let ``describeOffer formats the negotiated suite and X25519 cap`` () =
    Assert.Equal("suite=0x02(chacha20) x25519=0", describeOffer Suite.ChaCha20Poly1305 0us)
    Assert.Equal("suite=0x01(aegis) x25519=1", describeOffer Suite.Aegis128L Caps.X25519)

// ---- PSK file errors: an operator's typo must be one line and exit 2, never a stack dump ----

let private tempDir () =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbt-cli-" + System.Guid.NewGuid().ToString("N"))
    System.IO.Directory.CreateDirectory dir |> ignore
    dir

/// Write `content` to a scratch PSK file and remove it (with its directory) afterwards, so a
/// failing assertion cannot leave files behind in /tmp.
let private withPsk (content: string) (f: string -> 'T) =
    let dir = tempDir ()
    let path = System.IO.Path.Combine(dir, "psk.hex")
    System.IO.File.WriteAllText(path, content)
    try f path
    finally System.IO.Directory.Delete(dir, true)

let private missingPath () =
    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbt-no-such-" + System.Guid.NewGuid().ToString("N"), "psk.hex")

let private usageMessage (act: unit -> 'T) =
    match (try act () |> Choice1Of2 with e -> Choice2Of2 e) with
    | Choice2Of2 (UsageError message) -> message
    | Choice2Of2 other -> failwith ("expected UsageError, got " + other.GetType().Name)
    | Choice1Of2 _ -> failwith "expected UsageError, got a value"

[<Fact>]
let ``a missing psk-file is a usage error that names the path`` () =
    let path = missingPath ()
    let message = usageMessage (fun () -> readPsks path)
    Assert.Contains("psk-file not found", message)
    Assert.Contains(path, message)

[<Fact>]
let ``a psk-file with no keys, bad hex, or the wrong length is a usage error`` () =
    // Empty / comment-only file: unusable, and used to reach `.[0]` as an IndexOutOfRange.
    withPsk "# nothing here\n\n" (fun p -> Assert.Contains("has no keys", usageMessage (fun () -> readPsks p)))
    withPsk "not-hex\n" (fun p -> Assert.Contains("64 hex digits", usageMessage (fun () -> readPsks p)))
    withPsk (String.replicate 32 "0" + "\n") (fun p ->
        Assert.Contains("64 hex digits", usageMessage (fun () -> readPsks p)))
    // The message must never echo key material back to the operator.
    let secret = String.replicate 16 "ab" // 32 hex digits, invalid length
    withPsk (secret + "\n") (fun p -> Assert.DoesNotContain(secret, usageMessage (fun () -> readPsks p)))

[<Fact>]
let ``a valid psk-file still parses into 32-byte keys with comments stripped`` () =
    let keyA = String.replicate 64 "a"
    let keyB = String.replicate 64 "b"
    withPsk ("# comment\n" + keyA + "  # trailing\n\n" + keyB + "\n") (fun p ->
        let keys = readPsks p
        Assert.Equal(2, keys.Length)
        Assert.Equal(32, keys.[0].Length)
        Assert.All(keys.[0], fun b -> Assert.Equal(0xAAuy, b))
        Assert.All(keys.[1], fun b -> Assert.Equal(0xBBuy, b)))

[<Fact>]
let ``operator mistakes are usage errors, not internal errors`` () =
    // These used to raise plain `failwith`, which the old top-level catch labelled
    // "internal error" and exited 1 — a different contract from a missing PSK (exit 2).
    Assert.Contains("bad option: -x", usageMessage (fun () -> Options [| "-x" |] |> ignore))
    Assert.Contains("missing option --psk-file", usageMessage (fun () -> Options([||]).Text "psk-file" |> ignore))
    let badNumber = Options [| "--paths"; "abc" |]
    Assert.Contains("needs an integer", usageMessage (fun () -> badNumber.Int("paths", 4) |> ignore))
    Assert.Contains("bad endpoint: nope", usageMessage (fun () -> endpoint "nope" |> ignore))

[<Fact>]
let ``the client exits 2 on a missing psk-file instead of aborting with a stack`` () =
    // Regression: this used to throw DirectoryNotFoundException out of `Client.runCore` →
    // unhandled exception → .NET abort (exit 134) with a full stack trace.
    let args =
        [| "client"; "--server"; "127.0.0.1:9"; "--listen"; "127.0.0.1:9"; "--psk-file"; missingPath () |]
    Assert.Equal(2, Tbt.Cli.Program.main args)

[<Fact>]
let ``the spawned CLI is the same configuration and framework as these tests`` () =
    // The previous resolver searched `src/Tbt.Cli/bin` recursively and ranked only by target
    // framework, so a stale `Debug` build tied with the `Release` one and filesystem order
    // decided. This pins the property that makes the result deterministic: the CLI directory is
    // declared at build time and matches this test assembly's own `bin/<Configuration>/<tfm>`.
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let metadata =
        asm.GetCustomAttributes(typeof<System.Reflection.AssemblyMetadataAttribute>, false)
        |> Array.map (fun a -> a :?> System.Reflection.AssemblyMetadataAttribute)
        |> Array.tryFind (fun a -> a.Key = "TbtCliOutput")
    let declared =
        match metadata with
        | Some a when not (System.String.IsNullOrWhiteSpace a.Value) -> a.Value
        | _ -> failwith "no TbtCliOutput metadata on the test assembly (see Tbt.Session.Tests.fsproj)"
    // .../bin/Release/net10.0/Tbt.Session.Tests.dll -> configuration "Release", framework "net10.0"
    // The leaf is the target framework and its parent is the configuration; reading them in the
    // other order would compare against "net10.0/Release", which no output path ever ends with.
    let tfmDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    let configDir = System.IO.Path.GetDirectoryName tfmDir
    let expected = System.IO.Path.GetFileName configDir + "/" + System.IO.Path.GetFileName tfmDir
    let declaredUnix = declared.Replace('\\', '/')
    Assert.EndsWith(expected, declaredUnix)
    // ... and it must be the CLI's tree, not, say, this test project's own output that happens to
    // share the same "<Configuration>/<tfm>" tail.
    Assert.Contains("/src/Tbt.Cli/bin/", declaredUnix)
    Assert.True(System.IO.Directory.Exists declared, "declared CLI output directory does not exist: " + declared)

[<Fact>]
let ``the real process exits 2 with one readable line for a missing psk-file`` () =
    // In-process `Program.main` cannot prove what the OS sees. This spawns the built CLI, so a
    // regression back to an unhandled exception (abort, exit 134, stack dump) fails here.
    // Resolve the CLI through build-time metadata, not through the filesystem.
    //
    // The test assembly carries `TbtCliOutput` (injected by Tbt.Session.Tests.fsproj as
    // `$(Configuration)/$(TargetFramework)`), so there is exactly one candidate and it is the
    // build the tests were compiled against. A recursive search under `src/Tbt.Cli/bin` is not
    // reproducible: it also matches a stale `Debug` output or a leftover `-r <rid>` publish, and
    // F# `Seq.sortBy` preserves enumeration order for ties, so which one won would depend on the
    // filesystem rather than on the code under test. `TBTUN_CLI_DLL` remains the explicit
    // escape hatch for an unusual output path.
    let dll =
        let overridePath = System.Environment.GetEnvironmentVariable "TBTUN_CLI_DLL"
        if not (System.String.IsNullOrWhiteSpace overridePath) then
            if System.IO.File.Exists overridePath then overridePath
            else failwith ("TBTUN_CLI_DLL points at no file: " + overridePath)
        else
            let declared =
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof<System.Reflection.AssemblyMetadataAttribute>, false)
                |> Array.map (fun a -> a :?> System.Reflection.AssemblyMetadataAttribute)
                |> Array.tryFind (fun a -> a.Key = "TbtCliOutput")
                |> Option.map (fun a -> a.Value)
            match declared with
            | None ->
                failwith
                    "the test assembly carries no TbtCliOutput metadata; build it from tests/Tbt.Session.Tests \
                     (or set TBTUN_CLI_DLL)"
            | Some dir ->
                let candidate = System.IO.Path.Combine(dir, "tbtun.dll")
                if not (System.IO.File.Exists candidate) then
                    failwith (
                        "no tbtun.dll at "
                        + candidate
                        + "; build src/Tbt.Cli in the same configuration, or set TBTUN_CLI_DLL")
                // `dotnet <dll>` also needs the runtime config beside it; without it the process
                // starts and dies with a hosting error rather than the behaviour under test.
                if not (System.IO.File.Exists(System.IO.Path.ChangeExtension(candidate, ".runtimeconfig.json"))) then
                    failwith ("tbtun.dll at " + candidate + " has no runtimeconfig.json; it cannot be started by `dotnet`")
                candidate
    let psi = System.Diagnostics.ProcessStartInfo "dotnet"
    for a in [ dll; "client"; "--server"; "127.0.0.1:9"; "--listen"; "127.0.0.1:9"; "--psk-file"; missingPath () ] do
        psi.ArgumentList.Add a
    psi.UseShellExecute <- false
    psi.RedirectStandardError <- true
    psi.RedirectStandardOutput <- true
    use p = System.Diagnostics.Process.Start psi
    let err = p.StandardError.ReadToEnd()
    Assert.True(p.WaitForExit 60000, "the CLI process did not exit within 60 s")
    Assert.Equal(2, p.ExitCode)
    Assert.Contains("tbtun: psk-file not found", err)
    Assert.DoesNotContain("   at ", err) // a stack frame means the unhandled-exception path is back
