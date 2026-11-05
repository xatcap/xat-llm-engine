# 0003. Attach the model as a separate release file

- Status: Accepted
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

Each release must ship llama.cpp and a local model. GitHub requires each release file to be under 2 GiB. The chosen model, Ministral 3 3B Instruct Q4_K_M, is 2,147,023,008 bytes, about 0.44 MiB under that limit.

## Decision

Each release has one self-contained package per platform (macOS arm64, Linux x64, Windows x64), with llama.cpp in its `llama/` folder. The model is one separate file on the release, not part of any package. The engine never downloads anything: users download the package and the model, and place the model in `models/`.

Every input is pinned by SHA-256 in `build/llama-cpp.lock.json` and `build/model.lock.json`. The release workflow runs each package with the real model before it publishes anything, and keeps the release as a draft until all files have been uploaded.

## Consequences

- A platform package stays small, and the model is stored once rather than three times.
- Installation needs two downloads and one file move.
- The engine works offline, and it makes no surprise downloads.
- Each release stores a new copy of the 2 GiB model on GitHub.
- GitHub runners have no usable GPU, so CI proves CPU inference only. GPU use needs a manual check.
- The binaries are unsigned, so macOS Gatekeeper and Windows SmartScreen warn on first use.
- A larger model would have to be split with `llama-gguf-split`, which also needs a change to this packaging.

## Alternatives considered

- **Including the model in each package:** rejected because adding the model would push each package past the 2 GiB limit.
- **Downloading the model on first start:** rejected because the requirement is that the model ships with the release, and first start would then need internet access.
- **Qwen3 1.7B:** rejected by the maintainer in favour of a Mistral model.
