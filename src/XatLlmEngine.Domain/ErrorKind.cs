namespace XatLlmEngine.Domain;

/// <summary>The business category of an expected failure, independent of any transport.</summary>
public enum ErrorKind
{
    /// <summary>The caller's request is invalid. The caller must change it before a retry.</summary>
    InvalidRequest,

    /// <summary>The requested provider or model does not exist.</summary>
    NotFound,

    /// <summary>A provider refused the request because the caller sent too many requests.</summary>
    RateLimited,

    /// <summary>The provider cannot serve requests right now, for example while a local model loads.</summary>
    Unavailable,

    /// <summary>A provider failed or returned something the engine could not use.</summary>
    Upstream,
}
