# 0008. Read the first piece before the success status

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

A streamed answer uses server-sent events. After the engine sends status 200, it cannot change the status. Many failures happen before the first piece, for example a wrong key, a rate limit or a model that is not loaded. Callers handle these failures best as a normal HTTP error.

## Decision

The Application layer holds the stream rules, in `StreamChatHandler` and `StreamedAnswer`. The Host only writes the events.

1. The handler starts the provider's stream and reads the first piece before it returns. A failure before the first piece is a normal HTTP error, with the status from ADR 0007.
2. A stream that ends before its first piece is an `Upstream` failure (502).
3. After the first piece, the Host sends status 200 and a role piece. Then it sends one event for each piece with text or a finish reason.
4. The answer must end with a finish reason. A stream that ends without one fails, because the answer can be incomplete.
5. The token counts can arrive in any piece. The Host sends them in one last piece with no choices, when the provider reported them and the caller can get them (ADR 0011).
6. A complete stream ends with `data: [DONE]`.
7. A failure after the 200 ends the stream with an `error` event that has the normal error body, and no `[DONE]`.
8. The caller must dispose the started stream, so that the provider's connection returns to the pool.

## Consequences

- Callers get a real status code for most failures.
- A long wait for the first piece, for example a slow local model, could reach the read time limit of the caller. The amendment below handles this.
- A caller must handle an `error` event in a stream.

## Amendment (2026-11-06): keep-alive

When the first piece takes longer than `Api:StreamCommitSeconds` (default 15), the engine sends status 200 and the comment `: keep-alive`. It sends the comment again every 15 seconds until the first piece arrives. Callers that read server-sent events ignore comments, but each comment resets their read time limit.

- A failure before this time still gets its real status code. This includes these failures, because the engine refuses them at once:

- a request that the engine refuses,
- a full queue, and
- a used budget.
- A failure that comes after the engine has sent status 200 becomes an `error` event, with no `[DONE]`.
- `StreamCommitSeconds` set to 0 turns the comments off. Then the engine waits for the first piece without a limit, as before.

## Alternatives considered

- **Send 200 at once, and report every failure as an event:** rejected because callers and client libraries handle an HTTP error better.
- **Keep the rules in the Host:** rejected because a second API, such as gRPC, would need to copy them.
