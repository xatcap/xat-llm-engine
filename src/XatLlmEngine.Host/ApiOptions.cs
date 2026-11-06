using System.ComponentModel.DataAnnotations;

namespace XatLlmEngine.Host;

/// <summary>Settings of the engine's own API.</summary>
public sealed class ApiOptions
{
    /// <summary>The configuration section that holds these settings.</summary>
    public const string SectionName = "Api";

    /// <summary>Gets the largest request body that the API accepts, in bytes. A larger body gets status 413. The default is 20 MiB, which leaves room for images in messages.</summary>
    [Range(1024, 1_073_741_824)]
    public long MaxRequestBodyBytes { get; init; } = 20 * 1024 * 1024;

    /// <summary>
    /// Gets how long a stream can wait for its first piece before the engine sends status 200, in seconds. After that, the engine
    /// sends a keep-alive comment every 15 seconds, so that the caller's read time limit does not end. A failure after that becomes
    /// an error event. Use 0 to wait for the first piece without a limit. See ADR 0008.
    /// </summary>
    [Range(0, 3_600)]
    public int StreamCommitSeconds { get; init; } = 15;
}
