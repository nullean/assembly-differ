module Targets

open Argu
open System
open System.IO
open System.IO.Compression
open Bullseye
open CommandLine
open Fake.Tools.Git
open ProcNet

let exec binary args =
    Proc.Exec (binary, args |> List.toArray)
    
/// dotnet/sdk#53783: on a cold tool-resolver cache (every fresh CI runner, every fresh container),
/// restoring 2+ RID-specific tool packages in one manifest can misattribute one package's
/// DotnetToolSettings.xml to another, failing with "The command ... is not contained in the
/// package ...". The cache is warm after the first attempt, so a bare retry always succeeds -
/// see https://github.com/dotnet/sdk/issues/53783.
let private restoreTools =
    lazy(
        try exec "dotnet" ["tool"; "restore"]
        with _ -> exec "dotnet" ["tool"; "restore"]
    )
let private currentVersion =
    lazy(
        restoreTools.Value |> ignore
        let r = Proc.Start("dotnet", "minver", "-p", "canary.0", "-m", "0.1")
        let o = r.ConsoleOut |> Seq.find (fun l -> not(l.Line.StartsWith "MinVer:"))
        o.Line
    )
    
let private currentVersionInformational =
    lazy(sprintf "%s+%s" currentVersion.Value (Information.getCurrentSHA1( ".")))

let private clean (arguments:ParseResults<Arguments>) =
    if (Paths.Output.Exists) then Paths.Output.Delete (true)
    exec "dotnet" ["clean"] |> ignore
    
let private build (arguments:ParseResults<Arguments>) = exec "dotnet" ["build"; "-c"; "Release"] |> ignore

let private pristineCheck (arguments:ParseResults<Arguments>) =
    match Information.isCleanWorkingCopy "." with
    | true  -> printfn "The checkout folder does not have pending changes, proceeding"
    | _ ->
        ()
        //failwithf "The checkout folder has pending changes, aborting"

let private isPerRidPackage (name: string) =
    Paths.AotRuntimeIdentifiers |> List.exists (fun rid -> name.Contains(sprintf ".%s." rid))

/// `dotnet pack`'s RID-aware tool-packaging path (used once RuntimeIdentifiers is declared) copies
/// the *unsigned* obj/ build of this project's own assembly into the portable 'any' package, even
/// though the normal bin/ output is correctly strong-name signed — a long-standing obj-vs-bin mixup
/// in `dotnet pack` (see https://github.com/dotnet/sdk/issues/20197) that resurfaces here. Patched in
/// place after packing by swapping in the signed bin/ copies for every TFM the 'any' package ships.
let private fixAnyPackageSigning (anyPackagePath: string) =
    use archive = ZipFile.Open(anyPackagePath, ZipArchiveMode.Update)
    for tfm in Paths.ManagedTargetFrameworks do
        let entryName = sprintf "tools/%s/any/%s.dll" tfm Paths.ToolName
        let signedDll = Path.Combine(Paths.ToolProject.FullName, "bin", "Release", tfm, sprintf "%s.dll" Paths.ToolName)
        match archive.GetEntry(entryName), File.Exists signedDll with
        | null, _ | _, false -> ()
        | entry, true ->
            entry.Delete()
            let newEntry = archive.CreateEntry(entryName)
            use entryStream = newEntry.Open()
            use fileStream = File.OpenRead(signedDll)
            fileStream.CopyTo(entryStream)

let private generatePackages (arguments:ParseResults<Arguments>) =
    let output = Paths.RootRelative Paths.Output.FullName
    if not Paths.Output.Exists then Paths.Output.Create()

    // A plain `dotnet pack` emits the root package (whose DotnetToolSettings.xml v2 maps each RID to
    // its own package) AND a package per RID — but native AOT can only compile for the machine it
    // runs on, so those per-RID outputs from a single machine are self-contained MANAGED builds,
    // silently missing the AOT compilation. We therefore keep only the root and the portable 'any'
    // fallback here, and take the real per-RID packages from the CI matrix, where each is compiled
    // on a matching runner (see aot-pack in .github/workflows/ci.yml).
    let staging = Paths.RootRelative <| Path.Combine(Paths.Output.FullName, "..", "differ-staging")
    if Directory.Exists staging then Directory.Delete(staging, true)
    exec "dotnet" ["pack"; sprintf "src/%s/%s.csproj" Paths.ToolName Paths.ToolName; "-c"; "Release"; "-o"; staging] |> ignore

    DirectoryInfo(staging).GetFiles("*.nupkg")
    |> Seq.filter (fun f -> not (isPerRidPackage f.Name))
    |> Seq.iter (fun f ->
        let destination = Path.Combine(Paths.Output.FullName, f.Name)
        printfn "keeping %s" f.Name
        f.CopyTo(destination, true) |> ignore
        if f.Name.Contains(sprintf "%s.any." Paths.ToolName) then
            fixAnyPackageSigning destination)

    Directory.Delete(staging, true)

let private validatePackages (arguments:ParseResults<Arguments>) =
    let nugetPackage =
        // Only the 'any' package carries a signed managed assembly to check: the root package is
        // just a DotnetToolSettings.xml pointer with no dll of its own, and the per-RID AOT packages
        // hold a native binary with no managed identity either.
        let p =
            Paths.Output.GetFiles "*.nupkg"
            |> Seq.filter (fun f -> f.Name.Contains(sprintf "%s.any." Paths.ToolName))
            |> Seq.sortByDescending(fun f -> f.CreationTimeUtc) |> Seq.head
        Paths.RootRelative p.FullName
    exec "dotnet" ["nupkg-validator"; nugetPackage; "-v"; currentVersionInformational.Value; "-a"; Paths.ToolName; "-k"; "96c599bbe3e70f5d"; "--allow-roll-forward"] |> ignore

let private generateApiChanges (arguments:ParseResults<Arguments>) =
    let output = Paths.RootRelative <| Paths.Output.FullName
    let currentVersion = currentVersion.Value
    let project = Paths.RootRelative Paths.ToolProject.FullName
    let dotnetRun =[ "run"; "-c"; "Release"; "-f"; "net10.0"; "--project"; project]
    let args =
        [
            "diff";
            // The plain "assembly-differ" package id is just a DotnetToolSettings.xml v2 shim pointing
            // at per-RID sub-packages (see generatePackages below) - it ships no managed assembly of
            // its own, so NuGetAssemblyProvider would find 0 assemblies there. The portable, signed
            // managed build lives in "assembly-differ.any" instead.
            sprintf "previous-nuget|%s.any|%s|net8.0" Paths.ToolName currentVersion;
            sprintf "directory|src/%s/bin/Release/net10.0" Paths.ToolName;
            "--target"; Paths.ToolName; "-f"; "github-comment"; "--output"; output
        ]
        
    exec "dotnet" (dotnetRun @ ["--"] @ args) |> ignore
    
let private generateReleaseNotes (arguments:ParseResults<Arguments>) =
    let currentVersion = currentVersion.Value
    let output =
        Paths.RootRelative <| Path.Combine(Paths.Output.FullName, sprintf "release-notes-%s.md" currentVersion)
    let tokenArgs =
        match arguments.TryGetResult Token with
        | None -> []
        | Some token -> ["--token"; token;]
    let releaseNotesArgs =
        ["generate"]
        @ (Paths.Repository.Split("/") |> Seq.toList)
        @ ["--version"; currentVersion
           "--label"; "enhancement=New Features"
           "--label"; "bug=Bug Fixes"
           "--label"; "documentation=Docs Improvements"
        ] @ tokenArgs
        @ ["--output"; output]
        
    exec "dotnet" (["release-notes"] @ releaseNotesArgs) |> ignore

let private createReleaseOnGithub (arguments:ParseResults<Arguments>) =
    let currentVersion = currentVersion.Value
    let tokenArgs =
        match arguments.TryGetResult Token with
        | None -> []
        | Some token -> ["--token"; token;]
    let releaseNotes = Paths.RootRelative <| Path.Combine(Paths.Output.FullName, sprintf "release-notes-%s.md" currentVersion)
    let breakingChanges = Paths.RootRelative <| Path.Combine(Paths.Output.FullName, "github-breaking-changes-comments.md")
    let releaseArgs =
        ["create-release"]
        @ (Paths.Repository.Split("/") |> Seq.toList)
        @ ["--version"; currentVersion
           "--body"; releaseNotes; 
           "--body"; breakingChanges; 
        ] @ tokenArgs
        
    exec "dotnet" (["release-notes"] @ releaseArgs) |> ignore

/// Tags for the container image, mirroring the versioning currentVersion already derives from git:
/// "edge" always (so `ghcr.io/nullean/assembly-differ:edge` is always the latest master build), plus
/// "latest" and the plain semver when this is an exact release tag rather than a canary commit —
/// MinVer's canary suffix always contains a hyphen, a clean tag never does.
let private containerImageTags =
    lazy(
        let version = currentVersion.Value
        if version.Contains("-") then "edge" else sprintf "edge;latest;%s" version
    )

/// Publishes the CLI's native-AOT build as a container image via the .NET SDK's own container
/// support (`dotnet publish -t:PublishContainer`), the same mechanism used for curb — see
/// https://github.com/nullean/curb/pull/72. linux-x64 only for now; a second RID becomes a second
/// manifest-list platform later with no change to action.yml.
///
/// Base image is the chiseled/distroless runtime-deps image: no shell, minimal surface, and correct
/// for an AOT binary specifically because there is no managed runtime to host — a plain `runtime`
/// image would carry a CLR this binary never uses.
///
/// --push is an explicit flag, not inferred from a CI/event-name environment variable: the aot-pack
/// job's linux-x64 leg calls this on every trigger (PR, push, tag) purely to prove the container
/// build itself still works, with no ghcr.io credentials configured there, and an env-based "is this
/// a push?" check would have tried (and failed) to push from that job on every non-PR trigger. Only
/// the build job, which does log in, passes --push.
let private publishContainers (arguments:ParseResults<Arguments>) =
    let baseImageTag = "10.0-noble-chiseled"
    let registryArgs =
        if arguments.Contains Push then ["-p"; "ContainerRegistry=ghcr.io"] else []
    let args =
        ["publish"; Paths.RootRelative Paths.ToolProject.FullName; "-c"; "Release"; "-f"; "net10.0"; "-r"; "linux-x64"]
        @ ["/t:PublishContainer"
           "-p"; "DebugType=none"
           "-p"; sprintf "ContainerBaseImage=mcr.microsoft.com/dotnet/runtime-deps:%s" baseImageTag
           "-p"; sprintf "ContainerRepository=%s" Paths.Repository
           "-p"; sprintf "ContainerImageTags=\"%s\"" containerImageTags.Value
           "-p"; "ContainerUser=1001:1001"]
        @ registryArgs
    exec "dotnet" args |> ignore

let private release (arguments:ParseResults<Arguments>) = printfn "release"
    
let private publish (arguments:ParseResults<Arguments>) = printfn "publish" 

let Setup (parsed:ParseResults<Arguments>) (subCommand:Arguments) =
    let step (name:string) action = Targets.Target(name, new Action(fun _ -> action(parsed)))
    
    let cmd (name:string) commandsBefore steps action =
        let singleTarget = (parsed.TryGetResult SingleTarget |> Option.defaultValue false)
        let deps =
            match (singleTarget, commandsBefore) with
            | (true, _) -> [] 
            | (_, Some d) -> d
            | _ -> []
        let steps = steps |> Option.defaultValue []
        Targets.Target(name, deps @ steps, Action(action))
        
    step Clean.Name clean
    cmd Build.Name None (Some [Clean.Name]) <| fun _ -> build parsed
    
    step PristineCheck.Name pristineCheck
    step GeneratePackages.Name generatePackages 
    step ValidatePackages.Name validatePackages 
    step GenerateReleaseNotes.Name generateReleaseNotes
    step GenerateApiChanges.Name generateApiChanges
    cmd Release.Name
        (Some [PristineCheck.Name; Build.Name;])
        (Some [GeneratePackages.Name; ValidatePackages.Name; GenerateReleaseNotes.Name; GenerateApiChanges.Name])
        <| fun _ -> release parsed
        
    step CreateReleaseOnGithub.Name createReleaseOnGithub 
    step PublishContainers.Name publishContainers
    cmd Publish.Name
        (Some [Release.Name])
        (Some [CreateReleaseOnGithub.Name; PublishContainers.Name])
        <| fun _ -> publish parsed
