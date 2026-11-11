# assembly-differ

![nullean](https://raw.githubusercontent.com/nullean/nullean.github.io/main/assets/nullean-lockup-544x192.png)

A .NET tool that diffs the public API of two versions of your assemblies, from disk, NuGet or a git commit, and suggests
the version change the difference needs.

[![NuGet](https://img.shields.io/nuget/v/assembly-differ?style=flat-square)](https://www.nuget.org/packages/assembly-differ)

## Install

```sh
dotnet tool install --global assembly-differ
```

On Linux (x64, arm64), Windows (x64, arm64) and macOS (arm64) this installs a native-AOT executable from the
`assembly-differ.<rid>` package; everywhere else it falls back to the framework-dependent `assembly-differ.any`. Install
the `assembly-differ` package itself, not those.

## Quick start

Diff the last release on nuget.org against what you just built, and fail on a breaking change:

```sh
assembly-differ diff "previous-nuget|MyLib|1.3.0|net8.0" "directory|src/MyLib/bin/Release/net8.0" -t MyLib -f markdown -p major
```

A diff of Proc 0.13.0 against 0.14.0, which replaced its `Start*` overloads, ends with:

```console
      <Method Name="Start(StartArguments, CancellationToken)" DiffType="New" />
      <Method Name="Start(StartArguments)" DiffType="Deleted" />
...
[diff] Suggested version change: Major
```

| Spec | Assemblies |
|---|---|
| `assembly\|<path>` | one dll |
| `directory\|<path>` | every dll in a directory, matched by file name |
| `nuget\|<id>\|<version>\|[tfm]` | a package from the NuGet sources your NuGet.config lists |
| `previous-nuget\|<id>\|<version>\|[tfm]` | the release on nuget.org before `<version>` |
| `github\|<owner/repo>\|<commit>\|<build command>\|<relative output path>` | a commit, cloned and built |

| Flag | What |
|---|---|
| `-t`, `--target` | only diff these assemblies (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `xml` (default), `markdown`, `asciidoc` or `github-comment` |
| `-o`, `--output` | output directory or file; without it the diff only goes to the console |
| `-p`, `--prevent-change` | exit with `4` when the suggested change is at least `patch`, `minor` or `major` |
| `-a`, `--allow-empty-previous-nuget` | succeed when `previous-nuget` finds no earlier release (a first release) |

It also runs as a GitHub Action (`uses: nullean/assembly-differ@main`) and as a container
(`ghcr.io/nullean/assembly-differ`), both without a .NET SDK.

## Learn more

- Source, GitHub Action and container usage: https://github.com/nullean/assembly-differ
- Releases: https://github.com/nullean/assembly-differ/releases
