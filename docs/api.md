# API reference

This page is for programs that call the engine. The engine listens at `http://127.0.0.1:8420` by default. See [Quick start](quickstart.md) to start the engine and send a first request.

## How it works

- Callers send plain HTTP requests, or use an OpenAI client library.
- The model name selects the provider, for example `local/ministral-3-3b` or `openai/gpt-4o-mini`.
- Each provider has its own request template, in that provider's native format. See [Request templates](#request-templates).
- Every provider returns the same OpenAI-style response format, the same errors and the same health values.
- Local requests go to a [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` process. The engine starts and supervises this process.
- Cloud requests go directly to the vendor's API.

### Client libraries

OpenAI client libraries work for these providers, with the fields in each provider's template:

- `local`
- `openai`
- `mistral`

This example streams an answer from the local model with the OpenAI client library for Python:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:8420/v1", api_key="unused-on-loopback")
for chunk in client.chat.completions.create(
    model="local/ministral-3-3b",
    messages=[{"role": "user", "content": "Hello"}],
    stream=True,
):
    if chunk.choices and chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="")
```

## Routes

| Route | Purpose |
|---|---|
| `POST /v1/chat/completions` | Chat, with or without streaming. The accepted fields depend on the provider. See [Request templates](#request-templates). |
| `GET /v1/models` | Lists the configured models as `provider/model`. |
| `GET /health` | Always 200. Returns `status` (`healthy`, `degraded` or `unhealthy`), `version`, and `providers`: the `name`, `state` and `message` of each configured provider. For `local`, it also returns `active`, `queued`, `capacity` and `queue_limit`. The older fields `local` and `message` describe the local model. |
| `GET /health/live` | 200 while the engine runs. |
| `GET /health/ready` | 200 when at least one provider can take requests, otherwise 503. Add `?provider=local` to ask about one provider. |
| `GET /swagger` | Swagger UI. Development only. |
| `GET /openapi/v1.json` | The OpenAPI document. Development only. The same document is in [openapi.json](openapi.json), and each release attaches it. |

Every response includes an `X-Request-Id` header. Every log line of that request holds the same value as `RequestId`.

## Request templates

Each provider accepts the fields in its template. The engine forwards the values unchanged. It checks the token field when the provider has a `MaxTokensLimit`. For other fields, a bad value returns the vendor's own 400 message. This section is the contract for the values that the engine adds.

| Provider | Accepted fields |
|---|---|
| `local` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `stream_options`, `top_k`, `min_p`, `repeat_penalty`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `grammar` |
| `openai` | `messages`, `temperature`, `top_p`, `max_completion_tokens`, `stop`, `stream_options`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `reasoning_effort`, `user` |
| `mistral` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `random_seed`, `safe_prompt`, `frequency_penalty`, `presence_penalty`, `response_format` |

These rules apply to every provider:

- `model` and `stream` are always accepted.
- Any other top-level field that the template does not list returns a 400 error. The error names the field and lists the accepted fields. This happens even when the field's value is `null` or a default.
- The local model uses `LocalModel__DefaultTemperature` when a request sends no `temperature`.
- A provider with a `MaxTokensLimit` refuses a token value above the limit. It also refuses a token value that is not a number, or that is not a whole number of at least 1. These checks return a 400 error. A request with no token field gets the limit. The token field is the first of `max_tokens` and `max_completion_tokens` in the template.

Streaming usage depends on the provider:

- A provider whose template lists `stream_options` sends a usage piece only when it is asked for one. The engine always asks for the counts, because the metrics and the logs use them. It gives the counts to the caller only when the caller sets `stream_options.include_usage` to `true`.
- Mistral's template has no `stream_options`. The engine gives Mistral's counts to the caller when Mistral sends them.

To change a template, edit `RequestFields` in `appsettings.json` or in environment variables.

## Content rules

Answers are text only. `tools` and `n` are not in the default templates, because the unified response carries one text answer.

The engine does not inspect the items inside `messages`. It forwards `messages`, including images, to the provider unchanged. The vendor and the model decide what content works.

If a model sends reasoning output in a separate field, the engine removes that output. The engine keeps the text of the answer.

## Responses

### Complete answers

When `stream` is `false` or absent, `POST /v1/chat/completions` returns `application/json`:

```json
{
  "id": "chatcmpl-0123456789abcdef0123456789abcdef",
  "object": "chat.completion",
  "created": 1762700000,
  "model": "local/ministral-3-3b",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Hello!"
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 10,
    "completion_tokens": 2,
    "total_tokens": 12
  }
}
```

`finish_reason` is `stop`, `length` or `content_filter`. `usage` is present when the provider reported token counts.

### Streams

When `stream` is `true`, `POST /v1/chat/completions` returns `text/event-stream`. Each event is a JSON `chat.completion.chunk` object, and the stream ends with `data: [DONE]`.

The engine reads the first piece of a stream before it sends status 200. Most failures before the first piece still get a real status code. When the first piece takes longer than `Api__StreamCommitSeconds` (15 by default), the engine sends status 200. It then sends a `: keep-alive` comment every 15 seconds. These comments stop the caller's read time limit from ending. A failure after status 200 ends the stream with an `error` event. The stream has no `[DONE]` marker in that case.

## Errors

All errors use the body `{"error":{"message","type"}}`. There is one exception. A host name that `AllowedHosts` refuses gets ASP.NET's own HTML 400 response. The server also rejects malformed HTTP before the API runs.

| Status | Error type | Meaning |
|---|---|---|
| 400 | `invalid_request_error` | The request is invalid, including a field the provider does not list, or the provider rejected it. |
| 404 | `not_found_error` | The route, provider or model does not exist. A cloud provider with no API key counts as not existing. |
| 405 | `invalid_request_error` | The route exists, but does not accept this HTTP method. |
| 413 | `invalid_request_error` | The request body is larger than `Api:MaxRequestBodyBytes`. |
| 415 | `invalid_request_error` | The `Content-Type` is not `application/json`. |
| 429 | `rate_limit_error` | The provider is rate limiting. See `Retry-After`. |
| 500 | `internal_error` | An unexpected fault in the engine. Report it, with the `X-Request-Id` header of the response. The engine log has the cause. |
| 502 | `upstream_error` | The provider failed, and a retry does not help. For the local model, the model is not installed, `llama-server` could not start, or it stopped too often. The message gives the cause, and the engine log has the details. |
| 503 | `service_unavailable_error`, or `overloaded_error` | The provider cannot take requests now, or the local model cannot take them yet. See `Retry-After`. |

A 503 response has the type `overloaded_error` when the local queue is full. Other 503 responses have the type `service_unavailable_error`. One of these conditions applies to them:

- The local model is loading or restarting.
- The provider has too much load.
- The engine cannot reach the provider.
- The connection to the provider broke.
- The provider stopped sending data.

### Retry-After

The engine never retries a request, so one request to the engine is at most one request to the provider. The caller decides whether to try again. When the provider sends its own `Retry-After` header, the engine uses that wait, with these limits:

- The wait is at most 30 seconds.
- The wait is rounded up to whole seconds.
- The wait is at least 1 second.

If the provider sends no header, or the engine returns the 503 itself, the value is 5 seconds.

## Health

The provider states are:

- `ready`
- `busy`
- `starting`
- `restarting`
- `unresponsive`
- `missing`
- `failed`
- `stopping`
- `disabled`

A provider can take requests only when it is `ready` or `busy`. The engine does not test cloud providers. So an enabled cloud provider is `ready`. Accept values that you do not know. A later version can add values.

## Compatibility

The engine follows Semantic Versioning. These items are part of the contract:

- the routes,
- the request and response fields,
- the status codes and error types,
- the health values,
- the settings, and
- the fields of the ready file.

A breaking change to one of these needs a new major version. Before a removal, the engine marks the item as deprecated for at least one minor version, and the [CHANGELOG](../CHANGELOG.md) tells what to use in its place. Accept fields and values that you do not know, because a minor version can add them.

## Supported models

Not supported on any provider:

- Embeddings and reranking.
- Image generation.
- Speech and audio.
- Legacy models that only offer `/v1/completions`.
- Models offered only on OpenAI's `/v1/responses` API.
- Tool calling.

A cloud vendor works through configuration if it offers an OpenAI-compatible endpoint with Bearer keys. AWS Bedrock's native API does not work. Azure OpenAI's older deployment URLs, which use `api-key` headers, do not work. These statements were not tested against live vendor APIs.

The local model can be any GGUF model that the pinned llama.cpp supports.

