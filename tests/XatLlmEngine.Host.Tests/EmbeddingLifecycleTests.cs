using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace XatLlmEngine.Host.Tests;

/// <summary>
/// Starts the real engine program, as a parent app does, with the stand-in for llama-server. The tests check port 0, the ready
/// file and the stop when the standard input closes.
/// </summary>
public sealed class EmbeddingLifecycleTests : IDisposable
{
    /// <summary>The longest wait for a state of the engine.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    /// <summary>A temporary folder for the data folder, the ready file and the model file.</summary>
    private readonly string folder = Directory.CreateTempSubdirectory("xat-engine-embed-").FullName;

    /// <summary>Gets the path of the ready file.</summary>
    private string ReadyFilePath => Path.Combine(folder, "ready.json");

    /// <summary>Gets the data folder of the engine.</summary>
    private string DataFolder => Path.Combine(folder, "data");

    /// <summary>The engine writes the real address to the ready file, closing the input stops it, and llama-server and the file are then gone.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task InputCloses_EngineStopsWithServerAndDeletesReadyFile()
    {
        using var engine = Start();

        var ready = await ReadReadyFileAsync(engine);
        var url = ready.GetProperty("url").GetString()!;
        Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
        Assert.NotEqual("http://127.0.0.1:0", url);
        Assert.Equal(engine.Id, ready.GetProperty("pid").GetInt32());
        Assert.False(string.IsNullOrEmpty(ready.GetProperty("version").GetString()));

        var server = await WaitForLocalModelAsync(new Uri(url));
        engine.StandardInput.Close();

        await WaitForExitAsync(engine);
        Assert.Equal(0, engine.ExitCode);
        Assert.False(File.Exists(ReadyFilePath));
        await WaitUntilAsync(() => HasExited(server));
    }

    /// <summary>An input that is already closed at startup stops the engine cleanly after the start, with no crash.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task InputClosedAtStartup_EngineStopsCleanly()
    {
        using var engine = Start();
        engine.StandardInput.Close();

        await WaitForExitAsync(engine);

        Assert.Equal(0, engine.ExitCode);
        Assert.False(File.Exists(ReadyFilePath));
    }

    /// <summary>A ready file in a folder that does not exist stops the engine at startup with a failure exit code.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task ReadyFileFolderMissing_EngineFailsAtStartup()
    {
        using var engine = Start(Path.Combine(folder, "missing", "ready.json"));

        await WaitForExitAsync(engine);

        Assert.NotEqual(0, engine.ExitCode);
    }

    /// <summary>The engine removes a ready file that a killed engine left, before it writes its own.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task StaleReadyFile_IsReplaced()
    {
        File.WriteAllText(ReadyFilePath, """{"url":"http://127.0.0.1:1","pid":1,"version":"0.0.0"}""");
        using var engine = Start();

        await WaitUntilAsync(() => engine.HasExited || (File.Exists(ReadyFilePath) && File.ReadAllText(ReadyFilePath).Contains($"\"pid\":{engine.Id}", StringComparison.Ordinal)));

        Assert.False(engine.HasExited);
        engine.StandardInput.Close();
        await WaitForExitAsync(engine);
    }

    /// <summary>On Windows, a killed engine does not leave llama-server running, because both are in one job.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task EngineKilled_OnWindows_ServerStops()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows can stop the child processes of a killed process.");
        using var engine = Start();
        var ready = await ReadReadyFileAsync(engine);
        var server = await WaitForLocalModelAsync(new Uri(ready.GetProperty("url").GetString()!));

        engine.Kill(entireProcessTree: false);

        await WaitForExitAsync(engine);
        await WaitUntilAsync(() => HasExited(server));
    }

    /// <summary>The engines that the test started. Each one is killed after the test, also after a failed assertion.</summary>
    private readonly List<Process> engines = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var engine in engines)
        {
            try
            {
                if (!engine.HasExited)
                {
                    engine.Kill(entireProcessTree: true);
                    engine.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process has already ended.
            }
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A process that has not ended yet can hold a file. The system cleans the temporary folder later.
        }
    }

    /// <summary>Starts the engine program with the settings that a parent app uses.</summary>
    /// <param name="readyFile">The path of the ready file, or <see langword="null"/> for the default path of the test.</param>
    /// <returns>The engine process.</returns>
    private Process Start(string? readyFile = null)
    {
        var model = Path.Combine(folder, "model.gguf");
        File.WriteAllText(model, "fake model");
        var program = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "XatLlmEngine.Host.exe" : "XatLlmEngine.Host");
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Urls"] = "http://127.0.0.1:0";
        start.Environment["Engine__DataFolder"] = DataFolder;
        start.Environment["Engine__ReadyFile"] = readyFile ?? ReadyFilePath;
        start.Environment["Engine__StopWhenInputCloses"] = "true";
        start.Environment["LocalModel__ModelPath"] = model;
        start.Environment["LocalModel__ServerPath"] = Path.Combine(AppContext.BaseDirectory, "XatLlmEngine.FakeLlamaServer");

        var process = Process.Start(start)!;
        engines.Add(process);

        // The test reads both outputs, because a full pipe blocks the engine.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Waits for the ready file, and reads it.</summary>
    /// <param name="engine">The engine process.</param>
    /// <returns>The file contents.</returns>
    private async Task<JsonElement> ReadReadyFileAsync(Process engine)
    {
        await WaitUntilAsync(() => File.Exists(ReadyFilePath) || engine.HasExited);
        Assert.False(engine.HasExited, "The engine stopped before it wrote the ready file.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ReadyFilePath, TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    /// <summary>Waits until the local model is ready, and gives the process ID of the stand-in server.</summary>
    /// <param name="baseUrl">The engine's address from the ready file.</param>
    /// <returns>The process ID of llama-server.</returns>
    private async Task<int> WaitForLocalModelAsync(Uri baseUrl)
    {
        using var client = new HttpClient { BaseAddress = baseUrl };
        var started = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(new Uri("/health/ready?provider=local", UriKind.Relative), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            Assert.True(started.Elapsed < WaitLimit, "The local model did not become ready in time.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var record = await File.ReadAllTextAsync(Path.Combine(DataFolder, "llama-server.pid"), TestContext.Current.CancellationToken);
        return int.Parse(record.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    /// <summary>Waits for the engine to exit.</summary>
    /// <param name="engine">The engine process.</param>
    /// <returns>A task that fails when the limit passes first.</returns>
    private static async Task WaitForExitAsync(Process engine)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(WaitLimit);
        try
        {
            await engine.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            engine.Kill(entireProcessTree: true);
            Assert.Fail("The engine did not stop in time.");
        }
    }

    /// <summary>Tells whether a process has ended.</summary>
    /// <param name="pid">The process ID.</param>
    /// <returns><see langword="true"/> when no process has this ID, or the process has exited.</returns>
    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Waits for a condition.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task that fails when the limit passes first.</returns>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < WaitLimit, "The expected state was not reached in time.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
