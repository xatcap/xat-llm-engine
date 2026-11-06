# Include the engine in an app

This page is for developers who ship the engine inside a desktop app. The app starts the engine, finds its address, waits until it is ready, and stops it. The users of the app do not install anything else.

## What to ship

Put the unpacked release package in your app's folder, and put the model file in its `models/` folder. The engine needs no .NET installation.

| Path in the package | What it is |
|---|---|
| `XatLlmEngine.Host` (`.exe` on Windows) | The engine. |
| `llama/` | `llama-server` and its libraries. |
| `models/` | The place for the model file. Set `LocalModel__ModelPath` to use another place. |
| `appsettings.json` | The settings. You can change it, or set environment variables when you start the engine. |
| `licenses/` | The license texts. Keep them in your app. |

The engine folder can be read-only. The engine writes only to its data folder and to the ready file.

## Start the engine

Start the engine as a child process with these environment variables:

| Variable | Value | Why |
|---|---|---|
| `Urls` | `http://127.0.0.1:0` | The system chooses a free port, so the engine never fights with another program for a port. |
| `Engine__ReadyFile` | A path in a folder that only your app writes to | The engine writes its address to this file. The folder must exist, or the engine stops at startup. |
| `Engine__StopWhenInputCloses` | `true` | The engine stops when your app closes the engine's input, or when your app stops. |
| `Engine__DataFolder` | A folder that your app owns | The token budgets continue after an app update. See [Data folder](#data-folder). |
| `Providers__<name>__ApiKey` | The user's key | Turns on a cloud provider. Leave it out for local use only. |

Keep the engine's standard input open as a pipe. Read or discard the engine's output, because a full pipe can block the engine.

On macOS and Linux, start the engine in a new process group. Then your app can stop the engine and `llama-server` together, and not stop itself. See [Stop the engine](#stop-the-engine).

## Find the address

1. Delete an old ready file before you start the engine. The engine also removes an old file when it starts.
2. Start the engine.
3. Wait until the ready file exists. Stop the wait if the engine process exits, or after a time limit such as 60 seconds.
4. Read `url` from the file.

A wrong setting, or a ready file that the engine cannot write, stops the engine with an exit code that is not 0. The engine output tells the cause.

The file is JSON:

```json
{"url":"http://127.0.0.1:53123","pid":1234,"version":"2.1.0"}
```

The engine writes the file when the API starts. The local model can still be loading at that time. Wait for `GET /health/ready?provider=local` to return 200 before you send local requests. Cloud providers can take requests at once.

A later version can add fields to the file. Accept fields that you do not know.

If the first address in `Urls` is a Unix socket, `url` is the socket address, for example `http://unix:/path/to/engine.sock`.

## Stop the engine

Close the engine's standard input. The engine then stops `llama-server`, deletes the ready file and exits. Wait for the engine process to exit.

If your app stops unexpectedly, the system closes the input, and the engine also stops.

If the engine does not exit after 30 seconds, kill it:

- On Windows, a killed engine also stops `llama-server`.
- On macOS and Linux, kill the engine's process group, so that `llama-server` also stops. This works only when the engine has its own process group. Otherwise `llama-server` runs until the next engine start.

## Data folder

By default, the data folder depends on the folder where the engine is. Some app updaters put each version in a new folder. Then each update gets a new data folder, and these problems occur:

- The token budgets start again from zero.
- The engine does not find a `llama-server` that an old engine left.
- Two versions of the engine do not block each other.

Set `Engine__DataFolder` to a folder in your app's own data folder, for example `<app data>/llm-engine`. The engine logs a warning when this setting is empty.

## One engine for each app

Each app starts its own engine. Each engine loads its own copy of the model, which needs about 2 GiB of memory, and more for a large `LocalModel__ContextSize`. Two apps cannot share one engine. Only one engine can run for each data folder.

## Sign the files

The release files have no signature. Your app's installer must sign them with your app's identity:

- the engine program,
- `llama/llama-server`, and
- the libraries in `llama/`.

On macOS, sign each file in the folder before you sign the app bundle, and then notarize the app. A .NET program with the hardened runtime can need more entitlements, such as `com.apple.security.cs.allow-jit`.

> [!NOTE]
>
> The project has not tested a signed and notarized app yet. Check the signature and the notarization with your own build.

## Examples

These examples start the engine, read the ready file, and stop the engine. Add your own error handling.

### Python

```python
import json, os, subprocess, time
from pathlib import Path

ready_file = Path(app_data) / "engine-ready.json"
ready_file.unlink(missing_ok=True)
engine = subprocess.Popen(
    [str(engine_folder / "XatLlmEngine.Host")],
    stdin=subprocess.PIPE,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
    start_new_session=True,  # A new process group on macOS and Linux.
    env=dict(os.environ,
             Urls="http://127.0.0.1:0",
             Engine__ReadyFile=str(ready_file),
             Engine__StopWhenInputCloses="true",
             Engine__DataFolder=str(Path(app_data) / "llm-engine")),
)
deadline = time.monotonic() + 60
while not ready_file.exists():
    if engine.poll() is not None or time.monotonic() > deadline:
        raise RuntimeError("The engine did not start.")
    time.sleep(0.2)
url = json.loads(ready_file.read_text())["url"]

# ... send requests to url ...

engine.stdin.close()
engine.wait(timeout=30)
```

### Node.js

```javascript
// An ES module (.mjs), so that "await" can be at the top level.
import { spawn } from "node:child_process";
import fs from "node:fs";

fs.rmSync(readyFile, { force: true });
const engine = spawn(enginePath, [], {
  stdio: ["pipe", "ignore", "ignore"],
  detached: process.platform !== "win32", // A new process group on macOS and Linux.
  env: { ...process.env,
    Urls: "http://127.0.0.1:0",
    Engine__ReadyFile: readyFile,
    Engine__StopWhenInputCloses: "true",
    Engine__DataFolder: dataFolder },
});
const deadline = Date.now() + 60_000;
while (!fs.existsSync(readyFile)) {
  if (engine.exitCode !== null || Date.now() > deadline) throw new Error("The engine did not start.");
  await new Promise(r => setTimeout(r, 200));
}
const { url } = JSON.parse(fs.readFileSync(readyFile, "utf8"));

// ... send requests to url ...

engine.stdin.end();
```

### C#

```csharp
File.Delete(readyFile);
var start = new ProcessStartInfo(enginePath) { RedirectStandardInput = true, UseShellExecute = false };
start.Environment["Urls"] = "http://127.0.0.1:0";
start.Environment["Engine__ReadyFile"] = readyFile;
start.Environment["Engine__StopWhenInputCloses"] = "true";
start.Environment["Engine__DataFolder"] = dataFolder;
using var engine = Process.Start(start)!;
var deadline = DateTime.UtcNow.AddSeconds(60);
while (!File.Exists(readyFile))
{
    if (engine.HasExited || DateTime.UtcNow > deadline) throw new InvalidOperationException("The engine did not start.");
    await Task.Delay(200);
}
var url = JsonDocument.Parse(File.ReadAllText(readyFile)).RootElement.GetProperty("url").GetString();

// ... send requests to url ...

engine.StandardInput.Close();
await engine.WaitForExitAsync();
```

## Unix sockets

On macOS and Linux, the engine can also listen on a Unix socket, for example `Urls=http://unix:/path/to/engine.sock`. The file permissions of the socket then decide which users can connect. Most OpenAI client libraries cannot use a Unix socket.
