module Packaging

open System.IO
open System.IO.Compression

let isPerRidPackage (name: string) =
    Paths.AotRuntimeIdentifiers |> List.exists (fun rid -> name.Contains(sprintf ".%s." rid))

/// `dotnet pack`'s RID-aware tool-packaging path (used once RuntimeIdentifiers is declared) copies
/// the *unsigned* obj/ build of this project's own assembly into the portable 'any' package, even
/// though the normal bin/ output is correctly strong-name signed — a long-standing obj-vs-bin mixup
/// in `dotnet pack` (see https://github.com/dotnet/sdk/issues/20197) that resurfaces here. Patched in
/// place after packing by swapping in the signed bin/ copies for every TFM the 'any' package ships.
let fixAnyPackageSigning (anyPackagePath: string) =
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

/// Tags for the container image, mirroring the versioning Versions.current already derives from git:
/// "edge" always (so `ghcr.io/nullean/assembly-differ:edge` is always the latest main build), plus
/// "latest" and the plain semver when this is an exact release tag rather than a canary commit —
/// MinVer's canary suffix always contains a hyphen, a clean tag never does.
let containerImageTags (version: string) =
    if version.Contains("-") then "edge" else sprintf "edge;latest;%s" version
