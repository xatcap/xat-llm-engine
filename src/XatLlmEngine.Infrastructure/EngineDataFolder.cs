using System.Security.Cryptography;
using System.Text;

namespace XatLlmEngine.Infrastructure;

/// <summary>The engine's private data folder for this install. It holds the instance lock, the PID file of llama-server and the usage counts.</summary>
/// <remarks>
/// The folder is in the user's private data folder, with one subfolder for each install folder. In a shared temp folder,
/// another user can put a link there, and the engine then writes through the link.
/// </remarks>
/// <param name="path">The absolute path of the folder.</param>
internal sealed class EngineDataFolder(string path)
{
    /// <summary>Gets the absolute path of the folder.</summary>
    public string Path { get; } = path;

    /// <summary>Gets the default folder of this install.</summary>
    /// <returns>The folder.</returns>
    public static EngineDataFolder ForThisInstall()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..16];
        return new(System.IO.Path.Combine(Root(), "xat-llm-engine", hash));
    }

    /// <summary>Gets the user's data folder, or the engine's folder when the system has none.</summary>
    /// <returns>The folder.</returns>
    public static string Root()
    {
        var dataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(dataFolder) ? AppContext.BaseDirectory : dataFolder;
    }

    /// <summary>Gets the path of a file in the folder, and creates the folder when it does not exist.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="IOException">The engine cannot create the folder.</exception>
    /// <exception cref="UnauthorizedAccessException">The user may not create the folder.</exception>
    public string File(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path);
        }
        else
        {
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return System.IO.Path.Combine(Path, name);
    }
}
