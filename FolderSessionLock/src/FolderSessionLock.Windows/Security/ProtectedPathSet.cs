namespace FolderSessionLock.Windows.Security;

using FolderSessionLock.Windows.Services;

public sealed class ProtectedPathSet
{
    private ProtectedPathSet(string programFiles, string programData)
    {
        InstallDirectory = Path.Combine(programFiles, "FolderSessionLock");
        RecoveryRoot = Path.Combine(programData, "FolderSessionLock", "Recovery");
        RecoveryRecordsDirectory = Path.Combine(RecoveryRoot, "Records");
        ReplayDirectory = Path.Combine(programData, "FolderSessionLock", "Replay", "v1");
        ReadinessDirectory = Path.Combine(programData, "FolderSessionLock", "Readiness");
        LogsRoot = Path.Combine(programData, "FolderSessionLock", "Logs", "v1");
    }

    public string InstallDirectory { get; }

    public string RecoveryRoot { get; }

    public string RecoveryRecordsDirectory { get; }

    public string ReplayDirectory { get; }

    public string ReadinessDirectory { get; }

    public string LogsRoot { get; }

    public static ProtectedPathSet CreateProduction() => new(
        WindowsKnownFolderPath.GetRequiredPath(WindowsKnownFolderPath.ProgramFiles),
        WindowsKnownFolderPath.GetRequiredPath(WindowsKnownFolderPath.ProgramData));

    internal static ProtectedPathSet CreateForTest(string programFiles, string programData) => new(
        Path.GetFullPath(programFiles),
        Path.GetFullPath(programData));

    public string GetExpectedPath(ProtectedPathKind pathKind) => pathKind switch
    {
        ProtectedPathKind.InstallDirectory => InstallDirectory,
        ProtectedPathKind.RecoveryRoot => RecoveryRoot,
        ProtectedPathKind.RecoveryRecordsDirectory => RecoveryRecordsDirectory,
        ProtectedPathKind.ReplayDirectory => ReplayDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(pathKind)),
    };

    public IReadOnlyList<ProtectedPathSecurityCheckRequest> CreateRequests() =>
    [
        new(ProtectedPathKind.InstallDirectory, InstallDirectory),
        new(ProtectedPathKind.RecoveryRoot, RecoveryRoot),
        new(ProtectedPathKind.RecoveryRecordsDirectory, RecoveryRecordsDirectory),
        new(ProtectedPathKind.ReplayDirectory, ReplayDirectory),
    ];
}
