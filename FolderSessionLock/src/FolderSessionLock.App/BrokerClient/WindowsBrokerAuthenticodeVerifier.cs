using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.BrokerClient;

internal interface IBrokerAuthenticodeVerifier
{
    Result Verify(string brokerPath);
}

internal interface IBrokerAuthenticodePlatform
{
    Result<string> VerifyAndGetSignerThumbprint(string brokerPath);
}

internal interface IAuthenticodeTrustSession : IDisposable
{
    bool IsTrusted { get; }

    Result<string> GetSignerThumbprint();
}

internal interface IAuthenticodeTrustSessionFactory
{
    IAuthenticodeTrustSession Open(string brokerPath);
}

internal sealed class WindowsBrokerAuthenticodeVerifier : IBrokerAuthenticodeVerifier
{
    internal const string MetadataName = "BrokerPublisherThumbprint";
    private static readonly string[] BrokerTrustSet =
    [
        "FolderSessionLock.Broker.exe",
        "FolderSessionLock.Broker.dll",
        "FolderSessionLock.Core.dll",
        "FolderSessionLock.Windows.dll",
    ];
    private readonly string? _publisherThumbprint;
    private readonly IBrokerAuthenticodePlatform _platform;

    internal WindowsBrokerAuthenticodeVerifier()
        : this(ReadPublisherThumbprint(), new WindowsBrokerAuthenticodePlatform())
    {
    }

    internal WindowsBrokerAuthenticodeVerifier(
        string? publisherThumbprint,
        IBrokerAuthenticodePlatform platform)
    {
        _publisherThumbprint = publisherThumbprint;
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Result Verify(string brokerPath)
    {
        if (string.IsNullOrWhiteSpace(brokerPath))
        {
            return Failure();
        }

        if (_publisherThumbprint is null || _publisherThumbprint.Length == 0)
        {
            return Result.Success();
        }

        if (!TryNormalizeThumbprint(_publisherThumbprint, out string? expected))
        {
            return Failure();
        }

        string? installationDirectory = Path.GetDirectoryName(brokerPath);
        if (string.IsNullOrWhiteSpace(installationDirectory))
        {
            return Failure();
        }

        foreach (string fileName in BrokerTrustSet)
        {
            Result<string> signer = _platform.VerifyAndGetSignerThumbprint(
                Path.Combine(installationDirectory, fileName));
            if (signer.IsFailure
                || !TryNormalizeThumbprint(signer.Value, out string? actual)
                || !string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return Failure();
            }
        }

        return Result.Success();
    }

    internal static bool TryNormalizeThumbprint(string? value, out string? normalized)
    {
        normalized = null;
        if (value is null || value.Length != 40)
        {
            return false;
        }

        Span<char> result = stackalloc char[40];
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }

            result[index] = char.ToUpperInvariant(character);
        }

        normalized = new string(result);
        return true;
    }

    internal static string? ReadPublisherThumbprint() =>
        typeof(WindowsBrokerAuthenticodeVerifier).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                string.Equals(attribute.Key, MetadataName, StringComparison.Ordinal))
            ?.Value;

    internal static Error PathUntrustedError() => new(
        BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
        "The elevated broker installation could not be verified.",
        ErrorCategory.UnrecoverableError);

    private static Result Failure() => Result.Failure(PathUntrustedError());
}

internal sealed class WindowsBrokerAuthenticodePlatform : IBrokerAuthenticodePlatform
{
    private readonly IAuthenticodeTrustSessionFactory _sessions;

    internal WindowsBrokerAuthenticodePlatform()
        : this(new WindowsAuthenticodeTrustSessionFactory())
    {
    }

    internal WindowsBrokerAuthenticodePlatform(IAuthenticodeTrustSessionFactory sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public Result<string> VerifyAndGetSignerThumbprint(string brokerPath)
    {
        try
        {
            using IAuthenticodeTrustSession session = _sessions.Open(brokerPath);
            return session.IsTrusted
                ? session.GetSignerThumbprint()
                : Failure();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or CryptographicException
                or ExternalException
                or IOException
                or OutOfMemoryException)
        {
            return Failure();
        }
    }

    private static Result<string> Failure() =>
        Result<string>.Failure(WindowsBrokerAuthenticodeVerifier.PathUntrustedError());
}

internal sealed class WindowsAuthenticodeTrustSessionFactory
    : IAuthenticodeTrustSessionFactory
{
    public IAuthenticodeTrustSession Open(string brokerPath) =>
        new WindowsAuthenticodeTrustSession(brokerPath);
}

internal sealed class WindowsAuthenticodeTrustSession : IAuthenticodeTrustSession
{
    private const uint DataChoiceFile = 1;
    private const uint UiChoiceNone = 2;
    private const uint RevocationChecksNone = 0;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint ProviderFlags =
        0x00000080 | // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
        0x00001000 | // WTD_CACHE_ONLY_URL_RETRIEVAL
        0x00002000;  // WTD_DISABLE_MD2_MD4
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private nint _path;
    private nint _fileInfoPointer;
    private WintrustData _trustData;
    private bool _disposed;

    internal WindowsAuthenticodeTrustSession(string brokerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerPath);
        _path = Marshal.StringToCoTaskMemUni(brokerPath);
        var fileInfo = new WintrustFileInfo
        {
            StructureSize = checked((uint)Marshal.SizeOf<WintrustFileInfo>()),
            FilePath = _path,
            FileHandle = nint.Zero,
            KnownSubject = nint.Zero,
        };
        _fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, _fileInfoPointer, false);
        _trustData = new WintrustData
        {
            StructureSize = checked((uint)Marshal.SizeOf<WintrustData>()),
            UiChoice = UiChoiceNone,
            RevocationChecks = RevocationChecksNone,
            UnionChoice = DataChoiceFile,
            FileInfo = _fileInfoPointer,
            StateAction = StateActionVerify,
            ProviderFlags = ProviderFlags,
        };
        try
        {
            IsTrusted = WinVerifyTrust(
                new nint(-1),
                in GenericVerifyV2,
                ref _trustData) == 0
                && _trustData.StateData != nint.Zero;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsTrusted { get; }

    public Result<string> GetSignerThumbprint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsTrusted)
        {
            return Failure();
        }

        nint providerData = WTHelperProvDataFromStateData(_trustData.StateData);
        nint signerPointer = providerData == nint.Zero
            ? nint.Zero
            : WTHelperGetProvSignerFromChain(
                providerData,
                0,
                false,
                0);
        if (signerPointer == nint.Zero)
        {
            return Failure();
        }

        CryptProviderSigner signer =
            Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
        if (signer.CertChainCount == 0 || signer.CertChain == nint.Zero)
        {
            return Failure();
        }

        CryptProviderCertificate certificate =
            Marshal.PtrToStructure<CryptProviderCertificate>(signer.CertChain);
        if (certificate.CertificateContext == nint.Zero)
        {
            return Failure();
        }

        using var signerCertificate = new X509Certificate2(certificate.CertificateContext);
        string? thumbprint = signerCertificate.Thumbprint;
        return string.IsNullOrWhiteSpace(thumbprint)
            ? Failure()
            : Result<string>.Success(thumbprint);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_trustData.StateData != nint.Zero)
            {
                _trustData.StateAction = StateActionClose;
                _ = WinVerifyTrust(
                    new nint(-1),
                    in GenericVerifyV2,
                    ref _trustData);
            }
        }
        finally
        {
            if (_fileInfoPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(_fileInfoPointer);
                _fileInfoPointer = nint.Zero;
            }

            if (_path != nint.Zero)
            {
                Marshal.FreeCoTaskMem(_path);
                _path = nint.Zero;
            }

            _disposed = true;
        }
    }

    private static Result<string> Failure() =>
        Result<string>.Failure(WindowsBrokerAuthenticodeVerifier.PathUntrustedError());

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        internal uint StructureSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        internal uint StructureSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructureSize;
        internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        internal uint CertChainCount;
        internal nint CertChain;
        internal uint SignerType;
        internal nint Signer;
        internal uint Error;
        internal uint CounterSignerCount;
        internal nint CounterSigners;
        internal nint ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        internal uint StructureSize;
        internal nint CertificateContext;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool Commercial;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool TrustedRoot;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool SelfSigned;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool TestCertificate;
        internal uint RevokedReason;
        internal uint Confidence;
        internal uint Error;
        internal nint TrustListContext;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool TrustListSignerCertificate;
        internal nint CtlContext;
        internal uint CtlError;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool IsCyclic;
        internal nint ChainElement;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        in Guid actionId,
        ref WintrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);
}
