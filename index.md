# Project Documentation Index

This file is an index to the project-specific documentation, build commands, and release files in this repository.

## Project documentation

| Page | What it covers |
|------|----------------|
| [README.md](README.md) | The goal of the engine, who it is for, what it is not, a quick start, and links to the other pages. |
| [docs/quickstart.md](docs/quickstart.md) | Download a release, prepare its folder, and start the API. |
| [docs/api.md](docs/api.md) | Routes, request templates, content rules, streams, errors, health and supported models. |
| [docs/architecture.md](docs/architecture.md) | Architecture diagram, architectural layers, interactions between layers and components, and flow of data. |

## Build and test

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

## Build and release files

| File | What it covers |
|------|----------------|
| [CHANGELOG.md](CHANGELOG.md) | The changes in each release. Add an entry under `Unreleased` for each change that people can see. |
| [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) | Third-party software in the release packages, and their licenses. |
| [build/third-party.json](build/third-party.json) | The list of shipped third-party components and their license files, which `release.py check-notices` checks. |
| [build/release.py](build/release.py) | The release script: `package`, `fetch-model`, `smoke-test`, `openapi`, `finalize`, `check-changelog` and `check-notices`. |
| [build/llama-cpp.lock.json](build/llama-cpp.lock.json) | The pinned llama.cpp build. |
| [build/model.lock.json](build/model.lock.json) | The pinned local model file. |
| [.github/workflows/ci.yml](.github/workflows/ci.yml) | The CI build and tests. |
| [.github/workflows/release.yml](.github/workflows/release.yml) | The release workflow. |

