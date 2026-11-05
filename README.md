# xat-llm-engine

LLM bridge for local or cloud models. It gives other programs one OpenAI-compatible HTTP API for a bundled local model and for cloud providers such as Anthropic, OpenAI and Mistral.

## How it works

- Other programs call the engine's API, using any OpenAI client library or plain HTTP.
- The model name selects the provider: `local/ministral-3-3b` or `anthropic/claude-sonnet-5-5`.
- Local requests go to a [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` process, which the engine starts and supervises.
- Cloud requests go directly to the vendor's API.

## Install

Download these two files from the same [GitHub release](https://github.com/xatcap/xat-llm-engine/releases):

| File | Purpose |
|---|---|
| `xat-llm-engine-<version>-<platform>.tar.gz` (`.zip` on Windows) | The engine and llama.cpp. No .NET install is needed. Platforms are `osx-arm64`, `linux-x64` and `win-x64`. |
| `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf` | The local model (2 GiB). |

Then:

1. Unpack the engine.
2. Move the model file into its `models/` folder.
3. Optionally, check both downloads against `SHA256SUMS` from the release.
4. Start `XatLlmEngine.Host` (`XatLlmEngine.Host.exe` on Windows).

The API starts at `http://127.0.0.1:8420`. The model takes a little while to load, and `GET /health` shows when it is ready.

Without the model file the engine still runs, but `local` requests return 503.

### Platform notes

- **macOS:** the programs are unsigned, so a browser download is blocked. Remove the quarantine flag after unpacking: `xattr -dr com.apple.quarantine xat-llm-engine-<version>-osx-arm64`
- **Windows:** SmartScreen warns on first start, for the same reason.
- **Linux and Windows:** the packages use llama.cpp's Vulkan build. Machines without a usable GPU run on the CPU.

## Use the API

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}'
```

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

| Route | Key required | Purpose |
|---|---|---|
| `POST /v1/chat/completions` | Yes, if keys are set | Chat, streaming or not. Accepts `messages`, `temperature`, `top_p`, `stop`, `max_tokens`, `max_completion_tokens` and `stream_options.include_usage`. |
| `GET /v1/models` | Yes, if keys are set | Lists the configured models as `provider/model`. |
| `GET /health` | No | Returns `status` (`healthy` or `degraded`) and `local` (`ready`, `starting`, `restarting`, `missing`, `stopped` or `disabled`). |
| `GET /swagger` | No | Swagger UI. Development only. |
| `GET /openapi/v1.json` | No | The OpenAPI document. Development only. |

Text is the only supported content. Tools, images and `n` above 1 return a 400 error, so callers never get a silently different result.

| Status | Meaning |
|---|---|
| 400 | The request is invalid, or the provider rejected it. |
| 401 | The API key is missing or wrong. |
| 404 | The provider or model does not exist. A cloud provider with no API key counts as not existing. |
| 415 | The `Content-Type` is not `application/json`. |
| 429 | The provider is rate limiting. See `Retry-After`. |
| 502 | The provider failed. |
| 503 | The local model is loading or missing, or the provider is overloaded or cannot be reached. See `Retry-After`. |

A stream that fails after it has started has already sent status 200. It ends with an `error` event and no `[DONE]` marker.

## Configure

Settings live in `appsettings.json`, next to the program. Each one can be overridden with an environment variable, using `__` between levels.

**Keep API keys in environment variables, never in a file in source control.**

| Environment variable | Purpose |
|---|---|
| `Providers__anthropic__ApiKey` | Enables Anthropic. |
| `Providers__openai__ApiKey` | Enables OpenAI. |
| `Providers__mistral__ApiKey` | Enables Mistral's cloud API. |
| `Providers__<name>__MaxRetries` | How many times to retry a 429, 5xx or unreachable response from that provider. Retries happen only before the answer starts. A `Retry-After` header is followed, up to 30 seconds. The default is 2. |
| `Providers__<name>__RequestTimeoutSeconds` | How long to wait for the provider to start its answer, across all retries. The default is 600. |
| `Providers__<name>__DefaultMaxTokens` | The token limit when a caller sends none. Only Anthropic uses it, because its API requires a limit. The default is 4096. |
| `Engine__ApiKeys__0` | A key callers must send as `Authorization: Bearer <key>`. Add more with `__1`, `__2` and so on. The index is required. Without it, `Engine__ApiKeys` gives zero keys, which the startup log shows as `Accepted API keys: 0`. |
| `Urls` | The listening address. The default is `http://127.0.0.1:8420`. |
| `AllowedHosts` | The host names callers may use. The default allows only loopback names. |
| `LocalModel__ContextSize` | The context size in tokens. The default is 8192. |
| `LocalModel__GpuLayers` | `auto` (default), `all`, or a number. Use `0` to run on the CPU only. |
| `LocalModel__Parallel` | How many requests generate at once. The context size is shared between them. The default is 1. |
| `LocalModel__DefaultTemperature` | The temperature when a caller sends none. The default is 0.07, as Mistral recommends values below 0.1. |
| `LocalModel__StartupTimeoutSeconds` | How long the model may take to load. The default is 600. |
| `LocalModel__HealthCheckIntervalSeconds` | Seconds between health checks of a running llama-server. Three failed checks in a row make the engine replace it, because a hung server never exits by itself. The default is 10. |
| `LocalModel__ExtraArguments__0` | An extra `llama-server` argument. Add more with `__1`, `__2` and so on. |
| `LocalModel__Enabled` | Set to `false` to run with cloud providers only. |

`Models` under each provider only controls what `/v1/models` lists. Callers can request any model name the vendor offers, so `openai/<any-model>` works even though the `openai` list is empty by default.

### Add another provider

Any service that speaks the OpenAI protocol needs only configuration. For example, this adds `groq/<model>`:

```json
"Providers": {
  "groq": {
    "Type": "OpenAiCompatible",
    "BaseUrl": "https://api.groq.com/openai/v1",
    "Models": []
  }
}
```

Set its key with `Providers__groq__ApiKey`. Use `"UseMaxCompletionTokens": true` if the service needs that field instead of `max_tokens`.

The `BaseUrl` must use `https`. The engine accepts `http` only for an address on this machine, such as `http://127.0.0.1:8000`. Otherwise it refuses to start.

### Reach the engine from other machines

By default only programs on the same machine can connect. To open it to the network, set all three of these:

```bash
export Urls="http://0.0.0.0:8420"
export Engine__ApiKeys__0="<a long random key>"
export AllowedHosts="my-server.example.com"
```

The engine refuses to start on a network address without an API key. It serves plain HTTP, so use a TLS proxy for anything beyond a trusted network.

### Limits

- Run one engine per install. A second engine started from the same folder stops the first one's `llama-server`.
- If the engine is killed by force, its `llama-server` keeps running until the engine next starts. That start stops it, but only if the process name and start time still match the engine's PID file. The PID file is in the `xat-llm-engine` folder, inside the user's local application data folder.
- To see `llama-server`'s own output, start the engine with the argument `--Logging:LogLevel:XatLlmEngine.Infrastructure.LlamaCpp=Debug`.

## Develop

This needs the .NET SDK version in `global.json`, and Python 3.12 or newer for the release script.

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

The code follows Clean Architecture, as described in [docs/dotnet/clean-architecture.md](docs/dotnet/clean-architecture.md). Decisions and their reasons are in [docs/adr](docs/adr).

```text
src/XatLlmEngine.Domain          Core types and rules, with no dependencies
src/XatLlmEngine.Application     Use cases, and the IChatProvider port
src/XatLlmEngine.Infrastructure  Cloud adapters, and llama-server supervision
src/XatLlmEngine.Host            The API and the composition root
tests/                           One test project per layer, plus architecture tests
build/                           Pinned versions and the release script
```

### Debug with Swagger

In the Development environment, the API serves Swagger UI at `/swagger`. The OpenAPI document is at `/openapi/v1.json`. Neither route needs an API key.

Release packages run as Production, so these routes return 404 there. To show them in a package, start it with `ASPNETCORE_ENVIRONMENT=Development`.

`dotnet run --project src/XatLlmEngine.Host` uses the `Debug` profile in `src/XatLlmEngine.Host/Properties/launchSettings.json`. That profile sets Development and listens on `http://127.0.0.1:8420`. Debugging in an IDE uses the same profile and opens Swagger UI in a browser.

If API keys are set, use the Authorize button in Swagger UI to send one.

Swagger UI shows a streamed answer only after the stream has finished. To watch a stream as it arrives, use `curl` or a client library.

### Build a package locally

```bash
python3 build/release.py fetch-model --output artifacts/cache
python3 build/release.py package --rid osx-arm64 --version 0.0.0-local
python3 build/release.py smoke-test --rid osx-arm64 \
  --package artifacts/xat-llm-engine-0.0.0-local-osx-arm64.tar.gz \
  --model artifacts/cache/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf
```

Set the `DOTNET` environment variable if `dotnet` on your `PATH` is not the right SDK.

### Make a release

1. Pushing a `v*` tag starts `.github/workflows/release.yml`.
2. It builds each platform package, and runs each one with the real model.
3. It publishes only after every check has passed and every file has been uploaded.

To test without publishing, run the workflow by hand from the Actions tab, which is a dry run. Its `create_draft` option also uploads the files to a draft release.

To upgrade llama.cpp or the model, change `build/llama-cpp.lock.json` or `build/model.lock.json`. The model must stay under GitHub's limit of 2 GiB per file.

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for llama.cpp and the model.
