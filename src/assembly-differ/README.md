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

In a repository, build it, then run the tool without a command to compare every library project with its latest
release:

```sh
dotnet build -c Release && assembly-differ
```

Compare the package you just packed with its last release on nuget.org, and fail on a breaking change:

```sh
assembly-differ release build/output/MyLib.1.3.0.nupkg --fail-on major
```

`assembly-differ nuget Proc 0.13.0 0.14.0 -f xml`, a diff of two versions where 0.14.0 replaced the `Start*`
overloads, ends with:

```console
      <Method Name="Start(StartArguments, CancellationToken)" DiffType="New" />
      <Method Name="Start(StartArguments)" DiffType="Deleted" />
...
[diff] Suggested version change: Major
```

| Command | Compares |
|---|---|
| `release <path>` | a `.nupkg`, `.dll` or directory with the release before it on your NuGet.config feeds; a first release succeeds |
| `files <old> <new>` | two local builds: `.dll`s, directories or `.nupkg`s |
| `nuget <id> [<version>] [<new-version>]` | in a solution, what updating a package would change; anywhere, two published versions |
| `projects` | every library project in the directory against its latest release; what runs without a command |

| Option | What |
|---|---|
| `-t`, `--target` | only compare these assemblies (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `markdown` (default), `xml`, `asciidoc`, `github-comment` or `json` |
| `-o`, `--output` | a directory or file to write the report to, besides the console |
| `--fail-on` | exit with `4` when the suggested change is at least `patch`, `minor` or `major` |

`diff` with provider specs, such as `"nuget|Proc|0.13.0"`, still works as before for the builds that call it.

Coding agents can use it instead of decompiling assemblies: in a solution, `assembly-differ nuget X 2.0.0` shows what
updating X would break in the code it builds. The `assembly-differ.skills` package carries an agent skill for it.

It also runs as a GitHub Action (`uses: nullean/assembly-differ@v1`), which comments on and labels pull requests, and as a
container (`ghcr.io/nullean/assembly-differ`) that needs no .NET SDK.

## Learn more

- Source, GitHub Action and container usage: https://github.com/nullean/assembly-differ
- Releases: https://github.com/nullean/assembly-differ/releases
