namespace XatLlmEngine.Infrastructure;

/// <summary>Writes files so that a reader or a crash never sees half a file.</summary>
public static class AtomicFile
{
    /// <summary>Writes a new file next to the target first, and then replaces the target with it.</summary>
    /// <param name="path">The absolute path of the target file.</param>
    /// <param name="text">The file text.</param>
    /// <exception cref="IOException">The engine cannot write or move the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The user may not write the file.</exception>
    public static void WriteAllText(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
