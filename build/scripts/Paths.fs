module Paths

open System
open System.IO

let ToolName = "assembly-differ"
let Repository = sprintf "nullean/%s" ToolName

/// The RIDs we ship native-AOT tool packages for. AOT compilation requires a matching
/// OS/arch, so CI packs one RID per runner; this list only documents the set.
let AotRuntimeIdentifiers = ["linux-x64"; "linux-arm64"; "win-x64"; "win-arm64"; "osx-arm64"]

/// Must mirror assembly-differ.csproj's TargetFrameworks. Used to patch the signed managed dll back
/// into the packed 'any' fallback for every TFM it ships — see fixAnyPackageSigning in Targets.fs.
let ManagedTargetFrameworks = ["net8.0"; "net10.0"]

let Root =
    let mutable dir = DirectoryInfo(".")
    while dir.GetFiles("*.slnx").Length = 0 do dir <- dir.Parent
    Environment.CurrentDirectory <- dir.FullName
    dir
    
let RootRelative path = Path.GetRelativePath(Root.FullName, path) 
    
let Output = DirectoryInfo(Path.Combine(Root.FullName, "build", "output"))

let ToolProject = DirectoryInfo(Path.Combine(Root.FullName, "src", ToolName))
