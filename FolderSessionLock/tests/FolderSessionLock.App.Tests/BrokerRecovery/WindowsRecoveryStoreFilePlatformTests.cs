using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class WindowsRecoveryStoreFilePlatformTests
{
    private const int FileRenameInformationEx = 65;
    private const int FileRenameInformation = 10;
    private const int SetFileInformationByHandleRenameInfoEx = 22;
    private const int SetFileInformationByHandleRenameInfo = 3;
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    [Fact]
    public void Rename_New_UsesClass65FlagsZeroAndRelativeLeaf()
    {
        var native = new RecordingRenameNative();
        var platform = new WindowsRecoveryStoreFilePlatform(native);
        using SafeFileHandle fileHandle = Handle(0x1111);
        using SafeFileHandle directoryHandle = Handle(0x2222);
        const string leaf = "12345678-1234-4234-8234-123456789abc.fslr";

        Result result = platform.Rename(
            fileHandle,
            directoryHandle,
            leaf,
            replaceExisting: false);

        Assert.True(result.IsSuccess, result.Error?.Code);
        RenameCall call = Assert.Single(native.Calls);
        Assert.Equal(FileRenameInformationEx, call.FileInformationClass);
        Assert.Equal(0u, Flags(call));
        Assert.Equal(directoryHandle.DangerousGetHandle(), RootDirectory(call));
        Assert.Equal(leaf, FileName(call));
    }

    [Fact]
    public void Rename_Update_UsesClass65FlagsThreeAndRetainsOldAndNewHandles()
    {
        string root = CreateRoot();
        try
        {
            RunRetainedHandleReplace(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rename_BufferUsesUtf16ByteLengthAndMinimumStructSize()
    {
        var native = new RecordingRenameNative();
        var platform = new WindowsRecoveryStoreFilePlatform(native);
        using SafeFileHandle fileHandle = Handle(0x1111);
        using SafeFileHandle directoryHandle = Handle(0x2222);
        const string leaf = "é-12345678-1234-4234-8234-123456789abc.fslr";

        Result result = platform.Rename(
            fileHandle,
            directoryHandle,
            leaf,
            replaceExisting: false);

        Assert.True(result.IsSuccess, result.Error?.Code);
        RenameCall call = Assert.Single(native.Calls);
        int nameLength = Encoding.Unicode.GetByteCount(leaf);
        Assert.Equal((uint)nameLength, FileNameLength(call));
        Assert.True(
            call.Buffer.Length
                >= Marshal.SizeOf<WindowsRecoveryStoreFilePlatform.FileRenameInformation>()
                    + nameLength);
        Assert.Equal(leaf, FileName(call));
    }

    [Theory]
    [InlineData(@"C:\absolute.fslr")]
    [InlineData(@"child\record.fslr")]
    [InlineData("child/record.fslr")]
    public void Rename_RejectsAbsoluteOrNonLeafTargetsBeforeNativeCall(string target)
    {
        var native = new RecordingRenameNative();
        var platform = new WindowsRecoveryStoreFilePlatform(native);
        using SafeFileHandle fileHandle = Handle(0x1111);
        using SafeFileHandle directoryHandle = Handle(0x2222);

        Result result = platform.Rename(
            fileHandle,
            directoryHandle,
            target,
            replaceExisting: false);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED,
            result.Error!.Code);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Rename_MapsNewCollisionToAlreadyExists()
    {
        AssertRenameError(
            StatusObjectNameCollision,
            dosError: 5,
            replaceExisting: false,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS);
        AssertRenameError(
            StatusUnsuccessful,
            dosError: 80,
            replaceExisting: false,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS);
        AssertRenameError(
            StatusUnsuccessful,
            dosError: 183,
            replaceExisting: false,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS);
        AssertRenameError(
            StatusObjectNameCollision,
            dosError: 183,
            replaceExisting: true,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED);
    }

    [Fact]
    public void Rename_MapsUnsupportedAndFailureStatusesExactly()
    {
        AssertRenameError(
            StatusUnsuccessful,
            dosError: 50,
            replaceExisting: false,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED);
        AssertRenameError(
            StatusUnsuccessful,
            dosError: 87,
            replaceExisting: true,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED);
        AssertRenameError(
            StatusUnsuccessful,
            dosError: 5,
            replaceExisting: false,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED);
    }

    [Fact]
    public void Rename_NeverUsesClass10Class22Class3OrAbsoluteFallback()
    {
        var native = new RecordingRenameNative();
        var platform = new WindowsRecoveryStoreFilePlatform(native);
        using SafeFileHandle fileHandle = Handle(0x1111);
        using SafeFileHandle directoryHandle = Handle(0x2222);

        Assert.True(platform.Rename(
            fileHandle,
            directoryHandle,
            "11111111-1111-4111-8111-111111111111.fslr",
            replaceExisting: false).IsSuccess);
        Assert.True(platform.Rename(
            fileHandle,
            directoryHandle,
            "22222222-2222-4222-8222-222222222222.fslr",
            replaceExisting: true).IsSuccess);
        Result rejected = platform.Rename(
            fileHandle,
            directoryHandle,
            @"C:\fallback.fslr",
            replaceExisting: true);

        Assert.True(rejected.IsFailure);
        Assert.Equal(2, native.Calls.Count);
        Assert.All(native.Calls, call => Assert.Equal(FileRenameInformationEx, call.FileInformationClass));
        Assert.DoesNotContain(native.Calls, call => call.FileInformationClass == FileRenameInformation);
        Assert.DoesNotContain(
            native.Calls,
            call => call.FileInformationClass == SetFileInformationByHandleRenameInfoEx);
        Assert.DoesNotContain(
            native.Calls,
            call => call.FileInformationClass == SetFileInformationByHandleRenameInfo);
        Assert.All(native.Calls, call =>
        {
            string leaf = FileName(call);
            Assert.Equal(leaf, Path.GetFileName(leaf));
            Assert.False(Path.IsPathFullyQualified(leaf));
        });
    }

    private static void RunRetainedHandleReplace(string root)
    {
        var platform = new WindowsRecoveryStoreFilePlatform();
        using SafeFileHandle directoryHandle = platform.OpenDirectory(root).Value;
        const string canonicalLeaf = "12345678-1234-4234-8234-123456789abc.fslr";
        const string initialLeaf = "12345678-1234-4234-8234-123456789abc.tmp-11111111-1111-4111-8111-111111111111";
        const string updateLeaf = "12345678-1234-4234-8234-123456789abc.tmp-22222222-2222-4222-8222-222222222222";
        using SafeFileHandle initialHandle = platform.CreateTemporary(directoryHandle, initialLeaf).Value;
        Assert.True(platform.WriteAll(initialHandle, Encoding.UTF8.GetBytes("old")).IsSuccess);
        Assert.True(platform.Flush(initialHandle).IsSuccess);
        Assert.True(platform.Rename(
            initialHandle,
            directoryHandle,
            canonicalLeaf,
            replaceExisting: false).IsSuccess);
        Assert.Equal("old", ReadUtf8(platform, initialHandle));
        RecoveryRecordFileIdentity initialIdentity = platform.GetIdentity(initialHandle).Value;
        Assert.Equal(
            initialIdentity,
            platform.GetLeafIdentity(directoryHandle, canonicalLeaf).Value);
        initialHandle.Dispose();
        using SafeFileHandle oldCanonicalHandle = platform.OpenExisting(
            directoryHandle,
            canonicalLeaf).Value;
        using SafeFileHandle updateHandle = platform.CreateTemporary(directoryHandle, updateLeaf).Value;
        Assert.True(platform.WriteAll(updateHandle, Encoding.UTF8.GetBytes("new")).IsSuccess);
        Assert.True(platform.Flush(updateHandle).IsSuccess);

        Result replace = platform.Rename(
            updateHandle,
            directoryHandle,
            canonicalLeaf,
            replaceExisting: true);

        Assert.True(replace.IsSuccess, replace.Error?.Code);
        Assert.Equal("new", ReadUtf8(platform, updateHandle));
        Assert.Equal("old", ReadUtf8(platform, oldCanonicalHandle));
        Assert.True(File.Exists(Path.Combine(root, canonicalLeaf)));
        RecoveryRecordFileIdentity updateIdentity = platform.GetIdentity(updateHandle).Value;
        Assert.Equal(
            updateIdentity,
            platform.GetLeafIdentity(directoryHandle, canonicalLeaf).Value);
    }

    private static void AssertRenameError(
        int status,
        uint dosError,
        bool replaceExisting,
        string expectedCode)
    {
        var native = new RecordingRenameNative
        {
            Status = status,
            DosError = dosError,
        };
        var platform = new WindowsRecoveryStoreFilePlatform(native);
        using SafeFileHandle fileHandle = Handle(0x1111);
        using SafeFileHandle directoryHandle = Handle(0x2222);

        Result result = platform.Rename(
            fileHandle,
            directoryHandle,
            "12345678-1234-4234-8234-123456789abc.fslr",
            replaceExisting);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Single(native.Calls);
        Assert.Equal(status, native.ConvertedStatus);
    }

    private static string ReadUtf8(
        WindowsRecoveryStoreFilePlatform platform,
        SafeFileHandle handle)
    {
        Result<byte[]> read = platform.ReadAll(handle, maximumLength: 16);
        Assert.True(read.IsSuccess, read.Error?.Code);
        return Encoding.UTF8.GetString(read.Value);
    }

    private static SafeFileHandle Handle(int value) => new(new nint(value), ownsHandle: false);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static uint Flags(RenameCall call) => BinaryPrimitives.ReadUInt32LittleEndian(
        call.Buffer.AsSpan(Offset(nameof(WindowsRecoveryStoreFilePlatform.FileRenameInformation.Flags))));

    private static nint RootDirectory(RenameCall call)
    {
        ReadOnlySpan<byte> bytes = call.Buffer.AsSpan(
            Offset(nameof(WindowsRecoveryStoreFilePlatform.FileRenameInformation.RootDirectory)),
            IntPtr.Size);
        return IntPtr.Size == sizeof(long)
            ? new nint(BinaryPrimitives.ReadInt64LittleEndian(bytes))
            : new nint(BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }

    private static uint FileNameLength(RenameCall call) => BinaryPrimitives.ReadUInt32LittleEndian(
        call.Buffer.AsSpan(Offset(
            nameof(WindowsRecoveryStoreFilePlatform.FileRenameInformation.FileNameLength))));

    private static string FileName(RenameCall call) => Encoding.Unicode.GetString(
        call.Buffer,
        Offset(nameof(WindowsRecoveryStoreFilePlatform.FileRenameInformation.FileName)),
        checked((int)FileNameLength(call)));

    private static int Offset(string fieldName) => checked((int)Marshal.OffsetOf<
        WindowsRecoveryStoreFilePlatform.FileRenameInformation>(fieldName));

    private sealed record RenameCall(int FileInformationClass, byte[] Buffer);

    private sealed class RecordingRenameNative : IRecoveryStoreRenameNative
    {
        internal int Status { get; init; }
        internal uint DosError { get; init; }
        internal int? ConvertedStatus { get; private set; }
        internal List<RenameCall> Calls { get; } = [];

        public int SetRenameInformation(
            SafeFileHandle fileHandle,
            nint fileInformation,
            uint length,
            int fileInformationClass)
        {
            var buffer = new byte[checked((int)length)];
            Marshal.Copy(fileInformation, buffer, 0, buffer.Length);
            Calls.Add(new RenameCall(fileInformationClass, buffer));
            return Status;
        }

        public uint NtStatusToDosError(int status)
        {
            ConvertedStatus = status;
            return DosError;
        }
    }
}
