using System.Globalization;

// A replacement for llama-server, so that tests can check process supervision without a real model on every platform.
// It follows the real server's contract: the same arguments, the same environment variable for its key, and a /health
// route that answers 503 while "loading". Extra arguments trigger the failure cases that the tests need.
// FAKE_LLAMA_ARGS adds arguments, so that a manual test of a release-like engine can select a behaviour. The engine allows only tuning flags in its settings.
var settings = ParseArguments([.. args, .. (Environment.GetEnvironmentVariable("FAKE_LLAMA_ARGS") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
var port = int.Parse(settings["--port"], CultureInfo.InvariantCulture);
var expectedKey = Environment.GetEnvironmentVariable("LLAMA_API_KEY");
var loadDelay = TimeSpan.FromMilliseconds(settings.TryGetValue("--load-delay-ms", out var delay) ? int.Parse(delay, CultureInfo.InvariantCulture) : 0);

if (settings.TryGetValue("--record-start-to", out var startRecord))
{
    // One line for each start, so that a test can count the starts.
    File.AppendAllText(startRecord, "started" + Environment.NewLine);
}

if (settings.ContainsKey("--fail-immediately"))
{
    // The same kind of line that llama-server writes for a wrong value.
    Console.Error.WriteLine("error while handling argument \"--fake\": fake failure");
    return 3;
}

if (settings.TryGetValue("--noisy-kilobytes", out var kilobytes))
{
    // Enough output to fill a pipe that nobody reads, which would block this process forever.
    var line = new string('x', 1023);
    for (var written = 0; written < int.Parse(kilobytes, CultureInfo.InvariantCulture); written++)
    {
        Console.Out.WriteLine(line);
    }
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
var app = builder.Build();
var startedAt = TimeProvider.System.GetTimestamp();
var unhealthyAfter = settings.TryGetValue("--unhealthy-after-ms", out var unhealthy)
    ? TimeSpan.FromMilliseconds(int.Parse(unhealthy, CultureInfo.InvariantCulture))
    : TimeSpan.MaxValue;

var chunkDelay = Milliseconds("--chunk-delay-ms");
var completeDelay = Milliseconds("--complete-delay-ms");
var streamChunks = settings.TryGetValue("--stream-chunks", out var chunkCount) ? int.Parse(chunkCount, CultureInfo.InvariantCulture) : 2;
var stallAfter = settings.TryGetValue("--stall-stream-after-chunks", out var stall) ? int.Parse(stall, CultureInfo.InvariantCulture) : int.MaxValue;
var healthFailsWhileBusy = settings.ContainsKey("--health-fails-while-busy");
settings.TryGetValue("--record-concurrency-to", out var concurrencyRecord);
var active = 0;
var highest = 0;
var recordLock = new Lock();

if (settings.TryGetValue("--write-environment-to", out var environmentReport))
{
    var report = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(entry => $"{entry.Key}={entry.Value}");
    await File.WriteAllLinesAsync(environmentReport, report);
}

app.MapGet("/health", () =>
{
    var elapsed = TimeProvider.System.GetElapsedTime(startedAt);
    return elapsed < loadDelay || elapsed > unhealthyAfter || (healthFailsWhileBusy && Volatile.Read(ref active) > 0)
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok();
});

// The real server needs the key for this route, so an answer proves that the engine reached its own server.
app.MapGet("/props", (HttpContext context) =>
    context.Request.Headers.Authorization == $"Bearer {expectedKey}"
        ? Results.Ok(new { model_path = settings.GetValueOrDefault("--model", string.Empty) })
        : Results.StatusCode(StatusCodes.Status401Unauthorized));

app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    if (context.Request.Headers.Authorization != $"Bearer {expectedKey}")
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    Track(+1);
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var isStream = (await reader.ReadToEndAsync(context.RequestAborted)).Contains("\"stream\":true", StringComparison.Ordinal);
        if (!isStream)
        {
            await Task.Delay(completeDelay, context.RequestAborted);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"choices":[{"message":{"role":"assistant","content":"fake answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""",
                context.RequestAborted);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        for (var index = 0; index < streamChunks; index++)
        {
            if (index == stallAfter)
            {
                // A server that stops sending data but keeps the connection open.
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }

            await Task.Delay(chunkDelay, context.RequestAborted);
            var finish = index == streamChunks - 1 ? "\"stop\"" : "null";
            await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"piece{index} \"}},\"finish_reason\":{finish}}}]}}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
    }
    finally
    {
        Track(-1);
    }
});

if (settings.TryGetValue("--exit-after-ms", out var lifetime))
{
    _ = Task.Delay(int.Parse(lifetime, CultureInfo.InvariantCulture)).ContinueWith(_ => Environment.Exit(4), TaskScheduler.Default);
}

await app.RunAsync();
return 0;

// Reads a number of milliseconds from the arguments.
TimeSpan Milliseconds(string name) =>
    TimeSpan.FromMilliseconds(settings.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0);

// Counts the requests that run at the same time, and records the highest count.
void Track(int change)
{
    lock (recordLock)
    {
        active += change;
        if (active > highest)
        {
            highest = active;
            if (concurrencyRecord is not null)
            {
                File.WriteAllText(concurrencyRecord, highest.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}

// Reads "--name value" pairs. A flag with no value gets an empty string.
static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>();
    for (var index = 0; index < arguments.Length; index++)
    {
        var hasValue = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal);
        parsed[arguments[index]] = hasValue ? arguments[++index] : string.Empty;
    }

    return parsed;
}
