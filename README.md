# xat-llm-engine

LLM bridge for local or cloud models. It gives other programs one HTTP API for a bundled local model and for cloud providers such as Anthropic, OpenAI and Mistral.

## How it works

- Other programs call the engine's API. They use plain HTTP or a client library.
- The model name selects the provider: `local/ministral-3-3b` or `anthropic/claude-sonnet-5-5`.
- Each provider has its own request template, in that provider's native format. Send only the fields that provider lists. See [Request templates](#request-templates).
- OpenAI client libraries work for these OpenAI-format providers:
  - `local`
  - `openai`
  - `mistral`

  They do not work for `anthropic/...`, which uses Anthropic's own format.
- Every provider returns the same OpenAI-style response format.
- Local requests go to a [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` process, which the engine starts and supervises.
- Cloud requests go directly to the vendor's API.

## Install

Download these two files from the same [GitHub release](https://github.com/xatcap/xat-llm-engine/releases):

| File | Purpose |
|---|---|
| `xat-llm-engine-<version>-<platform>.tar.gz` (`.zip` on Windows) | The engine and llama.cpp. You do not need to install .NET. Platforms are `osx-arm64`, `linux-x64` and `win-x64`. |
| `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf` | The local model (2 GiB). |

Then:

1. Unpack the engine.
2. Move the model file into its `models/` folder.
3. Optionally, check both downloads against `SHA256SUMS` from the release.
4. Start `XatLlmEngine.Host` (`XatLlmEngine.Host.exe` on Windows).

The API starts at `http://127.0.0.1:8420`. The model takes a little while to load. `GET /health/ready?provider=local` returns 200 when it is ready.

Without the model file, the engine still runs. `local` requests return 502 until you add the file and restart the engine.

### Platform notes

- **macOS:** the programs have no signature, so macOS blocks a browser download. After you unpack the engine, remove the quarantine flag: `xattr -dr com.apple.quarantine xat-llm-engine-<version>-osx-arm64`
- **Windows:** SmartScreen shows a warning at the first start, for the same reason.
- **Linux and Windows:** the packages use the Vulkan build of llama.cpp. A machine without a usable GPU runs the model on the CPU.

## Quick start

Send a request to the local model with `curl`:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}'
```

Or stream the answer with the OpenAI client library for Python:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:8420/v1", api_key="unused-on-loopback")
for chunk in client.chat.completions.create(
    model="local/ministral-3-3b",
    messages=[{"role": "user", "content": "Hello"}],
    stream=True,
):
    if chunk.choices and chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="")
```

Anthropic uses its own format, so this request sends `system` and `max_tokens` as top-level fields:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"anthropic/claude-sonnet-5-5","system":"Answer in one sentence.","max_tokens":256,"messages":[{"role":"user","content":"Hello"}]}'
```

`GET /v1/models` lists the configured models. The [API reference](docs/api.md) lists all routes, the error codes, the stream rules and the health values.

## Request templates

Each provider accepts only the fields in its template, and any other field returns a 400 error. The [request templates](docs/api.md#request-templates) list the fields of each provider and the values that the engine adds.

## Enable cloud providers

Set the API key of each cloud provider that you want to use:

| Environment variable | Provider |
|---|---|
| `Providers__anthropic__ApiKey` | Anthropic |
| `Providers__openai__ApiKey` | OpenAI |
| `Providers__mistral__ApiKey` | Mistral's cloud API |

**Keep API keys in environment variables, never in a file in source control.**

All other settings are in `appsettings.json`, next to the program. An environment variable can override each setting. Use `__` between the levels, for example `LocalModel__ContextSize`. The [configuration reference](docs/configuration.md) lists each setting, and tells how to add another provider.

## What the engine does not do

- The engine does not support these features on any provider:
  - Tool calling.
  - Embeddings and reranking.
  - Image generation.
  - Speech and audio.
- Only programs on the same machine can connect. The API has no authentication, so the engine refuses to start on a network address.

[Supported models](docs/api.md#supported-models) gives more details.

## Documentation

| Page | What it covers |
|---|---|
| [API reference](docs/api.md) | Routes, request templates, content rules, streams, errors, health and supported models. |
| [Configuration](docs/configuration.md) | Each setting, how to add a provider, startup checks and how to change the local model. |
| [Operations](docs/operations.md) | Logs, metrics and traces, the data folder, how the engine handles local failures, and troubleshooting. |
| [Development](docs/development.md) | Build, test, project layout, Swagger, local packages and releases. |
| [Architecture decisions](docs/adr) | The significant decisions and their reasons. |
| [CHANGELOG.md](CHANGELOG.md) | The changes in each release. |

## Develop

This needs the .NET SDK version in `global.json`, and Python 3.12 or newer for the release script.

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

The [development guide](docs/development.md) tells how to update lock files, debug with Swagger, build a package and make a release. All text follows the rules in [docs/writing-style.md](docs/writing-style.md).

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for the third-party software and the model. Each package has the full license and notice texts in its `licenses/` folder.
