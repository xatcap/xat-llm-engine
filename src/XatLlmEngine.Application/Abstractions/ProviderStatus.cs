namespace XatLlmEngine.Application.Abstractions;

/// <summary>Whether a provider can take requests now.</summary>
public enum ProviderState
{
    /// <summary>The provider can take requests.</summary>
    Ready,

    /// <summary>The provider can take requests, but all its slots are in use. New requests wait in the queue.</summary>
    Busy,

    /// <summary>The provider is starting, for example while a local model loads.</summary>
    Starting,

    /// <summary>The provider stopped unexpectedly, and the engine starts it again.</summary>
    Restarting,

    /// <summary>The provider is busy and does not answer its health checks. New requests get 503 until it answers again.</summary>
    Unresponsive,

    /// <summary>A file that the provider needs is missing. Only a person can correct this.</summary>
    Missing,

    /// <summary>The provider failed, and the engine does not try again until it restarts.</summary>
    Failed,

    /// <summary>The engine is stopping.</summary>
    Stopping,

    /// <summary>The provider is configured, but turned off, for example because it has no API key.</summary>
    Disabled,
}

/// <summary>The load on a provider that queues requests in the engine.</summary>
/// <param name="Active">The requests that have a slot.</param>
/// <param name="Queued">The requests that wait for a slot.</param>
/// <param name="Capacity">The number of slots.</param>
/// <param name="QueueLimit">The number of requests that can wait.</param>
public sealed record ProviderLoad(int Active, int Queued, int Capacity, int QueueLimit);

/// <summary>The status of one provider.</summary>
/// <param name="State">Whether the provider can take requests.</param>
/// <param name="Load">The load, for a provider that queues requests in the engine.</param>
public sealed record ProviderStatus(ProviderState State, ProviderLoad? Load = null)
{
    /// <summary>Gets the status of a provider that can always take requests.</summary>
    public static ProviderStatus Ready { get; } = new(ProviderState.Ready);

    /// <summary>Gets a value indicating whether the provider can take new requests.</summary>
    public bool IsServing => State is ProviderState.Ready or ProviderState.Busy;
}

/// <summary>Lists the providers that the configuration has, but that are turned off.</summary>
public interface IDisabledProviders
{
    /// <summary>Gets the names of the providers that are turned off.</summary>
    IReadOnlyList<string> Names { get; }
}
