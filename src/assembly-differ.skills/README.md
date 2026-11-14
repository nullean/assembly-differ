# assembly-differ.skills

The agent skill for [assembly-differ](https://github.com/nullean/assembly-differ), the dotnet tool that compares the
public API of two versions of .NET assemblies. With it, a coding agent checks what a package update would break for
the code it is working on, or which version bump a library change needs, by running the tool instead of decompiling
assemblies.

```sh
dotnet tool install --global assembly-differ
dotnet add package assembly-differ.skills
```

`dotnet restore` puts the skill on the machine, in `skills/assembly-differ/SKILL.md` inside the package's folder of the
NuGet cache. Tools that load skills from package references, such as `dotnet-package-skills install`, copy it into
`.agents/skills`; you can also copy it there yourself. The package has no assembly and no dependencies, and it is a
development dependency, so it never flows to your own package's consumers.
