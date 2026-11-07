# Changelog

This file records the changes in each release. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version numbers follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html), as [ADR 0005](docs/adr/0005-versioning-policy.md) describes.

The release workflow uses the section of each version as its release notes. Write each entry for the people who use the engine, and mark each breaking change with **Breaking**.

## [Unreleased]

## [1.0.2] - 2026-11-06

### Added

- Support for apps that include the engine. See `docs/embedding.md`.
  - `Engine:ReadyFile`. When the API starts, the engine writes its address, its process ID and its version to this file. It deletes the file when it stops, and removes an old file when it starts. A folder that does not exist stops the engine at startup.
  - `Engine:StopWhenInputCloses` (default `false`). The engine stops when its standard input closes, also when the parent app stops unexpectedly.
  - `Urls=http://127.0.0.1:0` lets the system choose a free port. The engine refuses `localhost` with port 0, because the server cannot use it.
  - On Windows, a killed engine also stops its `llama-server`.
  - The engine logs a warning when `Engine:DataFolder` is empty, because an app update to a new folder then starts the token budgets again from zero.
- `/health` gives a `budget` object for each provider with a token budget: `used`, `limit`, `period_hours`, and `available_at` when the budget is used up.
- `docs/openapi.json` is the API document of the code, and each release attaches it with the release version. A test fails when the code and the file are different.
- A deprecation rule: a deprecated item stays for at least one minor version before a removal. See ADR 0005.
- A privacy section in `docs/operations.md`.
- Portable fields for the local model and every OpenAI-format provider. One body works for `local`, `openai` and `mistral`, so a caller changes only `model`. The engine renames a portable field to the provider's native name. For example, `seed` becomes `random_seed` for Mistral, and `max_tokens` becomes `max_completion_tokens` for OpenAI. The engine never changes a value. Native names still work. See ADR 0012 and "Portable fields" in `docs/api.md`.
  - Two names for one setting in one request return 400.
  - Mistral accepts `stream_options` with only `include_usage`. The engine removes the field, and holds back the token counts when `include_usage` is `false`.
  - A token limit error names the field that the caller sent.
  - The `chat` trace has the tag `xat_engine.renamed_fields`, with names only.
- Each package has a `licenses/` folder with the full license and notice texts of each third-party component. This includes the libraries inside llama.cpp and the .NET runtime. The Windows package also includes the license of LLVM OpenMP (`libomp.dll`). The release stops when a text is missing.
- `Api:StreamCommitSeconds` (default 15). When a stream waits longer for its first piece, the engine sends status 200. It then sends a `: keep-alive` comment every 15 seconds. These comments stop the caller's read time limit from ending.
- `Providers:<name>:TokenBudget` and `BudgetPeriodHours`. A provider that has used its budget in the rolling period gets 429 with the type `budget_exceeded_error`, and `/health` reports `budget_exhausted`. The counts survive a restart.
- `LocalModel:MaxTokensLimit`. A request that asks for more tokens than a limit gets 400.
- Metrics and traces with the .NET built-in types. They cover:
  - chat requests,
  - durations,
  - the time to the first piece,
  - tokens,
  - `llama-server` restarts, and
  - the local queue.
  See "Monitor" in `docs/operations.md`.
- The `X-Request-Id` response header. Every log line of a request holds the same ID. Each chat request writes one log line. The line holds these values:
  - the model,
  - the status,
  - the time, and
  - the tokens.
- `GET /health/live` and `GET /health/ready`. `/health/ready?provider=local` returns 200 only when the local model can take requests.
- `/health` returns `version` and `providers`, with the state of each configured provider and the load of the local model.
- The engine sends at most one request per slot to `llama-server`, and other local requests wait in the engine. `LocalModel:MaxQueuedRequests` (default 32) limits the queue. A request that finds the queue full gets 503 with the type `overloaded_error`.
- `LocalModel:UnresponsiveTimeoutSeconds` (default 300) and the `unresponsive` state of the local model. A busy `llama-server` that fails its health checks is no longer stopped while it sends data.
- `Engine:DataFolder`. The engine keeps its lock, its PID file and its usage counts in one data folder for each install.
- The engine checks that the server on the local port knows the key of this run.
- `Api:MaxRequestBodyBytes` (default 20 MiB). A larger request body gets 413. Before, a very large body gave 500.
- **Breaking:** The engine refuses tool content inside `messages` with a 400 error, for every provider. The tool content is one of these:
  - the roles `tool` and `function`,
  - the fields `tool_calls` and `function_call`, or
  - tool content blocks, such as `tool_use` and `tool_result`.
- `Providers:<name>:ReadIdleTimeoutSeconds` (default 300). A provider that sends the headers and then stops sending data now fails with 503. Before, the request stayed open until the caller disconnected.
- `CHANGELOG.md`. A release now needs a section for its version.

### Changed

- The 400 message for a field that a provider does not accept now also lists the portable fields that the provider accepts. The status and the error type are the same.
- **Breaking:** The engine no longer checks API keys from callers. The `Engine:ApiKeys` setting is removed. A configuration that still has it stops the engine at startup. The engine also no longer returns 401, and the error type `authentication_error` is gone.
- **Breaking:** The engine refuses to start on any address other than loopback. Before, a network address was allowed when `Engine:ApiKeys` had a key.
- **Breaking:** `/health` returns 200 in every case. Before, it returned 503 when the engine was unhealthy. Use `/health/ready` for a status code.
- **Breaking:** `Providers:<name>:DefaultMaxTokens` is now `MaxTokensLimit`. It is also a ceiling: a request that asks for more gets 400. An Anthropic provider must set it.
- For an OpenAI-format provider whose template lists `stream_options`, the engine always asks the provider for token counts in a stream. The caller still gets the usage piece only when it set `stream_options.include_usage`.
- `/health` returns `unhealthy` when no enabled provider can take requests. Before, it returned `healthy` when no provider was enabled.
- **Breaking:** The local model state `stopped` is now `stopping`, in `/health` and in the `xat_engine.local.state` metric.
- **Breaking:** Only one engine can run per install folder. A second engine stops at once with a message. Before, it stopped the first engine's `llama-server`.
- **Breaking:** A missing local model gives 502 with no `Retry-After`. Before, it gave 503.
- A llama.cpp config file found at startup makes only the local model fail. Before, it stopped the whole engine.
- Local error messages no longer contain file paths or server output. The engine log has the full cause.
- After 3 crashes, the message no longer tells you to correct the settings.
- **Breaking:** The engine never retries a request to a cloud provider. A retry of a generation can cost money twice, so the caller decides. The `MaxRetries` setting is removed. The engine still gives a provider's `Retry-After` to the caller.
- **Breaking:** Each provider must set `Type`. A missing `Type`, a number or an unknown name stops the engine at startup. Before, these values made the provider an OpenAI-compatible provider without a message.
- **Breaking:** A setting that does not exist in `LocalModel` or `Providers` stops the engine at startup. Remove `MaxRetries` from your settings.
- An `ApiKey` with spaces, line breaks or control characters stops the engine at startup. Before, every request then failed with status 500.
- A broken connection while the engine reads a provider's answer or error now gives 503 with `Retry-After`. Before, it gave 500.
- `RequestTimeoutSeconds` now limits only the wait for the response headers.
- The engine does not follow redirects from a provider. A redirect gives 502.
- The engine reads at most 64 KiB of a provider's error body and at most 16 MiB of an answer.
- When `llama-server` refuses the engine's key, the message no longer tells you to check an API key, because the engine makes that key.
- The release packages no longer include Polly or `Microsoft.Extensions.Http.Resilience`.
- NuGet packages are pinned in a `packages.lock.json` file for each project. CI and the release restore in locked mode.
- The release workflow runs all tests on all platforms before it builds a package.

### Fixed

- A listening address with the wrong format in `Urls` or `Kestrel:Endpoints` now stops the engine with the same kind of startup error as the other address checks, and the message names the address. Before, the engine stopped with an error from ASP.NET.
- The Windows package includes the llama.cpp license in `llama/LICENSE`. The llama.cpp Windows archive does not include this license, so the Windows packages of 1.0.0 and 1.0.1 did not have it.

## [1.0.1] - 2026-11-05

### Changed

- Each provider has its own request template.
- The documentation follows the ASD-STE100 writing rules.

## [1.0.0] - 2026-11-05

### Added

- The first release.
