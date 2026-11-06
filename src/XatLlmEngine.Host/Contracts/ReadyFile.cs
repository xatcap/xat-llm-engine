namespace XatLlmEngine.Host.Contracts;

/// <summary>The contents of the ready file. These names are part of the contract with the parent app. See ADR 0005.</summary>
/// <param name="Url">The address where the API listens, for example <c>http://127.0.0.1:53123</c>.</param>
/// <param name="Pid">The process ID of the engine.</param>
/// <param name="Version">The engine version.</param>
internal sealed record ReadyFile(string Url, int Pid, string Version);
