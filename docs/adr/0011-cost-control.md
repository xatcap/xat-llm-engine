# 0011. Limit the cost of cloud providers with a token limit and a token budget

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

Any program on the machine can call the engine, and each cloud request costs money. Before this decision, no setting limited the tokens of one request or of one provider. Nothing counted the tokens either, so nobody could see the cost.

## Decision

**A token limit for each request.** Each provider, and the local model, has `MaxTokensLimit` (0 means no limit). The Application layer applies it to the request's own copy of the body:

- The limit applies to the first of `max_tokens` and `max_completion_tokens` that the provider's template lists.
- A request that asks for more than the limit gets 400. A token field that is not a number also gets 400, because it could pass the limit.
- A request with no token field gets the limit.
- A limit with no token field in the template stops the engine at startup.
- Anthropic requires a limit on every request, so an Anthropic provider must set `MaxTokensLimit`. This replaces `DefaultMaxTokens`.

**A token budget for each cloud provider.** `TokenBudget` is the largest number of tokens for a rolling period. Input and output tokens both count. 0 means no budget. The length of the period is `BudgetPeriodHours` (default 24 hours):

- Before each request, the engine adds the tokens of the period. When the sum is at the budget or above it, the request gets 429 with the type `budget_exceeded_error`. `Retry-After` is the time until enough old tokens leave the period. It has no 30-second limit.
- `/health` reports the provider as `budget_exhausted`.
- After each answer, the engine adds its tokens. When the provider reports no tokens, the engine estimates them at 3 bytes of text for each token. This includes a stream that broke or that the caller stopped. A request that the caller canceled before any answer counts the estimate of its input, because the provider can have billed it. A request that failed before any answer counts no tokens.
- The counts are in hourly buckets in `usage.json` in the engine's data folder, so they survive a restart. One writer saves the newest state after each change. It writes a new file and then replaces the old one, so a crash never leaves half a file. A crash can lose the last changes.
- A file that the engine cannot read is kept as `usage.json.corrupt-<time>`, and the counts start from zero. The engine logs an error.

**Token counts in streams.** The OpenAI protocol sends token counts only when the request asks for them. When the template lists `stream_options`, the engine always adds `stream_options.include_usage`. The caller gets the usage piece only when it asked for it. When the template has no `stream_options`, the caller gets the counts whenever the provider sends them. Anthropic always sends them.

**Metrics.** Each request adds its tokens to `xat_engine.tokens`, with the tag `source` (`reported` or `estimated`).

## Consequences

- One request cannot ask for more tokens than the limit.
- A provider can go over its budget by the requests that run at the same time. Each of them can use up to its token limit. Set the budget with this in mind.
- An estimate is not exact. It is more likely too high than too low.
- The engine adds fields to the request: the token limit and `stream_options.include_usage`. The README lists them. ADR 0004 is changed to match.
- `DefaultMaxTokens` is gone. A configuration that still has it stops the engine at startup.

## Alternatives considered

- **A budget in money:** rejected because the prices change, and each vendor has its own prices.
- **Fixed days (UTC):** rejected because a rolling period does not give a full budget again at midnight.
- **403 for a used budget:** rejected because 429 is the OpenAI convention for a used quota, and the caller can try again later.
- **A database for the counts:** rejected because one small file is enough for one engine.
