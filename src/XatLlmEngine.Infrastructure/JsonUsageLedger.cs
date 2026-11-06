using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure;

/// <summary>Keeps the token use of each provider in hourly buckets, and saves them in <c>usage.json</c> in the data folder.</summary>
/// <remarks>
/// The buckets make the budget period roll with a resolution of one hour, and they keep the file small. One writer saves the
/// newest state after each change, so bursts of requests cause few writes. A crash can lose the last changes. The instance
/// lock makes sure that only one engine writes the file. See ADR 0011.
/// </remarks>
internal sealed partial class JsonUsageLedger : IUsageLedger, IHostedService
{
    /// <summary>The name of the file in the data folder.</summary>
    internal const string FileName = "usage.json";

    /// <summary>The length of one bucket.</summary>
    private static readonly TimeSpan BucketLength = TimeSpan.FromHours(1);

    /// <summary>The longest budget period. Older buckets are removed.</summary>
    private static readonly TimeSpan LongestPeriod = TimeSpan.FromHours(744);

    /// <summary>Protects the buckets and the state of the writer.</summary>
    private readonly Lock sync = new();

    /// <summary>The tokens of each provider, by the start of each hour in Unix seconds.</summary>
    private readonly Dictionary<string, SortedDictionary<long, long>> buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The data folder.</summary>
    private readonly EngineDataFolder dataFolder;

    /// <summary>The log.</summary>
    private readonly ILogger<JsonUsageLedger> logger;

    /// <summary>Tells whether the buckets have changes that are not in the file.</summary>
    private bool dirty;

    /// <summary>The writer that runs now, or <see langword="null"/> when none runs.</summary>
    private Task? writer;

    /// <summary>Tells whether the file was read.</summary>
    private bool loaded;

    /// <summary>Initializes a new instance of the <see cref="JsonUsageLedger"/> class.</summary>
    /// <param name="dataFolder">The data folder.</param>
    /// <param name="logger">The log.</param>
    public JsonUsageLedger(EngineDataFolder dataFolder, ILogger<JsonUsageLedger> logger)
    {
        this.dataFolder = dataFolder;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public long Used(string provider, TimeSpan period, DateTimeOffset now)
    {
        lock (sync)
        {
            EnsureLoaded();
            return InPeriod(provider, period, now).Sum(bucket => bucket.Value);
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset AvailableAt(string provider, TokenBudget budget, DateTimeOffset now)
    {
        lock (sync)
        {
            EnsureLoaded();
            var included = InPeriod(provider, budget.Period, now).ToList();
            var remaining = included.Sum(bucket => bucket.Value);
            foreach (var (start, tokens) in included)
            {
                // A bucket leaves the rolling period one period after its start.
                remaining -= tokens;
                if (remaining < budget.Tokens)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(start) + budget.Period;
                }
            }

            return now + budget.Period;
        }
    }

    /// <inheritdoc/>
    public void Record(string provider, long tokens, DateTimeOffset now)
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (sync)
        {
            EnsureLoaded();
            if (!buckets.TryGetValue(provider, out var history))
            {
                history = [];
                buckets[provider] = history;
            }

            var start = BucketStart(now);
            history[start] = history.GetValueOrDefault(start) + tokens;
            foreach (var old in history.Keys.Where(key => key + (long)LongestPeriod.TotalSeconds <= start).ToList())
            {
                history.Remove(old);
            }

            dirty = true;
            writer ??= Task.Run(WriteWhileDirty);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Saves the last changes when the engine stops.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A task that completes when the file is saved.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (sync)
        {
            running = writer;
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
    }

    /// <summary>Gives the start of the hour of a time, in Unix seconds.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The start of its bucket.</returns>
    private static long BucketStart(DateTimeOffset time)
    {
        var seconds = time.ToUnixTimeSeconds();
        var length = (long)BucketLength.TotalSeconds;
        return seconds - (seconds % length);
    }

    /// <summary>Gives the buckets of a provider that are in the rolling period, oldest first. The caller must hold the lock.</summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="period">The length of the period.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The buckets.</returns>
    private IEnumerable<KeyValuePair<long, long>> InPeriod(string provider, TimeSpan period, DateTimeOffset now)
    {
        var limit = now.ToUnixTimeSeconds() - (long)period.TotalSeconds;
        return buckets.TryGetValue(provider, out var history) ? history.Where(bucket => bucket.Key > limit) : [];
    }

    /// <summary>Saves the newest state until no change is left.</summary>
    private void WriteWhileDirty()
    {
        try
        {
            WriteUntilClean();
        }
        catch (Exception exception)
        {
            // A writer that stops must let the next change start a new writer.
            lock (sync)
            {
                writer = null;
            }

            LogSaveFailed(exception, dataFolder.Path);
        }
    }

    /// <summary>Saves the newest state until no change is left.</summary>
    private void WriteUntilClean()
    {
        while (true)
        {
            string text;
            lock (sync)
            {
                if (!dirty)
                {
                    writer = null;
                    return;
                }

                dirty = false;
                text = JsonSerializer.Serialize(
                    new UsageFile(1, buckets.ToDictionary(entry => entry.Key, entry => entry.Value.ToDictionary(bucket => bucket.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), bucket => bucket.Value))),
                    UsageJson.Default.UsageFile);
            }

            Save(text);
        }
    }

    /// <summary>Writes the file. It writes a new file first and then replaces the old one, so a crash never leaves half a file.</summary>
    /// <param name="text">The file text.</param>
    private void Save(string text)
    {
        try
        {
            AtomicFile.WriteAllText(dataFolder.File(FileName), text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(exception, dataFolder.Path);
        }
    }

    /// <summary>Reads the file once. A file that the engine cannot read is renamed, and the counts start from zero. The caller must hold the lock.</summary>
    private void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        var path = Path.Combine(dataFolder.Path, FileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), UsageJson.Default.UsageFile);
            foreach (var (provider, history) in file?.Providers ?? [])
            {
                if (history is null)
                {
                    continue;
                }

                buckets[provider] = new SortedDictionary<long, long>(history.ToDictionary(bucket => long.Parse(bucket.Key, System.Globalization.CultureInfo.InvariantCulture), bucket => bucket.Value));
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            buckets.Clear();
            var kept = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(path, kept);
            }
            catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
            {
                kept = path;
            }

            LogLoadFailed(exception, kept);
        }
    }

    /// <summary>Logs a file that the engine could not save.</summary>
    /// <param name="exception">The cause.</param>
    /// <param name="folder">The data folder.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine could not save the token use in {Folder}. A restart can lose the newest counts.")]
    private partial void LogSaveFailed(Exception exception, string folder);

    /// <summary>Logs a file that the engine could not read.</summary>
    /// <param name="exception">The cause.</param>
    /// <param name="kept">Where the old file is now.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "The engine could not read the token use. The counts start from zero, so the budgets allow more tokens than they should. The old file is {Kept}.")]
    private partial void LogLoadFailed(Exception exception, string kept);

    /// <summary>The content of the file.</summary>
    /// <param name="Version">The format version.</param>
    /// <param name="Providers">The tokens of each provider, by the start of each hour in Unix seconds.</param>
    internal sealed record UsageFile(int Version, Dictionary<string, Dictionary<string, long>> Providers);

    /// <summary>Source-generated JSON handling for the file.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(UsageFile))]
    internal sealed partial class UsageJson : JsonSerializerContext;
}
