# assembly-differ

A .NET tool that diffs the public API of two sets of assemblies and suggests the version change it needs (none,
patch, minor, major). Each side comes from a provider: `assembly`, `directory`, `nuget`, `previous-nuget` or
`github`; the result is written as `xml`, `markdown`, `asciidoc` or `github-comment`. It is also published as a
distroless container (`ghcr.io/nullean/assembly-differ`) and a GitHub Action (`action.yml`) that runs it.

## Layout

| Path | What |
|---|---|
| `src/assembly-differ` | the tool (net8.0 and net10.0), a `Nullean.Argh` CLI; `DiffCommand.cs` is the `diff` command |
| `src/assembly-differ/Providers` | where each side's assemblies come from, one factory per provider prefix |
| `src/assembly-differ/Exporters` | one exporter per `--format` |
| `src/assembly-differ/lib` | the vendored JustAssembly/JustDecompile assemblies that do the comparison |
| `build/scripts` | the F# build the `mise` tasks and CI call |
| `build/keys` | the strong-name key the tool's own assembly is signed with |
| `action.yml` | the GitHub Action: runs the `edge` container image |

## Commands

mise installs the SDK pinned in `global.json` (plus the net10.0 runtime the build, nupkg-validator and the API diff run
on). Run everything through it:

```bash
mise run build    # build the solution
mise run pack     # build, pack, validate the packages, report public API changes
mise run ci       # build, pack, validate and diff (there are no tests, so this is pack)
mise run check    # workflow security checks (zizmor)
mise run clean    # delete build output
```

`mise tasks ls` lists them all. Packages and API reports go to `build/output`. `./build.sh <target>` runs the same
build targets directly; CI calls it for each step, and for the ones `mise` has no task for (`publishcontainers`, the
release steps).

## Packaging

- The tool ships as native-AOT packages, one per RID (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`,
  `osx-arm64`), a root package that points at them, and a framework-dependent `assembly-differ.any` fallback.
- AOT only compiles for the machine it runs on, so a local `pack` keeps just the root and `any` packages. CI's
  `aot-pack` matrix builds each RID on a matching runner and the build job merges them in.
- `dotnet pack` puts the unsigned `obj/` assembly into the `any` package; `generatePackages` swaps in the signed
  `bin/` copy (`fixAnyPackageSigning`). `Paths.ManagedTargetFrameworks` must mirror the csproj's
  `TargetFrameworks`.
- The AOT build roots `NuGet.Protocol` and `Newtonsoft.Json` (`TrimmerRootAssembly`): the NuGet providers need
  constructors trimming would remove.

## Rules

- The CLI surface (`diff`, the provider specs and the flags) is the contract `action.yml`, the container and other
  repositories' builds rely on. Removing or renaming any of it is a breaking change.
- The build diffs this tool against its own previous release (`generateapichanges`), so a broken diff fails its own CI.
