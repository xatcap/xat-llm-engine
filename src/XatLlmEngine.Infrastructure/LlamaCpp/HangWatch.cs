namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>What the supervisor does after one health check of a ready server.</summary>
internal enum HangDecision
{
    /// <summary>The server is healthy. New requests can start.</summary>
    Serve,

    /// <summary>The check failed, but not often enough to act. Nothing changes.</summary>
    Wait,

    /// <summary>The busy server does not answer its checks. New requests get 503, and requests in progress continue.</summary>
    HoldNewRequests,

    /// <summary>The server hangs. The supervisor stops it and starts it again.</summary>
    Restart,
}

/// <summary>Decides whether a server that fails its health checks hangs, or is only busy.</summary>
/// <remarks>
/// A busy server can answer health checks slowly. Thus the rules are:
/// <list type="bullet">
/// <item>A healthy check: the server can serve.</item>
/// <item>Fewer than <see cref="LlamaServerSupervisor.FailedChecksLimit"/> failed checks in a row: no change.</item>
/// <item>That many failed checks while no request runs: the server hangs.</item>
/// <item>That many failed checks while requests run: new requests wait, and requests in progress continue.</item>
/// <item>While requests run, no healthy check and no data for the unresponsive time: the server hangs.</item>
/// </list>
/// A server that sends data is never stopped. See ADR 0009.
/// </remarks>
/// <param name="timeProvider">The clock.</param>
/// <param name="gate">The request gate, which knows whether requests run and when data came last.</param>
/// <param name="unresponsiveTimeout">How long a busy server can fail its checks and send no data.</param>
internal sealed class HangWatch(TimeProvider timeProvider, LocalRequestGate gate, TimeSpan unresponsiveTimeout)
{
    /// <summary>The timestamp of the last healthy check.</summary>
    private long lastHealthy = timeProvider.GetTimestamp();

    /// <summary>Gets the number of failed checks in a row.</summary>
    public int FailedChecks { get; private set; }

    /// <summary>Gets the cause of the last <see cref="HangDecision.Restart"/>, for the log.</summary>
    public string Cause { get; private set; } = string.Empty;

    /// <summary>Records one health check.</summary>
    /// <param name="healthy">Whether the server answered the check.</param>
    /// <returns>What the supervisor must do.</returns>
    public HangDecision Record(bool healthy)
    {
        if (healthy)
        {
            FailedChecks = 0;
            lastHealthy = timeProvider.GetTimestamp();
            return HangDecision.Serve;
        }

        FailedChecks++;
        if (FailedChecks < LlamaServerSupervisor.FailedChecksLimit)
        {
            return HangDecision.Wait;
        }

        if (gate.Active == 0)
        {
            Cause = $"llama-server failed {FailedChecks} health checks in a row while no request ran.";
            return HangDecision.Restart;
        }

        var lastSign = Math.Max(lastHealthy, gate.LastProgress);
        if (timeProvider.GetElapsedTime(lastSign) >= unresponsiveTimeout)
        {
            Cause = $"llama-server failed its health checks and sent no data for {unresponsiveTimeout.TotalSeconds:0} seconds while requests ran.";
            return HangDecision.Restart;
        }

        return HangDecision.HoldNewRequests;
    }
}
