# 0001. Run llama.cpp as a child process

- Status: Accepted
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

The engine must serve local models with llama.cpp, and each release must ship llama.cpp with it. llama.cpp publishes prebuilt `llama-server` programs for many platforms and GPU types. Its C API changes often, and a native crash inside a process ends that whole process.

## Decision

The engine starts the official `llama-server` program as a child process and calls it over HTTP on `127.0.0.1`. A hosted service (`LlamaServerSupervisor`) owns its life: it chooses a free port and a random key for each run, waits for `/health`, restarts the server after a crash or a hang (three failed health checks in a row), and stops it on shutdown. The key goes through the `LLAMA_API_KEY` environment variable, because command-line arguments are visible to other users on the machine. The engine also removes its own `Providers__*` and `Engine__*` variables from the server's environment, so the server never sees cloud or caller keys.

## Consequences

- A llama.cpp crash cannot bring down the API, and cloud providers keep working while the server restarts.
- Upgrading llama.cpp means changing `build/llama-cpp.lock.json`, not rewriting bindings.
- `llama-server` speaks the OpenAI protocol, so it shares its adapter with cloud providers.
- A hard kill of the engine cannot clean up the child process. A PID file in the `xat-llm-engine` folder, inside the user's local application data folder, lets the next start stop a leftover server. It does so only if the process name and start time both match the record. Until then, the leftover keeps the model in memory.
- Only one engine should run per install, because they share that PID file.
- Each request crosses a local HTTP hop. This is small compared with generation time.

## Alternatives considered

- **P/Invoke against `libllama`:** rejected because of the fast-changing API, and because a native crash would end the API process.
- **LLamaSharp:** rejected because its native builds would have to track llama.cpp separately, and it has the same crash exposure.
