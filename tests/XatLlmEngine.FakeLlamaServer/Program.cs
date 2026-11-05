using System.Globalization;

// A replacement for llama-server, so that tests can check process supervision without a real model on every platform.
// It follows the real server's contract: the same arguments, the same environment variable for its key, and a /health
// route that answers 503 while "loading". Extra arguments trigger the failure cases that the tests need.
var settings = ParseArguments(args);
var port = int.Parse(settings["--port"], CultureInfo.InvariantCulture);
var expectedKey = Environment.GetEnvironmentVariable("LLAMA_API_KEY");
var loadDelay = TimeSpan.FromMilliseconds(settings.TryGetValue("--load-delay-ms", out var delay) ? int.Parse(delay, CultureInfo.InvariantCulture) : 0);

if (settings.ContainsKey("--fail-immediately"))
{
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

if (settings.TryGetValue("--write-environment-to", out var environmentReport))
{
    var report = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(entry => $"{entry.Key}={entry.Value}");
    await File.WriteAllLinesAsync(environmentReport, report);
}

app.MapGet("/health", () =>
{
    var elapsed = TimeProvider.System.GetElapsedTime(startedAt);
    return elapsed < loadDelay || elapsed > unhealthyAfter
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok();
});

app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    if (context.Request.Headers.Authorization != $"Bearer {expectedKey}")
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var reader = new StreamReader(context.Request.Body);
    var isStream = (await reader.ReadToEndAsync(context.RequestAborted)).Contains("\"stream\":true", StringComparison.Ordinal);
    if (!isStream)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            """{"choices":[{"message":{"role":"assistant","content":"fake answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""",
            context.RequestAborted);
        return;
    }

    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync(
        """
        data: {"choices":[{"delta":{"role":"assistant","content":"fake "},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"answer"},"finish_reason":"stop"}]}

        data: [DONE]


        """,
        context.RequestAborted);
});

if (settings.TryGetValue("--exit-after-ms", out var lifetime))
{
    _ = Task.Delay(int.Parse(lifetime, CultureInfo.InvariantCulture)).ContinueWith(_ => Environment.Exit(4), TaskScheduler.Default);
}

await app.RunAsync();
return 0;

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
