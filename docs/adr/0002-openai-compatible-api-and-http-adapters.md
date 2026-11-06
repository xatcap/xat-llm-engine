# 0002. Expose an OpenAI-compatible API, with plain HTTP adapters

- Status: Partly replaced by [ADR 0004](0004-one-request-template-per-provider.md). [ADR 0012](0012-portable-fields.md) gives back one request format for the portable fields.
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

External processes need one interface to local and cloud models. Those processes can use any language, and many already use an OpenAI client library.

## Decision

The public API follows OpenAI's `POST /v1/chat/completions` and `GET /v1/models`. The text before the first `/` in the model name selects the provider, for example `local/ministral-3-3b` or `anthropic/claude-sonnet-5-5`.

Adapters use `HttpClient` directly rather than vendor SDKs. One `OpenAiCompatibleChatProvider` covers OpenAI and any compatible service through configuration alone, and `AnthropicChatProvider` handles the Anthropic Messages API.

## Consequences

- To use an existing OpenAI client, change its base URL and model name.
- A new compatible vendor needs only configuration. All providers share the same streaming and error behaviour.
- Unsupported features such as tools return a 400 error. The engine does not ignore them silently. A developer must add each new feature to the adapters.
- The engine also checks inside `messages`. A message gets a 400 error, for every provider, when it has one of these:
  - the role `tool` or `function`,
  - the field `tool_calls` or `function_call`,
  - a tool content block, such as `tool_use` or `tool_result`.
  Images pass unchanged. (Added 2026-11-06.)
- The engine must track vendor API changes itself, without help from an SDK update.

## Alternatives considered

- **A custom API:** rejected because every caller would then need custom client code.
- **Vendor SDKs:** rejected because each adapter uses only one or two calls, and SDK types would make streaming and errors differ per provider.
