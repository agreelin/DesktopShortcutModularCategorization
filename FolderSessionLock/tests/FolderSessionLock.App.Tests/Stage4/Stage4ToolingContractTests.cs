using System.Diagnostics;

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
        ProcessResult result = RunPowerShell(
            Path.Combine(
                FindSolutionRoot(),
                "tests",
                "FolderSessionLock.App.Tests",
                "Stage4",
                "Stage4WalCrossProcess.Tests.ps1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "STAGE4_WAL_CROSS_PROCESS_PASS",
            result.Output,
            StringComparison.Ordinal);
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
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Windows PowerShell 5.1 did not exit.");
        return new ProcessResult(
            process.ExitCode,
            standardOutput + Environment.NewLine + standardError);
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
}

[CollectionDefinition("Stage4 tooling process isolation", DisableParallelization = true)]
public sealed class Stage4ToolingProcessIsolationCollection;
