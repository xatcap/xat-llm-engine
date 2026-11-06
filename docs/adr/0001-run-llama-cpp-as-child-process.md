# 0001. Run llama.cpp as a child process

- Status: Accepted
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

The engine must serve local models with llama.cpp. Each release must include llama.cpp. llama.cpp publishes prebuilt `llama-server` programs for many platforms and GPU types. Its C API changes often, and a native crash inside a process ends that whole process.

## Decision

The engine starts the official `llama-server` program as a child process and calls it over HTTP on `127.0.0.1`. A hosted service (`LlamaServerSupervisor`) controls the child process. The service does these tasks:

- It chooses a free port and a random key for each run.
- It waits for `/health`.
- It restarts the server after a crash or a hang. [ADR 0009](0009-local-load-and-hang-policy.md) defines a hang.
- It stops the server when the engine stops.

The key goes through the `LLAMA_API_KEY` environment variable, because command-line arguments are visible to other users on the machine. The engine also removes its own `Providers__*` variables from the server's environment, so the server never sees cloud keys.

## Consequences

- A llama.cpp crash cannot stop the API. Cloud providers continue to work while the server restarts.
- To upgrade llama.cpp, change `build/llama-cpp.lock.json`. You do not have to write bindings again. A test then fails until a person checks the engine's llama.cpp rules against the new source and changes `LlamaCppCompatibility.VerifiedTag`.
- `llama-server` speaks the OpenAI protocol, so it shares its adapter with cloud providers.
- If the engine is killed, it cannot stop the child process. Amendment 3 changes this on Windows. A PID file in the engine's data folder lets the next start stop a leftover server. It does so only if the process name and start time both match the record. Until then, the leftover keeps the model in memory.
- Only one engine can run per install. See Amendment 2.
- Each request makes one more local HTTP call. The time for this call is small compared with the generation time.

## Amendment (2026-11-06)

The engine no longer restarts the server after every failure.

- A failed start ends the attempts until the engine restarts. Examples are:
  - a wrong setting,
  - a model that does not load,
  - a load timeout, and
  - a llama.cpp config file.
  Local requests return 502 with the cause.
- The engine restarts the server after a crash or a hang only when the server was ready before. After 3 crashes in 10 minutes, the engine stops the restarts, and local requests return 502.
- The engine removes all llama.cpp variables from the server's environment. It also removes the `Providers__*` variables. See `LlamaEnvironment.IsLlamaSetting`.

Reason: a wrong setting does not correct itself. A restart loop hid the cause, and callers saw only "still loading".

## Amendment 2 (2026-11-06)

- **One engine per install.** The engine takes a lock file in its data folder before anything else starts. A second engine from the same folder stops at once with a message. Before, it stopped the first engine's `llama-server` through the shared PID file. The system frees the lock when the engine process ends, also after a crash.
- **The data folder.** The engine keeps these files in one folder for each install:
  - the lock,
  - the PID file, and
  - the usage counts.
  The folder is `xat-llm-engine/<hash of the install folder>` in the user's local application data folder. `Engine:DataFolder` can set another folder. The engine reads the PID file of earlier versions once.
- **A llama.cpp config file** found at startup now makes only the local model fail, with 502. Before, it stopped the whole engine. Cloud providers now continue to work. This is the same as a file found before a restart.
- **A missing model** now gives 502 with no `Retry-After`, because only a person can correct it. Before, it gave 503, which asked callers to retry.
- **Safe messages.** A local failure gives callers a short message with no file paths, exception text or server output. The engine log has the full cause.
- **The hang rules** changed. See [ADR 0009](0009-local-load-and-hang-policy.md).
- **The structure.** `LlamaServerSupervisor` keeps only the run loop. Separate classes hold these rules:
  - the config file rule,
  - the environment,
  - the arguments,
  - the process,
  - the health checks,
  - the crash budget, and
  - the hang rules.
  Unit tests can check each class alone.

## Amendment 3 (2026-11-06)

A desktop app can include the engine and start it. This amendment gives the app a clean stop, and limits the servers that a killed engine leaves.

- **Stop when the input closes.** When `Engine:StopWhenInputCloses` is `true`, the engine stops when its standard input closes. The system closes the input when the parent app stops, also after a crash. This is the same on every system, and it is the only clean stop that a parent app on Windows has. The setting is off by default, because a service manager can give an input that closes at once.
- **Windows.** The engine puts `llama-server` in a job object with the "kill on job close" limit. When the engine process ends, also after a kill, Windows stops `llama-server`.
- **macOS and Linux.** .NET cannot run code in the child process before the program starts, so the engine cannot use `PR_SET_PDEATHSIG` or a new process group. A killed engine still leaves `llama-server` running until the next start. A parent app can stop both with a signal to the engine's process group, because `llama-server` stays in that group.
- **A watch of the parent process ID** was not added. The input close covers the same case, and it needs no polling and no check against a reused process ID.

## Alternatives considered

- **P/Invoke against `libllama`:** rejected because of the fast-changing API, and because a native crash would end the API process.
- **LLamaSharp:** rejected because its native builds would have to track llama.cpp separately, and it has the same crash exposure.
