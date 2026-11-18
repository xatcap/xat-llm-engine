<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/xatcap-logo-dark.svg">
    <img src="docs/assets/xatcap-logo.svg" alt="The xatcap logo: a straw sun hat with a ribbon bow" width="160">
  </picture>
</p>

# xat-llm-engine

An LLM engine that you put inside your desktop app. It gives your app one local API for offline models, with strict contracts.

## Who it is for

The engine is for developers of desktop apps who want these things:

- AI features that work offline, with small local models.
- Search query expansion, with a second small local model.
- No extra installation for the users of the app. The engine and the model go inside the app's installer.

## Why use it

- **It goes inside your app.** One folder with no runtime to install. Your app starts it, finds its address, and stops it.
- **It works offline first.** The local model runs on the user's machine. The engine sends no telemetry and never downloads files.
- **It never retries a request.** One request to the engine is at most one request to a local model.
- **It fails in a predictable way.** Each kind of failure has one status code and one error type. The health values are stable, so your app can show the right message.
- **It does not break your app on an update.** The engine follows Semantic Versioning, and each release has its API document.

## What it is not

- It is not a model library. It runs two local models: one for chat and one for query expansion.
- It is not a hosted gateway or a team server. It accepts requests only from programs on the same machine.
- It does not do tool calling, embeddings, images or audio.

## How it compares

These tools are good at different jobs:

| Choose | When you want |
|---|---|
| [Ollama](https://ollama.com) | Many local models, which the user installs and manages in a separate app. |
| [LiteLLM](https://github.com/BerriAI/litellm) | A gateway to many providers, for a team or a server. |
| [OpenRouter](https://openrouter.ai) | One hosted API to many cloud models. |
| xat-llm-engine | Local models inside your own desktop app, with strict contracts. |

## Quick start

1. Download the engine package for your platform and the model file from the same [GitHub release](https://github.com/xatcap/xat-llm-engine/releases).
2. Unpack the engine, and move the model file into its `models/` folder.
3. Start `XatLlmEngine.Host` (`XatLlmEngine.Host.exe` on Windows).
4. Send a request:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}'
```

The model takes a little while to load. [Quick start](docs/quickstart.md) has the full steps and the notes for macOS, Windows and Linux.

## Query expansion

The engine has a second model, Qwen3 0.6B, that writes other search queries for one search query. The release package already holds this model. Send a search query to `POST /v1/query-expansion`. The engine returns other queries for the same search.

```bash
curl http://127.0.0.1:8420/v1/query-expansion \
  -H "Content-Type: application/json" \
  -d '{"query":"how to reset a router","count":3}'
```

The answer holds a list named `queries`, and the name of the model:

```json
{"queries":["...","...","..."],"model":"query-expansion/qwen3-0.6b"}
```

The field `count` is optional. The default is 3, and the largest value is 10. The [API reference](docs/api.md#query-expansion) has the full contract.

## Privacy

The engine sends no telemetry. At the default log level, its logs never hold prompts or answers.

## Documentation

| Page | What it covers |
|---|---|
| [Quick start](docs/quickstart.md) | Download a release, prepare its folder, and start the API. |
| [API reference](docs/api.md) | Routes, query expansion, request templates, streams, errors, health and compatibility. |
| [Architecture](docs/architecture.md) | Architecture diagram, layers, component interactions and data flow. |
| [CHANGELOG.md](CHANGELOG.md) | The changes in each release. |

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for the third-party software and the model. Each package has the full license and notice texts in its `licenses/` folder.
