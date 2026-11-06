# 0007. Map each kind of failure to one status code and one error type

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

Callers decide what to do after a failure: correct the request, wait and try again, or stop. They need the same status code for the same kind of failure, from every provider. The engine also must not show internal details, such as file paths, exception text or a provider's message about the engine's key.

## Decision

The code returns an expected failure as a `Result` with an `EngineError`. An `EngineError` has three parts:

- an `ErrorKind`,
- a message that is safe to show, and
- an optional wait. The Host maps the kind to the status, in one place (`ErrorMapping`):

| `ErrorKind` | Status | `type` | `Retry-After` | What the caller can do |
|---|---|---|---|---|
| `InvalidRequest` | 400 | `invalid_request_error` | No | Correct the request. |
| `NotFound` | 404 | `not_found_error` | No | Correct the provider or model name, or set the provider's API key. |
| `RateLimited` | 429 | `rate_limit_error` | Yes | Wait, then try again. |
| `Unavailable` | 503 | `service_unavailable_error` | Yes | Wait, then try again. |
| `Upstream` | 502 | `upstream_error` | No | A retry does not help. Read the message. |
| `Overloaded` | 503 | `overloaded_error` | Yes | The engine's queue for the provider is full. Wait, then try again. |
| `BudgetExceeded` | 429 | `budget_exceeded_error` | Yes | The provider has used its token budget. Wait for the time in `Retry-After`. |

The Host also returns these statuses itself:

| Status | `type` | When |
|---|---|---|
| 404 | `not_found_error` | The route does not exist. |
| 405 | `invalid_request_error` | The route does not accept the HTTP method. |
| 413 | `invalid_request_error` | The body is larger than `Api:MaxRequestBodyBytes`. |
| 415 | `invalid_request_error` | The `Content-Type` is not `application/json`. |
| 500 | `internal_error` | A fault in the engine. The message has no details. The log has the cause. |

Every switch over `ErrorKind` lists each value and has no default arm. The build fails when a new kind has no status.

The engine forwards a provider's own message only for these statuses. The caller can correct those problems:

- 400
- 404
- 413
- 422

Other provider messages stay hidden, because an authentication error can echo part of the engine's key. A local failure gives a short message to the caller. The engine log gets the details.

`Retry-After` is the provider's own wait, with these limits:

- at most 30 seconds,
- rounded up to whole seconds, and
- at least 1 second.

When the engine returns a 429 or 503 with no provider wait, the value is 5 seconds. A used token budget is different. `Retry-After` is the time until enough tokens leave the budget period. This time has no 30-second limit (ADR 0011).

## Consequences

- A caller can choose its action from the status alone, for every provider.
- A new `ErrorKind` needs a status, a type and a row in this table.
- A failure that is not in this table is a bug, and it gives 500.

## Alternatives considered

- **Forward the provider's status unchanged:** rejected because providers use different statuses for the same problem, for example Anthropic's 529.
- **Forward every provider message:** rejected because some messages contain part of the engine's key.
