# xat-llm-engine

LLM bridge for local or cloud models. It gives other programs one HTTP API for a bundled local model and for cloud providers such as Anthropic, OpenAI and Mistral.

## How it works

- Other programs call the engine's API. They use plain HTTP or a client library.
- The model name selects the provider: `local/ministral-3-3b` or `anthropic/claude-sonnet-5-5`.
- Each provider has its own request template, in that provider's native format. Send only the fields that provider lists. See [Request templates](#request-templates).
- OpenAI client libraries work for OpenAI-format providers, such as `local`, `openai` and `mistral`. They do not work for `anthropic/...`, which uses Anthropic's own format.
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

The API starts at `http://127.0.0.1:8420`. The model takes a little while to load, and `GET /health` shows when it is ready.

Without the model file the engine still runs, but `local` requests return 503.

### Platform notes

- **macOS:** the programs have no signature, so macOS blocks a browser download. After you unpack the engine, remove the quarantine flag: `xattr -dr com.apple.quarantine xat-llm-engine-<version>-osx-arm64`
- **Windows:** SmartScreen shows a warning at the first start, for the same reason.
- **Linux and Windows:** the packages use the Vulkan build of llama.cpp. A machine without a usable GPU runs the model on the CPU.

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

Anthropic uses its own format, so this request sends `max_tokens` as a top-level field:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"anthropic/claude-sonnet-5-5","max_tokens":256,"messages":[{"role":"user","content":"Hello"}]}'
```

Set the Anthropic system prompt with a `PUT` request. The prompt applies to every later Anthropic request until the next restart.

```bash
curl -X PUT http://127.0.0.1:8420/v1/system-prompts/anthropic \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Answer in one sentence."}'
```

| Route | Purpose |
|---|---|
| `POST /v1/chat/completions` | Chat, with or without streaming. The accepted fields depend on the provider. See [Request templates](#request-templates). |
| `GET /v1/models` | Lists the configured models as `provider/model`. |
| `PUT /v1/system-prompts/{provider}` | Replaces the provider's system prompt. An empty value clears it. Body: `{"prompt":"..."}`. Returns 200 and the full prompt as `{"provider","prompt"}`. See [System prompts](#system-prompts). |
| `POST /v1/system-prompts/{provider}/append` | Adds text to the end of the provider's system prompt. A newline separates the text from the existing prompt. Body: `{"prompt":"..."}`. Returns 200 and the full prompt as `{"provider","prompt"}`. |
| `GET /health` | Returns `status` (`healthy` or `degraded`), `local` (`ready`, `starting`, `restarting`, `missing`, `stopped` or `disabled`) and `message`, which explains `local`. It always returns 200, including while the local model loads. Read `local` to see whether the local model is ready. |
| `GET /swagger` | Swagger UI. Development only. |
| `GET /openapi/v1.json` | The OpenAPI document. Development only. |

Answers are text only. `tools` and `n` are not in any template, because the unified response carries one text answer. The engine forwards images in messages to the provider unchanged. The vendor and the model decide whether images work.

| Status | Meaning |
|---|---|
| 400 | The request is invalid. This includes a field the provider does not list, and a chat request that sets a system prompt. The provider can also reject the request. |
| 404 | The route, provider or model does not exist. A cloud provider with no API key counts as not existing. |
| 405 | The route exists, but does not accept this HTTP method. |
| 415 | The `Content-Type` is not `application/json`. |
| 429 | The provider is rate limiting. See `Retry-After`. |
| 502 | The provider failed. |
| 503 | One of these conditions applies: the local model is loading or missing, the provider has too much load, or the engine cannot reach the provider. See `Retry-After`. |

All errors use the body `{"error":{"message","type"}}`. There is one exception. A host name that `AllowedHosts` refuses gets ASP.NET's own HTML 400 response. The server also rejects malformed HTTP before the API runs.

A stream that fails after it has started has already sent status 200. It ends with an `error` event and no `[DONE]` marker.

## Request templates

Each provider accepts only the fields in its template. The engine forwards these fields to the provider unchanged, and it does not check their values. A bad value returns the vendor's own 400 message.

| Provider | Accepted fields |
|---|---|
| `local` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `stream_options`, `top_k`, `min_p`, `repeat_penalty`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `grammar` |
| `anthropic` | `messages`, `max_tokens`, `temperature`, `top_p`, `top_k`, `stop_sequences`, `metadata` |
| `openai` | `messages`, `temperature`, `top_p`, `max_completion_tokens`, `stop`, `stream_options`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `reasoning_effort`, `user` |
| `mistral` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `random_seed`, `safe_prompt`, `frequency_penalty`, `presence_penalty`, `response_format` |

These rules apply to every provider:

- `model` and `stream` are always accepted.
- Any other top-level field that the template does not list returns a 400 error. The error names the field and lists the accepted fields. This happens even when the field's value is `null` or a default.
- The local model uses `LocalModel__DefaultTemperature` when a request sends no `temperature`. Anthropic uses `Providers__anthropic__DefaultMaxTokens` when a request sends no `max_tokens`.

Streaming usage depends on the provider:

- OpenAI-format providers send a usage piece only when the caller sets `stream_options.include_usage`.
- Mistral's template has no `stream_options`, so Mistral streams carry no usage.
- Anthropic streams always end with a usage piece. Its `choices` is empty.

## System prompts

Each provider has its own system prompt. The engine keeps the prompts in memory. A restart returns each provider to its `SystemPrompt` setting.

Use `PUT /v1/system-prompts/{provider}` to replace a prompt. Use `POST /v1/system-prompts/{provider}/append` to add text to the end of it. Both routes take `{"prompt":"..."}`.

A blank prompt, which is empty or only spaces, means that the engine adds nothing.

Chat requests cannot set a system prompt. The engine returns 400 when a request has a top-level `system` field. It also returns 400 when a message has the role `system` or `developer`, in any letter case. This applies to every provider, even when no prompt is stored. This breaks OpenAI client code and frameworks that add a system message. Remove the system message from the request, and use a stored prompt instead.

The bundled local model has a default prompt in its chat template. A stored prompt replaces this default completely. Append adds text only to the stored prompt. With no stored prompt, the appended text becomes the whole prompt, and the default no longer applies. Send `"prompt":""` to clear the prompt. The engine then adds nothing, and the default applies again.

Vendors may add their own instructions to every request. A stored prompt cannot remove the behaviour that a model is trained with. Cloud behaviour has not been tested against live vendor APIs.

These errors apply to the system prompt routes:

- 404: the provider is unknown, the local model is disabled, or a cloud provider has no API key.
- 400: the body is malformed, or `prompt` is missing or is not text.
- 415: the `Content-Type` is not `application/json`.

These routes have no authentication. Any program on this machine can change a provider's prompt. The change affects every caller of that provider until the next restart. The prompt is not a security control. A caller can still add instructions to a `user` message.

## Supported models

Not supported on any provider:

- Embeddings and reranking.
- Image generation.
- Speech and audio.
- Legacy models that only offer `/v1/completions`.
- Models offered only on OpenAI's `/v1/responses` API.
- Tool calling.

If a model sends reasoning output in a separate field, the engine removes that output. The engine keeps the text of the answer.

A cloud vendor works through configuration if it offers an OpenAI-compatible endpoint with Bearer keys. AWS Bedrock's native API does not work. Azure OpenAI's older deployment URLs, which use `api-key` headers, do not work.

The local model can be any GGUF model that the pinned llama.cpp supports. The engine runs one local model at a time. To change it, set `LocalModel__ModelPath` and `LocalModel__ModelName`, then restart the engine. For example, a Qwen model. Other local formats need another local server, added as an `OpenAiCompatible` provider on a loopback address such as `http://127.0.0.1:8000`, with a non-blank `ApiKey`.

These statements were not tested against live vendor APIs.

## Configure

Settings live in `appsettings.json`, next to the program. This file lists every setting of the engine's own sections (`LocalModel` and `Providers`), and also `Urls` and `AllowedHosts`. ASP.NET has more listening settings, such as `Kestrel:Endpoints`, that are not in this file. Each setting can be overridden with an environment variable, using `__` between levels.

**Keep API keys in environment variables, never in a file in source control.** Each provider's `ApiKey` stays blank in `appsettings.json`.

| Environment variable | Purpose |
|---|---|
| `Providers__anthropic__ApiKey` | Enables Anthropic. |
| `Providers__openai__ApiKey` | Enables OpenAI. |
| `Providers__mistral__ApiKey` | Enables Mistral's cloud API. |
| `Providers__<name>__MaxRetries` | How many times to retry a 429, 5xx or unreachable response from that provider. Retries happen only before the answer starts. The engine obeys a `Retry-After` header, up to 30 seconds. The default is 2. |
| `Providers__<name>__RequestTimeoutSeconds` | How long to wait for the provider to start its answer, across all retries. The default is 600. |
| `Providers__<name>__DefaultMaxTokens` | The token limit when a caller sends none. Only Anthropic uses it, because its API requires a limit. The default is 4096. |
| `Providers__<name>__SystemPrompt` | The system prompt at engine start. Callers can change it until the next restart. Blank by default. |
| `Providers__<name>__RequestFields__0` | Sets the field at index 0 of a provider's request template. Add more with `__1`, `__2` and so on. Overrides replace entries by index, and they cannot shorten the list. To remove a field, edit `appsettings.json`. |
| `Urls` | The listening address. It must be a loopback address. The default is `http://127.0.0.1:8420`. |
| `AllowedHosts` | The host names callers may use. The default allows only loopback names. |
| `LocalModel__ContextSize` | The context size in tokens. The default is 8192. |
| `LocalModel__GpuLayers` | `auto` (default), `all`, or a number. Use `0` to run on the CPU only. |
| `LocalModel__Parallel` | How many requests generate at once. These requests share the context size. The default is 1. |
| `LocalModel__DefaultTemperature` | The temperature when a caller sends none. The default is 0.07, as Mistral recommends values below 0.1. |
| `LocalModel__SystemPrompt` | The system prompt at engine start. Callers can change it until the next restart. Blank by default. |
| `LocalModel__StartupTimeoutSeconds` | How long the model may take to load. The default is 600. |
| `LocalModel__HealthCheckIntervalSeconds` | Seconds between health checks of a running llama-server. Three failed checks in a row make the engine replace it, because a hung server never exits by itself. The default is 10. |
| `LocalModel__ExtraArguments__0` | An extra `llama-server` argument. Add more with `__1`, `__2` and so on. |
| `LocalModel__RequestFields__0` | Sets the field at index 0 of the local model's request template. Add more with `__1`, `__2` and so on. Overrides replace entries by index, and they cannot shorten the list. To remove a field, edit `appsettings.json`. |
| `LocalModel__Enabled` | Set to `false` to run with cloud providers only. |

`Models` under each provider only controls what `/v1/models` lists. Callers can request any model name the vendor offers, so `openai/<any-model>` works even though the `openai` list is empty by default.

### Add another provider

Any service that speaks the OpenAI protocol needs only configuration. For example, this adds `groq/<model>`:

```json
"Providers": {
  "groq": {
    "Type": "OpenAiCompatible",
    "BaseUrl": "https://api.groq.com/openai/v1",
    "Models": [],
    "RequestFields": [ "messages", "temperature", "top_p", "max_tokens", "stop", "stream_options", "seed" ]
  }
}
```

Set its key with `Providers__groq__ApiKey`. The `RequestFields` list is the provider's request template. The list above is an example. Copy the fields that the vendor really accepts.

A provider entry without `RequestFields` stops the engine at startup, even when that provider has no key. A local model without `RequestFields` also stops the engine at startup, even when you disable the local model. Custom entries from before this change must add `RequestFields`.

When a vendor adds a request field, add its name to that provider's `RequestFields`. That is one line.

The `BaseUrl` must use `https`. The engine accepts `http` only for an address on this machine, such as `http://127.0.0.1:8000`. Otherwise it refuses to start.

### Reach the engine from other machines

This is not possible at this time. The API has no authentication, so only programs on the same machine can connect. The engine refuses to start on a network address.

### Limits

- Run one engine per install. A second engine started from the same folder stops the first one's `llama-server`.
- If the engine is killed, its `llama-server` continues to run until the next engine start. That start stops the `llama-server`, but only if the process name and start time still match the engine's PID file. The PID file is in the `xat-llm-engine` folder, inside the user's local application data folder.
- To see `llama-server`'s own output, start the engine with the argument `--Logging:LogLevel:XatLlmEngine.Infrastructure.LlamaCpp=Debug`.

## Develop

This needs the .NET SDK version in `global.json`, and Python 3.12 or newer for the release script.

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

The code follows Clean Architecture, as described in [docs/dotnet/clean-architecture.md](docs/dotnet/clean-architecture.md). Decisions and their reasons are in [docs/adr](docs/adr). All text follows the rules in [docs/writing-style.md](docs/writing-style.md).

```text
src/XatLlmEngine.Domain          Core types and rules, with no dependencies
src/XatLlmEngine.Application     Use cases, and the IChatProvider port
src/XatLlmEngine.Infrastructure  Cloud adapters, and llama-server supervision
src/XatLlmEngine.Host            The API and the composition root
tests/                           One test project per layer, plus architecture tests
build/                           Pinned versions and the release script
```

### Debug with Swagger

In the Development environment, the API serves Swagger UI at `/swagger`. The OpenAPI document is at `/openapi/v1.json`.

Release packages run as Production, so these routes return 404 there. To show them in a package, start it with `ASPNETCORE_ENVIRONMENT=Development`.

`dotnet run --project src/XatLlmEngine.Host` uses the `Debug` profile in `src/XatLlmEngine.Host/Properties/launchSettings.json`. That profile sets Development and listens on `http://127.0.0.1:8420`. Debugging in an IDE uses the same profile and opens Swagger UI in a browser.

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
3. It publishes only after every check passes and every file is on the release.

To test without publishing, run the workflow by hand from the Actions tab, which is a dry run. Its `create_draft` option also uploads the files to a draft release.

To upgrade llama.cpp or the model, change `build/llama-cpp.lock.json` or `build/model.lock.json`. The model must stay under GitHub's limit of 2 GiB per file.

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for llama.cpp and the model.
