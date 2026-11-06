# 0005. Use Semantic Versioning, a changelog and locked packages

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

People start the engine by hand, and desktop apps include it and start it. These callers must know when an update can break them.

Before this decision, the release notes came only from the GitHub pull request titles. No rule said which changes need a new major version. A tag could also start a release from a commit that failed its tests. The NuGet packages were not pinned, so two builds of the same commit could use different transitive packages.

## Decision

Release tags use [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). The form is `vMAJOR.MINOR.PATCH`. A pre-release label is optional, for example `v2.0.0-rc.1`.

A change is **breaking** when it can stop a caller or an installation that worked before. These changes are breaking:

- A change that callers can see in one of these:
  - a `/v1` route,
  - a request field,
  - a response field,
  - a status code, or
  - an error `type`.
- A change to `/health` that removes or renames a field or a value.
- A change to a setting that removes or renames it, or that makes a working configuration fail at startup.
- A change to the files and folders of the package that people use.

A breaking change increases the MAJOR number. A new feature that is not breaking increases the MINOR number. A fix that is not breaking increases the PATCH number. A new value in a list of values, such as a new health state, is not breaking. Callers must accept values that they do not know.

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each change adds an entry under `## [Unreleased]`. Each breaking entry starts with **Breaking**. Before a tag, the maintainer renames that section to `## [x.y.z] - YYYY-MM-DD`.

The release workflow:

1. Runs all tests on all platforms. The packages are built only after the tests pass.
2. Stops if `CHANGELOG.md` has no section for the tag (`build/release.py check-changelog`).
3. Uses that section as the release notes.

Each project has a `packages.lock.json` file. CI and the release restore in locked mode, so every NuGet package, transitive packages included, is pinned by its content hash.

## Amendment (2026-11-06)

Desktop apps now include the engine and start it. These apps need a stable contract and time to change their code.

- **More contracts.** These items are also part of the contract, with the same rules for a breaking change:
  - the fields of the ready file (`url`, `pid` and `version`), and
  - the settings `Engine:ReadyFile` and `Engine:StopWhenInputCloses`.
  A new field in the ready file is not breaking. Callers must accept fields that they do not know.
- **Deprecation.** Before a removal, the engine marks an item as deprecated for at least one minor version. The CHANGELOG marks the item with **Deprecated**, and tells callers what to use in its place. The removal is then a breaking change in a later major version.
- **The API document.** `docs/openapi.json` is the API document of the code. A test fails when the code and the file are different, so each change to the contract shows in review. Each release attaches the file with the release version.

## Consequences

- Callers can read the version to know whether an update can break them.
- The release notes say what changed for the people who use the engine.
- A release needs one more step: the CHANGELOG section for its version.
- A release takes longer, because it runs the tests first.
- A package update or an SDK update must also update the lock files, with `dotnet restore --force-evaluate`.

## Alternatives considered

- **Release notes from GitHub only:** rejected because pull request titles do not mark breaking changes.
- **Calendar versions:** rejected because they do not tell callers whether an update can break them.
- **A release that depends on the CI result of the tagged commit:** rejected because the release workflow cannot easily read the result of another workflow. Running the tests again is simpler and also covers a tag on a commit that CI never ran.
