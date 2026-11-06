# 0006. Never retry a request, and limit the time of each part of a request

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

Before this decision, the engine retried a cloud request up to 2 times after a 429 status, a 5xx status or a connection failure. A chat request is not idempotent. A provider can charge for a request that failed after it started work, so a retry can cost money twice. A retry also hides problems from the caller, and many callers already retry with their own client library. Then one caller request can become 9 provider requests.

The engine had one time limit for the wait for the response headers. After the headers arrived, it had no limit. A provider that sent the headers and then stopped kept the request open until the caller disconnected.

## Decision

The engine never retries a request, to a cloud provider or to the local model. The caller decides whether to try again. When a provider sends a `Retry-After` header, the engine gives that wait to the caller, up to 30 seconds. When the engine returns a 503 or a 429 with no wait from a provider, it sends `Retry-After: 5`. A used token budget sends the time until enough tokens leave the period (ADR 0011).

Each cloud provider has two time limits:

| Setting | Default | What it limits |
|---|---|---|
| `RequestTimeoutSeconds` | 600 | The wait from the request until the response headers arrive. A non-streaming answer starts only when it is complete, so this time is long. |
| `ReadIdleTimeoutSeconds` | 300 | The wait for more data after the headers. It applies to each read of the body, and so to the time between stream events. Reasoning models can send nothing for minutes. |

When a limit ends, or the connection breaks, the result is a 503 with `Retry-After`. If a stream has already started, it ends with an `error` event. The engine applies these limits itself, with the clock that the tests can control.

The local model has no time limits of this kind. Local generation can be slow, and the supervisor finds a server that hangs (ADR 0001).

The engine also:

- Reads at most 64 KiB of an error body and at most 16 MiB of an answer.
- Does not follow redirects. .NET removes the `Authorization` header on a redirect to another host, but it keeps other headers such as Anthropic's `x-api-key`.

## Consequences

- One caller request is at most one provider request, so the cost is clear.
- A caller that wants retries must do them. Most client libraries already do.
- A short provider failure that a retry would hide now reaches the caller as a 503 or 429.
- A provider that stalls frees the request after `ReadIdleTimeoutSeconds`.
- The engine no longer needs Polly or `Microsoft.Extensions.Http.Resilience`. The release packages ship fewer third-party packages.
- The `MaxRetries` setting is gone. A configuration that still has it stops the engine at startup, with a message.

## Alternatives considered

- **Retry only before the provider starts work, for example after a connection failure:** rejected because the engine cannot know whether the provider started work.
- **Retry only 429 and 503:** rejected because the provider asks the caller to wait. The engine would hold the caller's request open for that time.
- **One overall time limit for the whole request:** rejected because a long stream would then fail even while data still arrives.
