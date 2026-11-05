# 0003. Attach the model as a separate release file

- Status: Accepted
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

Each release must ship llama.cpp and a local model. GitHub requires each release file to be under 2 GiB. The chosen model, Ministral 3 3B Instruct Q4_K_M, is 2,147,023,008 bytes, about 0.44 MiB under that limit.

## Decision

Each release has one self-contained package per platform (macOS arm64, Linux x64, Windows x64), with llama.cpp in its `llama/` folder. The model is one separate file on the release, not part of any package. The engine never downloads anything: users download the package and the model, and place the model in `models/`.

`build/llama-cpp.lock.json` and `build/model.lock.json` pin every input by SHA-256. The release workflow runs each package with the real model before it publishes anything. The release stays a draft until all files are uploaded.

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
