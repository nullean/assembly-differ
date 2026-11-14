<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/nullean/nullean.github.io/main/assets/nullean-lockup-544x192.png">
    <img src="https://raw.githubusercontent.com/nullean/nullean.github.io/main/assets/nullean-lockup-light-544x192.png" width="272" alt="nullean">
  </picture>
</p>

<h1 align="center">assembly-differ</h1>

<p align="center">Diff the public API of two versions of your assemblies, from disk, NuGet or a git commit, and find out whether the change is breaking.</p>

<p align="center">
  <a href="https://www.nuget.org/packages/assembly-differ"><img src="https://img.shields.io/nuget/v/assembly-differ?style=flat-square" alt="NuGet"></a>
  <a href="https://github.com/nullean/assembly-differ/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/nullean/assembly-differ/ci.yml?branch=main&style=flat-square&label=ci" alt="CI"></a>
</p>

Proc 0.14.0 added a `CancellationToken` to its `Start*` methods. Diffing it against 0.13.0 straight from nuget.org shows
each old overload deleted and its replacement added, which makes the release a major change:

```console
$ assembly-differ nuget Proc 0.13.0 0.14.0 -t proc -f xml
[diff] Difference found: NuGetAssemblyProvider:proc.dll and NuGetAssemblyProvider:proc.dll
<Assembly Name="proc, Version=0.0.0.0, Culture=neutral, PublicKeyToken=96c599bbe3e70f5d" DiffType="Modified">
  <Module Name="proc.dll" DiffType="Modified">
    <Type Name="ProcNet.LongRunningApplicationSubscription" DiffType="Modified">
      <Property Name="WaitHandle" DiffType="Modified">
        <Method Name="get" DiffType="Modified">
          <DeclarationDiffs>
            <DiffItem DiffType="Modified">Member is more visible.</DiffItem>
          </DeclarationDiffs>
        </Method>
      </Property>
    </Type>
    <Type Name="ProcNet.ObservableProcessBase&lt;TConsoleOut&gt;" DiffType="Modified">
      <Method Name="WaitForCompletion(Nullable&lt;TimeSpan&gt;, CancellationToken)" DiffType="New" />
      <Method Name="WaitForCompletion(Nullable&lt;TimeSpan&gt;)" DiffType="Deleted" />
    </Type>
    <Type Name="ProcNet.Proc" DiffType="Modified">
      <Method Name="Start(StartArguments, CancellationToken)" DiffType="New" />
      <Method Name="Start(StartArguments)" DiffType="Deleted" />
      <Method Name="StartLongRunning(LongRunningArguments, TimeSpan, IConsoleOutWriter, CancellationToken)" DiffType="New" />
      <Method Name="StartLongRunning(LongRunningArguments, TimeSpan, IConsoleOutWriter)" DiffType="Deleted" />
      <Method Name="StartRedirected(StartArguments, StandardInputHandler, IConsoleLineHandler, CancellationToken)" DiffType="New" />
      <Method Name="StartRedirected(StartArguments, StandardInputHandler, IConsoleLineHandler)" DiffType="Deleted" />
      <Method Name="StartRedirected(StartArguments, IConsoleLineHandler, CancellationToken)" DiffType="New" />
      <Method Name="StartRedirected(StartArguments, IConsoleLineHandler)" DiffType="Deleted" />
    </Type>
  </Module>
</Assembly>[diff] Suggested version change: Major
```

## Install

```sh
dotnet tool install --global assembly-differ
```

On Linux (x64, arm64), Windows (x64, arm64) and macOS (arm64) this installs a self-contained native-AOT executable: no
shared .NET runtime, no JIT warm-up. Everywhere else it falls back to a framework-dependent build, which needs the .NET
runtime installed.

## Quick start

Compare the package you just packed with its last release on nuget.org, and fail the build on a breaking change:

```sh
assembly-differ release build/output/MyLib.1.3.0.nupkg --fail-on major
```

The package id and version come from the `.nupkg`, and the release compared with is the newest stable one before
`1.3.0` on the feeds your NuGet.config lists, so the same command keeps working as you release. A package with no
earlier release is a first release: there is nothing to compare, so the command says so and succeeds, unless you pass
`--require-baseline`.

| Command | Compares |
|---|---|
| `release <path>` | a new build, as a `.nupkg`, a `.dll` or a directory, with the release before it; pass `--package` and `--version` unless `<path>` is a `.nupkg` |
| `files <old> <new>` | two local builds, each a `.dll`, a directory of dlls or a `.nupkg` |
| `nuget <id> <old-version> <new-version>` | two published versions, from the NuGet sources your NuGet.config lists |
| `github <owner/repo> <commit> <path> --build <command> --artifacts <path>` | a new build with an older commit, cloned and built with the command you give |

Every command takes the same options:

| Option | What |
|---|---|
| `-t`, `--target` | only compare these assemblies, by file name without extension (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `markdown` (default), `xml`, `asciidoc`, `github-comment` (a pull request comment with a collapsible diff per assembly) or `json` (`api-changes.json`: the suggested change, breaking changes and every change as a tree, for tools) |
| `-o`, `--output` | a directory or file to write the report to, besides the console |
| `--fail-on` | exit with `4` when the suggested change is at least `patch`, `minor` or `major`; `none` (the default) never fails |
| `--framework` | the target framework to compare, such as `net8.0` (`files`, `nuget` and `release`); defaults to the newest the packages share |

A single assembly on each side is compared whatever its file name. With more, assemblies pair by file name, and one
only a side has is printed; it counts as removed or added API when `--target` names it.

The suggested change follows semantic versioning. It is `None` when the public API is unchanged, `Patch` when only
assembly references changed, and `Minor` when API was only added or attributes changed: Proc 0.13.0 marks
`NoWrapInThread` `[Obsolete]`, and `assembly-differ nuget Proc 0.12.0 0.13.0` suggests `Minor`. It is `Major` when
something was removed or changed, and when a new interface member, abstract member or `required` member makes existing
code change.

More examples:

```sh
# two local builds, every dll matched by name
assembly-differ files build/1.2.0 build/1.3.0

# two published packages, as Markdown
assembly-differ nuget Proc 0.13.0 0.14.0 -t proc

# the release before 1.3.0 against a build directory
assembly-differ release src/MyLib/bin/Release/net8.0 --package MyLib --version 1.3.0 -t MyLib
```

### Provider specs

Before it had a command per source, the tool took two provider specs, and `diff` still does, unchanged, for the builds
that call it: `assembly-differ diff "previous-nuget|MyLib|1.3.0|net8.0" "directory|bin" -t MyLib -p major`. A spec is
`assembly|<path>`, `directory|<path>`, `nuget|<id>|<version>|[tfm]`, `previous-nuget|<id>|<version>|[tfm]` or
`github|<owner/repo>|<commit>|<build command>|<relative output path>`. Its options are `-t`, `-o`, `-f` (default
`xml`), `-p`/`--prevent-change` in place of `--fail-on`, and `-a`/`--allow-empty-previous-nuget`, which succeeds when
`previous-nuget` finds no earlier release.

### GitHub Action

```yaml
- uses: nullean/assembly-differ@main
  with:
    first: "previous-nuget|MyLib|1.3.0|net8.0"
    second: "directory|src/MyLib/bin/Release/net8.0"
    args: --target MyLib --format markdown
```

The action runs `assembly-differ diff <first> <second> <args>` in the prebuilt, distroless
`ghcr.io/nullean/assembly-differ:edge` container, so the workflow needs no .NET SDK. `args` passes through verbatim.
Container actions run on Linux runners only.

### Container

The same image works anywhere `docker run` does, such as GitLab CI or a machine without the .NET SDK:

```sh
docker run --rm -v "$(pwd)":/workspace ghcr.io/nullean/assembly-differ:edge files /workspace/old /workspace/new
```

It is native-AOT on a chiseled `runtime-deps` base, with no shell, running as a non-root user. `edge` tracks the latest
build of `main`; `latest` and the version tags (for example `0.18.1`) follow releases.

## Packages

The tool ships as one package per platform. Install the root package; the .NET SDK picks the right one.

| Package | What it is | |
|---|---|---|
| [`assembly-differ`](src/assembly-differ/README.md) | the tool; points the SDK at the package for your platform | [![NuGet](https://img.shields.io/nuget/v/assembly-differ?style=flat-square)](https://www.nuget.org/packages/assembly-differ) |
| `assembly-differ.<rid>` | the native-AOT build for `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64` and `osx-arm64` | [![NuGet](https://img.shields.io/nuget/v/assembly-differ.linux-x64?style=flat-square)](https://www.nuget.org/packages/assembly-differ.linux-x64) |
| `assembly-differ.any` | the framework-dependent fallback (net10.0, net11.0) for every other platform | [![NuGet](https://img.shields.io/nuget/v/assembly-differ.any?style=flat-square)](https://www.nuget.org/packages/assembly-differ.any) |

## Learn more

- [Releases](https://github.com/nullean/assembly-differ/releases)
- [Contributing](https://github.com/nullean/.github/blob/main/CONTRIBUTING.md)

assembly-differ's comparison is a port of [JustAssembly](https://github.com/telerik/JustAssembly), licensed under Apache 2.0.

MIT licensed. Copyright Nullean and contributors.
