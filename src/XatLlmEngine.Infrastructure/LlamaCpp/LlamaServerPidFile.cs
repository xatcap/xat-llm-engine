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

    /// <summary>The longest wait for a leftover server to exit after the engine stops it.</summary>
    private static readonly TimeSpan LeftoverExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The length to which Linux cuts a process name.</summary>
    private const int LinuxNameLength = 15;

    /// <summary>The name of the PID file in the data folder.</summary>
    internal const string FileName = "llama-server.pid";

    /// <summary>Builds the PID file path of engine versions before the data folder. The engine reads this file once, so that an update does not leave a server running.</summary>
    /// <param name="program">The llama-server path.</param>
    /// <returns>The old PID file path.</returns>
    public static string LegacyPidFilePath(string program)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(program)))[..16];
        return Path.Combine(EngineDataFolder.Root(), "xat-llm-engine", $"llama-server-{hash}.pid");
    }

    /// <summary>Tells whether a PID file describes this exact process: the same program and the same start time.</summary>
    /// <param name="process">The process that now has the recorded ID.</param>
    /// <param name="startTicks">The recorded start time, in UTC ticks.</param>
    /// <param name="program">The llama-server path.</param>
    /// <returns><see langword="true"/> when the process is the recorded server.</returns>
    private static bool IsRecordedServer(Process process, long startTicks, string program) =>
        HasProgramName(process.ProcessName, program)
        && (process.StartTime.ToUniversalTime() - new DateTime(startTicks, DateTimeKind.Utc)).Duration() < StartTimeTolerance;

    /// <summary>
    /// Compares a process name with a program. On Linux, .NET can report the full name or the name cut to 15 characters,
    /// so both match. No other part of the name matches. The start-time check in <see cref="IsRecordedServer"/> also
    /// protects another program with the same first 15 characters.
    /// </summary>
    /// <param name="processName">The name that the system reports.</param>
    /// <param name="program">The llama-server path.</param>
    /// <returns><see langword="true"/> when the name belongs to the program.</returns>
    internal static bool HasProgramName(string processName, string program)
    {
        var expected = OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(program) : Path.GetFileName(program);
        if (string.Equals(processName, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return OperatingSystem.IsLinux()
            && expected.Length > LinuxNameLength
            && string.Equals(processName, expected[..LinuxNameLength], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stops a server that remains after the engine was killed, and waits until it has exited.</summary>
    /// <remarks>
    /// This method checks the name and start time first, because the system can give the old ID to an unrelated program.
    /// The caller must hold the instance lock, so that this server cannot belong to another running engine.
    /// </remarks>
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

                // The old server holds memory and maybe the GPU. The new server must not start before it is gone.
                if (!leftover.WaitForExit(LeftoverExitTimeout))
                {
                    LogLeftoverSlow(logger, pid, LeftoverExitTimeout);
                }

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

    /// <summary>Logs a leftover server that did not exit in time.</summary>
    /// <param name="logger">The log.</param>
    /// <param name="pid">The process ID.</param>
    /// <param name="timeout">The wait.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "The llama-server from an earlier run, process {Pid}, did not exit within {Timeout}.")]
    private static partial void LogLeftoverSlow(ILogger logger, int pid, TimeSpan timeout);

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
