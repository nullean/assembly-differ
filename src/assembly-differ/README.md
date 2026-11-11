# assembly-differ

A .NET tool that compares the public API of two versions of a set of assemblies, writes the differences and suggests
the version change they need: none, patch, minor or major.

```bash
dotnet tool install --global assembly-differ
assembly-differ diff "nuget|MyLib|1.2.0|net8.0" "directory|src/MyLib/bin/Release/net8.0" -t MyLib -f markdown -o out
```

## Where each side comes from

| Spec | Assemblies |
|---|---|
| `assembly\|<path>` | one dll |
| `directory\|<path>` | every dll in a directory |
| `nuget\|<id>\|<version>\|[tfm]` | a package from the NuGet sources your NuGet.config lists |
| `previous-nuget\|<id>\|<version>\|[tfm]` | the release on nuget.org before `<version>` |
| `github\|<owner/repo>\|<commit>\|<build command>\|<relative output path>` | a commit, cloned and built |

## Options

| Flag | What |
|---|---|
| `-t`, `--target` | only diff these assemblies (repeatable); defaults to every assembly both sides have |
| `-f`, `--format` | `xml` (default), `markdown`, `asciidoc` or `github-comment` |
| `-o`, `--output` | output directory or file; without it the diff only goes to the console |
| `-p`, `--prevent-change` | exit with `4` when the suggested change is at least `patch`, `minor` or `major` |
| `-a`, `--allow-empty-previous-nuget` | succeed when `previous-nuget` finds no earlier release (a first release) |

On Linux, Windows and macOS (arm64) the tool installs as a native-AOT binary; elsewhere it falls back to a
framework-dependent build. It also runs as a container (`ghcr.io/nullean/assembly-differ`) and as a GitHub Action:
see https://github.com/nullean/assembly-differ.
