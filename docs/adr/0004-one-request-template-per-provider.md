# 0004. Give each provider its own request template

- Status: Accepted, amended. [ADR 0012](0012-portable-fields.md) adds portable field names.
- Date: 2026-11-05
- Deciders: Project maintainer

## Context

The engine used one request format for every provider. A setting that only one vendor needed meant a change to shared code, and every provider used that code.

The engine silently removed fields that it did not know. ADR 0002 says that unsupported features return a 400 error instead.

## Decision

Each provider lists the request fields it accepts as `RequestFields` in configuration. This list is the provider's request template.

Callers send the native format of the provider they name. The engine forwards each listed field unchanged, except for the defaults that [the API reference](../api.md#request-templates) lists under "Request templates". That section is the contract for these defaults. The engine returns a 400 error for any other top-level field. Responses stay in one unified format.

This decision replaces two parts of ADR 0002. The consequence "Existing OpenAI clients work by changing their base URL and model name" now applies only to providers that use the OpenAI format. The part that gives one request format for all providers no longer holds.

## Consequences

- A vendor's request change needs only that provider's configuration. Shared code does not change.
- Callers must know the request format of each provider they use.
- The engine does not check the values of forwarded fields, except the token field. The vendor's own error comes back for a bad value.
- Response format changes stay in each provider's adapter, so callers still see one format.
- OpenAI-format providers still share `OpenAiClient`. If one of them drifts from the OpenAI format, it gets its own adapter.

## Amendment (2026-11-06)

The engine adds these values to the request it sends. It never changes a value that the caller sent:

- The local model gets `LocalModel:DefaultTemperature` when the request has no `temperature`.
- A provider with `MaxTokensLimit` gets the limit when the request has no token field. See [ADR 0011](0011-cost-control.md).
- An OpenAI-format provider whose template lists `stream_options` gets `stream_options.include_usage` in a stream.

The engine also checks the token field against the limit, and refuses tool content inside `messages` (ADR 0002).

## Amendment 2 (2026-11-06)

[ADR 0012](0012-portable-fields.md) changes the rule "callers send the native format" for the providers that use the OpenAI format. These providers also accept portable field names, and the engine renames them to the native names. The engine still never changes a value. For a template with no `stream_options`, the engine also checks the caller's `stream_options` and removes it. Anthropic is not changed.

## Alternatives considered

- **One common template only:** rejected because the coupling remains. A vendor-specific field still changes the template that every provider shares.
- **A common template plus a `provider_options` object:** rejected by the maintainer in favour of full per-provider templates.
- **gRPC:** rejected because OpenAI clients cannot use it. Every vendor and llama-server uses HTTP with server-sent events. Every caller would also need generated code.
