# Configuration

## Settings file and overrides

Settings live in `appsettings.json`, next to the program. This file lists every setting of these sections:

- `Engine`
- `Api`
- `LocalModel`
- `Providers`

It also lists `Urls` and `AllowedHosts`. ASP.NET has more listening settings, such as `Kestrel:Endpoints`. These are not in this file. Each setting can be overridden with an environment variable. Use `__` between the levels.

**Keep API keys in environment variables, never in a file in source control.** Each provider's `ApiKey` stays blank in `appsettings.json`.

## Engine

| Environment variable | Purpose |
|---|---|
| `Engine__DataFolder` | The folder for the engine's own files: the instance lock, the PID file and the usage counts. Leave it empty for the default folder of this install. An app that includes the engine must set a folder that does not change after an update. See [The data folder](operations.md#one-engine-for-each-install). |
| `Engine__ReadyFile` | The path of the ready file. When the API starts, the engine writes its address, its process ID and its version to this file. The engine deletes the file when it stops. Leave it empty for no file. A relative path starts at the engine's folder. The folder of the file must exist. See [Include the engine in an app](embedding.md). |
| `Engine__StopWhenInputCloses` | Set to `true` to stop the engine when its standard input closes. The default is `false`. Do not set it when a service manager starts the engine, because the input can close at once. See [Include the engine in an app](embedding.md). |

## API and listening

| Environment variable | Purpose |
|---|---|
| `Urls` | The listening address. It must be a loopback address. The default is `http://127.0.0.1:8420`. Use `http://127.0.0.1:0` to let the system choose a free port, and read the port from the ready file. The engine refuses `localhost` with port 0. |
| `AllowedHosts` | The host names callers may use. The default allows only loopback names. |
| `Api__StreamCommitSeconds` | How long a stream can wait for its first piece before the engine sends status 200 and keep-alive comments. 0 turns this off. The default is 15. |
| `Api__MaxRequestBodyBytes` | The largest request body, in bytes. A larger body gets 413. The default is 20971520 (20 MiB). |

## Local model

| Environment variable | Purpose |
|---|---|
| `LocalModel__Enabled` | Set to `false` to run with cloud providers only. |
| `LocalModel__ModelName` | The name that callers use after the `local/` prefix. The default is `ministral-3-3b`. |
| `LocalModel__ModelPath` | The path of the GGUF model file. A relative path starts at the engine's folder. The default is `models/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf`. |
| `LocalModel__ServerPath` | The path of the `llama-server` program, without the `.exe` suffix. The engine adds the suffix on Windows. A relative path starts at the engine's folder. The default is `llama/llama-server`. |
| `LocalModel__ContextSize` | The context size in tokens. The default is 8192. `--ctx-size` or `-c` in `ExtraArguments` replaces this setting. |
| `LocalModel__GpuLayers` | `auto` (default), `all`, or a number. Use `0` to run on the CPU only. `--gpu-layers` or `-ngl` in `ExtraArguments` replaces this setting. |
| `LocalModel__Parallel` | How many requests generate at once. These requests share the context size. The engine sends no more than this number to llama-server. The default is 1. `--parallel` or `-np` in `ExtraArguments` replaces this setting. |
| `LocalModel__DefaultTemperature` | The temperature when a caller sends none. The default is 0.07, as Mistral recommends values below 0.1. |
| `LocalModel__MaxTokensLimit` | The largest number of output tokens for a local request. 0 means no limit. |
| `LocalModel__StartupTimeoutSeconds` | How long the model may take to load. When the time ends, the local model fails until the engine restarts. The default is 600. |
| `LocalModel__HealthCheckIntervalSeconds` | Seconds between health checks of a running llama-server. Three failed checks in a row while no request runs make the engine replace it, because a hung server never exits by itself. The default is 10. |
| `LocalModel__MaxQueuedRequests` | How many local requests can wait for a free slot. A request that finds the queue full gets 503 at once. The default is 32. |
| `LocalModel__UnresponsiveTimeoutSeconds` | How long a busy llama-server can fail its health checks and also send no data, before the engine replaces it. The default is 300. |
| `LocalModel__ExtraArguments__0` | An extra `llama-server` argument. Add more with `__1`, `__2` and so on. Write a flag and its value as two entries, for example `--threads` and then `4`. A value must follow its flag. A value cannot start with `-`, unless it is a number such as `-1`. The engine allows only tuning flags, such as `--threads` and `--flash-attn`. Any other flag, such as `--log-file` or `--webui`, stops the engine at startup. |
| `LocalModel__RequestFields__0` | Sets the field at index 0 of the local model's request template. See [Request fields](#request-fields). |

## Providers

| Environment variable | Purpose |
|---|---|
| `Providers__anthropic__ApiKey` | Enables Anthropic. |
| `Providers__openai__ApiKey` | Enables OpenAI. |
| `Providers__mistral__ApiKey` | Enables Mistral's cloud API. |
| `Providers__<name>__Type` | The wire protocol: `OpenAiCompatible` or `Anthropic`. It is required. A missing or unknown value stops the engine at startup. |
| `Providers__<name>__BaseUrl` | The base address of the provider, for example `https://api.openai.com/v1`. It must use `https`. Plain `http` is allowed only for an address on this machine, such as `http://127.0.0.1:8000`. Otherwise the engine refuses to start. |
| `Providers__<name>__Models__0` | A model name that `/v1/models` lists. Add more with `__1`, `__2` and so on. Overrides replace entries by index, and they cannot shorten the list. See [Models lists](#models-lists). |
| `Providers__<name>__RequestTimeoutSeconds` | How long to wait for the provider to start its answer. The default is 600. The engine never retries a request. When a provider sends `Retry-After`, the engine gives it to the caller. |
| `Providers__<name>__ReadIdleTimeoutSeconds` | How long to wait for more data after the answer has started. This applies to the time between stream events. When the time ends, the request fails with 503. The default is 300. |
| `Providers__<name>__MaxTokensLimit` | The largest number of output tokens that a caller can ask for. 0 means no limit. A request with no token field gets the limit. Anthropic must set it, and its default is 4096. |
| `Providers__<name>__TokenBudget` | The largest number of tokens, input and output together, that the provider can use in `BudgetPeriodHours`. 0 means no budget. After that, requests get 429 until old tokens leave the period. |
| `Providers__<name>__BudgetPeriodHours` | The length of the rolling budget period, in hours. The default is 24. |
| `Providers__<name>__RequestFields__0` | Sets the field at index 0 of a provider's request template. See [Request fields](#request-fields). |

### Models lists

`Models` under each provider only controls what `/v1/models` lists. Callers can request any model name the vendor offers, so `openai/<any-model>` works even though the `openai` list is empty by default.

### Request fields

`RequestFields` is the request template of a provider or of the local model. The [API reference](api.md#request-templates) lists the default templates.

- When a vendor adds a request field, add its name to that provider's `RequestFields`. That is one line.
- An environment variable such as `Providers__<name>__RequestFields__0` sets the field at index 0. Add more with `__1`, `__2` and so on.
- Overrides replace entries by index, and they cannot shorten the list. To remove a field, edit `appsettings.json`.
- A provider entry without `RequestFields` stops the engine at startup, even when that provider has no key.
- A local model without `RequestFields` also stops the engine at startup, even when you disable the local model.
- Custom entries from version 1.0.0 must add `RequestFields`.
- The template also decides the renames of the [portable fields](api.md#portable-fields). For example, a provider that lists `random_seed` gets a portable `seed` as `random_seed`. A portable field works for a provider only when the template lists the field or one of its other names. The exception is `stream_options`. An OpenAI-format provider accepts it even when its template has none. The [API reference](api.md#portable-fields) gives the rules.

## Add another provider

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

Set its key with `Providers__groq__ApiKey`. The `RequestFields` list above is an example. Copy the fields that the vendor really accepts. A vendor that uses another name for a portable field needs a code change, because the rename table is in the code. See [ADR 0012](adr/0012-portable-fields.md).

[Supported models](api.md#supported-models) tells which vendors work.

## Startup checks

The engine checks every entry in `LocalModel` and `Providers` at startup, also an entry with no key. These mistakes stop the engine with a message:

- A setting that does not exist, for example a wrong name or the removed `MaxRetries`.
- A `Type` that is missing, a number, or not `OpenAiCompatible` or `Anthropic`.
- An `ApiKey` with spaces, line breaks or control characters. A key copied from a file often ends with a line break.
- A `BaseUrl` that uses `http` for an address that is not on this machine.
- A missing `RequestFields` list.
- A provider with the name `local`, or a name that contains `/`.
- An Anthropic provider with a `MaxTokensLimit` below 1.
- A `MaxTokensLimit` above 0 when `RequestFields` has no `max_tokens` and no `max_completion_tokens`.
- A value that is out of range or has the wrong format.
- A listening address on the network, or `localhost` with port 0.
- An `Engine:ReadyFile` path that is not valid, or that is in a folder that does not exist.

## Change the local model

The local model can be any GGUF model that the pinned llama.cpp supports, for example a Qwen model. The engine runs one local model at a time. To change it, set `LocalModel__ModelPath` and `LocalModel__ModelName`, then restart the engine.

Other local formats need another local server. Add it as an `OpenAiCompatible` provider on a loopback address such as `http://127.0.0.1:8000`, with a non-blank `ApiKey`.

## Network access

Programs on other machines cannot reach the engine at this time. The API has no authentication, so only programs on the same machine can connect. The engine refuses to start on a network address.
