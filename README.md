<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/xatcap-logo-dark.svg">
    <img src="docs/assets/xatcap-logo.svg" alt="The xatcap logo: a straw sun hat with a ribbon bow" width="160">
  </picture>
</p>

# xat-llm-engine

An LLM engine that you put inside your desktop app. It gives your app one local API for a bundled offline model and for cloud models, with strict contracts and cost limits.

## Who it is for

The engine is for developers of desktop apps who want these things:

- AI features that work offline by default, with a small local model.
- Cloud models from OpenAI or Mistral when the app needs more, with a limit on the output tokens of each request.
- No extra installation for the users of the app. The engine and the model go inside the app's installer.

## Why use it

- **It goes inside your app.** One folder with no runtime to install. Your app starts it, finds its address, and stops it.
- **It works offline first.** The local model runs on the user's machine. The engine sends no telemetry and never downloads files.
- **It limits the cost of cloud models.** The engine never retries a request. Each provider can have a token limit for each request.
- **It fails in a predictable way.** Each kind of failure has one status code and one error type. The health values are stable, so your app can show the right message.
- **It does not break your app on an update.** The engine follows Semantic Versioning, and each release has its API document.

## What it is not

- It is not a model library. It runs one local model at a time.
- It is not a hosted gateway or a team server. It accepts requests only from programs on the same machine.
- It does not do tool calling, embeddings, images or audio.

## How it compares

These tools are good at different jobs:

| Choose | When you want |
|---|---|
| [Ollama](https://ollama.com) | Many local models, which the user installs and manages in a separate app. |
| [LiteLLM](https://github.com/BerriAI/litellm) | A gateway to many providers, for a team or a server. |
| [OpenRouter](https://openrouter.ai) | One hosted API to many cloud models. |
| xat-llm-engine | One local model and a few cloud providers inside your own desktop app, with strict contracts and cost limits. |

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

## Cloud providers

Set the API key of a provider in an environment variable, for example `Providers__openai__ApiKey`. Then send requests to a model such as `openai/<model>`. Each provider has its own [request template](docs/api.md#request-templates). Every provider gives the same response format.

## Privacy

The engine sends no telemetry. At the default log level, its logs never hold prompts or answers.

## Documentation

| Page | What it covers |
|---|---|
| [Quick start](docs/quickstart.md) | Download a release, prepare its folder, and start the API. |
| [API reference](docs/api.md) | Routes, request templates, streams, errors, health and compatibility. |
| [Architecture](docs/architecture.md) | Architecture diagram, layers, component interactions and data flow. |
| [CHANGELOG.md](CHANGELOG.md) | The changes in each release. |

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for the third-party software and the model. Each package has the full license and notice texts in its `licenses/` folder.
