---
name: breaking-changes
description: Decide whether a change is breaking and label it. Use when labelling a PR, when a change touches public API or observable behaviour, or when the user mentions breaking changes, semver, or 1.0.
---

# Breaking changes

Every PR carries exactly one release label (the `pr-labels` check enforces it). release-drafter turns the labels into
the next version.

## Is it breaking?

Label it `breaking` when a consumer who upgrades must change something or gets different behaviour:

- a public type, member, parameter or overload is removed, renamed or changes signature;
- a default, an output format, an exit code or a thrown exception changes;
- a target framework is dropped, or a minimum dependency version rises enough to force consumers to upgrade.

These are not breaking: adding API, internal changes, fixing behaviour that contradicted the documentation (label
that `bug` and say so in the PR body).

If package validation (`EnablePackageValidation`) fails on `mise run pack`, the change is breaking at the binary
level, whatever it looks like.

## What the label does

| Repo version | `breaking` | `feature` / `enhancement` | everything else |
|---|---|---|---|
| 0.x | minor | minor | patch |
| 1.x and later | major | minor | patch |

0.x never gets a major bump from a label. **Going to 1.0 is always the maintainer's manual decision**: they set the
draft's tag to `1.0.0` when publishing. Do not propose it, and do not edit the resolver on your own.

When the maintainer has released 1.0.0, add a major resolver to `.github/release-drafter.yml` (and nothing else) in
its own PR:

```yaml
version-resolver:
  major:
    labels: [breaking]
  minor:
    labels: [feature, enhancement]
  default: patch
```

## In the PR body

A `breaking` PR states what a consumer must change, in a **Breaking** line (see the pr skill).
