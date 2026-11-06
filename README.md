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

Anthropic uses its own format, so this request sends `system` and `max_tokens` as top-level fields:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"anthropic/claude-sonnet-5-5","system":"Answer in one sentence.","max_tokens":256,"messages":[{"role":"user","content":"Hello"}]}'
```

| Route | Purpose |
|---|---|
| `POST /v1/chat/completions` | Chat, with or without streaming. The accepted fields depend on the provider. See [Request templates](#request-templates). |
| `GET /v1/models` | Lists the configured models as `provider/model`. |
| `GET /health` | Always 200. Returns `status` (`healthy`, `degraded` or `unhealthy`), `version`, and `providers`: the `name`, `state` and `message` of each configured provider. For `local`, it also returns `active`, `queued`, `capacity` and `queue_limit`. The older fields `local` and `message` describe the local model. |
| `GET /health/live` | 200 while the engine runs. |
| `GET /health/ready` | 200 when at least one provider can take requests, otherwise 503. Add `?provider=local` to ask about one provider. |
| `GET /swagger` | Swagger UI. Development only. |
| `GET /openapi/v1.json` | The OpenAPI document. Development only. |

Answers are text only. `tools` and `n` are not in any template, because the unified response carries one text answer. The engine also refuses tool content inside `messages` with a 400 error. Tool content is one of these:

- A message with the role `tool` or `function`.
- A message with the field `tool_calls` or `function_call`.
- A content block such as `tool_use` or `tool_result`.

The engine forwards images in messages to the provider unchanged. The vendor and the model decide whether images work.

| Status | Error type | Meaning |
|---|---|---|
| 400 | `invalid_request_error` | The request is invalid, including a field the provider does not list, or the provider rejected it. |
| 404 | `not_found_error` | The route, provider or model does not exist. A cloud provider with no API key counts as not existing. |
| 405 | `invalid_request_error` | The route exists, but does not accept this HTTP method. |
| 413 | `invalid_request_error` | The request body is larger than `Api:MaxRequestBodyBytes`. |
| 415 | `invalid_request_error` | The `Content-Type` is not `application/json`. |
| 429 | `rate_limit_error`, or `budget_exceeded_error` | The provider is rate limiting, or it has used its token budget. See `Retry-After`. |
| 500 | `internal_error` | An unexpected fault in the engine. Report it, with the time of the request. The engine log has the cause. |
| 502 | `upstream_error` | The provider failed, and a retry does not help. For the local model, the model is not installed, `llama-server` could not start, or it stopped too often. The message gives the cause, and the engine log has the details. |
| 503 | `service_unavailable_error`, or `overloaded_error` | The provider cannot take requests now, or the local model cannot take them yet. See `Retry-After`. |

A 503 response has the type `overloaded_error` when the local queue is full. Other 503 responses have the type `service_unavailable_error`. One of these conditions applies to them:

- The local model is loading or restarting.
- The provider has too much load.
- The engine cannot reach the provider.
- The connection to the provider broke.
- The provider stopped sending data.

The engine never retries a request, so one request to the engine is at most one request to the provider. The caller decides whether to try again. When the provider sends its own `Retry-After` header, the engine uses that wait, with these limits:

- The wait is at most 30 seconds.
- The wait is rounded up to whole seconds.
- The wait is at least 1 second.

If the provider sends no header, or the engine returns the 503 itself, the value is 5 seconds. A used token budget sends the time until enough tokens leave the budget period.

The provider states are:

- `ready`
- `busy`
- `starting`
- `restarting`
- `unresponsive`
- `missing`
- `failed`
- `stopping`
- `disabled`
- `budget_exhausted`

A provider can take requests only when it is `ready` or `busy`. The engine does not test cloud providers. So an enabled cloud provider is `ready`. [ADR 0010](docs/adr/0010-health-contract.md) describes the health contract. Accept values that you do not know. A later version can add values.

[ADR 0007](docs/adr/0007-error-model-and-status-codes.md) describes the status codes and error types. All errors use the body `{"error":{"message","type"}}`. There is one exception. A host name that `AllowedHosts` refuses gets ASP.NET's own HTML 400 response. The server also rejects malformed HTTP before the API runs.

The engine reads the first piece of a stream before it sends status 200. Most failures before the first piece still get a real status code. When the first piece takes longer than `Api__StreamCommitSeconds` (15 by default), the engine sends status 200. It then sends a `: keep-alive` comment every 15 seconds. These comments stop the caller's read time limit from ending. A failure after status 200 ends the stream with an `error` event. The stream has no `[DONE]` marker in that case. [ADR 0008](docs/adr/0008-streaming-contract.md) describes the stream rules.

## Request templates

Each provider accepts only the fields in its template. The engine forwards these fields to the provider unchanged, and it checks only the token field. A bad value returns the vendor's own 400 message. This section is the contract for the values that the engine adds.

| Provider | Accepted fields |
|---|---|
| `local` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `stream_options`, `top_k`, `min_p`, `repeat_penalty`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `grammar` |
| `anthropic` | `messages`, `system`, `max_tokens`, `temperature`, `top_p`, `top_k`, `stop_sequences`, `metadata` |
| `openai` | `messages`, `temperature`, `top_p`, `max_completion_tokens`, `stop`, `stream_options`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `reasoning_effort`, `user` |
| `mistral` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `random_seed`, `safe_prompt`, `frequency_penalty`, `presence_penalty`, `response_format` |

These rules apply to every provider:

- `model` and `stream` are always accepted.
- Any other top-level field that the template does not list returns a 400 error. The error names the field and lists the accepted fields. This happens even when the field's value is `null` or a default.
- The local model uses `LocalModel__DefaultTemperature` when a request sends no `temperature`.
- A provider with a `MaxTokensLimit` refuses a token value above the limit. It also refuses a token value that is not a whole number of at least 1. Both return a 400 error. A request with no token field gets the limit. The token field is the first of `max_tokens` and `max_completion_tokens` in the template. Anthropic requires a limit. Its default is 4096.

Streaming usage depends on the provider:

- OpenAI-format providers send a usage piece only when the caller sets `stream_options.include_usage`. The engine always asks the provider for the counts, because the budget and the metrics need them. It gives the counts to the caller only when the caller asked.
- Mistral's template has no `stream_options`. The engine gives Mistral's counts to the caller when Mistral sends them.
- Anthropic streams always end with a usage piece. Its `choices` is empty.

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

Settings live in `appsettings.json`, next to the program. This file lists every setting of these sections:

- `Engine`
- `Api`
- `LocalModel`
- `Providers`

It also lists `Urls` and `AllowedHosts`. ASP.NET has more listening settings, such as `Kestrel:Endpoints`. These are not in this file. Each setting can be overridden with an environment variable. Use `__` between the levels.

**Keep API keys in environment variables, never in a file in source control.** Each provider's `ApiKey` stays blank in `appsettings.json`.

| Environment variable | Purpose |
|---|---|
| `Providers__anthropic__ApiKey` | Enables Anthropic. |
| `Providers__openai__ApiKey` | Enables OpenAI. |
| `Providers__mistral__ApiKey` | Enables Mistral's cloud API. |
| `Providers__<name>__Type` | The wire protocol: `OpenAiCompatible` or `Anthropic`. It is required. A missing or unknown value stops the engine at startup. |
| `Providers__<name>__BaseUrl` | The base address of the provider, for example `https://api.openai.com/v1`. It must use `https`. Plain `http` is allowed only for an address on this machine. |
| `Providers__<name>__RequestTimeoutSeconds` | How long to wait for the provider to start its answer. The default is 600. The engine never retries a request. When a provider sends `Retry-After`, the engine gives it to the caller. |
| `Providers__<name>__ReadIdleTimeoutSeconds` | How long to wait for more data after the answer has started. This applies to the time between stream events. When the time ends, the request fails with 503. The default is 300. |
| `Providers__<name>__MaxTokensLimit` | The largest number of output tokens that a caller can ask for. 0 means no limit. A request with no token field gets the limit. Anthropic must set it, and its default is 4096. |
| `Providers__<name>__TokenBudget` | The largest number of tokens, input and output together, that the provider can use in `BudgetPeriodHours`. 0 means no budget. After that, requests get 429 until old tokens leave the period. |
| `Providers__<name>__BudgetPeriodHours` | The length of the rolling budget period, in hours. The default is 24. |
| `LocalModel__MaxTokensLimit` | The largest number of output tokens for a local request. 0 means no limit. |
| `Providers__<name>__RequestFields__0` | Sets the field at index 0 of a provider's request template. Add more with `__1`, `__2` and so on. Overrides replace entries by index, and they cannot shorten the list. To remove a field, edit `appsettings.json`. |
| `Engine__DataFolder` | The folder for the engine's own files: the instance lock, the PID file and the usage counts. Leave it empty for the default folder of this install. |
| `Api__StreamCommitSeconds` | How long a stream can wait for its first piece before the engine sends status 200 and keep-alive comments. 0 turns this off. The default is 15. |
| `Api__MaxRequestBodyBytes` | The largest request body, in bytes. A larger body gets 413. The default is 20971520 (20 MiB). |
| `Urls` | The listening address. It must be a loopback address. The default is `http://127.0.0.1:8420`. |
| `AllowedHosts` | The host names callers may use. The default allows only loopback names. |
| `LocalModel__ModelName` | The name that callers use after the `local/` prefix. The default is `ministral-3-3b`. |
| `LocalModel__ModelPath` | The path of the GGUF model file. A relative path starts at the engine's folder. The default is `models/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf`. |
| `LocalModel__ServerPath` | The path of the `llama-server` program, without the `.exe` suffix. The engine adds the suffix on Windows. A relative path starts at the engine's folder. The default is `llama/llama-server`. |
| `LocalModel__ContextSize` | The context size in tokens. The default is 8192. `--ctx-size` or `-c` in `ExtraArguments` replaces this setting. |
| `LocalModel__GpuLayers` | `auto` (default), `all`, or a number. Use `0` to run on the CPU only. `--gpu-layers` or `-ngl` in `ExtraArguments` replaces this setting. |
| `LocalModel__Parallel` | How many requests generate at once. These requests share the context size. The engine sends no more than this number to llama-server. The default is 1. `--parallel` or `-np` in `ExtraArguments` replaces this setting. |
| `LocalModel__DefaultTemperature` | The temperature when a caller sends none. The default is 0.07, as Mistral recommends values below 0.1. |
| `LocalModel__StartupTimeoutSeconds` | How long the model may take to load. When the time ends, the local model fails until the engine restarts. The default is 600. |
| `LocalModel__HealthCheckIntervalSeconds` | Seconds between health checks of a running llama-server. Three failed checks in a row while no request runs make the engine replace it, because a hung server never exits by itself. The default is 10. |
| `LocalModel__MaxQueuedRequests` | How many local requests can wait for a free slot. A request that finds the queue full gets 503 at once. The default is 32. |
| `LocalModel__UnresponsiveTimeoutSeconds` | How long a busy llama-server can fail its health checks and also send no data, before the engine replaces it. The default is 300. |
| `LocalModel__ExtraArguments__0` | An extra `llama-server` argument. Add more with `__1`, `__2` and so on. Write a flag and its value as two entries, for example `--threads` and then `4`. A value must follow its flag. A value cannot start with `-`, unless it is a number such as `-1`. The engine allows only tuning flags, such as `--threads` and `--flash-attn`. Any other flag, such as `--log-file` or `--webui`, stops the engine at startup. |
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

A provider entry without `RequestFields` stops the engine at startup, even when that provider has no key. A local model without `RequestFields` also stops the engine at startup, even when you disable the local model. Custom entries from version 1.0.0 must add `RequestFields`.

When a vendor adds a request field, add its name to that provider's `RequestFields`. That is one line.

The `BaseUrl` must use `https`. The engine accepts `http` only for an address on this machine, such as `http://127.0.0.1:8000`. Otherwise it refuses to start.

The engine checks every entry in `LocalModel` and `Providers` at startup, also an entry with no key. These mistakes stop the engine with a message:

- A setting that does not exist, for example a wrong name or the removed `MaxRetries`.
- A `Type` that is missing, a number, or not `OpenAiCompatible` or `Anthropic`.
- An `ApiKey` with spaces, line breaks or control characters. A key copied from a file often ends with a line break.
- A value that is out of range or has the wrong format.

### Reach the engine from other machines

This is not possible at this time. The API has no authentication, so only programs on the same machine can connect. The engine refuses to start on a network address.

### Monitor

The engine uses only the .NET built-in metrics and traces. No exporter is needed.

Every response has an `X-Request-Id` header. Every log line of that request holds the same value as `RequestId`. A chat request also has `CompletionId`, which is the `id` of the answer. Each chat request writes one log line. The line holds these values:

- The model.
- The status.
- The time.
- The tokens.

The line never holds the prompt or the answer.

To watch the metrics, install `dotnet-counters` and run:

```bash
dotnet-counters monitor --counters XatLlmEngine --name XatLlmEngine.Host
```

| Instrument | Type | Tags | What it measures |
|---|---|---|---|
| `xat_engine.chat.requests` | Counter | `provider`, `model`, `stream`, `outcome` | Chat requests. `outcome` is `ok`, `canceled`, `error` or a failure kind such as `rate_limited`. |
| `xat_engine.chat.duration` | Histogram (s) | the same | The time of a whole chat request. |
| `xat_engine.chat.time_to_first_chunk` | Histogram (s) | `provider`, `model`, `stream` | The time until the first piece of a stream. |
| `xat_engine.tokens` | Counter | `provider`, `model`, `stream`, `direction`, `source` | Tokens. `direction` is `input` or `output`. `source` is `reported`, or `estimated` when the provider reported none. |
| `xat_engine.local.restarts` | Counter | `cause` | Restarts of `llama-server`. `cause` is `crash` or `hang`. |
| `xat_engine.local.state` | Gauge | `state` | The state of the local model. |
| `xat_engine.local.active` | Gauge | | Local requests that have a slot. |
| `xat_engine.local.queued` | Gauge | | Local requests that wait for a slot. |

`model` is a model name from the configuration, or `other` for any other name. The activity source `XatLlmEngine` gives one `chat` activity for each request. Record it with `dotnet-trace`.

### Limits

- Requests that run at the same time can exceed a token budget, because each one can use up to its token limit. When a provider reports no tokens, the engine estimates them at 3 bytes for each token. The counts are in `usage.json` in the data folder. [ADR 0011](docs/adr/0011-cost-control.md) describes the rules.

- Only one engine can run per install. A second engine started from the same folder stops at once with a message, and the first engine continues to work. To run two engines, use two copies in different folders.
- The engine keeps its own files in a data folder for each install: `xat-llm-engine/<hash>` inside the user's local application data folder. `Engine__DataFolder` sets another folder.
- If the engine is killed, its `llama-server` continues to run until the next engine start. That start stops the `llama-server`, but only if the process name and start time still match the engine's PID file in the data folder.
- To see `llama-server`'s own output, start the engine with the argument `--Logging:LogLevel:XatLlmEngine.Infrastructure.LlamaCpp=Debug`.
- The engine does not start `llama-server` again after a failed start. A wrong setting does not correct itself. Examples are:
  - a wrong value in `ExtraArguments`,
  - a model that does not load, and
  - a load that takes too long.
  Local requests then return 502 with the cause.
- If `llama-server` stops unexpectedly after it was ready, the engine starts it again. After 3 stops in 10 minutes, the engine does not start it again, and local requests return 502.
- The engine sends at most one request per slot (`LocalModel__Parallel`) to `llama-server`. Other requests wait in the engine, up to `LocalModel__MaxQueuedRequests`. A request that finds the queue full gets 503 at once. [ADR 0009](docs/adr/0009-local-load-and-hang-policy.md) describes the rules.
- A busy `llama-server` that fails its health checks is not stopped while it sends data. New local requests get 503 until it answers again. If it fails its checks and sends no data for `LocalModel__UnresponsiveTimeoutSeconds`, the engine starts it again.
- A local error message has no file paths or server output. The engine log has the full cause.
- The engine removes the llama.cpp variables from the environment of `llama-server`. These are the variables that start with one of these prefixes:
  - `LLAMA_`
  - `LLAMACPP_`
  - `GGML_`
  - `MTMD_`
  - `AIP_`
  - `HF_`
  - `LLGUIDANCE_`

  The engine also removes these variables:
  - `MODEL_ENDPOINT`
  - `HTTP_PROXY`
  - `HTTPS_PROXY`

  The variables of the operating system and the GPU drivers stay.
- The local model fails with 502 if a llama.cpp config file exists, because `llama-server` always reads it. A file from another llama.cpp installation also does this. Cloud providers continue to work. Remove or rename the file, then restart the engine. The engine log shows the path. The engine does this check before each start of `llama-server`. The files are:
  - Linux and macOS: `/etc/llama.cpp/config.ini`, and `$XDG_CONFIG_HOME/llama.cpp/config.ini` or `~/.config/llama.cpp/config.ini`.
  - Windows: `%PROGRAMDATA%\llama.cpp\config.ini` and `%APPDATA%\llama.cpp\config.ini`.

## Develop

This needs the .NET SDK version in `global.json`, and Python 3.12 or newer for the release script.

```bash
dotnet build XatLlmEngine.slnx
dotnet test --solution XatLlmEngine.slnx
```

Each project has a `packages.lock.json` file that pins its NuGet packages. CI restores with `--locked-mode`, so it fails when a lock file is out of date. After you change a package version or the SDK, update the lock files with `dotnet restore XatLlmEngine.slnx --force-evaluate`, and commit them.

Each change that people can see needs an entry under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md).

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

Versions follow Semantic Versioning, as [ADR 0005](docs/adr/0005-versioning-policy.md) describes. A breaking change needs a new major version.

1. Rename the `## [Unreleased]` section of [CHANGELOG.md](CHANGELOG.md) to `## [x.y.z] - YYYY-MM-DD`, and add a new empty `## [Unreleased]` section above it.
2. Push the tag `vx.y.z`. This starts `.github/workflows/release.yml`.
3. The workflow runs all tests on all platforms. It stops if a test fails, or if `CHANGELOG.md` has no section for the version.
4. It builds each platform package, and runs each one with the real model.
5. It publishes only after every check passes and every file is on the release. The CHANGELOG section becomes the release notes.

The workflow also runs `build/release.py finalize`. It checks that each file is under GitHub's 2 GiB limit, and it writes `SHA256SUMS`.

To check the changelog section before you push the tag, run `python3 build/release.py check-changelog --version x.y.z`.

To test without publishing, run the workflow by hand from the Actions tab. That is a dry run. Its `create_draft` option also uploads the files to a draft release.

To upgrade llama.cpp or the model, change `build/llama-cpp.lock.json` or `build/model.lock.json`. The model must stay under GitHub's limit of 2 GiB per file.

## License

MIT. See [LICENSE](LICENSE) for this project, and [build/THIRD_PARTY_NOTICES.md](build/THIRD_PARTY_NOTICES.md) for the third-party software and the model. Each package has the full license and notice texts in its `licenses/` folder.

When you add or update a NuGet package that the engine ships, add it to [build/third-party.json](build/third-party.json) with its license files. Also add it to `build/THIRD_PARTY_NOTICES.md`. `python3 build/release.py check-notices` checks this, and CI runs it.
