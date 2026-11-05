# 0005. Store system prompts per provider in memory

- Status: Accepted
- Date: 2026-11-05
- Deciders: Project maintainer

## Context

Each provider takes its system prompt in a different place. The Anthropic API uses a top-level `system` field. OpenAI-format providers use a message with the role `system`. The local model also has a default prompt in its chat template.

Before this change, the operator had no setting for a system prompt. Each caller had to send the prompt in every request.

## Decision

Each provider has its own system prompt. The engine keeps each prompt in memory. A restart returns each prompt to its configured value.

Callers change a prompt with two routes. `PUT /v1/system-prompts/{provider}` replaces the prompt. `POST /v1/system-prompts/{provider}/append` adds text to its end. The operator sets the start value with `Providers__<name>__SystemPrompt` and `LocalModel__SystemPrompt`.

A store holds the prompts and limits. One handler per use case changes them.

A chat request cannot set its own system prompt. The engine returns 400 when a request has a top-level `system` field, or a message with the role `system` or `developer`. A blank prompt means the engine adds nothing.

Each adapter must implement a required method that checks the request and adds the stored prompt, because only the adapter knows its provider's format. The engine rejects a missing or non-list `messages` field, so that no request can avoid the stored prompt. This is another exception to ADR 0004's rule of not checking forwarded values.

This decision replaces part of [ADR 0004](0004-one-request-template-per-provider.md): the engine now rejects `system` and adds the stored prompt, so it no longer forwards `system` unchanged.

Each provider has a limit on its prompt length, set with `MaxSystemPromptLength`. The default is 8000 characters. The engine does not accept a configured limit above 1000000 characters, because the engine keeps each prompt in memory and sends it with every request.

## Consequences

- This is a breaking change. Callers that send `system` or a `system` message get 400 errors. OpenAI client libraries and frameworks that add a system message stop working until their code changes.
- The API has no authentication. Any program on this machine can change a provider's prompt, and every caller of that provider sees the change until the next restart.
- The 400 rule stops `system` fields and `system` messages only. It cannot stop instructions in a `user` message.
- A restart returns each prompt to its configured value. Callers must set their prompts again after a restart.
- A stored prompt replaces the local model's default prompt. Append adds only to the stored prompt, so with no stored prompt the default stops applying.
- A new adapter must implement this method.
- Known limit: a config-only OpenAI-compatible vendor with a different prompt field is not detected if that field is added to `RequestFields`. Do not add such a field.
- Cloud behaviour has not been tested against live vendor APIs.

## Alternatives considered

- **The caller's prompt wins:** a caller's prompt replaces the stored prompt. Rejected by the maintainer.
- **Combining both prompts:** the stored prompt and the caller's prompt are joined. Rejected by the maintainer.
- **Replacing silently:** the engine replaces the caller's prompt without an error. Rejected by the maintainer.
- **Saving prompts to a file:** prompts would survive a restart. Rejected by the maintainer.
