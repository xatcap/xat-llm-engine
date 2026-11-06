# 0012. Accept portable field names for providers that use the OpenAI format

- Status: Accepted
- Date: 2026-11-06
- Deciders: Project maintainer

## Context

ADR 0004 gives each provider its own request template. A caller must send the native format of the provider that it names. An app that lets its users choose "offline or cloud" must then build a different body for each provider.

The providers that use the OpenAI format differ only in some names:

- The token field is `max_tokens` for `local` and `mistral`, and `max_completion_tokens` for `openai`.
- The seed field is `seed` for `local` and `openai`, and `random_seed` for `mistral`.
- `mistral` has no `stream_options`. OpenAI client libraries often send `stream_options.include_usage`.

The goal: a caller changes only `model` to move between `local`, `openai` and `mistral`.

## Decision

**Portable fields.** These names are portable:

- `messages`, `temperature`, `top_p`, `stop`, `frequency_penalty`, `presence_penalty` and `response_format`
- `max_tokens` and `max_completion_tokens`
- `seed`
- `stream_options`

**Which providers.** The local model and every provider with `Type: OpenAiCompatible` accept the portable fields (`IChatProvider.AcceptsPortableFields`). Anthropic gets no renames, because its format differs in more than names.

**Rename only.** When the template does not list a portable field, the engine renames it to the native name that the template lists. A fixed table in the code gives the other names:

| Portable name | Other names, in order |
|---|---|
| `max_tokens` | `max_completion_tokens` |
| `max_completion_tokens` | `max_tokens` |
| `seed` | `random_seed` |

The template decides the target. The engine never changes a value. The table goes one way only, so `random_seed` stays a native name of Mistral and is not portable.

**Native names stay.** A request that uses only native names reaches the provider with no change. The native names are not deprecated.

**Two names for one setting.** When a rename makes two fields of the request into one native field, the request gets 400. An example is `seed` and `random_seed` to `mistral`. A template that lists both names keeps both, because no rename takes place.

**Stream options.** For a template with no `stream_options`, the engine accepts `stream_options` only when it holds only `include_usage` with `true` or `false`. Other content returns 400, because the engine cannot apply it.

- The engine removes the field from the request.
- With `include_usage` set to `false`, the caller gets no token counts in a stream.
- With `true`, or with no `stream_options`, the caller gets the counts that the provider sends.
- The engine can only hold back counts. It cannot make a provider send counts. A vendor that sends counts only on request then sends none, and the budget uses an estimate (ADR 0011).

**Other fields.** A field that is not portable works only with the providers that list it, for example `grammar`, `reasoning_effort` and `safe_prompt`. Other providers return 400, and the message says that the field is not portable.

**Cost control.** The rename happens before the token limit (ADR 0011). The limit applies to the native field. Its errors name the field that the caller sent.

**Trace.** The `chat` activity gets the tag `xat_engine.renamed_fields` with the names of the renamed fields, for example `seed>random_seed`. A removed `stream_options` shows as `stream_options>(removed)`. The tag never holds values.

**Versioning.** A field that returned 400 and is now accepted is a new feature, so it needs a MINOR version (ADR 0005).

## Consequences

- One body with only portable fields works for `local`, `openai` and `mistral`.
- Requests with native names do not change.
- A new vendor that uses `max_tokens`, `max_completion_tokens` or `random_seed` still needs only configuration. A vendor with another name for a portable field needs a new entry in the table, which is a code change.
- The same names do not give the same results. Value ranges, seed results and the support of `response_format` types differ for each vendor. The vendor's own error comes back for a bad value.
- The portable fields are now part of the contract. A later removal needs a major version.
- The text of some 400 messages changes. The status and the error type stay the same (ADR 0007).

## Alternatives considered

- **A setting for each provider with its other names (`FieldAliases`):** rejected. Each provider entry would repeat the table, the setting needs its own startup checks, and it can disagree with `RequestFields`.
- **Renames in the adapters:** rejected. The template check and the token limit run in the Application layer, so they would need the same table.
- **Remove any `stream_options` for Mistral:** rejected, because the engine would drop other content with no message.
- **A response header that lists the renames:** rejected, because no caller needs it now. The trace and the documentation show the renames.
- **Anthropic:** not part of this decision. System messages move to a top-level field, image parts have another format, and some portable fields do not exist there. A later ADR can decide this.
