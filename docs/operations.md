# Operations

This page tells how to watch the engine, how it handles failures of the local model, and how to find the cause of a problem.

## Monitor

The engine uses only the .NET built-in metrics and traces. No exporter is needed.

### Logs

Every response has an `X-Request-Id` header. Every log line of that request holds the same value as `RequestId`. A chat request also has `CompletionId`, which is the `id` of the answer. Each chat request writes one log line. The line holds these values:

- The model.
- The status.
- The time.
- The tokens.

The line never holds the prompt or the answer.

### Metrics

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

`model` is a model name from the configuration, or `other` for any other name.

### Traces

The activity source `XatLlmEngine` gives one `chat` activity for each request. Record it with `dotnet-trace`.

## One engine for each install

- Only one engine can run per install. A second engine started from the same folder stops at once with a message, and the first engine continues to work. To run two engines, use two copies in different folders.
- The engine keeps its own files in a data folder for each install: `xat-llm-engine/<hash>` inside the user's local application data folder. `Engine__DataFolder` sets another folder.
- If the engine is killed, its `llama-server` continues to run until the next engine start. That start stops the `llama-server`, but only if the process name and start time still match the engine's PID file in the data folder.

## Local model failures and recovery

- The engine does not start `llama-server` again after a failed start. A wrong setting does not correct itself. Examples are:
  - a wrong value in `ExtraArguments`,
  - a model that does not load, and
  - a load that takes too long.

  Local requests then return 502 with the cause.
- If `llama-server` stops unexpectedly after it was ready, the engine starts it again. After 3 stops in 10 minutes, the engine does not start it again, and local requests return 502.
- The engine sends at most one request per slot (`LocalModel__Parallel`) to `llama-server`. Other requests wait in the engine, up to `LocalModel__MaxQueuedRequests`. A request that finds the queue full gets 503 at once.
- A busy `llama-server` that fails its health checks is not stopped while it sends data. New local requests get 503 until it answers again. If it fails its checks and sends no data for `LocalModel__UnresponsiveTimeoutSeconds`, the engine starts it again.

[ADR 0009](adr/0009-local-load-and-hang-policy.md) describes the rules.

## Token budget accuracy

Requests that run at the same time can exceed a token budget, because each one can use up to its token limit. When a provider reports no tokens, the engine estimates them at 3 bytes for each token. The counts are in `usage.json` in the data folder. [ADR 0011](adr/0011-cost-control.md) describes the rules.

## Troubleshooting

### See the output of llama-server

A local error message has no file paths or server output. The engine log has the full cause. To see `llama-server`'s own output, start the engine with the argument `--Logging:LogLevel:XatLlmEngine.Infrastructure.LlamaCpp=Debug`.

### A llama.cpp config file stops the local model

The local model fails with 502 if a llama.cpp config file exists, because `llama-server` always reads it. A file from another llama.cpp installation also does this. Cloud providers continue to work. The engine does this check before each start of `llama-server`, and the engine log shows the path. The files are:

- Linux and macOS: `/etc/llama.cpp/config.ini`, and `$XDG_CONFIG_HOME/llama.cpp/config.ini` or `~/.config/llama.cpp/config.ini`.
- Windows: `%PROGRAMDATA%\llama.cpp\config.ini` and `%APPDATA%\llama.cpp\config.ini`.

To correct the problem:

1. Remove or rename the file.
2. Restart the engine.

### Environment variables that llama-server does not get

The engine removes the llama.cpp variables from the environment of `llama-server`. These are the variables that start with one of these prefixes:

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
