using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace XatLlmEngine.Infrastructure.LlamaCpp;

/// <summary>
/// On Windows, puts llama-server in a job object that stops its processes when the engine process ends. Then a parent app
/// that kills the engine does not leave llama-server running. Other systems have no such feature. See ADR 0001.
/// </summary>
internal static partial class ChildProcessJob
{
    /// <summary>The job object information class for the extended limits.</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>The limit that stops each process of the job when the last handle to the job closes.</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>The Windows error code from the creation of the job, when the creation failed.</summary>
    private static int JobError;

    /// <summary>The job of this engine. Only the engine holds its handle, so the system closes it when the engine ends, also after a kill.</summary>
    private static readonly Lazy<SafeFileHandle?> Job = new(() => OperatingSystem.IsWindows() ? CreateJob() : null, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Puts a process in the job. On other systems than Windows, it does nothing.</summary>
    /// <remarks>
    /// The process runs for a short time before it is in the job. A process that it starts in that time is not in the job.
    /// llama-server starts no other processes.
    /// </remarks>
    /// <param name="process">A started process.</param>
    /// <param name="error">The Windows error code when Windows refused, otherwise 0.</param>
    /// <returns><see langword="false"/> when Windows refused. Then a killed engine can leave the process running until the next start.</returns>
    public static bool TryAdd(Process process, out int error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (Job.Value is { } job && AssignProcessToJobObject(job, process.Handle))
        {
            return true;
        }

        error = Job.Value is null ? JobError : Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>Makes the job and sets the limit that stops its processes.</summary>
    /// <returns>The job, or <see langword="null"/> when Windows refused.</returns>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? CreateJob()
    {
        var job = CreateJobObjectW(0, 0);
        if (job.IsInvalid)
        {
            JobError = Marshal.GetLastPInvokeError();
            job.Dispose();
            return null;
        }

        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            JobError = Marshal.GetLastPInvokeError();
            job.Dispose();
            return null;
        }

        return job;
    }

    /// <summary>Makes a job object.</summary>
    /// <param name="securityAttributes">No security attributes.</param>
    /// <param name="name">No name.</param>
    /// <returns>The job handle.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial SafeFileHandle CreateJobObjectW(nint securityAttributes, nint name);

    /// <summary>Sets limits of a job object.</summary>
    /// <param name="job">The job.</param>
    /// <param name="informationClass">The kind of information.</param>
    /// <param name="information">The limits.</param>
    /// <param name="length">The size of the limits.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    /// <summary>Puts a process in a job object.</summary>
    /// <param name="job">The job.</param>
    /// <param name="process">The process handle.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    /// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        /// <summary>The time limit of each process.</summary>
        public long PerProcessUserTimeLimit;

        /// <summary>The time limit of the job.</summary>
        public long PerJobUserTimeLimit;

        /// <summary>The limits that apply.</summary>
        public uint LimitFlags;

        /// <summary>The smallest working set.</summary>
        public nuint MinimumWorkingSetSize;

        /// <summary>The largest working set.</summary>
        public nuint MaximumWorkingSetSize;

        /// <summary>The largest number of processes.</summary>
        public uint ActiveProcessLimit;

        /// <summary>The processor affinity.</summary>
        public nuint Affinity;

        /// <summary>The priority class.</summary>
        public uint PriorityClass;

        /// <summary>The scheduling class.</summary>
        public uint SchedulingClass;
    }

    /// <summary>IO_COUNTERS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        /// <summary>The read operations.</summary>
        public ulong ReadOperationCount;

        /// <summary>The write operations.</summary>
        public ulong WriteOperationCount;

        /// <summary>The other operations.</summary>
        public ulong OtherOperationCount;

        /// <summary>The bytes read.</summary>
        public ulong ReadTransferCount;

        /// <summary>The bytes written.</summary>
        public ulong WriteTransferCount;

        /// <summary>The other bytes.</summary>
        public ulong OtherTransferCount;
    }

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        /// <summary>The basic limits.</summary>
        public BasicLimitInformation BasicLimitInformation;

        /// <summary>The input and output counts.</summary>
        public IoCounters IoInfo;

        /// <summary>The memory limit of each process.</summary>
        public nuint ProcessMemoryLimit;

        /// <summary>The memory limit of the job.</summary>
        public nuint JobMemoryLimit;

        /// <summary>The most memory that one process used.</summary>
        public nuint PeakProcessMemoryUsed;

        /// <summary>The most memory that the job used.</summary>
        public nuint PeakJobMemoryUsed;
    }
}
