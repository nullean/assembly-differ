---
name: upgrade-deps
description: Update NuGet packages, .NET tools, the SDK or GitHub Actions. Use when the user asks to bump, upgrade or update dependencies, or to triage dependabot PRs.
---

# Upgrade dependencies

Versions live in one place each:

| What | Where |
|---|---|
| NuGet packages | `Directory.Packages.props` (Central Package Management); never put `Version=` on a `PackageReference` |
| .NET tools | `dotnet-tools.json` |
| .NET SDK | `global.json` only; mise reads it from there |
| other CLI tools | `mise.toml` `[tools]` |
| GitHub Actions | pinned by commit SHA with a `# vX.Y.Z` comment; reusable workflows from `nullean/.github` likewise |

## Steps

1. Branch first. One concern per PR: SDK bumps, package bumps and action bumps go separately when any of them changes
   behaviour.
2. Find what is outdated: `dotnet list package --outdated`, plus `dotnet tool list` against nuget.org.
3. Bump, then `mise run ci`. A new SDK also needs `mise install`.
4. Read the release notes of every major bump and say in the PR body what changed for this repo.
5. Label the PR `dependencies` unless the bump forces consumers to change something; then it is `breaking` (see
   [breaking-changes](../breaking-changes/SKILL.md)).

For dependabot PRs: check CI, read the changelog for majors, merge groups that pass. Do not rebase a dependabot PR by
hand; comment `@dependabot rebase`.

Pin an action to the commit its release tag points at:
`gh api repos/<owner>/<action>/commits/<tag> --jq .sha`.
