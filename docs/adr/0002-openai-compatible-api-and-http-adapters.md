# 0002. Expose an OpenAI-compatible API, with plain HTTP adapters

- Status: Partly replaced by [ADR 0004](0004-one-request-template-per-provider.md)
- Date: 2026-11-04
- Deciders: Project maintainer

## Context

External processes need one interface to local and cloud models. Those processes may be written in any language, and many already use an OpenAI client library.

## Decision

The public API follows OpenAI's `POST /v1/chat/completions` and `GET /v1/models`. The text before the first `/` in the model name selects the provider, for example `local/ministral-3-3b` or `anthropic/claude-sonnet-5-5`.

Adapters use `HttpClient` directly rather than vendor SDKs. One `OpenAiCompatibleChatProvider` covers OpenAI and any compatible service through configuration alone, and `AnthropicChatProvider` handles the Anthropic Messages API.

## Consequences

- Existing OpenAI clients work by changing their base URL and model name.
- Adding a compatible vendor needs configuration only, and all providers share streaming and error behaviour.
- Unsupported features such as tools return a 400 error rather than being silently ignored. Each new feature must be added to the adapters by hand.
- The engine must track vendor API changes itself, without help from an SDK update.

## Alternatives considered

- **A custom API:** rejected because every caller would then need custom client code.
- **Vendor SDKs:** rejected because each adapter uses only one or two calls, and SDK types would make streaming and errors differ per provider.
