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
$ assembly-differ diff "nuget|Proc|0.13.0|netstandard2.0" "nuget|Proc|0.14.0|netstandard2.0" -t proc
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

Diff the last release on nuget.org against what you just built, as Markdown, and fail the build on a breaking change:

```sh
assembly-differ diff "previous-nuget|MyLib|1.3.0|net8.0" "directory|src/MyLib/bin/Release/net8.0" -t MyLib -f markdown -p major
```

`previous-nuget` finds the latest release before `1.3.0`, so the same command keeps working as you release.

Each side is a provider spec, and the two sides can mix:

| Spec | Assemblies |
|---|---|
| `assembly\|<path>` | one dll |
| `directory\|<path>` | every dll in a directory, matched by file name against the other side |
| `nuget\|<id>\|<version>\|[tfm]` | a package from the NuGet sources your NuGet.config lists |
| `previous-nuget\|<id>\|<version>\|[tfm]` | the release on nuget.org before `<version>` |
| `github\|<owner/repo>\|<commit>\|<build command>\|<relative output path>` | a commit, cloned and built with the command you give |

| Flag | What |
|---|---|
| `-t`, `--target` | only diff these assemblies (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `xml` (default), `markdown`, `asciidoc`, `github-comment` (a pull request comment with a collapsible diff per assembly) or `json` (`api-changes.json`: the suggested change, breaking changes and every change as a tree, for tools) |
| `-o`, `--output` | output directory or file; without it the diff only goes to the console |
| `-p`, `--prevent-change` | exit with `4` when the suggested change is at least `patch`, `minor` or `major`; `none` (the default) never fails |
| `-a`, `--allow-empty-previous-nuget` | succeed when `previous-nuget` finds no earlier release, for a first release |

The suggested change follows semantic versioning. It is `None` when the public API is unchanged, `Patch` when only
assembly references changed, and `Minor` when API was only added or attributes changed: diffing Proc 0.12.0 against
0.13.0, which only adds `NoWrapInThread`, suggests `Minor`. It is `Major` when something was removed or changed, and
when a new interface member, abstract member or `required` member makes existing code change. Always name the `diff` subcommand: a provider spec as the first argument is read as
a command name.

More examples:

```sh
# two local builds, every dll matched by name
assembly-differ diff "directory|build/1.2.0" "directory|build/1.3.0"

# two published packages
assembly-differ diff "nuget|Proc|0.13.0|netstandard2.0" "nuget|Proc|0.14.0|netstandard2.0" -t proc -f markdown

# the last release before 0.14.0 against a local build
assembly-differ diff "previous-nuget|Proc|0.14.0|netstandard2.0" "directory|build/proc" -t proc
```

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
docker run --rm -v "$(pwd)":/workspace ghcr.io/nullean/assembly-differ:edge diff "directory|/workspace/old" "directory|/workspace/new"
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
