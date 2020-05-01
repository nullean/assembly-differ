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
dotnet tool install -g assembly-differ
```

## Run 

```bat
dotnet assembly-differ
```

You can omit `dotnet` if you install this as a global tool


to see the supported Assembly Providers and outputs:

```bat
assembly-differ <Old Assembly Provider> <New Assembly Provider> [Options]

Supported Assembly Providers:

  assembly|<assembly path>
  directory|<directory path>
  nuget|<package id>|<version>|[framework version]
  previous-nuget|<package id>|<version>|[framework version]
  github|<owner>/<repo>|<commit>|<build command>|<relative output path>

Options:
  -t, --target=VALUE         the assembly targets. Defaults to *all* assemblies
                               located by the provider
  -f, --format=VALUE         the format of the diff output. Supported formats
                               are xml, markdown, asciidoc. Defaults to xml
  -o, --output=VALUE         the output directory. Defaults to current directory
  -h, -?, --help             show this message and exit
```

#### Examples:

Diff between two local assemblies:

```bat
dotnet assembly-differ "assembly|C:\6.1.0\Nest.dll" "assembly|C:\6.2.0\Nest.dll"
```

Diff between all assemblies in directories, matched by name:

```bat
dotnet assembly-differ "directory|C:\6.1.0" "directory|C:\6.2.0"
```

Diff NuGet packages:

```bat
dotnet assembly-differ "nuget|NEST|6.1.0|net46" "nuget|NEST|6.2.0|net46"
```

Diff Previous NuGet packages:

Imagine you want to release `6.2.0` and want to diff with whatever is the latest nuget package before `6.2.0`
`previous-nuget` will do the heavy lifting of finding that previous release

```bat
dotnet assembly-differ "previous-nuget|NEST|6.2.0|net46" "directory|C:\6.2.0" 
```

Diff GitHub commits:

```bat
dotnet assembly-differ "github|elastic/elasticsearch-net|6.1.0|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46" "github|elastic/elasticsearch-net|6.2.0|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46"
```

Any of the above can be mixed. For example, to compare GitHub HEAD against last NuGet package, and output in Markdown

```bat
dotnet assembly-differ --format markdown "nuget|NEST|6.2.0|net46" "github|elastic/elasticsearch-net|HEAD|cmd /C call build.bat skiptests skipdocs|build\output\Nest\net46"
```

## Development

You can run the tool locally against itself using the following during development

```bat
dotnet build -c Release
dotnet run --framework netcoreapp3.1 -- "previous-nuget|assembly-differ|0.9.1|netcoreapp3.1" "directory|bin/Release/netcoreapp3.1" --target=assembly-differ
```

# FUTURE PLANS

* Instruct the tool to emit errors if breaking changes exists
* Pass the tool with the version you intend to release and have the tool report the version it thinks it should be based on the differences between the assemblies
* Wrap all of this in Github Actions

