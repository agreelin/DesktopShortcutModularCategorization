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

        /// <summary>
        /// Atomically writes bytes to a file and flushes the containing directory.
        /// </summary>
        /// <param name="path">The destination file path.</param>
        /// <param name="bytes">The bytes to write.</param>
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

        /// <summary>
        /// Appends a line and the platform-specific newline sequence to a file.
        /// </summary>
        /// <param name="path">The path of the file to append.</param>
        /// <param name="line">The bytes to append before the newline sequence.</param>
        /// <returns>The resulting length of the file in bytes.</returns>
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

        /// <summary>
        /// Sets the length of an existing file and flushes the change to storage.
        /// </summary>
        /// <param name="path">The path of the file to truncate or extend.</param>
        /// <param name="length">The new file length in bytes.</param>
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

        /// <summary>
        /// Generates cryptographically strong random bytes.
        /// </summary>
        /// <param name="count">The number of bytes to generate.</param>
        /// <returns>An array containing the generated random bytes.</returns>
        public static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }
            return bytes;
        }

        /// <summary>
        /// Retrieves information about the system's TPM device.
        /// </summary>
        /// <returns>The TPM device information returned by the system.</returns>
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

        /// <summary>
        /// Protects data for use by the current Windows user.
        /// </summary>
        /// <param name="bytes">The data to protect.</param>
        /// <param name="entropy">Optional additional entropy used during protection.</param>
        /// <returns>The protected data.</returns>
        public static byte[] ProtectCurrentUser(byte[] bytes, byte[] entropy)
        {
            return ProtectedData.Protect(
                bytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }

        /// <summary>
        /// Decrypts data protected for the current user.
        /// </summary>
        /// <param name="bytes">The protected data.</param>
        /// <param name="entropy">Optional additional entropy used during protection.</param>
        /// <returns>The decrypted data.</returns>
        public static byte[] UnprotectCurrentUser(byte[] bytes, byte[] entropy)
        {
            return ProtectedData.Unprotect(
                bytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }

        /// <summary>
        /// Computes an HMAC-SHA256 digest for the specified data.
        /// </summary>
        /// <param name="key">The secret key used to compute the digest.</param>
        /// <param name="bytes">The data to authenticate.</param>
        /// <returns>The digest encoded as an uppercase hexadecimal string.</returns>
        public static string HmacSha256(byte[] key, byte[] bytes)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                return Hex(hmac.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// Compares two hexadecimal strings using a full character-by-character scan.
        /// </summary>
        /// <param name="left">The first hexadecimal string.</param>
        /// <param name="right">The second hexadecimal string.</param>
        /// <returns><see langword="true"/> if both strings are identical, <see langword="false"/> otherwise.</returns>
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

        /// <summary>
        /// Computes the SHA-256 hash of the specified bytes.
        /// </summary>
        /// <param name="bytes">The bytes to hash.</param>
        /// <returns>The hash encoded as an uppercase hexadecimal string.</returns>
        public static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// Describes the identity and reparse-point status of a file system path.
        /// </summary>
        /// <param name="path">The file or directory path to inspect.</param>
        /// <param name="directory">Whether the path identifies a directory.</param>
        /// <returns>The requested path, normalized final path, stable file identity, and reparse-point status.</returns>
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

        /// <summary>
        /// Applies an owner and protected DACL security descriptor to a directory while verifying its identity remains stable.
        /// </summary>
        /// <param name="path">The directory path whose security is updated.</param>
        /// <param name="securityDescriptor">The security descriptor containing the owner and DACL to apply.</param>
        /// <returns>The directory identity after the security update.</returns>
        /// <exception cref="Win32Exception">Thrown when the directory cannot be opened, the security descriptor cannot be read, or the security update fails.</exception>
        /// <exception cref="IOException">Thrown when the directory identity changes during the security update or the directory is a reparse point.</exception>
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

        /// <summary>
        /// Renames a file without replacing an existing destination and flushes the destination directory.
        /// </summary>
        /// <param name="source">The path of the file to rename.</param>
        /// <param name="destination">The destination path.</param>
        /// <exception cref="Win32Exception">Thrown when the rename operation fails.</exception>
        public static void RenameNoReplace(string source, string destination)
        {
            if (!MoveFileEx(source, destination, MoveFileWriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
        }

        /// <summary>
        /// Describes the identity and final path of an open file handle.
        /// </summary>
        /// <param name="handle">The open file handle to inspect.</param>
        /// <param name="requestedPath">The path originally used to request the handle.</param>
        /// <returns>The file's normalized paths, stable identity, and reparse-point status.</returns>
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

        /// <summary>
        /// Verifies a file's Authenticode signature and identifies its signing certificate.
        /// </summary>
        /// <param name="path">The path of the file to verify.</param>
        /// <returns>The signing certificate's thumbprint and SubjectPublicKeyInfo SHA-256 hash.</returns>
        /// <exception cref="CryptographicException">Thrown if Authenticode validation or signer certificate extraction fails.</exception>
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

        /// <summary>
        /// Encodes a certificate's public key as a DER-encoded SubjectPublicKeyInfo structure.
        /// </summary>
        /// <param name="certificate">The certificate whose public key is encoded.</param>
        /// <returns>The DER-encoded SubjectPublicKeyInfo bytes.</returns>
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

        /// <summary>
        /// Encodes a dotted object identifier as an ASN.1 DER object identifier.
        /// </summary>
        /// <param name="value">The dotted decimal object identifier.</param>
        /// <returns>The DER-encoded object identifier.</returns>
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

        /// <summary>
        /// Creates an ASN.1 SEQUENCE containing the specified DER-encoded values.
        /// </summary>
        /// <param name="values">The DER-encoded values to include in the sequence.</param>
        /// <returns>The DER-encoded ASN.1 SEQUENCE.</returns>
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

        /// <summary>
        /// Encodes a value using ASN.1 DER tag-length-value format.
        /// </summary>
        /// <param name="tag">The ASN.1 tag to encode.</param>
        /// <param name="value">The value to encode.</param>
        /// <returns>The DER-encoded tag, length, and value.</returns>
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

        /// <summary>
        /// Converts bytes to an uppercase hexadecimal string without separators.
        /// </summary>
        /// <param name="bytes">The bytes to convert.</param>
        /// <returns>The uppercase hexadecimal representation of the bytes.</returns>
        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        public sealed class FileIdentity
        {
            /// <summary>
            /// Initializes a file identity with its requested path, resolved path, stable identifier, and reparse status.
            /// </summary>
            /// <param name="requestedPath">The path used to access the file.</param>
            /// <param name="finalPath">The resolved final path of the file.</param>
            /// <param name="identity">The stable file identity.</param>
            /// <param name="isReparse">Indicates whether the file is a reparse point.</param>
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
            /// <summary>
            /// Initializes an Authenticode identity with its certificate thumbprint and public key hash.
            /// </summary>
            /// <param name="thumbprint">The certificate thumbprint.</param>
            /// <param name="spkiSha256">The SHA-256 hash of the certificate's SubjectPublicKeyInfo.</param>
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
            /// <summary>
            /// Initializes TPM device information with the values returned by the TPM interface.
            /// </summary>
            /// <param name="result">The result code from the TPM device information query.</param>
            /// <param name="structVersion">The version of the device information structure.</param>
            /// <param name="tpmVersion">The TPM version.</param>
            /// <param name="tpmInterfaceType">The TPM interface type.</param>
            /// <param name="tpmImpRevision">The TPM implementation revision.</param>
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

        /// <summary>
            /// Retrieves file attributes and identity information for an open file handle.
            /// </summary>
            /// <param name="file">The open file handle to inspect.</param>
            /// <param name="information">Receives the file attributes and identity information.</param>
            /// <returns><c>true</c> if the information is retrieved; otherwise, <c>false</c>.</returns>
            [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        /// <summary>
            /// Retrieves the final normalized path associated with an open file handle.
            /// </summary>
            /// <param name="file">The open file handle.</param>
            /// <param name="path">The buffer that receives the path.</param>
            /// <param name="pathLength">The capacity of <paramref name="path"/>.</param>
            /// <param name="flags">Flags controlling the returned path format.</param>
            /// <returns>The path length, or zero if the operation fails.</returns>
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            int pathLength,
            uint flags);

        /// <summary>
        /// Flushes buffered data associated with a file handle to the device.
        /// </summary>
        /// <param name="file">The file handle whose buffers should be flushed.</param>
        /// <returns><c>true</c> if the buffers were flushed successfully; <c>false</c> otherwise.</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);

        /// <summary>
            /// Moves or renames a file or directory using the specified options.
            /// </summary>
            /// <param name="existingFileName">The path of the file or directory to move.</param>
            /// <param name="newFileName">The destination path.</param>
            /// <param name="flags">Flags that control the move operation.</param>
            /// <returns><c>true</c> if the move succeeds; <c>false</c> otherwise.</returns>
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            uint flags);

        /// <summary>
            /// Retrieves information about the system's TPM device.
            /// </summary>
            /// <param name="size">The size, in bytes, of the device information structure.</param>
            /// <param name="information">Receives the TPM device information.</param>
            /// <returns>A TBS result code indicating whether the operation succeeded.</returns>
            [DllImport(
            "tbs.dll",
            EntryPoint = "Tbsi_GetDeviceInfo",
            ExactSpelling = true)]
        private static extern uint TbsiGetDeviceInfo(
            uint size,
            ref TbsDeviceInfo information);

        /// <summary>
            /// Retrieves the owner information from a security descriptor.
            /// </summary>
            /// <param name="securityDescriptor">A pointer to the security descriptor.</param>
            /// <param name="owner">Receives a pointer to the owner security identifier.</param>
            /// <param name="ownerDefaulted">Receives whether the owner information is defaulted.</param>
            /// <returns><c>true</c> if the owner information is retrieved successfully; otherwise, <c>false</c>.</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorOwner(
            IntPtr securityDescriptor,
            out IntPtr owner,
            [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

        /// <summary>
            /// Retrieves the discretionary access control list (DACL) information from a security descriptor.
            /// </summary>
            /// <param name="securityDescriptor">A pointer to the security descriptor.</param>
            /// <param name="daclPresent">Receives whether the descriptor contains a DACL.</param>
            /// <param name="dacl">Receives a pointer to the DACL.</param>
            /// <param name="daclDefaulted">Receives whether the DACL was provided by a default mechanism.</param>
            /// <returns><c>true</c> if the operation succeeds, <c>false</c> otherwise.</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(
            IntPtr securityDescriptor,
            [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
            out IntPtr dacl,
            [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

        /// <summary>
            /// Applies the specified security information to an object represented by a handle.
            /// </summary>
            /// <param name="handle">The handle to the object.</param>
            /// <param name="objectType">The type of object represented by the handle.</param>
            /// <param name="securityInformation">The security information to update.</param>
            /// <param name="owner">A pointer to the owner security identifier.</param>
            /// <param name="group">A pointer to the group security identifier.</param>
            /// <param name="dacl">A pointer to the discretionary access control list.</param>
            /// <param name="sacl">A pointer to the system access control list.</param>
            /// <returns>Zero if the operation succeeds; otherwise, a Win32 error code.</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            SafeFileHandle handle,
            uint objectType,
            uint securityInformation,
            IntPtr owner,
            IntPtr group,
            IntPtr dacl,
            IntPtr sacl);

        /// <summary>
            /// Performs trust verification for the specified subject and verification action.
            /// </summary>
            /// <param name="windowHandle">The window handle used for any trust-provider user interface.</param>
            /// <param name="actionId">The identifier of the verification action to perform.</param>
            /// <param name="trustData">The trust-verification data and state.</param>
            /// <returns>Zero if verification succeeds; otherwise, a trust-provider status code.</returns>
            [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WintrustData trustData);

        /// <summary>
        /// Retrieves provider data from WinTrust verification state data.
        /// </summary>
        /// <param name="stateData">A pointer to WinTrust state data.</param>
        /// <returns>A pointer to the corresponding provider data.</returns>
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        /// <summary>
            /// Retrieves signer information from a Wintrust provider data structure.
            /// </summary>
            /// <param name="providerData">The provider data handle.</param>
            /// <param name="signerIndex">The signer index to retrieve.</param>
            /// <param name="counterSigner">Whether to retrieve a counter-signature.</param>
            /// <param name="counterSignerIndex">The counter-signature index to retrieve.</param>
            /// <returns>A pointer to the requested signer information, or <see cref="IntPtr.Zero"/> if unavailable.</returns>
            [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(
            IntPtr providerData,
            uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
            uint counterSignerIndex);
    }
}
