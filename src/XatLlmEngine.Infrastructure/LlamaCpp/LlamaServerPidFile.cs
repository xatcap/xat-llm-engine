using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>Records the running llama-server in a PID file, so that the next engine start can stop a server that a killed engine left behind.</summary>
/// <remarks>The file is only an aid for cleanup. The methods that read, write or delete the file log a failure, and do not throw it.</remarks>
internal static partial class LlamaServerPidFile
{
    /// <summary>The largest difference between two readings of a process's start time. Some systems calculate it slightly differently each time.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Builds a PID file path unique to this install. The file is in the user's private data folder. In a shared temp folder, another user can put a link there, and this process then writes through the link.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <returns>The PID file path.</returns>
    public static string PidFilePath(string program)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(program)))[..16];
        var dataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(dataFolder) ? AppContext.BaseDirectory : dataFolder, "xat-llm-engine", $"llama-server-{hash}.pid");
    }

    /// <summary>Builds the text of a PID file. The start time identifies the process, because the system can give the same ID to a later process.</summary>
    /// <param name="process">The running process.</param>
    /// <returns>The file's text.</returns>
    public static string FormatPidRecord(Process process) =>
        string.Create(CultureInfo.InvariantCulture, $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}");

    /// <summary>Tells whether a PID file describes this exact process: the same name and the same start time.</summary>
    /// <param name="process">The process that now has the recorded ID.</param>
    /// <param name="startTicks">The recorded start time, in UTC ticks.</param>
    /// <param name="program">The llama-server path.</param>
    /// <returns><see langword="true"/> when the process is the recorded server.</returns>
    private static bool IsRecordedServer(Process process, long startTicks, string program) =>
        string.Equals(
            process.ProcessName,
            OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(program) : Path.GetFileName(program),
            StringComparison.OrdinalIgnoreCase)
        && (process.StartTime.ToUniversalTime() - new DateTime(startTicks, DateTimeKind.Utc)).Duration() < StartTimeTolerance;

    /// <summary>Stops a server that remains after the engine was killed.</summary>
    /// <remarks>This method checks the name and start time first, because the system can give the old ID to an unrelated program.</remarks>
    /// <param name="pidFile">The PID file path.</param>
    /// <param name="program">The llama-server path.</param>
    /// <param name="logger">The log.</param>
    public static void StopLeftoverServer(string pidFile, string program, ILogger logger)
    {
        try
        {
            var record = File.Exists(pidFile) ? File.ReadAllText(pidFile).Split(' ') : [];
            if (record.Length != 2
                || !int.TryParse(record[0], CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(record[1], CultureInfo.InvariantCulture, out var startTicks))
            {
                return;
            }

            using var leftover = Process.GetProcessById(pid);
            if (IsRecordedServer(leftover, startTicks, program))
            {
                leftover.Kill(entireProcessTree: true);
                LogStoppedLeftover(logger, pid);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            // The process has gone, or it belongs to someone else, so there is nothing to stop.
            LogLeftoverCheckFailed(logger, exception);
        }
    }

    /// <summary>Records the running server. The method only logs a failure, because the file is only an aid for cleanup.</summary>
    /// <param name="pidFile">The PID file path.</param>
    /// <param name="record">The file's text.</param>
    /// <param name="logger">The log.</param>
    public static void WritePidFile(string pidFile, string record, ILogger logger)
    {
        try
        {
            var folder = Path.GetDirectoryName(pidFile)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(pidFile, record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPidFileFailed(logger, exception, pidFile);
        }
    }

    /// <summary>Removes the PID file after the server has stopped.</summary>
    /// <param name="pidFile">The PID file path.</param>
    /// <param name="logger">The log.</param>
    public static void TryDeletePidFile(string pidFile, ILogger logger)
    {
        try
        {
            File.Delete(pidFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPidFileFailed(logger, exception, pidFile);
        }
    }

    /// <summary>Logs the removal of a leftover server.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="pid">The process ID.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine stopped a llama-server from an earlier run, process {Pid}.")]
    private static partial void LogStoppedLeftover(ILogger logger, int pid);

    /// <summary>Logs why the leftover check found nothing to stop.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(Level = LogLevel.Debug, Message = "The engine stopped no llama-server from an earlier run.")]
    private static partial void LogLeftoverCheckFailed(ILogger logger, Exception exception);

    /// <summary>Logs a PID file problem.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="exception">The cause.</param>
    /// <param name="pidFile">The PID file path.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine could not update {PidFile}. If the engine is killed, you may have to stop its llama-server manually.")]
    private static partial void LogPidFileFailed(ILogger logger, Exception exception, string pidFile);
}
