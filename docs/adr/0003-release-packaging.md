# 0003. Attach the model as a separate release file

- Status: Accepted
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

Each release must ship llama.cpp and a local model. GitHub requires each release file to be under 2 GiB. The chosen model, Ministral 3 3B Instruct Q4_K_M, is 2,147,023,008 bytes, about 0.44 MiB under that limit.

## Decision

Each release has one self-contained package for each platform:

- macOS arm64
- Linux x64
- Windows x64

Each package has llama.cpp in its `llama/` folder. The model is one separate file on the release, not part of any package. The engine never downloads anything. Users download the package and the model, and they place the model in `models/`.

`build/llama-cpp.lock.json` and `build/model.lock.json` pin llama.cpp and the model by SHA-256. A `packages.lock.json` file in each project pins every NuGet package by its content hash, and the build restores in locked mode. The release workflow runs all tests before it builds the packages. Then it runs each package with the real model before it publishes anything. The release stays a draft until all files are uploaded.

Each package has a `licenses/` folder. The folder holds the full license and notice texts of each third-party component that the package ships. These components are:

- llama.cpp and the libraries inside it
- the .NET runtime and ASP.NET Core, from the exact runtime packs of the build
- each NuGet package

`build/third-party.json` lists them. The release script stops when a shipped package is not in that list. It also stops when a text is missing. The smoke test checks that each text is in the package. (Added 2026-11-06.)

## Consequences

- A platform package stays small. The release stores the model once, not three times.
- The installation needs two downloads and one file move.
- The engine works offline, and it makes no surprise downloads.
- Each release stores a new copy of the 2 GiB model on GitHub.
- GitHub runners have no usable GPU, so CI proves CPU inference only. GPU use needs a manual check.
- The binaries have no signature, so macOS Gatekeeper and Windows SmartScreen show a warning at first use.
- A larger model would have to be split with `llama-gguf-split`, which also needs a change to this packaging.

## Alternatives considered

- **Include the model in each package:** rejected because each package would then be larger than the 2 GiB limit.
- **Download the model at first start:** rejected for two reasons. The model must be part of the release, and the first start would need internet access.
- **Qwen3 1.7B:** rejected by the maintainer in favour of a Mistral model.
