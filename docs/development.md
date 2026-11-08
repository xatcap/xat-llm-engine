# Development

## Build and test

This needs the .NET SDK version in `global.json`, and Python 3.12 or newer for the release script.

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

Each project has a `packages.lock.json` file that pins its NuGet packages. CI restores with `--locked-mode`, so it fails when a lock file is out of date. After you change a package version or the SDK, update the lock files with `dotnet restore XatLlmEngine.slnx --force-evaluate`, and commit them.

Each change that people can see needs an entry under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md).

### Update the API document

[docs/openapi.json](openapi.json) is the API document of the code. A test fails when the code and the file are different. After a change to the API, update the file, and then review the change:

```bash
UPDATE_OPENAPI=1 dotnet test --solution XatLlmEngine.slnx
git diff docs/openapi.json
```

A change in this file is a change to the contract. [ADR 0005](adr/0005-versioning-policy.md) tells which changes are breaking.

## Architecture

The code follows Clean Architecture, as described in [portable/dotnet/clean-architecture.md](portable/dotnet/clean-architecture.md). Decisions and their reasons are in [adr](adr). All text follows the rules in [writing-style.md](portable/writing-style/writing-style.md).

```text
src/XatLlmEngine.Domain          Core types and rules, with no dependencies
src/XatLlmEngine.Application     Use cases, and the IChatProvider port
src/XatLlmEngine.Infrastructure  Cloud adapters, and llama-server supervision
src/XatLlmEngine.Host            The API and the composition root
tests/                           One test project per layer, plus architecture tests
build/                           Pinned versions and the release script
```

## Debug with Swagger

In the Development environment, the API serves Swagger UI at `/swagger`. The OpenAPI document is at `/openapi/v1.json`.

Release packages run as Production, so these routes return 404 there. To show them in a package, start it with `ASPNETCORE_ENVIRONMENT=Development`.

`dotnet run --project src/XatLlmEngine.Host` uses the `Debug` profile in `src/XatLlmEngine.Host/Properties/launchSettings.json`. That profile sets Development and listens on `http://127.0.0.1:8420`. Debugging in an IDE uses the same profile and opens Swagger UI in a browser.

Swagger UI shows a streamed answer only after the stream has finished. To watch a stream as it arrives, use `curl` or a client library.

## Build a package locally

```bash
python3 build/release.py fetch-model --output artifacts/cache
python3 build/release.py package --rid osx-arm64 --version 0.0.0-local
python3 build/release.py smoke-test --rid osx-arm64 \
  --package artifacts/xat-llm-engine-0.0.0-local-osx-arm64.tar.gz \
  --model artifacts/cache/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf
```

The smoke test starts the engine as an app does. The system chooses the port, and the test reads it from the ready file. The test stops the engine when it closes the engine's input. Then it checks that the ready file and `llama-server` are gone.

Set the `DOTNET` environment variable if `dotnet` on your `PATH` is not the right SDK.

## Make a release

Versions follow Semantic Versioning, as [ADR 0005](adr/0005-versioning-policy.md) describes. A breaking change needs a new major version.

1. In [CHANGELOG.md](../CHANGELOG.md), rename `## [Unreleased]` to `## [x.y.z] - YYYY-MM-DD`.
2. Add a new empty `## [Unreleased]` section above it.
3. Push the tag `vx.y.z`. This starts `.github/workflows/release.yml`.

Then the release workflow runs:

1. The workflow runs all tests on all platforms. It stops if a test fails, or if `CHANGELOG.md` has no section for the version.
2. It builds each platform package, and runs each one with the real model.
3. It publishes only after every check passes and every file is on the release. The CHANGELOG section becomes the release notes.

The workflow also runs `build/release.py openapi`, which attaches `docs/openapi.json` with the release version. Then it runs `build/release.py finalize`. It checks that each file is under GitHub's 2 GiB limit, and it writes `SHA256SUMS`.

To check the changelog section before you push the tag, run `python3 build/release.py check-changelog --version x.y.z`.

To test without publishing, run the workflow by hand from the Actions tab. That is a dry run. Its `create_draft` option also uploads the files to a draft release.

### Upgrade llama.cpp or the model

Change `build/llama-cpp.lock.json` or `build/model.lock.json`. The model must stay under GitHub's limit of 2 GiB per file.

## Third-party notices

When you add or update a NuGet package that the engine ships, add it to [build/third-party.json](../build/third-party.json) with its license files. Also add it to [build/THIRD_PARTY_NOTICES.md](../build/THIRD_PARTY_NOTICES.md). `python3 build/release.py check-notices` checks this, and CI runs it.
