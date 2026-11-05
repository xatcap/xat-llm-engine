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
- It restarts the server after a crash or a hang. A hang is three failed health checks in a row.
- It stops the server when the engine stops.

The key goes through the `LLAMA_API_KEY` environment variable, because command-line arguments are visible to other users on the machine. The engine also removes its own `Providers__*` variables from the server's environment, so the server never sees cloud keys.

## Consequences

- A llama.cpp crash cannot stop the API. Cloud providers continue to work while the server restarts.
- To upgrade llama.cpp, change `build/llama-cpp.lock.json`. You do not have to write bindings again.
- `llama-server` speaks the OpenAI protocol, so it shares its adapter with cloud providers.
- If the engine is killed, it cannot stop the child process. A PID file in the `xat-llm-engine` folder, inside the user's local application data folder, lets the next start stop a leftover server. It does so only if the process name and start time both match the record. Until then, the leftover keeps the model in memory.
- Only one engine should run per install, because they share that PID file.
- Each request makes one more local HTTP call. The time for this call is small compared with the generation time.

## Alternatives considered

- **P/Invoke against `libllama`:** rejected because of the fast-changing API, and because a native crash would end the API process.
- **LLamaSharp:** rejected because its native builds would have to track llama.cpp separately, and it has the same crash exposure.
