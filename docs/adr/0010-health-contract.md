# 0010. Report the health of each provider, with stable values

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

A person starts the engine now. Later, an app can start it and must know when it can send requests. Before this decision, `/health` reported only the local model. Its values came from the names of an internal enum, so a rename in the code changed the public API. It reported `healthy` when no provider was enabled. It did not show the load or the cloud providers.

## Decision

The Application layer builds the health (`GetHealthHandler`). Each provider reports a `ProviderState`. The Host converts each value to its public text in one switch, which lists each value. The text never comes from an enum name.

**Provider states:**

| Value | Meaning | Can take requests |
|---|---|---|
| `ready` | The provider can take requests. | Yes |
| `busy` | All slots are in use. New requests wait in the queue. | Yes |
| `starting` | The local model loads. | No |
| `restarting` | `llama-server` stopped, and the engine starts it again. | No |
| `unresponsive` | `llama-server` is busy and does not answer its health checks. | No |
| `missing` | A file is missing. Only a person can correct this. | No |
| `failed` | The provider failed until the engine restarts. | No |
| `stopping` | The engine is stopping. | No |
| `disabled` | The provider is configured, but turned off. | No |
| `budget_exhausted` | The provider has used its token budget (ADR 0011). | No |

The engine does not test cloud providers, so an enabled cloud provider is `ready`.

**Overall status**, from the enabled providers only:

| Value | Rule |
|---|---|
| `healthy` | Every enabled provider can take requests. |
| `degraded` | At least one enabled provider can take requests, and at least one cannot. |
| `unhealthy` | No enabled provider can take requests. This includes zero enabled providers, and an engine that is stopping. |

**Routes:**

| Route | Status | Use |
|---|---|---|
| `GET /health` | Always 200. | People and scripts. The body has `status`, `version`, `providers` (name, state, message, and the load for `local`), and the older fields `local` and `message`. |
| `GET /health/live` | 200 while the process runs. | A supervisor that restarts a dead process. |
| `GET /health/ready` | 200 when the overall status is not `unhealthy`, otherwise 503. | An app that waits before it sends requests. |
| `GET /health/ready?provider=<name>` | 200 when that provider can take requests, otherwise 503. | An app that needs one provider, for example `local`. |

The health routes show provider names. The engine listens only on loopback (see [Network access](../configuration.md#network-access)), so only programs on the same machine can read them.

## Consequences

- A rename in the code cannot change the health values.
- An app can wait for `/health/ready?provider=local` before it sends local requests.
- `unhealthy` is a new overall value. A new state is not a breaking change (ADR 0005), so callers must accept values that they do not know.
- `local` and `message` stay for older callers. They can go in a later major version.

## Alternatives considered

- **Return 503 from `/health` when a provider fails:** rejected because a person must always read the body. Cloud providers can still work.
- **Test each cloud provider in each health check:** rejected because each test costs a request, and it can count against a rate limit.
