using Microsoft.Extensions.Logging.Abstractions;
using XatLlmEngine.Application.Abstractions;

namespace XatLlmEngine.Infrastructure.Tests;

/// <summary>Tests the token use that a budget reads, and its file.</summary>
public sealed class UsageLedgerTests : IDisposable
{
    /// <summary>A fixed time at the start of an hour.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The data folder of this test.</summary>
    private readonly string folder = Directory.CreateTempSubdirectory().FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(folder, recursive: true);

    /// <summary>Tokens count in the period, and leave it one period after the start of their hour.</summary>
    [Fact]
    public void Used_RollingPeriod_DropsOldHours()
    {
        var ledger = Ledger();
        ledger.Record("cloud", 100, Noon);
        ledger.Record("cloud", 50, Noon.AddHours(2));
        ledger.Record("other", 7, Noon);

        Assert.Equal(150, ledger.Used("cloud", TimeSpan.FromHours(3), Noon.AddHours(2).AddMinutes(30)));
        Assert.Equal(50, ledger.Used("cloud", TimeSpan.FromHours(3), Noon.AddHours(3).AddMinutes(1)));
        Assert.Equal(7, ledger.Used("OTHER", TimeSpan.FromHours(3), Noon));
    }

    /// <summary>The budget is free again when enough old hours have left the period.</summary>
    [Fact]
    public void AvailableAt_GivesTimeWhenUseDropsBelowBudget()
    {
        var ledger = Ledger();
        ledger.Record("cloud", 100, Noon);
        ledger.Record("cloud", 100, Noon.AddHours(1));
        var budget = new TokenBudget(150, TimeSpan.FromHours(24));

        Assert.Equal(Noon.AddHours(24), ledger.AvailableAt("cloud", budget, Noon.AddHours(1)));
    }

    /// <summary>The counts last across an engine restart, and the file is replaced in one step.</summary>
    /// <returns>A task for the test.</returns>
    [Fact]
    public async Task Record_Restart_KeepsCounts()
    {
        var first = Ledger();
        first.Record("cloud", 42, Noon);
        await first.StopAsync(CancellationToken.None);

        Assert.Equal(42, Ledger().Used("cloud", TimeSpan.FromHours(1), Noon));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    /// <summary>A file that the engine cannot read is kept under another name, and the counts start from zero.</summary>
    [Fact]
    public void Load_CorruptFile_StartsFromZero()
    {
        File.WriteAllText(Path.Combine(folder, JsonUsageLedger.FileName), "{ not json");

        Assert.Equal(0, Ledger().Used("cloud", TimeSpan.FromHours(1), Noon));
        Assert.Single(Directory.GetFiles(folder, "usage.json.corrupt-*"));
    }

    /// <summary>Builds a ledger on the test's data folder.</summary>
    /// <returns>The ledger.</returns>
    private JsonUsageLedger Ledger() => new(new EngineDataFolder(folder), NullLogger<JsonUsageLedger>.Instance);
}
