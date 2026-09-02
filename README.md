<p>
<img align="right" src="nuget-icon.png">  

# assembly-differ
</p>

Compare and Diff assemblies from different sources.
Useful for determining what changes are introduced across versions, and if any are _breaking_.

Outputs differences in XML, Markdown or AsciiDoc. 

Differ builds on the amazing work done by [JustAssembly, licensed under Apache 2.0](https://github.com/telerik/JustAssembly)

## Installation


Distributed as a .NET tool so install using the following

```
dotnet tool install assembly-differ
```

On Linux, Windows and macOS/arm64, this resolves to a self-contained native-AOT executable — no
shared .NET runtime required, and no first-run JIT warmup. Everywhere else, it falls back to a
framework-dependent build (requires the .NET runtime the tool targets to already be installed).

## Run 

```bat
dotnet assembly-differ
```

You can omit `dotnet` if you install this as a global tool


to see the supported Assembly Providers and outputs:

```bat
assembly-differ diff <Old Assembly Provider> <New Assembly Provider> [Options]

Supported Assembly Providers:

  assembly|<assembly path>
  directory|<directory path>
  nuget|<package id>|<version>|[framework version]
  previous-nuget|<package id>|<version>|[framework version]
  github|<owner>/<repo>|<commit>|<build command>|<relative output path>

Options:
  -t, --target <values>              the assembly targets. Defaults to *all* assemblies
                                      located by the provider. May be given more than once.
  -f, --format <string>              the format of the diff output. Supported formats
                                      are xml, markdown, asciidoc. Defaults to xml
  -o, --output <string>              the output directory or file name. If not specified only prints to console
  -p, --prevent-change <string>      fail if the change detected is higher than specified:
                                      none, patch, minor, or major. Defaults to none
  -a, --allow-empty-previous-nuget   don't fail when no previous nuget package could be found to diff against
  -h, --help                         show this message and exit
```

> [!NOTE]
> Starting from `1.0.0`, the tool moved off `Mono.Options` onto [`Nullean.Argh`](https://github.com/nullean/argh)
> for CLI parsing, which introduces an explicit `diff` subcommand alongside the bare invocation. `diff` is
> optional as long as an option comes before the two provider arguments (e.g.
> `assembly-differ --target NEST "nuget|..." "nuget|..."`); providers as the very first arguments
> (`assembly-differ "nuget|..." "nuget|..."`) still need the explicit subcommand
> (`assembly-differ diff "nuget|..." "nuget|..."`), since the CLI parser resolves a bare leading argument
> as a subcommand name first. `--target` also moves from a single comma/pipe-separated value to a
> repeatable flag (`--target a --target b` instead of `--target a,b`).

#### Examples:

Diff between two local assemblies:

```bat
dotnet assembly-differ diff "assembly|C:\6.1.0\Nest.dll" "assembly|C:\6.2.0\Nest.dll"
```

Diff between all assemblies in directories, matched by name:

```bat
dotnet assembly-differ diff "directory|C:\6.1.0" "directory|C:\6.2.0"
```

Diff NuGet packages:

```bat
dotnet assembly-differ diff "nuget|NEST|6.1.0|net46" "nuget|NEST|6.2.0|net46"
```

Diff Previous NuGet packages:

Imagine you want to release `6.2.0` and want to diff with whatever is the latest nuget package before `6.2.0`
`previous-nuget` will do the heavy lifting of finding that previous release

```bat
dotnet assembly-differ diff "previous-nuget|NEST|6.2.0|net46" "directory|C:\6.2.0" 
```

Diff GitHub commits:

```bat
dotnet assembly-differ diff "github|elastic/elasticsearch-net|6.1.0|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46" "github|elastic/elasticsearch-net|6.2.0|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46"
```

Any of the above can be mixed. For example, to compare GitHub HEAD against last NuGet package, and output in Markdown — note that leading with an option lets you drop the `diff` subcommand:

```bat
dotnet assembly-differ --format markdown "nuget|NEST|6.2.0|net46" "github|elastic/elasticsearch-net|HEAD|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46"
```

## Development

You can run the tool locally against itself using the following during development

```bat
dotnet build -c Release
dotnet run -f net10.0 -- diff "previous-nuget|assembly-differ|0.9.1|net10.0" "directory|bin/Release/net10.0" --target assembly-differ
```

# FUTURE PLANS

* Instruct the tool to emit errors if breaking changes exists
* Pass the tool with the version you intend to release and have the tool report the version it thinks it should be based on the differences between the assemblies
* Wrap all of this in Github Actions

