# 0009. Queue local requests in the engine, and stop a busy server only when it sends nothing

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

The API must work under heavy load. Before this decision, every local request went to `llama-server` at once. `llama-server` has a fixed number of slots (`Parallel`, default 1). The other requests waited inside `llama-server`, and each one held a connection and an HTTP thread. The engine also replaced the server after three failed health checks in a row. Under load, the health checks could get no answer in time. The engine then stopped a working server in the middle of a generation, and counted it as a crash. After three such crashes in ten minutes, the local model failed until a person restarted the engine.

A server that really hangs never exits by itself, so the engine must still find it.

## Decision

**A gate in the engine.** The engine sends at most one request per slot to `llama-server`. The number of slots is `Parallel`, or the `--parallel` value in `ExtraArguments`. Other requests wait in the engine, in order of arrival:

- At most `MaxQueuedRequests` requests can wait (default 32). A request that finds the queue full gets 503 with `Retry-After` at once, with the error type `overloaded_error`.
- A request waits until a slot is free or its caller disconnects. A caller that disconnects leaves the queue.
- When the server stops serving, every waiting request gets the error at once.
- A stream keeps its slot until the caller has read or closed it.

The engine does not set `--threads-http`. In the tested llama.cpp build, `llama-server` keeps at least four HTTP threads more than its slots. It adds more threads when it needs them. The engine sends at most one request per slot, so a health check always has a thread.

**The hang rules.** The engine checks `/health` every `HealthCheckIntervalSeconds` (default 10):

| Condition | Action |
|---|---|
| The process exited. | Restart. This counts as a crash. |
| 3 failed checks in a row, and no request runs. | The server hangs. Restart. This counts as a crash. |
| 3 failed checks in a row, and requests run. | State `unresponsive`. New requests get 503. Requests in progress continue. The server is not stopped. |
| `unresponsive`, and no healthy check and no data from the server for `UnresponsiveTimeoutSeconds` (default 300). | The server hangs. Restart. The requests in progress fail. |
| A healthy check. | Back to `ready`. New requests can start. |

"Data" is a stream piece or a complete answer from `llama-server`.

**The port check.** After the model loads, the engine calls `/props` with the key of this run. A program that took the port before `llama-server` cannot know that key, so the local model fails with a clear message.

## Consequences

- The number of requests at `llama-server` never grows with the load, so the health checks still get an answer.
- A full queue gives a fast 503, not a long wait.
- A working server is not stopped while it sends data.
- A non-streaming generation can take longer than `UnresponsiveTimeoutSeconds` while the server also fails its health checks. Then the engine stops it. Such a server does not follow its own health contract. Increase the setting if this occurs.
- These rules assume that `llama-server` answers `/health` while all slots are busy, and that `/props` needs the key. The tag in `LlamaCppCompatibility.VerifiedTag` must be checked against these assumptions when llama.cpp changes.

## Alternatives considered

- **Keep the queue in `llama-server`:** rejected because each waiting request holds an HTTP thread, and the health checks then fail under load.
- **Never stop a server that has requests:** rejected because a hung server would then keep its requests for ever.
- **A longer health check interval:** rejected because it moves the problem to a higher load. It also slows the search for a real hang.
