---
name: assembly-differ
description: Find out what a .NET package update or a library change does to a public API without decompiling anything. Use before updating a NuGet package (what breaks for this code), after changing a library's public API (which version bump it needs), and when reviewing such a pull request. Runs the assembly-differ dotnet tool and reads its json report.
---

# assembly-differ

`assembly-differ` compares the public API of two versions of .NET assemblies and suggests the version change semantic
versioning calls for: `None`, `Patch`, `Minor` or `Major`. Use it instead of decompiling assemblies or reading release
notes to work out what changed: it reads the metadata directly, and in a solution it can narrow the answer down to the
API this code actually uses.

Install it once, if `assembly-differ --version` fails:

```bash
dotnet tool install --global assembly-differ
```

## Which command

| Question | Command, run in the solution directory |
|---|---|
| What would updating package X to 2.0.0 break here? | `dotnet build && assembly-differ nuget X 2.0.0` |
| What would updating X to its latest release break here? | `dotnet build && assembly-differ nuget X` |
| What did my changes do to this repo's libraries since their last release? | `dotnet build -c Release && assembly-differ` |
| What does this packed package change since the release before it? | `assembly-differ release path/to/MyLib.1.3.0.nupkg` |
| What changed between two published versions? | `assembly-differ nuget X 1.0.0 2.0.0` |
| What changed between two local builds? | `assembly-differ files old/MyLib.dll new/MyLib.dll` |

Every command takes `-f json -o <dir>` to write `api-changes.json`, `-t <assembly name>` to compare only some
assemblies, and `--fail-on <none|patch|minor|major>` to exit with code 4 at or above a change; `files`, `nuget` and
`release` take `--framework <tfm>` to pick a target framework.

## Rules that matter

- **Build first.** `assembly-differ` never builds. Without a command it compares Release builds and skips a project
  without one, with a warning. `nuget X` in a solution reads `obj/project.assets.json` (restore first) and the build
  output (build first).
- **`nuget X` in a solution shows only what this code uses**: removed or changed types and members the built projects
  reference, and new abstract or `required` members on types they use. That is the answer to "what breaks for me".
  Add `--all` to see every change. Members match by name, so a change to one overload shows when another is used. A
  constant the code uses does not show, because the compiler copies its value in; a removed constant still breaks the
  next build.
- **Test projects and projects with `IsPackable` false are skipped** without a command; `--include-tests`,
  `--include-unpackable` or `--project <path>` change that.
- **A package with no earlier release is a first release**: nothing to compare, exit code 0 (`--require-baseline`
  fails instead).

## Reading the result

Prefer `-f json`: it is stable and complete. The document has `suggestedVersionChange`, `breakingChanges` (the count of
changes that on their own call for a major version), `baseline` (the release compared with, for `release`),
`firstRelease`, and `assemblies[].changes`, a tree of changes:

```json
{ "kind": "Method", "name": "Draw()", "change": "removed", "suggestedVersionChange": "Major" }
```

`kind` is `Type`, `Method`, `Property`, `Field`, `Event`, `Attribute`, `AssemblyReference` or `Declaration` (a change in
a member's declaration, such as "Member type changed from Int32 to Int64."), `change` is `added`, `removed` or
`modified`, and nested changes sit under `changes`. A new abstract or `required` member has `"addsObligation": true`.

Exit codes: `0` success, `1` an error (the message says what to do), `2` invalid arguments, `4` the change reached
`--fail-on`.

## Acting on it

- Before updating a package: when `breakingChanges` is 0 for the code's own usage, the update is safe to make; otherwise
  fix each listed member's call sites as part of the update.
- After changing a library: the suggested change is the version bump to release with. A `Major` change needs a
  `breaking` release label (release-drafter) or a deliberate decision; say so in the pull request.
- Report what you ran and what it said; do not claim an update is safe without the run.
