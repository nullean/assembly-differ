<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/nullean/nullean.github.io/main/assets/nullean-lockup-544x192.png">
    <img src="https://raw.githubusercontent.com/nullean/nullean.github.io/main/assets/nullean-lockup-light-544x192.png" width="272" alt="nullean">
  </picture>
</p>

<h1 align="center">assembly-differ</h1>

<p align="center">Diff the public API of two versions of your assemblies or packages, find out whether the change is breaking, and see what a package update would break in your own code: for people, pull requests and coding agents.</p>

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
shared .NET runtime, no JIT warm-up. Everywhere else it falls back to a framework-dependent build, which needs the .NET 11
runtime installed.

## For coding agents

An agent asked to update a package, or to change a library, usually has to decompile assemblies and read release notes
to find out what changes. `assembly-differ` answers that from the metadata, in one command, and in a solution it narrows
the answer to the API the code actually uses:

```sh
dotnet build && assembly-differ nuget Polly 8.5.0 -f json -o .   # what updating Polly to 8.5.0 breaks in this code
dotnet build -c Release && assembly-differ -f json -o .          # what my changes did to this repo's libraries
```

`api-changes.json` holds the suggested version change, the number of breaking changes and every change as a tree. The
[`assembly-differ.skills`](src/assembly-differ.skills/README.md) package carries an agent skill that teaches all of
this: add it to a project next to installing the tool, and `dotnet restore` puts `skills/assembly-differ/SKILL.md` on
the machine for skill loaders such as `dotnet-package-skills` to copy into `.agents/skills`.

```sh
dotnet tool install --global assembly-differ
dotnet add package assembly-differ.skills
```

## Quick start

In a repository, build it, then run the tool without a command. It compares every library project with the latest
release of its package:

```sh
dotnet build -c Release && assembly-differ
```

It reads the one solution in the directory, or else the project files in it, and skips test projects and projects with
`IsPackable` set to `false` (`--include-tests` and `--include-unpackable` compare them, and `--project` picks
projects). It does not build: a project without Release output is skipped with a warning.

Compare the package you just packed with its last release, and fail the build on a breaking change:

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
| `upgrades` | in a solution, every package it references directly that has a newer release, and what updating each breaks in your code |
| `nuget <id> [<version>] [<new-version>]` | in a solution, the version your projects resolve with the latest release, or with `<version>`: what updating would change; anywhere, two published versions |
| `projects` | every library project in the directory against its latest release; what runs without a command |

Every command takes the same options:

| Option | What |
|---|---|
| `-t`, `--target` | only compare these assemblies, by file name without extension (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `markdown` (default), `xml`, `asciidoc`, `github-comment` (a pull request comment with a collapsible diff per assembly) or `json` (`api-changes.json`: the suggested change, breaking changes and every change as a tree, for tools) |
| `-o`, `--output` | a directory or file to write the report to, besides the console |
| `--fail-on` | exit with `4` when the suggested change is at least `patch`, `minor` or `major`; `none` (the default) never fails |
| `--framework` | the target framework to compare, such as `net8.0` (`files`, `nuget` and `release`); defaults to the newest the packages share |

To compare with an older commit, check it out and build it next to your working copy, then compare the two builds:

```sh
git worktree add ../old v1.2.0 && dotnet build ../old -c Release
assembly-differ files ../old/src/MyLib/bin/Release/net8.0 src/MyLib/bin/Release/net8.0
```

In a solution, `nuget` shows only the changes to the types and members your built projects reference: a removed
method you call, not the hundred you don't. It reads that from your build output, so build first; without a build it
shows every change, with a warning, and `--all` always does. Members match by name, and a constant you use does not show,
because the compiler copies its value into your code.

Packages download into NuGet's global packages folder, as `dotnet restore` does, so a second run downloads nothing.
What the tool unpacks and copies to compare goes into a scratch folder of the run's own, under the system's temporary
folder, and is deleted when the run ends; set `ASSEMBLY_DIFFER_KEEP_SCRATCH=1` to keep it.

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

# what updating Polly from the version this solution resolves to 8.5.0 would change for this code
dotnet build && assembly-differ nuget Polly 8.5.0

# two published packages, as Markdown
assembly-differ nuget Proc 0.13.0 0.14.0 -t proc

# the release before 1.3.0 against a build directory
assembly-differ release src/MyLib/bin/Release/net8.0 --package MyLib --version 1.3.0 -t MyLib
```

### On a pull request

`pull-request` puts a report on the pull request a GitHub Actions run is for. Write the report with `--format json`
first:

```sh
assembly-differ release build/output/MyLib.1.3.0.nupkg -f json -o build/output
assembly-differ pull-request build/output/api-changes.json
```

It writes the job summary, keeps one comment on the pull request up to date, and sets the release label the suggested
change calls for: `breaking` for `Major` and `feature` for `Minor`, the labels release-drafter turns into the next
version (`--major-label`, `--minor-label` and `--patch-label` rename them). It records the labels it set in its comment,
and a later run removes only those, never a label a person added. It needs `GITHUB_TOKEN` with `pull-requests: write`.
Without write access, as on a pull request from a fork, it writes the job summary, warns and succeeds.

With `--fail-on major`, a breaking change fails the check, and the comment says how to go ahead: add the
`breaking-allowed` label (`--allow-label` renames it). Adding the label runs the workflow again, provided it runs on
the `labeled` and `unlabeled` events, and the check passes with the change labelled `breaking`. So a breaking change is
caught, labelled for release-drafter, and merged only once someone says it is intended.

### Provider specs

Before it had a command per source, the tool took two provider specs, and `diff` still does, unchanged, for the builds
that call it: `assembly-differ diff "previous-nuget|MyLib|1.3.0|net8.0" "directory|bin" -t MyLib -p major`. A spec is
`assembly|<path>`, `directory|<path>`, `nuget|<id>|<version>|[tfm]`, `previous-nuget|<id>|<version>|[tfm]` or
`github|<owner/repo>|<commit>|<build command>|<relative output path>`; `github` is deprecated in favour of the `git
worktree` recipe above. Its options are `-t`, `-o`, `-f` (default
`xml`), `-p`/`--prevent-change` in place of `--fail-on`, and `-a`/`--allow-empty-previous-nuget`, which succeeds when
`previous-nuget` finds no earlier release.

### GitHub Action

On every pull request, report what it changes in your libraries' public API, label it for release-drafter, and fail
on a breaking change until someone adds the `breaking-allowed` label:

```yaml
name: api-changes
on:
  pull_request:
    # labeled and unlabeled run the check again when someone adds or removes breaking-allowed
    types: [opened, synchronize, reopened, labeled, unlabeled]

permissions:
  contents: read
  pull-requests: write # the comment and the labels

jobs:
  api-changes:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x
      - run: dotnet build -c Release
      - uses: nullean/assembly-differ@v1
```

Without `package`, the action compares every library project of the solution with the latest release of its package,
the way running the tool without a command does. Give `package: build/output/MyLib.1.3.0.nupkg` to compare a packed
package with the release before it instead. It installs the tool with `dotnet tool`, so it needs the .NET 10 SDK or
later on the runner, the one your build uses.

| Input | Default | What |
|---|---|---|
| `package` | | a `.nupkg` to compare with the release before it; empty compares the solution's libraries |
| `projects` | | project files to compare instead, one per line |
| `target` | | only these assemblies, one per line |
| `framework` | | the target framework to compare |
| `fail-on` | `major` | fail when the suggested change is at least this, unless the pull request has `allow-label`; `none` never fails |
| `allow-label` | `breaking-allowed` | the label that lets a change at `fail-on` through |
| `major-label`, `minor-label`, `patch-label` | `breaking`, `feature`, none | the release label for each suggested change |
| `comment`, `labels` | `true` | keep the comment, set the labels |
| `working-directory` | `.` | where the solution is |
| `token` | `github.token` | needs `pull-requests: write` |
| `version` | latest | the assembly-differ version to install |

Outputs: `suggested-change` (`None`, `Patch`, `Minor` or `Major`), `breaking-changes`, and `report`, the path of
`api-changes.json`.

On a pull request from a fork the token cannot write, so the action writes the job summary and still applies
`fail-on`, but leaves the comment and labels alone. The deprecated `first`, `second` and `args` inputs still run `diff`
with two provider specs in the container image, as the action did before.

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
| [`assembly-differ.skills`](src/assembly-differ.skills/README.md) | the agent skill for the tool, content only; a development dependency | [![NuGet](https://img.shields.io/nuget/v/assembly-differ.skills?style=flat-square)](https://www.nuget.org/packages/assembly-differ.skills) |
| `assembly-differ.any` | the framework-dependent fallback (net11.0) for every other platform | [![NuGet](https://img.shields.io/nuget/v/assembly-differ.any?style=flat-square)](https://www.nuget.org/packages/assembly-differ.any) |

## Learn more

- [Releases](https://github.com/nullean/assembly-differ/releases)
- [Contributing](https://github.com/nullean/.github/blob/main/CONTRIBUTING.md)

assembly-differ's comparison is a port of [JustAssembly](https://github.com/telerik/JustAssembly), licensed under Apache 2.0.

MIT licensed. Copyright Nullean and contributors.
