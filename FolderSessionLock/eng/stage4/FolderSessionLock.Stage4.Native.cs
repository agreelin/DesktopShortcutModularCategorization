using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Stage4
{
    public static class Native
    {
        private const uint GenericRead = 0x80000000;
        private const uint ReadControl = 0x00020000;
        private const uint WriteDac = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;
        private const uint FileShareDelete = 4;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint MoveFileWriteThrough = 0x00000008;
        private const uint SeFileObject = 1;
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const uint WtdChoiceFile = 1;
        private const uint WtdUiNone = 2;
        private const uint WtdStateVerify = 1;
        private const uint WtdStateClose = 2;
        private static readonly Guid GenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static void AtomicWrite(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            string backup = temporary + ".backup";
            try
            {
                using (FileStream stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, backup, true);
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }
                FlushDirectory(directory);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
        }

        public static long AppendLine(string path, byte[] line)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            byte[] newline = Encoding.UTF8.GetBytes(Environment.NewLine);
            using (FileStream stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(line, 0, line.Length);
                stream.Write(newline, 0, newline.Length);
                stream.Flush(true);
                return stream.Length;
            }
        }

        public static void Truncate(string path, long length)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.SetLength(length);
                stream.Flush(true);
            }
        }

        public static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }
            return bytes;
        }

        public static TpmDeviceInfo GetTpmDeviceInfo()
        {
            TbsDeviceInfo native = new TbsDeviceInfo();
            uint result = TbsiGetDeviceInfo(
                (uint)Marshal.SizeOf(typeof(TbsDeviceInfo)),
                ref native);
            return new TpmDeviceInfo(
                result,
                native.StructVersion,
                native.TpmVersion,
                native.TpmInterfaceType,
                native.TpmImpRevision);
        }

        public static byte[] ProtectCurrentUser(byte[] bytes, byte[] entropy)
        {
            return ProtectedData.Protect(
                bytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }

        public static byte[] UnprotectCurrentUser(byte[] bytes, byte[] entropy)
        {
            return ProtectedData.Unprotect(
                bytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }

        public static string HmacSha256(byte[] key, byte[] bytes)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                return Hex(hmac.ComputeHash(bytes));
            }
        }

        public static bool FixedTimeEqualsHex(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(bytes));
            }
        }

        public static FileIdentity DescribeFile(string path, bool directory)
        {
            uint flags = FileFlagOpenReparsePoint;
            if (directory)
            {
                flags |= FileFlagBackupSemantics;
            }
            using (SafeFileHandle handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return DescribeHandle(handle, path);
            }
        }

        public static FileIdentity SetDirectorySecurity(
            string path,
            byte[] securityDescriptor)
        {
            using (SafeFileHandle handle = CreateFile(
                path,
                GenericRead | ReadControl | WriteDac | WriteOwner,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                FileIdentity before = DescribeHandle(handle, path);
                GCHandle pinned = GCHandle.Alloc(
                    securityDescriptor,
                    GCHandleType.Pinned);
                try
                {
                    IntPtr descriptor = pinned.AddrOfPinnedObject();
                    IntPtr owner;
                    bool ownerDefaulted;
                    IntPtr dacl;
                    bool daclPresent;
                    bool daclDefaulted;
                    if (!GetSecurityDescriptorOwner(
                        descriptor,
                        out owner,
                        out ownerDefaulted) ||
                        !GetSecurityDescriptorDacl(
                            descriptor,
                            out daclPresent,
                            out dacl,
                            out daclDefaulted) ||
                        !daclPresent)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    uint result = SetSecurityInfo(
                        handle,
                        SeFileObject,
                        OwnerSecurityInformation |
                            DaclSecurityInformation |
                            ProtectedDaclSecurityInformation,
                        owner,
                        IntPtr.Zero,
                        dacl,
                        IntPtr.Zero);
                    if (result != 0)
                    {
                        throw new Win32Exception((int)result);
                    }
                    FileIdentity after = DescribeHandle(handle, path);
                    if (!String.Equals(
                        before.FinalPath,
                        after.FinalPath,
                        StringComparison.Ordinal) ||
                        !String.Equals(
                            before.Identity,
                            after.Identity,
                            StringComparison.Ordinal) ||
                        before.IsReparse ||
                        after.IsReparse)
                    {
                        throw new IOException(
                            "Directory identity changed during ACL mutation.");
                    }
                    return after;
                }
                finally
                {
                    pinned.Free();
                }
            }
        }

        public static void RenameNoReplace(string source, string destination)
        {
            if (!MoveFileEx(source, destination, MoveFileWriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
        }

        private static FileIdentity DescribeHandle(
            SafeFileHandle handle,
            string requestedPath)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            StringBuilder finalPath = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(
                handle,
                finalPath,
                finalPath.Capacity,
                0);
            if (length == 0 || length >= finalPath.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            string normalized = finalPath.ToString();
            const string prefix = @"\\?\";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(prefix.Length);
            }
            ulong fileIndex =
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow;
            return new FileIdentity(
                Path.GetFullPath(requestedPath).TrimEnd('\\'),
                normalized.TrimEnd('\\'),
                information.VolumeSerialNumber.ToString("X8") +
                    fileIndex.ToString("X16"),
                (information.FileAttributes & 0x400) != 0);
        }

        public static AuthenticodeIdentity VerifyAuthenticode(string path)
        {
            IntPtr pathPointer = IntPtr.Zero;
            IntPtr filePointer = IntPtr.Zero;
            WintrustData data = new WintrustData();
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(path);
                WintrustFileInfo file = new WintrustFileInfo();
                file.StructureSize = (uint)Marshal.SizeOf(typeof(WintrustFileInfo));
                file.FilePath = pathPointer;
                filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WintrustFileInfo)));
                Marshal.StructureToPtr(file, filePointer, false);
                data.StructureSize = (uint)Marshal.SizeOf(typeof(WintrustData));
                data.UiChoice = WtdUiNone;
                data.UnionChoice = WtdChoiceFile;
                data.FileInfo = filePointer;
                data.StateAction = WtdStateVerify;
                data.ProviderFlags = 0x00000080 | 0x00001000 | 0x00002000;
                Guid action = GenericVerifyV2;
                int result = WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref data);
                if (result != 0 || data.StateData == IntPtr.Zero)
                {
                    throw new CryptographicException("Authenticode validation failed.");
                }
                IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
                IntPtr signerPointer = provider == IntPtr.Zero
                    ? IntPtr.Zero
                    : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signerPointer == IntPtr.Zero)
                {
                    throw new CryptographicException("Authenticode signer is missing.");
                }
                CryptProviderSigner signer =
                    (CryptProviderSigner)Marshal.PtrToStructure(
                        signerPointer,
                        typeof(CryptProviderSigner));
                if (signer.CertChainCount == 0 || signer.CertChain == IntPtr.Zero)
                {
                    throw new CryptographicException("Authenticode chain is missing.");
                }
                CryptProviderCertificate providerCertificate =
                    (CryptProviderCertificate)Marshal.PtrToStructure(
                        signer.CertChain,
                        typeof(CryptProviderCertificate));
                using (X509Certificate2 certificate =
                    new X509Certificate2(providerCertificate.CertificateContext))
                using (SHA256 hash = SHA256.Create())
                {
                    return new AuthenticodeIdentity(
                        certificate.Thumbprint,
                        Hex(hash.ComputeHash(
                            EncodeSubjectPublicKeyInfo(certificate))));
                }
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = WtdStateClose;
                    Guid action = GenericVerifyV2;
                    WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
                if (filePointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(filePointer);
                }
                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
        }

        private static byte[] EncodeSubjectPublicKeyInfo(X509Certificate2 certificate)
        {
            byte[] algorithm = DerSequence(
                DerOid(certificate.PublicKey.Oid.Value),
                certificate.PublicKey.EncodedParameters.RawData);
            byte[] bitString = new byte[
                certificate.PublicKey.EncodedKeyValue.RawData.Length + 1];
            Buffer.BlockCopy(
                certificate.PublicKey.EncodedKeyValue.RawData,
                0,
                bitString,
                1,
                certificate.PublicKey.EncodedKeyValue.RawData.Length);
            return DerSequence(algorithm, Der(0x03, bitString));
        }

        private static byte[] DerOid(string value)
        {
            string[] parts = value.Split('.');
            using (MemoryStream stream = new MemoryStream())
            {
                stream.WriteByte((byte)(
                    int.Parse(parts[0]) * 40 + int.Parse(parts[1])));
                for (int index = 2; index < parts.Length; index++)
                {
                    ulong number = ulong.Parse(parts[index]);
                    byte[] encoded = new byte[10];
                    int offset = encoded.Length;
                    encoded[--offset] = (byte)(number & 0x7F);
                    while ((number >>= 7) != 0)
                    {
                        encoded[--offset] = (byte)((number & 0x7F) | 0x80);
                    }
                    stream.Write(encoded, offset, encoded.Length - offset);
                }
                return Der(0x06, stream.ToArray());
            }
        }

        private static byte[] DerSequence(params byte[][] values)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                foreach (byte[] value in values)
                {
                    stream.Write(value, 0, value.Length);
                }
                return Der(0x30, stream.ToArray());
            }
        }

        private static byte[] Der(byte tag, byte[] value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.WriteByte(tag);
                if (value.Length < 128)
                {
                    stream.WriteByte((byte)value.Length);
                }
                else
                {
                    byte[] length = BitConverter.GetBytes(value.Length);
                    Array.Reverse(length);
                    int first = 0;
                    while (first < length.Length && length[first] == 0)
                    {
                        first++;
                    }
                    stream.WriteByte((byte)(0x80 | (length.Length - first)));
                    stream.Write(length, first, length.Length - first);
                }
                stream.Write(value, 0, value.Length);
                return stream.ToArray();
            }
        }

        private static void FlushDirectory(string path)
        {
            using (SafeFileHandle handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    FlushFileBuffers(handle);
                }
            }
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        public sealed class FileIdentity
        {
            public FileIdentity(
                string requestedPath,
                string finalPath,
                string identity,
                bool isReparse)
            {
                RequestedPath = requestedPath;
                FinalPath = finalPath;
                Identity = identity;
                IsReparse = isReparse;
            }
            public string RequestedPath { get; private set; }
            public string FinalPath { get; private set; }
            public string Identity { get; private set; }
            public bool IsReparse { get; private set; }
        }

        public sealed class AuthenticodeIdentity
        {
            public AuthenticodeIdentity(string thumbprint, string spkiSha256)
            {
                Thumbprint = thumbprint;
                SpkiSha256 = spkiSha256;
            }
            public string Thumbprint { get; private set; }
            public string SpkiSha256 { get; private set; }
        }

        public sealed class TpmDeviceInfo
        {
            public TpmDeviceInfo(
                uint result,
                uint structVersion,
                uint tpmVersion,
                uint tpmInterfaceType,
                uint tpmImpRevision)
            {
                Result = result;
                StructVersion = structVersion;
                TpmVersion = tpmVersion;
                TpmInterfaceType = tpmInterfaceType;
                TpmImpRevision = tpmImpRevision;
            }

            public uint Result { get; private set; }
            public uint StructVersion { get; private set; }
            public uint TpmVersion { get; private set; }
            public uint TpmInterfaceType { get; private set; }
            public uint TpmImpRevision { get; private set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TbsDeviceInfo
        {
            internal uint StructVersion;
            internal uint TpmVersion;
            internal uint TpmInterfaceType;
            internal uint TpmImpRevision;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            internal uint StructureSize;
            internal IntPtr FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            internal uint StructureSize;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr FileInfo;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
            internal IntPtr SignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderSigner
        {
            internal uint StructureSize;
            internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
            internal uint CertChainCount;
            internal IntPtr CertChain;
            internal uint SignerType;
            internal IntPtr Signer;
            internal uint Error;
            internal uint CounterSignerCount;
            internal IntPtr CounterSigners;
            internal IntPtr ChainContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderCertificate
        {
            internal uint StructureSize;
            internal IntPtr CertificateContext;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            int pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            uint flags);

        [DllImport(
            "tbs.dll",
            EntryPoint = "Tbsi_GetDeviceInfo",
            ExactSpelling = true)]
        private static extern uint TbsiGetDeviceInfo(
            uint size,
            ref TbsDeviceInfo information);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorOwner(
            IntPtr securityDescriptor,
            out IntPtr owner,
            [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(
            IntPtr securityDescriptor,
            [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
            out IntPtr dacl,
            [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            SafeFileHandle handle,
            uint objectType,
            uint securityInformation,
            IntPtr owner,
            IntPtr group,
            IntPtr dacl,
            IntPtr sacl);

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WintrustData trustData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(
            IntPtr providerData,
            uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
            uint counterSignerIndex);
    }
}
