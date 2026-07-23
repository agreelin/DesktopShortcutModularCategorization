using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsRepositoryPathClassifier : IRepositoryRootClassifier
{
    private static readonly string[] Markers = [".git", ".hg", ".svn"];
    private readonly WindowsRepositoryPathPlatform _platform;

    public WindowsRepositoryPathClassifier()
        : this(new WindowsRepositoryPathPlatform())
    {
    }

    internal WindowsRepositoryPathClassifier(WindowsRepositoryPathPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Result<bool> IsUnderRepositoryRoot(ValidatedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var evidence = new List<AncestorEvidence>();
        var links = new List<AncestorLink>();
        try
        {
            Result<AncestorEvidence> target = ReadEvidence(
                directory.Handle,
                ownsHandle: false,
                NormalizeFinalPath(directory.FinalPath));
            if (target.IsFailure || target.Value.Identity != directory.Identity)
            {
                return Failure();
            }

            string rootPath = Path.GetPathRoot(target.Value.FinalPath)!;
            Result<SafeFileHandle> rootOpen = _platform.OpenVolumeRoot(rootPath);
            if (rootOpen.IsFailure)
            {
                return Failure();
            }

            using SafeFileHandle rootHandle = rootOpen.Value;
            Result<AncestorEvidence> root = ReadEvidence(
                rootHandle,
                ownsHandle: false,
                NormalizeFinalPath(rootPath));
            if (root.IsFailure
                || root.Value.Identity.VolumeSerialNumber
                    != target.Value.Identity.VolumeSerialNumber)
            {
                return Failure();
            }

            evidence.Add(target.Value);
            while (true)
            {
                AncestorEvidence current = evidence[^1];
                foreach (string marker in Markers)
                {
                    RepositoryMarkerProbe probe = _platform.ProbeMarker(current.Handle, marker);
                    if (probe == RepositoryMarkerProbe.Error)
                    {
                        return Failure();
                    }

                    if (probe == RepositoryMarkerProbe.Found)
                    {
                        return VerifyChain(evidence, links)
                            ? Result<bool>.Success(true)
                            : Failure();
                    }
                }

                if (!VerifyEvidence(current))
                {
                    return Failure();
                }

                if (string.Equals(
                    current.FinalPath,
                    root.Value.FinalPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return VerifyChain(evidence, links)
                        ? Result<bool>.Success(false)
                        : Failure();
                }

                string? expectedParentPath = Path.GetDirectoryName(current.FinalPath);
                string childLeaf = Path.GetFileName(current.FinalPath);
                if (string.IsNullOrEmpty(expectedParentPath)
                    || string.IsNullOrEmpty(childLeaf))
                {
                    return Failure();
                }

                Result<AncestorEvidence> parent;
                if (string.Equals(
                    NormalizeFinalPath(expectedParentPath),
                    root.Value.FinalPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    parent = Result<AncestorEvidence>.Success(root.Value);
                }
                else
                {
                    string relative = NormalizeFinalPath(expectedParentPath)[root.Value.FinalPath.Length..];
                    Result<SafeFileHandle> parentOpen = _platform.OpenAncestor(rootHandle, relative);
                    if (parentOpen.IsFailure)
                    {
                        return Failure();
                    }

                    parent = ReadEvidence(
                        parentOpen.Value,
                        ownsHandle: true,
                        NormalizeFinalPath(expectedParentPath));
                    if (parent.IsFailure)
                    {
                        parentOpen.Value.Dispose();
                        return Failure();
                    }
                }

                if (!VerifyChild(parent.Value.Handle, childLeaf, current.Identity))
                {
                    if (parent.Value.OwnsHandle)
                    {
                        parent.Value.Handle.Dispose();
                    }

                    return Failure();
                }

                evidence.Add(parent.Value);
                links.Add(new AncestorLink(parent.Value, current, childLeaf));
            }
        }
        finally
        {
            foreach (AncestorEvidence item in evidence)
            {
                if (item.OwnsHandle)
                {
                    item.Handle.Dispose();
                }
            }
        }
    }

    private Result<AncestorEvidence> ReadEvidence(
        SafeFileHandle handle,
        bool ownsHandle,
        string? expectedPath)
    {
        Result<DirectoryIdentity> identity = _platform.GetIdentity(handle);
        Result<string> finalPath = _platform.GetFinalPath(handle);
        if (identity.IsFailure || finalPath.IsFailure)
        {
            return Failure<AncestorEvidence>();
        }

        string normalized = NormalizeFinalPath(finalPath.Value);
        if (expectedPath is not null
            && !string.Equals(normalized, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<AncestorEvidence>();
        }

        return Result<AncestorEvidence>.Success(new(
            handle,
            ownsHandle,
            identity.Value,
            normalized));
    }

    private bool VerifyEvidence(AncestorEvidence evidence)
    {
        Result<DirectoryIdentity> identity = _platform.GetIdentity(evidence.Handle);
        Result<string> finalPath = _platform.GetFinalPath(evidence.Handle);
        return identity.IsSuccess
            && finalPath.IsSuccess
            && identity.Value == evidence.Identity
            && string.Equals(
                NormalizeFinalPath(finalPath.Value),
                evidence.FinalPath,
                StringComparison.OrdinalIgnoreCase);
    }

    private bool VerifyChild(
        SafeFileHandle parent,
        string childLeaf,
        DirectoryIdentity expectedIdentity)
    {
        Result<DirectoryIdentity> result = _platform.GetChildIdentity(parent, childLeaf);
        return result.IsSuccess && result.Value == expectedIdentity;
    }

    private bool VerifyChain(
        IReadOnlyList<AncestorEvidence> evidence,
        IReadOnlyList<AncestorLink> links) =>
        evidence.All(VerifyEvidence)
        && links.All(link => VerifyChild(
            link.Parent.Handle,
            link.ChildLeaf,
            link.Child.Identity));

    private static string NormalizeFinalPath(string finalPath)
    {
        const string prefix = @"\\?\";
        string path = finalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? finalPath[prefix.Length..]
            : finalPath;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static Result<bool> Failure() => Result<bool>.Failure(ClassificationError());

    private static Result<T> Failure<T>() => Result<T>.Failure(ClassificationError());

    private static Error ClassificationError() => new(
        BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError);

    private sealed record AncestorEvidence(
        SafeFileHandle Handle,
        bool OwnsHandle,
        DirectoryIdentity Identity,
        string FinalPath);

    private sealed record AncestorLink(
        AncestorEvidence Parent,
        AncestorEvidence Child,
        string ChildLeaf);
}

internal enum RepositoryMarkerProbe
{
    NotFound,
    Found,
    Error,
}

internal class WindowsRepositoryPathPlatform
{
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint StatusObjectNameNotFound = 0xC0000034;
    private const uint StatusObjectPathNotFound = 0xC000003A;
    private readonly WindowsFolderPathPlatform _paths = new();

    internal uint LastStatus { get; private set; }

    internal virtual RepositoryMarkerProbe ProbeMarker(
        SafeFileHandle directoryHandle,
        string marker)
    {
        Result<SafeFileHandle> open = OpenRelative(
            directoryHandle,
            marker,
            FileSynchronousIoNonalert | FileOpenReparsePoint,
            out uint status);
        LastStatus = status;
        if (open.IsSuccess)
        {
            open.Value.Dispose();
            return RepositoryMarkerProbe.Found;
        }

        return status is StatusObjectNameNotFound or StatusObjectPathNotFound
            ? RepositoryMarkerProbe.NotFound
            : RepositoryMarkerProbe.Error;
    }

    internal virtual Result<SafeFileHandle> OpenVolumeRoot(string rootPath) =>
        _paths.OpenPath(rootPath, NativeMethods.FileReadAttributes);

    internal virtual Result<SafeFileHandle> OpenAncestor(
        SafeFileHandle volumeRoot,
        string relativePath)
    {
        Result<SafeFileHandle> result = OpenRelative(
            volumeRoot,
            relativePath,
            FileDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
            out uint status);
        LastStatus = status;
        return result;
    }

    internal virtual Result<DirectoryIdentity> GetChildIdentity(
        SafeFileHandle parent,
        string childLeaf)
    {
        Result<SafeFileHandle> child = OpenRelative(
            parent,
            childLeaf,
            FileDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
            out uint status);
        LastStatus = status;
        if (child.IsFailure)
        {
            return FailureIdentity();
        }

        using SafeFileHandle handle = child.Value;
        return GetIdentity(handle);
    }

    internal virtual Result<DirectoryIdentity> GetIdentity(SafeFileHandle handle) =>
        _paths.GetDirectoryIdentity(handle);

    internal virtual Result<string> GetFinalPath(SafeFileHandle handle) =>
        _paths.GetFinalPath(handle);

    private static Result<SafeFileHandle> OpenRelative(
        SafeFileHandle directoryHandle,
        string leaf,
        uint options,
        out uint status)
    {
        nint nameBuffer = Marshal.StringToHGlobalUni(leaf);
        nint unicodeBuffer = nint.Zero;
        bool addedRef = false;
        try
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)(leaf.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leaf.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(name, unicodeBuffer, false);
            directoryHandle.DangerousAddRef(ref addedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = unicodeBuffer,
                Attributes = 0x00000040,
            };
            int result = NtCreateFile(
                out nint rawHandle,
                NativeMethods.FileReadAttributes | 0x00100000,
                ref attributes,
                out _,
                nint.Zero,
                0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                FileOpen,
                options,
                nint.Zero,
                0);
            status = unchecked((uint)result);
            return result >= 0
                ? Result<SafeFileHandle>.Success(new SafeFileHandle(rawHandle, true))
                : FailureHandle();
        }
        finally
        {
            if (addedRef)
            {
                directoryHandle.DangerousRelease();
            }

            if (unicodeBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(unicodeBuffer);
            }

            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static Result<SafeFileHandle> FailureHandle() =>
        Result<SafeFileHandle>.Failure(ClassificationError());

    private static Result<DirectoryIdentity> FailureIdentity() =>
        Result<DirectoryIdentity>.Failure(ClassificationError());

    private static Error ClassificationError() => new(
        BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal nint RootDirectory;
        internal nint ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor;
        internal nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status;
        internal nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out nint fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);
}
