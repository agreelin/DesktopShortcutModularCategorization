using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.Tests.Stage4;

[Collection("Stage4 tooling process isolation")]
public sealed class Stage4ToolingContractTests
{
    [Fact]
    public void PowerShell51_ModuleBehaviorContractPasses()
    {
        ProcessResult result = RunPowerShell(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4ToolingBehavior.Tests.ps1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("STAGE4_TOOLING_BEHAVIOR_PASS", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell51_RepositoryIntegrityBehaviorContractPasses()
    {
        ProcessResult result = RunPowerShell(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4RepositoryIntegrityBehavior.Tests.ps1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_REPOSITORY_INTEGRITY_BEHAVIOR_PASS",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell51_WalCrossProcessContractPasses()
    {
        ProcessResult result = RunPowerShellWithTimeout(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4WalCrossProcess.Tests.ps1"),
            240_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_WAL_CROSS_PROCESS_PASS",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell51_PublishOverlapsHaveIdenticalContent()
    {
        ProcessResult result = RunPowerShellWithTimeout(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4PublishOverlapBehavior.Tests.ps1"),
            120_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_PUBLISH_OVERLAP_BEHAVIOR_PASS",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell51_FormalLauncherBundleContractPasses()
    {
        ProcessResult result = RunPowerShellWithTimeout(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4FormalLauncherBundle.Tests.ps1"),
            180_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell51_RecoveryAuthorityBundleContractPasses()
    {
        ProcessResult result = RunPowerShellWithTimeout(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4RecoveryAuthorityBundle.Tests.ps1"),
            120_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_RECOVERY_AUTHORITY_BUNDLE_PASS Cases=218 Assertions=305",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCreationFlags_JobBreakawayBehaviorMatchesContract()
    {
        AssertCurrentTokenIsNonElevated();

        const uint createBreakawayFromJob = 0x01000000;
        const uint createNoWindow = 0x08000000;
        const uint expectedFlags = 0x09000000;
        Assert.Equal(expectedFlags, createBreakawayFromJob | createNoWindow);
        Assert.Equal(0u, expectedFlags & 0x00000200);

        string fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            JobProbeResult allowed = RunJobProbe(fixtureRoot, breakawayAllowed: true);
            Assert.True(allowed.Created);
            Assert.Equal(0, allowed.Error);
            Assert.True(allowed.ProcessId > 0);
            Assert.True(allowed.ThreadId > 0);
            Assert.Equal(1, allowed.AttemptCount);
            Assert.Equal(0, allowed.FallbackCount);
            Assert.False(allowed.TargetWasInProbeJob);
            Assert.False(allowed.JobReopenableAfterCleanup);

            JobProbeResult forbidden = RunJobProbe(
                fixtureRoot,
                breakawayAllowed: false);
            Assert.False(forbidden.Created);
            Assert.Equal(5, forbidden.Error);
            Assert.Equal(0u, forbidden.ProcessId);
            Assert.Equal(0u, forbidden.ThreadId);
            Assert.Equal(0, forbidden.ProcessHandle);
            Assert.Equal(0, forbidden.ThreadHandle);
            Assert.Equal(1, forbidden.AttemptCount);
            Assert.Equal(0, forbidden.FallbackCount);
            Assert.False(forbidden.TargetWasInProbeJob);
            Assert.False(forbidden.JobReopenableAfterCleanup);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }

        Assert.False(Directory.Exists(fixtureRoot));
    }

    [Fact]
    public void EntryPointReturnsFixedInvalidArgumentsExitCode()
    {
        ProcessResult result = RunPowerShell(
            Path.Combine(
                FindSolutionRoot(),
                "eng",
                "stage4",
                "Invoke-Stage4.ps1"),
            "Preflight",
            "-RunId",
            "invalid");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("RunId must match", result.Output, StringComparison.Ordinal);
    }

    private static ProcessResult RunPowerShell(string script, params string[] arguments)
    {
        return RunPowerShellWithTimeout(script, 30_000, arguments);
    }

    private static ProcessResult RunPowerShellWithTimeout(
        string script,
        int timeoutMilliseconds,
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> standardOutputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask =
            process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeoutMilliseconds);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill.
            }

            process.WaitForExit(10_000);
            Task.WaitAll(
                [standardOutputTask, standardErrorTask],
                10_000);
            string timeoutOutput =
                (standardOutputTask.IsCompletedSuccessfully
                    ? standardOutputTask.Result
                    : "<stdout drain incomplete>") +
                Environment.NewLine +
                (standardErrorTask.IsCompletedSuccessfully
                    ? standardErrorTask.Result
                    : "<stderr drain incomplete>");
            Assert.Fail(
                "Windows PowerShell 5.1 did not exit." +
                Environment.NewLine +
                timeoutOutput);
        }

        process.WaitForExit();
        Task.WaitAll(standardOutputTask, standardErrorTask);
        string standardOutput = standardOutputTask.Result;
        string standardError = standardErrorTask.Result;
        return new ProcessResult(
            process.ExitCode,
            standardOutput + Environment.NewLine + standardError);
    }

    private static JobProbeResult RunJobProbe(
        string fixtureRoot,
        bool breakawayAllowed)
    {
        string probeId = Guid.NewGuid().ToString("N");
        string jobName = $"FSL.Stage4.JobProbe.{probeId}";
        string eventName = $"FSL.Stage4.JobProbe.Event.{probeId}";
        string resultPath = Path.Combine(fixtureRoot, $"result-{probeId}.json");
        Process? helper = null;
        Process? target = null;
        bool targetWasInProbeJob = false;
        JobProbeWireResult? wire = null;
        using var gate = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventName);
        using SafeFileHandle job = NativeJob.CreateJobObject(
            IntPtr.Zero,
            jobName);
        Assert.False(job.IsInvalid);
        SetProbeJobLimits(job, breakawayAllowed);

        try
        {
            string powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            string encodedProbe = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(GetJobProbePowerShell()));
            var start = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(encodedProbe);
            start.Environment["FSL_JOB_PROBE_EVENT"] = eventName;
            start.Environment["FSL_JOB_PROBE_RESULT"] = resultPath;
            start.Environment["FSL_JOB_PROBE_TARGET"] = powerShell;
            start.Environment["TEMP"] = fixtureRoot;
            start.Environment["TMP"] = fixtureRoot;

            helper = Process.Start(start);
            Assert.NotNull(helper);
            Assert.True(
                NativeJob.AssignProcessToJobObject(
                    job,
                    helper!.SafeHandle),
                $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
            gate.Set();
            Assert.True(
                helper.WaitForExit(30_000),
                "The controlled helper-in-job did not exit.");
            string helperOutput =
                helper.StandardOutput.ReadToEnd() +
                Environment.NewLine +
                helper.StandardError.ReadToEnd();
            Assert.Equal(
                0,
                helper.ExitCode);
            Assert.True(
                File.Exists(resultPath),
                "The controlled helper did not persist its result: " +
                helperOutput);
            wire = JsonSerializer.Deserialize<JobProbeWireResult>(
                File.ReadAllText(resultPath));
            Assert.NotNull(wire);

            if (wire!.Created)
            {
                target = Process.GetProcessById(checked((int)wire.ProcessId));
                Assert.True(
                    NativeJob.IsProcessInJob(
                        target.SafeHandle,
                        job,
                        out bool inProbeJob),
                    $"IsProcessInJob failed: {Marshal.GetLastWin32Error()}");
                targetWasInProbeJob = inProbeJob;
            }
        }
        finally
        {
            if (target is not null)
            {
                try
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                    }

                    target.WaitForExit(10_000);
                }
                finally
                {
                    target.Dispose();
                }
            }

            if (helper is not null)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill(entireProcessTree: true);
                    }

                    helper.WaitForExit(10_000);
                }
                finally
                {
                    helper.Dispose();
                }
            }
        }

        job.Dispose();
        using SafeFileHandle reopened = NativeJob.OpenJobObject(
            0x0004,
            false,
            jobName);
        bool jobReopenable = !reopened.IsInvalid;
        Assert.NotNull(wire);
        return new JobProbeResult(
            wire!.Created,
            wire.Error,
            wire.ProcessId,
            wire.ThreadId,
            wire.ProcessHandle,
            wire.ThreadHandle,
            wire.AttemptCount,
            wire.FallbackCount,
            targetWasInProbeJob,
            jobReopenable);
    }

    private static void SetProbeJobLimits(
        SafeFileHandle job,
        bool breakawayAllowed)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags =
                    0x00002000u |
                    (breakawayAllowed ? 0x00000800u : 0u),
            },
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            Assert.True(
                NativeJob.SetInformationJobObject(job, 9, buffer, (uint)size),
                $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void AssertCurrentTokenIsNonElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.False(
            principal.IsInRole(WindowsBuiltInRole.Administrator),
            "The native job probe requires the current token to be non-elevated.");
        Assert.True(
            NativeJob.OpenProcessToken(
                NativeJob.GetCurrentProcess(),
                0x0008,
                out SafeFileHandle token),
            $"OpenProcessToken failed: {Marshal.GetLastWin32Error()}");
        using (token)
        {
            int size = sizeof(int);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Assert.True(
                    NativeJob.GetTokenInformation(
                        token,
                        20,
                        buffer,
                        size,
                        out int returned),
                    $"GetTokenInformation failed: {Marshal.GetLastWin32Error()}");
                Assert.Equal(size, returned);
                Assert.Equal(0, Marshal.ReadInt32(buffer));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string GetJobProbePowerShell()
    {
        return """
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            using System.Text;
            public static class FslJobProbeNative {
              public const uint CREATE_BREAKAWAY_FROM_JOB=0x01000000;
              public const uint CREATE_NO_WINDOW=0x08000000;
              public const uint FSL_CREATION_FLAGS=
                CREATE_BREAKAWAY_FROM_JOB|CREATE_NO_WINDOW;
              [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
              public struct STARTUPINFO {
                public int cb;public string reserved,desktop,title;
                public uint x,y,xSize,ySize,xChars,yChars,fill,flags;
                public short show,reserved2;public IntPtr reservedPtr;
                public IntPtr input,output,error;
              }
              [StructLayout(LayoutKind.Sequential)]
              public struct PROCESS_INFORMATION {
                public IntPtr process,thread;public uint processId,threadId;
              }
              [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
              public static extern bool CreateProcessW(
                string app,StringBuilder command,IntPtr pa,IntPtr ta,
                bool inherit,uint flags,IntPtr environment,string directory,
                ref STARTUPINFO startup,out PROCESS_INFORMATION process);
              [DllImport("kernel32.dll",SetLastError=true)]
              public static extern bool CloseHandle(IntPtr handle);
            }
            '@
            $gate = [Threading.EventWaitHandle]::OpenExisting(
              $env:FSL_JOB_PROBE_EVENT)
            try {
              if (-not $gate.WaitOne(10000)) { throw 'Probe gate timed out.' }
              $startup = New-Object FslJobProbeNative+STARTUPINFO
              $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
              $process = New-Object FslJobProbeNative+PROCESS_INFORMATION
              $command = [Text.StringBuilder]::new(
                '"' + $env:FSL_JOB_PROBE_TARGET +
                '" -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 20"')
              $attemptCount = 1
              $fallbackCount = 0
              $created = [FslJobProbeNative]::CreateProcessW(
                $env:FSL_JOB_PROBE_TARGET,$command,[IntPtr]::Zero,
                [IntPtr]::Zero,$false,
                [FslJobProbeNative]::FSL_CREATION_FLAGS,[IntPtr]::Zero,
                $env:TEMP,[ref]$startup,[ref]$process)
              $errorCode = if ($created) {
                0
              } else {
                [Runtime.InteropServices.Marshal]::GetLastWin32Error()
              }
              $result = [pscustomobject][ordered]@{
                Created = [bool]$created
                Error = [int]$errorCode
                ProcessId = [uint32]$process.processId
                ThreadId = [uint32]$process.threadId
                ProcessHandle = [int64]$process.process
                ThreadHandle = [int64]$process.thread
                AttemptCount = [int]$attemptCount
                FallbackCount = [int]$fallbackCount
              }
              if ($process.thread -ne [IntPtr]::Zero) {
                [void][FslJobProbeNative]::CloseHandle($process.thread)
              }
              if ($process.process -ne [IntPtr]::Zero) {
                [void][FslJobProbeNative]::CloseHandle($process.process)
              }
              [IO.File]::WriteAllText(
                $env:FSL_JOB_PROBE_RESULT,
                ($result | ConvertTo-Json -Compress),
                [Text.UTF8Encoding]::new($false,$true))
              exit 0
            }
            finally {
              $gate.Dispose()
            }
            """;
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderSessionLock.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record JobProbeResult(
        bool Created,
        int Error,
        uint ProcessId,
        uint ThreadId,
        long ProcessHandle,
        long ThreadHandle,
        int AttemptCount,
        int FallbackCount,
        bool TargetWasInProbeJob,
        bool JobReopenableAfterCleanup);

    private sealed record JobProbeWireResult(
        bool Created,
        int Error,
        uint ProcessId,
        uint ThreadId,
        long ProcessHandle,
        long ThreadHandle,
        int AttemptCount,
        int FallbackCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private static class NativeJob
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(
            IntPtr securityAttributes,
            string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle OpenJobObject(
            uint desiredAccess,
            bool inheritHandle,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeFileHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(
            SafeProcessHandle process,
            SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(
            IntPtr process,
            uint desiredAccess,
            out SafeFileHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            SafeFileHandle token,
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);
    }
}

[CollectionDefinition("Stage4 tooling process isolation", DisableParallelization = true)]
public sealed class Stage4ToolingProcessIsolationCollection;
