# Quick start

This page shows how to download a release, prepare its folder, and start the API.

## 1. Download a release

Download these two files from the same [GitHub release](https://github.com/xatcap/xat-llm-engine/releases):

| File | Purpose |
|---|---|
| `xat-llm-engine-<version>-<platform>.tar.gz` (`.zip` on Windows) | The engine, `llama-server`, and the query-expansion model (Qwen3 0.6B, about 380 MiB). You do not need to install .NET. The platforms are `osx-arm64`, `linux-x64` and `win-x64`. |
| `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf` | The chat model (about 2 GiB). |

Optionally, download `SHA256SUMS` from the same release and check both files against it.

## 2. Prepare the folder

> [!NOTE]
>
> On macOS, the release files have no signature, so macOS blocks a browser download. Remove the quarantine flag in step 3 before you start the engine.

1. Unpack the engine archive.
2. Move `Ministral-3-3B-Instruct-2512-Q4_K_M.gguf` into the `models/` folder inside the unpacked folder. The query-expansion model is already in that folder.
3. On macOS, remove the quarantine flag from the unpacked folder:

```bash
xattr -dr com.apple.quarantine xat-llm-engine-<version>-osx-arm64
```

The unpacked folder now has this layout:

```text
xat-llm-engine-<version>-<platform>/
  XatLlmEngine.Host          (.exe on Windows)
  appsettings.json
  llama/
    llama-server             (.exe on Windows)
    ...
  models/
    Ministral-3-3B-Instruct-2512-Q4_K_M.gguf
    Qwen3-0.6B-Q4_K_M.gguf
  licenses/
    ...
```

## 3. Start the API

> [!NOTE]
>
> On Windows, SmartScreen shows a warning at the first start because the program has no signature.

1. Open a terminal in the unpacked folder.
2. Start the engine:

   - On macOS and Linux:

     ```bash
     ./XatLlmEngine.Host
     ```

   - On Windows:

     ```powershell
     .\XatLlmEngine.Host.exe
     ```

The API listens at `http://127.0.0.1:8420` by default. In the background, the engine starts `llama-server` for each model and loads both models.

Without the chat model file, the engine still starts. Chat requests return 502 until you add the file and restart the engine. Without the query-expansion model file, requests to `/v1/query-expansion` return 502 in the same way.

## 4. Check readiness and send a request

1. Check whether the chat model is ready:

```bash
curl http://127.0.0.1:8420/health/ready?provider=local
```

This route returns 503 while the model loads, and 200 when the model can take requests. Use `provider=query-expansion` to check the query-expansion model.

2. Send a chat request:

```bash
curl http://127.0.0.1:8420/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"local/ministral-3-3b","messages":[{"role":"user","content":"Hello"}]}'
```

3. Expand a search query:

```bash
curl http://127.0.0.1:8420/v1/query-expansion \
  -H "Content-Type: application/json" \
  -d '{"query":"how to reset a router","count":3}'
```

The answer holds a list named `queries`. See the [API reference](api.md#query-expansion) for the fields and the errors.
