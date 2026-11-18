# API reference

This page is for programs that call the engine. The engine listens at `http://127.0.0.1:8420` by default. See [Quick start](quickstart.md) to start the engine and send a first request.

## How it works

- Callers send plain HTTP requests, or use an OpenAI client library.
- The model name selects the model for chat, for example `local/ministral-3-3b`.
- The engine uses the OpenAI-compatible wire format on its public API, because llama-server uses it.
- Each local model has its own request template. See [Request templates](#request-templates).
- Query expansion has its own route. See [Query expansion](#query-expansion).
- All routes use the same error body and the same health values.
- Local requests go to a [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` process. The engine starts and supervises one process for each local model.

### Client libraries

OpenAI client libraries work for the `local` provider, with the fields in its template.

This example streams an answer from the chat model with the OpenAI client library for Python:

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
| `POST /v1/chat/completions` | Chat, with or without streaming. The accepted fields depend on the model. See [Request templates](#request-templates). |
| `POST /v1/query-expansion` | Writes other search queries for one search query. See [Query expansion](#query-expansion). |
| `GET /v1/models` | Lists the configured models as `provider/model`, for example `local/ministral-3-3b` and `query-expansion/qwen3-0.6b`. |
| `GET /health` | Always 200. Returns `status` (`healthy`, `degraded` or `unhealthy`), `version`, and `providers`: the `name`, `state`, `message` and load values of each provider. For a local model, the load values are `active`, `queued`, `capacity` and `queue_limit`. The older fields `local` and `message` describe the chat model. |
| `GET /health/live` | 200 while the engine runs. |
| `GET /health/ready` | 200 when at least one provider can take requests, otherwise 503. Add `?provider=local` or `?provider=query-expansion` to ask about one provider. |
| `GET /swagger` | Swagger UI. Development only. |
| `GET /openapi/v1.json` | The OpenAPI document. Development only. The same document is in [openapi.json](openapi.json), and each release attaches it. |

Every response includes an `X-Request-Id` header. Every log line of that request holds the same value as `RequestId`.

## Request templates

Each local model accepts the fields in its template. The engine forwards the values unchanged. It checks the token field when the model has a `MaxTokensLimit`. For other fields, a bad value returns the message from llama-server. This section is the contract for the values that the engine adds.

| Provider | Accepted fields |
|---|---|
| `local` | `messages`, `temperature`, `top_p`, `max_tokens`, `stop`, `stream_options`, `top_k`, `min_p`, `repeat_penalty`, `seed`, `frequency_penalty`, `presence_penalty`, `response_format`, `grammar` |
| `query-expansion` | `messages`, `temperature`, `max_tokens`, `stop` |

These rules apply to every model:

- `model` and `stream` are always accepted.
- Any other top-level field that the template does not list returns a 400 error. The error names the field and lists the accepted fields. This happens even when the field's value is `null` or a default.
- Each model uses its own default temperature when a request sends no `temperature`. The settings are `LocalModel__DefaultTemperature` and `QueryExpansionModel__DefaultTemperature`.
- A model with a `MaxTokensLimit` refuses a token value above the limit. It also refuses a token value that is not a number, or that is not a whole number of at least 1. These checks return a 400 error. A request with no token field gets the limit. The token field is `max_tokens`.

The `query-expansion` template is for the chat route only. Use [POST /v1/query-expansion](#query-expansion) instead. That route writes the prompt for you.

Streaming usage:

- A template that lists `stream_options` sends a usage piece only when it is asked for one. The engine always asks for the counts, because the metrics and the logs use them. It gives the counts to the caller only when the caller sets `stream_options.include_usage` to `true`.

To change a template, edit `RequestFields` in the `LocalModel` or `QueryExpansionModel` section of `appsettings.json`, or in environment variables.

## Content rules

Answers are text only. `tools` and `n` are not in the default templates, because the unified response carries one text answer.

The engine does not inspect the items inside `messages`. It forwards `messages`, including images, to the model unchanged. The model decides what content works.

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

`finish_reason` is `stop`, `length` or `content_filter`. `usage` is present when the model reported token counts.

### Streams

When `stream` is `true`, `POST /v1/chat/completions` returns `text/event-stream`. Each event is a JSON `chat.completion.chunk` object, and the stream ends with `data: [DONE]`.

The engine reads the first piece of a stream before it sends status 200. Most failures before the first piece still get a real status code. When the first piece takes longer than `Api__StreamCommitSeconds` (15 by default), the engine sends status 200. It then sends a `: keep-alive` comment every 15 seconds. These comments stop the caller's read time limit from ending. A failure after status 200 ends the stream with an `error` event. The stream has no `[DONE]` marker in that case.

## Query expansion

`POST /v1/query-expansion` writes other search queries for one search query. The engine uses the query-expansion model, Qwen3 0.6B. The engine writes the prompt, so the request holds only the search query.

### Request

The body is a JSON object. Any field other than these two returns a 400 error.

| Field | Type | Meaning |
|---|---|---|
| `query` | string | Required. The search query. It has from 1 to 1000 characters, after the spaces at the start and the end are removed. |
| `count` | integer | Optional. The number of queries to write, from 1 to 10. The default is 3. |

```json
{"query": "how to reset a router", "count": 3}
```

### Response

When the request succeeds, the status is 200 and the body is `application/json`:

```json
{
  "queries": [
    "...",
    "...",
    "..."
  ],
  "model": "query-expansion/qwen3-0.6b"
}
```

The engine cleans the answer of the model. It removes list markers, quotes, thinking blocks and introduction lines that end with a colon. It also removes repeated queries and a copy of the original `query`. Thus, `queries` can hold fewer items than `count`.

### Errors

| Status | Error type | Meaning |
|---|---|---|
| 400 | `invalid_request_error` | A field is not allowed, a field has the wrong type, or `query` or `count` is out of range. |
| 413 | `invalid_request_error` | The request body is larger than `Api:MaxRequestBodyBytes`. |
| 415 | `invalid_request_error` | The `Content-Type` is not `application/json`. |
| 502 | `upstream_error` | The model file is missing, the model failed, or the model wrote no new query. For the last case, try again or change the query. |
| 503 | `service_unavailable_error`, or `overloaded_error` | The model is turned off, loading, restarting or not answering its health checks. Or its queue is full. See [Retry-After](#retry-after). |

## Errors

All errors use the body `{"error":{"message","type"}}`. There is one exception. A host name that `AllowedHosts` refuses gets ASP.NET's own HTML 400 response. The server also rejects malformed HTTP before the API runs.

| Status | Error type | Meaning |
|---|---|---|
| 400 | `invalid_request_error` | The request is invalid. This includes a field that the template does not list. |
| 404 | `not_found_error` | The route, provider or model does not exist, or the provider is turned off. A request to a removed cloud provider, such as `openai/<model>`, also gets 404. |
| 405 | `invalid_request_error` | The route exists, but does not accept this HTTP method. |
| 413 | `invalid_request_error` | The request body is larger than `Api:MaxRequestBodyBytes`. |
| 415 | `invalid_request_error` | The `Content-Type` is not `application/json`. |
| 500 | `internal_error` | An unexpected fault in the engine. Report it, with the `X-Request-Id` header of the response. The engine log has the cause. |
| 502 | `upstream_error` | The local model failed, and a retry does not help. The model file is missing, `llama-server` could not start, or it stopped too often. The message gives the cause, and the engine log has the details. |
| 503 | `service_unavailable_error`, or `overloaded_error` | The local model cannot take requests now. See [Retry-After](#retry-after). |

A 503 response has the type `overloaded_error` when the local queue is full. Other 503 responses have the type `service_unavailable_error`. One of these conditions applies to them:

- A local model is loading or restarting.
- A local model does not answer its health checks.
- The engine is stopping.

### Retry-After

The engine never retries a request, so one request to the engine is at most one request to a local model. The caller decides whether to try again. A 503 response has a `Retry-After` header with the value 5, in seconds.

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

A provider can take requests only when it is `ready` or `busy`. The overall `status` is `degraded` when one local model can take requests and the other cannot. Accept values that you do not know. A later version can add values.

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

Not supported:

- Embeddings and reranking.
- Image generation.
- Speech and audio.
- Tool calling.

The engine has two local models:

- `local/ministral-3-3b` for chat. The model file is a separate release file.
- `query-expansion/qwen3-0.6b` for query expansion. The release package includes the model file.

Each local model can be any GGUF model that the pinned llama.cpp supports.
