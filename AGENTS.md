# assembly-differ

A .NET tool that diffs the public API of two sets of assemblies and suggests the version change it needs (none,
patch, minor, major). A command per source (`projects`, the default, `release`, `files`, `nuget`) picks the two sides; the result is
written as `markdown`, `xml`, `asciidoc`, `github-comment` or `json`. It is also published as a
distroless container (`ghcr.io/nullean/assembly-differ`) and a GitHub Action (`action.yml`) that runs it.

## Layout

| Path | What |
|---|---|
| `src/assembly-differ` | the tool (net11.0), a `Nullean.Argh` CLI; `DiffCommand.cs` is the `diff` command |
| `src/assembly-differ/Providers` | where each side's assemblies come from, one factory per provider prefix |
| `src/assembly-differ/Exporters` | one exporter per `--format` |
| `src/assembly-differ/Engine` | the comparison: reads both assemblies with System.Reflection.Metadata and reports what differs |
| `tests/assembly-differ.Tests` | TUnit tests that run the built tool as a process on assemblies Roslyn compiles from C# source |
| `build/scripts` | the F# build ([Nullean.Make.Fs](https://www.nuget.org/packages/Nullean.Make.Fs), net11.0) the `mise` tasks and CI call |
| `build/keys` | the strong-name key the tool's own assembly is signed with |
| `action.yml` | the GitHub Action: runs the `edge` container image |

## Commands

mise installs the SDK pinned in `global.json`. Run everything through it:

```bash
mise run build    # build the solution
mise run test     # build, then run the tests
mise run pack     # build, pack, validate the packages, report public API changes
mise run ci       # build, test, pack, validate and diff, then the curb check
mise run format   # format the repository with curb
mise run check    # formatting drift (curb) and workflow security checks (zizmor); CI runs the curb half
mise run clean    # delete build output
mise run release  # show the release draft; `mise run release publish` releases
```

`mise tasks ls` lists them all. Packages and API reports go to `build/output`. `./build.sh <target>` runs the same
build targets directly; CI calls it for each step, and for the one `mise` has no task for (`publishcontainers`).

## Releasing

release-drafter keeps a draft release up to date from the labels of merged pull requests. **Publishing the draft**
releases: it creates the tag, and `ci.yml` runs on it. Its `main-green` job first waits for main's push run of `ci.yml` on
the released commit and fails unless it passed (macOS runs only on pushes to main, plus the osx-arm64 AOT pack on a
release); then it builds every RID's native-AOT package, pushes
`ghcr.io/nullean/assembly-differ` (tagged `latest` and the version) and pushes the packages to nuget.org.
`mise run release` shows the draft. The [releasing](.claude/skills/releasing/SKILL.md) and
[breaking-changes](.claude/skills/breaking-changes/SKILL.md) skills have the details. Every pull request carries exactly
one release label.

## Packaging

- The tool ships as native-AOT packages, one per RID (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`,
  `osx-arm64`), a root package that points at them, and a framework-dependent `assembly-differ.any` fallback.
- AOT only compiles for the machine it runs on, so a local `pack` keeps just the root and `any` packages. CI's
  `aot-pack` matrix builds each RID on a matching runner and the build job merges them in.
- `dotnet pack` puts the unsigned `obj/` assembly into the `any` package; `generatePackages` swaps in the signed
  `bin/` copy (`fixAnyPackageSigning`). `Paths.ManagedTargetFrameworks` must mirror the csproj's
  `TargetFrameworks`.
- The csproj turns on NuGet's `NuGet.UseSystemTextJsonDeserialization` feature switch, so NuGet.Protocol reads feed
  JSON with source-generated System.Text.Json rather than Newtonsoft.Json reflection, which trimming breaks (#62).
  `./build.sh smokeaot` publishes the native-AOT binary for this machine and runs `previous-nuget` and `nuget` with
  it; CI's `aot-pack` job runs it on every RID.

## Tests

- The tests run the tool as a process, so they hold for whatever engine sits behind the CLI. `Fixture` compiles an
  old and a new `Lib.dll` from C# source with Roslyn; `Cli` starts the net11.0 build of the tool on them.
- `DiffCommandTests` are characterization tests: Nullean.Snapshots keeps what `diff` prints, writes and returns in
  `.snapshots/`, wrong answers included. A changed snapshot is a change to the CLI contract: review the received
  file, then accept it with `snap accept` or `SNAPSHOT_ACCEPT=1` and say why in the pull request.
- `CorpusTests` snapshots every format for 100 generated library versions (`RichApi`). The snapshots were written by
  the JustAssembly engine this tool used before `Engine/`, so they pin its reports down exactly.
- `ReferenceTests` compares this build with another one on more generated versions. It runs only when
  `ASSEMBLY_DIFFER_REFERENCE` names the other build's `assembly-differ.dll` (`ASSEMBLY_DIFFER_SEEDS` sets how many).
- `Properties` holds CsCheck properties over generated APIs. A known bug is a property marked `[Skip]` with its
  issue link; the fix removes the `[Skip]`.

## Rules

- `Engine/` reproduces the reports of JustAssembly, which it replaced, quirks included: a type that became less visible
  is reported as more visible, and nested types are named `.Name`. Changing a report is a change to the CLI contract.

- The CLI surface is the contract `action.yml`, the container and other repositories' builds rely on: the commands
  (`projects`, the default, `release`, `files`, `nuget`, `pull-request`) and their options, and the hidden `diff` with its provider specs and flags.
  Removing or renaming any of it is a breaking change. `ApiCommands.cs` holds the commands, `DiffRunner.cs` the
  comparison they share with `diff`.
- The build diffs this tool against its own previous release (`generateapichanges`), so a broken diff fails its own CI.
- Build targets are Make.Fs `target { }` blocks in `build/scripts/Program.fs` whose commands are Proc.Fs `sh $"…"`
  lines; values come from `text` inside `shell { }` (`Versions.fs`). Do not call the C# `ProcNet` API from the build.
  CI calls targets by name (`./build.sh generatepackages -s`): keep multi-word case names as one word.
