Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FlbModelNames = @(
    'schemaVersion',
    'authorityProfile',
    'contractId',
    'checkpoint',
    'attemptId',
    'runId',
    'rootBinding',
    'recoveryAuthority')
$script:FlbRootBindingNames = @(
    'fixtureId',
    'sourceLeafName',
    'bundleLeafName')
$script:FlbRecoveryAuthorityNames = @(
    'contractId',
    'contractSha256')
$script:FlbBundleNames = @(
    'outer-launcher.ps1',
    'launch-observer.ps1',
    'launch-observer-contract.json')
$script:FlbSourceNames = @(
    'elevated-reconcile.ps1',
    'recovery-contract.json')
$script:FlbProfiles = @('Formal', 'TestFixture')
$script:FlbZeros = '0' * 64
$script:FlbSelfHashRule =
    'SHA256(canonical UTF-8 bytes after replacing only bindingManifest.contractCanonicalSha256 with 64 ASCII zeroes)'
$script:FlbRunIdPattern = '^\d{8}T\d{6}Z-[0-9a-f]{8}$'
$script:FlbShaPattern = '^[0-9A-F]{64}$'
$script:FlbGitPattern = '^[0-9a-f]{40}$'
$script:FlbSidPattern = '^S-\d(?:-\d+)+$'
$script:FlbResultFieldOrder = @(
    'schemaVersion',
    'recordOrdinal',
    'attemptId',
    'runId',
    'checkpoint',
    'wrapperSha256',
    'recoveryContractSha256',
    'phase',
    'status',
    'outcome',
    'observerPid',
    'targetPid',
    'exitCode',
    'gateId',
    'timestampUtc')
$script:FlbAllowedWrites = @(
    'Create exactly the fixed launch-attempt.jsonl latch using CreateNew and WriteThrough.',
    'Write and Flush(true) LaunchCommitted before any RunAs activity.',
    'Append, Flush(true), and verify RunAsInvoking immediately before the unique RunAs call.',
    'Append, Flush(true), and verify exactly one terminal LaunchResult after RunAs returns or throws.')
$script:FlbForbiddenActions = @(
    'No launch when formalExecutionEligible is false.',
    'No alternate target, fallback launcher, second RunAs call, retry, or relaunch.',
    'No runtime arguments or caller-supplied executable, path, command, policy, ACL, or metadata.',
    'No retry when any object exists at the fixed latch path.',
    'No repository, evidence, anchor, release, recovery, Program Files, ProgramData, service, registry, certificate, ACL, account, VMware, restart, or logout mutation by the launcher.',
    'No stdout or stderr redirection for the recovery wrapper.',
    'No dynamic command, Invoke-Expression, controller, product executable, Git process, or shell fallback.')
$script:FlbExitCodes = [ordered]@{
    Success = 0
    ArgumentGate = 64
    LatchExists = 65
    EnvironmentGate = 66
    ContractGate = 67
    ObserverRootGate = 68
    ObserverBindingGate = 69
    SourceRecoveryGate = 70
    RepositoryGate = 71
    CanonicalGate = 72
    ExternalAnchorGate = 73
    ReleaseGate = 74
    SystemStateGate = 75
    LatchCommitFailure = 76
    LaunchFailure = 77
    LaunchResultFailure = 78
    RecoveryNonZero = 79
    UnexpectedFailure = 80
    PreAppendTemporalGate = 81
}

function Test-FslFlbLatchRecordShape {
    param([AllowNull()][psobject]$Record)
    if ($null -eq $Record) { return $false }
    $names = @($Record.PSObject.Properties | ForEach-Object Name)
    $expected = @(
        'schemaVersion','recordOrdinal','attemptId','runId','checkpoint',
        'wrapperSha256','recoveryContractSha256','phase','status','outcome',
        'observerPid','targetPid','exitCode','gateId','timestampUtc')
    if (($names -join '|') -cne ($expected -join '|') -or
        $Record.schemaVersion -isnot [int] -or
        $Record.recordOrdinal -isnot [int] -or
        $Record.attemptId -isnot [string] -or
        $Record.runId -isnot [string] -or
        $Record.checkpoint -isnot [string] -or
        $Record.wrapperSha256 -isnot [string] -or
        $Record.recoveryContractSha256 -isnot [string] -or
        $Record.phase -isnot [string] -or
        $Record.status -isnot [string] -or
        ($null -ne $Record.outcome -and $Record.outcome -isnot [string]) -or
        $Record.observerPid -isnot [int] -or
        ($null -ne $Record.targetPid -and $Record.targetPid -isnot [int]) -or
        ($null -ne $Record.exitCode -and $Record.exitCode -isnot [int]) -or
        ($null -ne $Record.gateId -and $Record.gateId -isnot [string]) -or
        $Record.timestampUtc -isnot [string]) {
        return $false
    }
    return (
        [int]$Record.schemaVersion -eq 1 -and
        [int]$Record.recordOrdinal -ge 1 -and
        [int]$Record.recordOrdinal -le 3 -and
        [int]$Record.observerPid -gt 0 -and
        [string]$Record.wrapperSha256 -cmatch '^[0-9A-F]{64}$' -and
        [string]$Record.recoveryContractSha256 -cmatch '^[0-9A-F]{64}$')
}

function ConvertTo-FslFlbLatchCanonicalLine {
    param([AllowNull()][psobject]$Record)
    if (-not (Test-FslFlbLatchRecordShape $Record)) { return $null }
    return ($Record | ConvertTo-Json -Compress -Depth 4)
}

function Test-FslFlbLatchBytes {
    param([AllowNull()][byte[]]$Bytes, [AllowNull()][object[]]$ExpectedRecords)
    try {
        $records = @($ExpectedRecords)
        if ($null -eq $Bytes -or $records.Count -lt 1 -or
            $records.Count -gt 3 -or $Bytes.Length -eq 0 -or
            $Bytes[$Bytes.Length - 1] -ne 0x0A -or $Bytes -contains 0x0D -or
            ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and
                $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF)) {
            return $false
        }
        $lines = [Collections.Generic.List[string]]::new()
        foreach ($record in $records) {
            $line = ConvertTo-FslFlbLatchCanonicalLine $record
            if ($null -eq $line) { return $false }
            [void]$lines.Add($line)
        }
        $canonicalText = ($lines -join "`n") + "`n"
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $canonicalBytes = $encoding.GetBytes($canonicalText)
        if ($canonicalBytes.Length -ne $Bytes.Length) { return $false }
        for ($byteIndex = 0; $byteIndex -lt $Bytes.Length; $byteIndex++) {
            if ($Bytes[$byteIndex] -ne $canonicalBytes[$byteIndex]) {
                return $false
            }
        }
        $actualText = $encoding.GetString($Bytes)
        $actualLines = @($actualText.Split([char]0x0A))
        if ($actualLines.Count -ne $records.Count + 1 -or
            $actualLines[$actualLines.Count - 1] -cne '') {
            return $false
        }
        $parsed = @()
        for ($recordIndex = 0; $recordIndex -lt $records.Count; $recordIndex++) {
            if ([string]::IsNullOrEmpty($actualLines[$recordIndex])) {
                return $false
            }
            $value = $actualLines[$recordIndex] | ConvertFrom-Json
            if (-not (Test-FslFlbLatchRecordShape $value)) { return $false }
            $parsed += $value
        }
        $first = $parsed[0]
        $previousTime = [DateTimeOffset]::MinValue
        $format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
        $culture = [Globalization.CultureInfo]::InvariantCulture
        $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
            [Globalization.DateTimeStyles]::AdjustToUniversal
        for ($recordIndex = 0; $recordIndex -lt $parsed.Count; $recordIndex++) {
            $record = $parsed[$recordIndex]
            if ([int]$record.recordOrdinal -ne $recordIndex + 1 -or
                [string]$record.attemptId -cne [string]$first.attemptId -or
                [string]$record.runId -cne [string]$first.runId -or
                [string]$record.checkpoint -cne [string]$first.checkpoint -or
                [string]$record.wrapperSha256 -cne
                    [string]$first.wrapperSha256 -or
                [string]$record.recoveryContractSha256 -cne
                    [string]$first.recoveryContractSha256 -or
                [int]$record.observerPid -ne [int]$first.observerPid) {
                return $false
            }
            $time = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParseExact(
                [string]$record.timestampUtc,
                $format,
                $culture,
                $styles,
                [ref]$time) -or $time -lt $previousTime) {
                return $false
            }
            $previousTime = $time
            if ($recordIndex -eq 0 -and (
                [string]$record.phase -cne 'LaunchCommitted' -or
                [string]$record.status -cne 'Pending')) {
                return $false
            }
            if ($recordIndex -eq 1 -and (
                [string]$record.phase -cne 'RunAsInvoking' -or
                [string]$record.status -cne 'Pending')) {
                return $false
            }
            if ($recordIndex -lt 2 -and (
                $null -ne $record.outcome -or $null -ne $record.targetPid -or
                $null -ne $record.exitCode -or $null -ne $record.gateId)) {
                return $false
            }
        }
        if ($parsed.Count -eq 3) {
            $terminal = $parsed[2]
            if ([string]$terminal.phase -cne 'LaunchResult' -or
                [string]$terminal.status -cne 'Completed' -or
                [string]$terminal.outcome -cnotin @(
                    'Exited','UacCancelled','LaunchFailed')) {
                return $false
            }
            if ([string]$terminal.outcome -ceq 'Exited') {
                if ($terminal.targetPid -isnot [int] -or
                    [int]$terminal.targetPid -le 0 -or
                    $terminal.exitCode -isnot [int] -or
                    ([int]$terminal.exitCode -eq 0 -and
                        $null -ne $terminal.gateId) -or
                    ([int]$terminal.exitCode -ge 84 -and
                        [int]$terminal.exitCode -le 139 -and (
                            $terminal.gateId -isnot [string] -or
                            -not ([string]$terminal.gateId).StartsWith(
                                ('FSL-RAB-CG-{0:D3}-' -f
                                    ([int]$terminal.exitCode - 83)),
                                [StringComparison]::Ordinal))) -or
                    (([int]$terminal.exitCode -lt 84 -or
                        [int]$terminal.exitCode -gt 139) -and
                        [int]$terminal.exitCode -ne 0 -and
                        $null -ne $terminal.gateId)) {
                    return $false
                }
            }
            elseif ($null -ne $terminal.targetPid -or
                $null -ne $terminal.exitCode -or
                $null -ne $terminal.gateId) {
                return $false
            }
        }
        return $true
    }
    catch { return $false }
}

# The rendered observer is assembled from these module function bodies, so
# generator tests and observer execution have one latch implementation source.
$script:FlbLatchHelperTemplate = @(
    'function Test-FslFlbLatchRecordShape {'
    (Get-Command Test-FslFlbLatchRecordShape).Definition.TrimEnd()
    '}'
    ''
    'function ConvertTo-FslFlbLatchCanonicalLine {'
    (Get-Command ConvertTo-FslFlbLatchCanonicalLine).Definition.TrimEnd()
    '}'
    ''
    'function Test-FslFlbLatchBytes {'
    (Get-Command Test-FslFlbLatchBytes).Definition.TrimEnd()
    '}'
) -join "`n"

if (-not ('FolderSessionLock.Stage4.FormalLauncherNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Stage4
{
    public sealed class FormalTokenProof
    {
        public string MachineName { get; set; }
        public int ElevationType { get; set; }
        public string CurrentAccountSid { get; set; }
        public string LinkedAccountSid { get; set; }
        public int CurrentSidType { get; set; }
        public int LinkedSidType { get; set; }
        public bool CurrentAdministratorsDenyOnly { get; set; }
        public bool CurrentAdministratorsEnabled { get; set; }
        public bool LinkedAdministratorsDenyOnly { get; set; }
        public bool LinkedAdministratorsEnabled { get; set; }
        public string CurrentAccountDomain { get; set; }
        public string LinkedAccountDomain { get; set; }
    }

    public sealed class FormalLauncherNative
    {
        private const uint ReadAttributes = 0x80;
        private const uint ShareRead = 1;
        private const uint ShareWrite = 2;
        private const uint ShareDelete = 4;
        private const uint OpenExisting = 3;
        private const uint OpenReparse = 0x00200000;
        private const uint BackupSemantics = 0x02000000;

        private FormalLauncherNative(
            string requestedPath,
            string finalPath,
            string identity,
            uint linkCount,
            bool reparse)
        {
            RequestedPath = requestedPath;
            FinalPath = finalPath;
            Identity = identity;
            LinkCount = linkCount;
            Reparse = reparse;
        }

        public string RequestedPath { get; private set; }
        public string FinalPath { get; private set; }
        public string Identity { get; private set; }
        public uint LinkCount { get; private set; }
        public bool Reparse { get; private set; }

        public static string[] ParseWindowsCommandLine(string commandLine)
        {
            if (commandLine == null)
                throw new ArgumentNullException("commandLine");
            int count;
            IntPtr vector = CommandLineToArgvW(commandLine, out count);
            if (vector == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (count < 1 || count > 65536)
                    throw new InvalidOperationException("Invalid argv count.");
                string[] result = new string[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr value = Marshal.ReadIntPtr(
                        vector, checked(index * IntPtr.Size));
                    if (value == IntPtr.Zero)
                        throw new InvalidOperationException("Null argv entry.");
                    result[index] = Marshal.PtrToStringUni(value);
                    if (result[index] == null)
                        throw new InvalidOperationException("Invalid argv text.");
                }
                return result;
            }
            finally { LocalFree(vector); }
        }

        public static bool ValidateZlibEnvelope(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length < 7) return false;
                int cmf = bytes[0], flg = bytes[1];
                if ((cmf & 15) != 8 || (cmf >> 4) > 7 ||
                    (((cmf << 8) | flg) % 31) != 0 || (flg & 32) != 0)
                    return false;
                DeflateBits bits = new DeflateBits(bytes, 2, bytes.Length - 4);
                long output = 0;
                bool final;
                do
                {
                    final = bits.Read(1) != 0;
                    int kind = bits.Read(2);
                    if (kind == 0)
                    {
                        bits.AlignZero();
                        int length = bits.ReadByte() | (bits.ReadByte() << 8);
                        int complement =
                            bits.ReadByte() | (bits.ReadByte() << 8);
                        if (((length ^ 0xFFFF) & 0xFFFF) != complement)
                            return false;
                        bits.SkipBytes(length);
                        output = checked(output + length);
                    }
                    else if (kind == 1 || kind == 2)
                    {
                        Huffman literal;
                        Huffman distance;
                        if (kind == 1)
                        {
                            int[] literalLengths = new int[288];
                            for (int i = 0; i <= 143; i++) literalLengths[i] = 8;
                            for (int i = 144; i <= 255; i++) literalLengths[i] = 9;
                            for (int i = 256; i <= 279; i++) literalLengths[i] = 7;
                            for (int i = 280; i <= 287; i++) literalLengths[i] = 8;
                            int[] distanceLengths = new int[32];
                            for (int i = 0; i < 32; i++) distanceLengths[i] = 5;
                            literal = new Huffman(literalLengths);
                            distance = new Huffman(distanceLengths);
                        }
                        else
                        {
                            ReadDynamicTrees(
                                bits, out literal, out distance);
                        }
                        ScanCompressed(bits, literal, distance, ref output);
                    }
                    else return false;
                    if (output > 268435456) return false;
                } while (!final);
                bits.Finish();
                uint a = 1, b = 0;
                long decompressed = 0;
                using (var input = new System.IO.MemoryStream(
                    bytes, 2, bytes.Length - 6, false))
                using (var deflate = new System.IO.Compression.DeflateStream(
                    input, System.IO.Compression.CompressionMode.Decompress))
                {
                    byte[] buffer = new byte[8192];
                    int count;
                    while ((count = deflate.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        decompressed = checked(decompressed + count);
                        if (decompressed > 268435456) return false;
                        for (int index = 0; index < count; index++)
                        {
                            a = (a + buffer[index]) % 65521;
                            b = (b + a) % 65521;
                        }
                    }
                }
                uint stored = ((uint)bytes[bytes.Length - 4] << 24) |
                    ((uint)bytes[bytes.Length - 3] << 16) |
                    ((uint)bytes[bytes.Length - 2] << 8) |
                    bytes[bytes.Length - 1];
                return ((b << 16) | a) == stored;
            }
            catch { return false; }
        }

        private static void ReadDynamicTrees(
            DeflateBits bits, out Huffman literal, out Huffman distance)
        {
            int literalCount = bits.Read(5) + 257;
            int distanceCount = bits.Read(5) + 1;
            int codeCount = bits.Read(4) + 4;
            if (literalCount > 286 || distanceCount > 32)
                throw new InvalidOperationException();
            int[] order = {
                16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };
            int[] codeLengths = new int[19];
            for (int i = 0; i < codeCount; i++)
                codeLengths[order[i]] = bits.Read(3);
            Huffman codeTree = new Huffman(codeLengths);
            int total = literalCount + distanceCount;
            int[] lengths = new int[total];
            int offset = 0;
            while (offset < total)
            {
                int symbol = codeTree.Decode(bits);
                if (symbol <= 15) lengths[offset++] = symbol;
                else
                {
                    int repeat;
                    int value;
                    if (symbol == 16)
                    {
                        if (offset == 0) throw new InvalidOperationException();
                        repeat = bits.Read(2) + 3;
                        value = lengths[offset - 1];
                    }
                    else if (symbol == 17)
                    {
                        repeat = bits.Read(3) + 3;
                        value = 0;
                    }
                    else if (symbol == 18)
                    {
                        repeat = bits.Read(7) + 11;
                        value = 0;
                    }
                    else throw new InvalidOperationException();
                    if (offset + repeat > total)
                        throw new InvalidOperationException();
                    while (repeat-- > 0) lengths[offset++] = value;
                }
            }
            int[] literals = new int[literalCount];
            int[] distances = new int[distanceCount];
            Array.Copy(lengths, 0, literals, 0, literalCount);
            Array.Copy(lengths, literalCount, distances, 0, distanceCount);
            if (literals[256] == 0) throw new InvalidOperationException();
            literal = new Huffman(literals);
            distance = new Huffman(distances);
        }

        private static void ScanCompressed(
            DeflateBits bits, Huffman literal, Huffman distance,
            ref long output)
        {
            int[] lengthBase = {
                3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,
                59,67,83,99,115,131,163,195,227,258 };
            int[] lengthExtra = {
                0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,
                4,5,5,5,5,0 };
            int[] distanceBase = {
                1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,
                513,769,1025,1537,2049,3073,4097,6145,8193,12289,
                16385,24577 };
            int[] distanceExtra = {
                0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,
                10,11,11,12,12,13,13 };
            while (true)
            {
                int symbol = literal.Decode(bits);
                if (symbol < 256) output = checked(output + 1);
                else if (symbol == 256) return;
                else
                {
                    if (symbol < 257 || symbol > 285)
                        throw new InvalidOperationException();
                    int index = symbol - 257;
                    int length = lengthBase[index] +
                        bits.Read(lengthExtra[index]);
                    int distanceSymbol = distance.Decode(bits);
                    if (distanceSymbol < 0 || distanceSymbol > 29)
                        throw new InvalidOperationException();
                    int distanceValue = distanceBase[distanceSymbol] +
                        bits.Read(distanceExtra[distanceSymbol]);
                    if (distanceValue > output)
                        throw new InvalidOperationException();
                    output = checked(output + length);
                }
                if (output > 268435456)
                    throw new InvalidOperationException();
            }
        }

        private sealed class DeflateBits
        {
            private readonly byte[] bytes;
            private readonly int endBit;
            private int bit;
            internal DeflateBits(byte[] value, int startByte, int endByte)
            {
                bytes = value;
                bit = checked(startByte * 8);
                endBit = checked(endByte * 8);
                if (startByte < 0 || endByte < startByte ||
                    endByte > value.Length) throw new InvalidOperationException();
            }
            internal int Read(int count)
            {
                if (count < 0 || count > 16 || bit + count > endBit)
                    throw new InvalidOperationException();
                int value = 0;
                for (int index = 0; index < count; index++, bit++)
                    value |= ((bytes[bit >> 3] >> (bit & 7)) & 1) << index;
                return value;
            }
            internal int ReadByte()
            {
                if ((bit & 7) != 0) throw new InvalidOperationException();
                return Read(8);
            }
            internal void AlignZero()
            {
                while ((bit & 7) != 0)
                    if (Read(1) != 0) throw new InvalidOperationException();
            }
            internal void SkipBytes(int count)
            {
                if ((bit & 7) != 0 || count < 0 ||
                    bit + checked(count * 8) > endBit)
                    throw new InvalidOperationException();
                bit += count * 8;
            }
            internal void Finish()
            {
                AlignZero();
                if (bit != endBit) throw new InvalidOperationException();
            }
        }

        private sealed class Huffman
        {
            private readonly System.Collections.Generic.Dictionary<long, int>
                symbols = new System.Collections.Generic.Dictionary<long, int>();
            private readonly int maximum;
            internal Huffman(int[] lengths)
            {
                int[] counts = new int[16];
                foreach (int length in lengths)
                {
                    if (length < 0 || length > 15)
                        throw new InvalidOperationException();
                    if (length != 0) counts[length]++;
                }
                int code = 0;
                int[] next = new int[16];
                for (int length = 1; length <= 15; length++)
                {
                    code = (code + counts[length - 1]) << 1;
                    if (code + counts[length] > (1 << length))
                        throw new InvalidOperationException();
                    next[length] = code;
                    if (counts[length] != 0) maximum = length;
                }
                if (maximum == 0) throw new InvalidOperationException();
                for (int symbol = 0; symbol < lengths.Length; symbol++)
                {
                    int length = lengths[symbol];
                    if (length == 0) continue;
                    long key = ((long)length << 32) |
                        (uint)next[length]++;
                    if (symbols.ContainsKey(key))
                        throw new InvalidOperationException();
                    symbols.Add(key, symbol);
                }
            }
            internal int Decode(DeflateBits bits)
            {
                int code = 0;
                for (int length = 1; length <= maximum; length++)
                {
                    code = (code << 1) | bits.Read(1);
                    int symbol;
                    if (symbols.TryGetValue(
                        ((long)length << 32) | (uint)code, out symbol))
                        return symbol;
                }
                throw new InvalidOperationException();
            }
        }

        public static FormalLauncherNative Read(string path, bool directory)
        {
            string full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
            uint flags = OpenReparse | (directory ? BackupSemantics : 0);
            using (SafeFileHandle handle = CreateFile(
                full,
                ReadAttributes,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Information info;
                if (!GetFileInformationByHandle(handle, out info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                StringBuilder final = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(
                    handle, final, final.Capacity, 0);
                if (length == 0 || length >= final.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                string normalized = final.ToString();
                if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
                    normalized = normalized.Substring(4);
                ulong index = ((ulong)info.FileIndexHigh << 32) |
                    info.FileIndexLow;
                return new FormalLauncherNative(
                    full,
                    normalized.TrimEnd('\\'),
                    info.VolumeSerialNumber.ToString("X8") +
                        index.ToString("X16"),
                    info.NumberOfLinks,
                    (info.FileAttributes & 0x400) != 0);
            }
        }

        public static FormalTokenProof ReadFormalTokenProof()
        {
            IntPtr current = IntPtr.Zero;
            IntPtr linked = IntPtr.Zero;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out current))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                int elevationType = ReadTokenInt32(current, 18);
                linked = ReadLinkedToken(current);
                string currentSid;
                string currentDomain;
                int currentSidType;
                ReadTokenIdentity(
                    current, out currentSid, out currentDomain,
                    out currentSidType);
                string linkedSid;
                string linkedDomain;
                int linkedSidType;
                ReadTokenIdentity(
                    linked, out linkedSid, out linkedDomain,
                    out linkedSidType);
                GroupState currentAdministrators =
                    ReadAdministratorsGroup(current);
                GroupState linkedAdministrators =
                    ReadAdministratorsGroup(linked);
                return new FormalTokenProof
                {
                    MachineName = Environment.MachineName,
                    ElevationType = elevationType,
                    CurrentAccountSid = currentSid,
                    LinkedAccountSid = linkedSid,
                    CurrentSidType = currentSidType,
                    LinkedSidType = linkedSidType,
                    CurrentAdministratorsDenyOnly =
                        currentAdministrators.DenyOnly,
                    CurrentAdministratorsEnabled =
                        currentAdministrators.Enabled,
                    LinkedAdministratorsDenyOnly =
                        linkedAdministrators.DenyOnly,
                    LinkedAdministratorsEnabled =
                        linkedAdministrators.Enabled,
                    CurrentAccountDomain = currentDomain,
                    LinkedAccountDomain = linkedDomain
                };
            }
            finally
            {
                if (linked != IntPtr.Zero) CloseHandle(linked);
                if (current != IntPtr.Zero) CloseHandle(current);
            }
        }

        private static int ReadTokenInt32(IntPtr token, int informationClass)
        {
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                int returned;
                if (!GetTokenInformation(
                    token, informationClass, buffer, sizeof(int), out returned) ||
                    returned != sizeof(int))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.ReadInt32(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static IntPtr ReadLinkedToken(IntPtr token)
        {
            IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                int returned;
                if (!GetTokenInformation(
                    token, 19, buffer, IntPtr.Size, out returned) ||
                    returned != IntPtr.Size)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.ReadIntPtr(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void ReadTokenIdentity(
            IntPtr token, out string sidValue, out string domainValue,
            out int sidType)
        {
            int required = 0;
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out required);
            if (required <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                int returned;
                if (!GetTokenInformation(
                    token, 1, buffer, required, out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                sidValue = new System.Security.Principal.SecurityIdentifier(
                    sid).Value;
                uint nameLength = 0;
                uint domainLength = 0;
                int use;
                LookupAccountSid(
                    null, sid, null, ref nameLength, null, ref domainLength,
                    out use);
                if (nameLength == 0 || domainLength == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                StringBuilder name = new StringBuilder((int)nameLength);
                StringBuilder domain = new StringBuilder((int)domainLength);
                if (!LookupAccountSid(
                    null, sid, name, ref nameLength, domain, ref domainLength,
                    out use))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                domainValue = domain.ToString();
                sidType = use;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private sealed class GroupState
        {
            internal bool DenyOnly;
            internal bool Enabled;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            internal IntPtr Sid;
            internal uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenGroupsLayout
        {
            internal uint GroupCount;
            internal SidAndAttributes FirstGroup;
        }

        private static GroupState ReadAdministratorsGroup(IntPtr token)
        {
            var administrators = new System.Security.Principal.SecurityIdentifier(
                "S-1-5-32-544");
            byte[] sidBytes = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(sidBytes, 0);
            IntPtr administratorsSid = Marshal.AllocHGlobal(sidBytes.Length);
            int required = 0;
            GetTokenInformation(token, 2, IntPtr.Zero, 0, out required);
            if (required <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.Copy(
                    sidBytes, 0, administratorsSid, sidBytes.Length);
                int returned;
                if (!GetTokenInformation(
                    token, 2, buffer, required, out returned) ||
                    returned < sizeof(uint) || returned > required)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                uint rawCount = unchecked((uint)Marshal.ReadInt32(buffer));
                int offset = checked((int)Marshal.OffsetOf(
                    typeof(TokenGroupsLayout), "FirstGroup"));
                int stride = Marshal.SizeOf(typeof(SidAndAttributes));
                if (offset < sizeof(uint) || stride < IntPtr.Size + sizeof(uint) ||
                    rawCount > 65536 ||
                    checked(offset + checked((int)rawCount * stride)) > returned)
                    throw new InvalidOperationException(
                        "TOKEN_GROUPS bounds are invalid.");
                int matches = 0;
                uint attributes = 0;
                for (int index = 0; index < (int)rawCount; index++)
                {
                    IntPtr entryPointer = IntPtr.Add(
                        buffer, checked(offset + index * stride));
                    SidAndAttributes entry =
                        (SidAndAttributes)Marshal.PtrToStructure(
                            entryPointer, typeof(SidAndAttributes));
                    if (entry.Sid == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "TOKEN_GROUPS contains a null SID.");
                    if (EqualSid(entry.Sid, administratorsSid))
                    {
                        matches++;
                        attributes = entry.Attributes;
                    }
                }
                if (matches != 1)
                    throw new InvalidOperationException(
                        "The Administrators SID must occur exactly once.");
                return new GroupState
                {
                    Enabled = (attributes & 0x00000004) != 0,
                    DenyOnly = (attributes & 0x00000010) != 0
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(administratorsSid);
            }
        }

        private sealed class ConversionSource
        {
            internal string Path;
            internal bool Exists;
            internal string Text;
            internal long Length;
            internal long CreationTicks;
            internal long WriteTicks;
            internal int Attributes;
            internal byte[] Sha256;
            internal string NativeFileIdentity;
        }

        private sealed class ConversionProfile
        {
            internal bool AutoCrlf;
            internal string Fingerprint;
        }

        private static ConversionSource CaptureConversionSource(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            if (!System.IO.File.Exists(full))
            {
                if (System.IO.Directory.Exists(full)) return null;
                return new ConversionSource
                {
                    Path = full,
                    Exists = false,
                    Text = null
                };
            }
            FormalLauncherNative before = Read(full, false);
            if (before.Reparse || before.LinkCount != 1 ||
                !String.Equals(
                    before.FinalPath, full, StringComparison.OrdinalIgnoreCase))
                return null;
            var beforeInfo = new System.IO.FileInfo(full);
            long beforeLength = beforeInfo.Length;
            long beforeCreation = beforeInfo.CreationTimeUtc.Ticks;
            long beforeWrite = beforeInfo.LastWriteTimeUtc.Ticks;
            int beforeAttributes = (int)beforeInfo.Attributes;
            if (beforeLength > 1024 * 1024) return null;
            byte[] bytes = System.IO.File.ReadAllBytes(full);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch { return null; }
            FormalLauncherNative after = Read(full, false);
            var afterInfo = new System.IO.FileInfo(full);
            if (after.Reparse || after.LinkCount != 1 ||
                before.Identity != after.Identity ||
                !String.Equals(
                    after.FinalPath, full, StringComparison.OrdinalIgnoreCase) ||
                beforeLength != bytes.Length ||
                afterInfo.Length != beforeLength ||
                afterInfo.CreationTimeUtc.Ticks != beforeCreation ||
                afterInfo.LastWriteTimeUtc.Ticks != beforeWrite ||
                (int)afterInfo.Attributes != beforeAttributes)
                return null;
            byte[] digest;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                digest = sha.ComputeHash(bytes);
            return new ConversionSource
            {
                Path = full,
                Exists = true,
                Text = text,
                Length = bytes.Length,
                CreationTicks = beforeCreation,
                WriteTicks = beforeWrite,
                Attributes = beforeAttributes,
                Sha256 = digest,
                NativeFileIdentity = after.Identity
            };
        }

        private static bool IsConfigName(string value)
        {
            if (String.IsNullOrEmpty(value) ||
                !((value[0] >= 'A' && value[0] <= 'Z') ||
                  (value[0] >= 'a' && value[0] <= 'z')))
                return false;
            for (int index = 1; index < value.Length; index++)
            {
                char c = value[index];
                if (!((c >= 'A' && c <= 'Z') ||
                      (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '-'))
                    return false;
            }
            return value.Length <= 32768;
        }

        private static bool DecodeConfigValue(string text, out string value)
        {
            value = null;
            var output = new StringBuilder();
            var whitespace = new StringBuilder();
            bool quoted = false;
            bool seen = false;
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (!quoted && (c == '#' || c == ';')) break;
                if (c == '"')
                {
                    quoted = !quoted;
                    seen = true;
                    output.Append(whitespace);
                    whitespace.Length = 0;
                    continue;
                }
                if (c == '\\')
                {
                    if (++index >= text.Length) return false;
                    char escaped = text[index];
                    char decoded;
                    if (escaped == '"') decoded = '"';
                    else if (escaped == '\\') decoded = '\\';
                    else if (escaped == 'n') decoded = '\n';
                    else if (escaped == 't') decoded = '\t';
                    else if (escaped == 'b') decoded = '\b';
                    else return false;
                    output.Append(whitespace);
                    whitespace.Length = 0;
                    output.Append(decoded);
                    seen = true;
                }
                else if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (seen) whitespace.Append(c);
                }
                else
                {
                    output.Append(whitespace);
                    whitespace.Length = 0;
                    output.Append(c);
                    seen = true;
                }
                if (output.Length + whitespace.Length > 32768)
                    return false;
            }
            if (quoted) return false;
            value = output.ToString();
            return true;
        }

        private static bool ParseConversionConfig(
            ConversionSource source,
            ref bool hasAutoCrlf,
            ref bool autoCrlf)
        {
            if (!source.Exists) return true;
            string text = source.Text;
            if (text.IndexOf('\0') >= 0 ||
                new UTF8Encoding(false, true).GetByteCount(text) > 1024 * 1024)
                return false;
            for (int index = 0; index < text.Length; index++)
                if (text[index] == '\r' &&
                    (index + 1 >= text.Length || text[index + 1] != '\n'))
                    return false;
            string[] physical = text.Replace("\r\n", "\n").Split('\n');
            if (physical.Length > 65536) return false;
            var logical = new System.Collections.Generic.List<string>();
            for (int physicalIndex = 0;
                physicalIndex < physical.Length;
                physicalIndex++)
            {
                var lineBuilder = new StringBuilder(physical[physicalIndex]);
                int continuations = 0;
                while (lineBuilder.Length > 0 &&
                    lineBuilder[lineBuilder.Length - 1] == '\\')
                {
                    int slashCount = 0;
                    for (int slashIndex = lineBuilder.Length - 1;
                        slashIndex >= 0 && lineBuilder[slashIndex] == '\\';
                        slashIndex--)
                        slashCount++;
                    if ((slashCount & 1) == 0) break;
                    if (++physicalIndex >= physical.Length ||
                        ++continuations > 64)
                        return false;
                    lineBuilder.Length--;
                    lineBuilder.Append(
                        physical[physicalIndex].TrimStart(' ', '\t'));
                    if (lineBuilder.Length > 32768) return false;
                }
                logical.Add(lineBuilder.ToString());
            }
            string section = "";
            string subsection = "";
            int assignments = 0;
            var lfs = new System.Collections.Generic.Dictionary<
                string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourceLine in logical)
            {
                string line = sourceLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;
                if (line[0] == '[')
                {
                    bool headerQuoted = false;
                    bool headerEscaped = false;
                    int headerEnd = -1;
                    for (int headerIndex = 1;
                        headerIndex < line.Length;
                        headerIndex++)
                    {
                        char headerCharacter = line[headerIndex];
                        if (headerEscaped)
                        {
                            headerEscaped = false;
                            continue;
                        }
                        if (headerCharacter == '\\' && headerQuoted)
                        {
                            headerEscaped = true;
                            continue;
                        }
                        if (headerCharacter == '"')
                            headerQuoted = !headerQuoted;
                        else if (headerCharacter == ']' && !headerQuoted)
                        {
                            headerEnd = headerIndex;
                            break;
                        }
                    }
                    if (headerEnd < 0) return false;
                    string headerRemainder =
                        line.Substring(headerEnd + 1).TrimStart(' ', '\t');
                    if (headerRemainder.Length > 0 &&
                        headerRemainder[0] != '#' &&
                        headerRemainder[0] != ';')
                        return false;
                    string header = line.Substring(1, headerEnd - 1);
                    int separator = header.IndexOfAny(
                        new char[] { ' ', '\t' });
                    string rawSection = separator < 0
                        ? header
                        : header.Substring(0, separator);
                    if (!IsConfigName(rawSection)) return false;
                    section = rawSection.ToLowerInvariant();
                    subsection = "";
                    if (separator >= 0)
                    {
                        string tail = header.Substring(separator).Trim();
                        if (tail.Length < 2 || tail[0] != '"' ||
                            tail[tail.Length - 1] != '"')
                            return false;
                        if (!DecodeConfigValue(tail, out subsection) ||
                            subsection.Length > 32768)
                            return false;
                    }
                    if (section == "include" || section == "includeif")
                        return false;
                    continue;
                }
                int nameEnd = 0;
                while (nameEnd < line.Length &&
                    line[nameEnd] != ' ' && line[nameEnd] != '\t' &&
                    line[nameEnd] != '=')
                    nameEnd++;
                string rawKey = line.Substring(0, nameEnd);
                if (!IsConfigName(rawKey) || section.Length == 0 ||
                    ++assignments > 4096)
                    return false;
                int cursor = nameEnd;
                while (cursor < line.Length &&
                    (line[cursor] == ' ' || line[cursor] == '\t'))
                    cursor++;
                string value = "true";
                if (cursor < line.Length)
                {
                    if (line[cursor] == '#' || line[cursor] == ';')
                        value = "true";
                    else
                    {
                        if (line[cursor] != '=') return false;
                        if (!DecodeConfigValue(
                                line.Substring(cursor + 1), out value))
                            return false;
                    }
                }
                string key = rawKey.ToLowerInvariant();
                if (section == "core" && key == "autocrlf")
                {
                    if (!String.Equals(
                            value, "true", StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(
                            value, "false", StringComparison.OrdinalIgnoreCase))
                        return false;
                    hasAutoCrlf = true;
                    autoCrlf = String.Equals(
                        value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (section == "core" &&
                    (key == "eol" || key == "attributesfile" ||
                     key == "worktree" || key == "safecrlf" ||
                     key == "checkroundtripencoding" ||
                     key == "bigfilethreshold"))
                    return false;
                else if (section == "extensions" &&
                    key == "worktreeconfig")
                    return false;
                else if (section == "filter")
                {
                    if (!String.Equals(
                            subsection, "lfs", StringComparison.Ordinal))
                        return false;
                    string expected;
                    if (key == "clean")
                        expected = "git-lfs clean -- %f";
                    else if (key == "smudge")
                        expected = "git-lfs smudge -- %f";
                    else if (key == "process")
                        expected = "git-lfs filter-process";
                    else if (key == "required")
                        expected = "true";
                    else return false;
                    if (!String.Equals(
                            value, expected, StringComparison.Ordinal))
                        return false;
                    lfs[key] = value;
                }
            }
            return lfs.Count == 0 || lfs.Count == 4;
        }

        private static System.Collections.Generic.Dictionary<string, string>
            CaptureEnvironment()
        {
            var snapshot = new System.Collections.Generic.Dictionary<
                string, string>(StringComparer.OrdinalIgnoreCase);
            System.Collections.IDictionary values =
                Environment.GetEnvironmentVariables(
                    EnvironmentVariableTarget.Process);
            foreach (System.Collections.DictionaryEntry entry in values)
            {
                string name = entry.Key as string;
                string value = entry.Value as string;
                if (String.IsNullOrEmpty(name) || value == null ||
                    snapshot.ContainsKey(name))
                    return null;
                snapshot.Add(name, value);
            }
            return snapshot;
        }

        private static bool TryCanonicalEnvironmentPath(
            System.Collections.Generic.Dictionary<string, string> environment,
            string name,
            bool required,
            out string value)
        {
            value = null;
            string raw;
            if (!environment.TryGetValue(name, out raw))
            {
                if (required) return false;
                value = "";
                return true;
            }
            if (String.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0)
                return false;
            try
            {
                string canonical = System.IO.Path.GetFullPath(raw);
                if (!System.IO.Path.IsPathRooted(raw) ||
                    !String.Equals(
                        raw, canonical, StringComparison.OrdinalIgnoreCase))
                    return false;
                value = canonical;
                return true;
            }
            catch { return false; }
        }

        private static bool ParseCommandConfigEnvironment(
            System.Collections.Generic.Dictionary<string, string> environment)
        {
            var numberedPattern = new System.Text.RegularExpressions.Regex(
                "^GIT_CONFIG_(KEY|VALUE)_(0|[1-9][0-9]*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            foreach (string name in environment.Keys)
                if (name.StartsWith(
                        "GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(
                        name,
                        "GIT_CONFIG_COUNT",
                        StringComparison.OrdinalIgnoreCase) &&
                    !numberedPattern.IsMatch(name))
                    return false;
            bool hasCount = environment.ContainsKey("GIT_CONFIG_COUNT");
            int numbered = 0;
            foreach (string name in environment.Keys)
                if (numberedPattern.IsMatch(name))
                    numbered++;
            if (!hasCount) return numbered == 0;
            string rawCount = environment["GIT_CONFIG_COUNT"];
            if (rawCount.Length == 0 ||
                (rawCount.Length > 1 && rawCount[0] == '0') ||
                rawCount.Length > 2)
                return false;
            for (int index = 0; index < rawCount.Length; index++)
                if (rawCount[index] < '0' || rawCount[index] > '9')
                    return false;
            int count;
            if (!Int32.TryParse(
                    rawCount,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out count) ||
                count > 64 || numbered != checked(2 * count))
                return false;
            for (int index = 0; index < count; index++)
            {
                string key;
                string value;
                if (!environment.TryGetValue(
                        "GIT_CONFIG_KEY_" +
                            index.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        out key) ||
                    !environment.TryGetValue(
                        "GIT_CONFIG_VALUE_" +
                            index.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        out value) ||
                    !String.Equals(
                        key,
                        "safe.directory",
                        StringComparison.OrdinalIgnoreCase) ||
                    value.Length > 32768 ||
                    value.IndexOfAny(new char[] { '\0', '\r', '\n' }) >= 0)
                    return false;
            }
            return true;
        }

        private static bool ParseSystemAttributes(ConversionSource source)
        {
            if (!source.Exists) return true;
            string text = source.Text;
            if (text.IndexOf('\0') >= 0) return false;
            for (int index = 0; index < text.Length; index++)
                if (text[index] == '\r' &&
                    (index + 1 >= text.Length || text[index + 1] != '\n'))
                    return false;
            foreach (string sourceLine in
                text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = sourceLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] fields = line.Split(
                    new char[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 2 ||
                    !fields[0].StartsWith("*.", StringComparison.Ordinal) ||
                    fields[0].Length <= 2 ||
                    fields[0].Substring(2).IndexOfAny(
                        new char[] { '/', '\\', '*', '?', '[', ']' }) >= 0 ||
                    fields[1] != "diff=astextplain")
                    return false;
            }
            return true;
        }

        private static byte[] UInt32Le(uint value)
        {
            return new byte[]
            {
                (byte)value,
                (byte)(value >> 8),
                (byte)(value >> 16),
                (byte)(value >> 24)
            };
        }

        private static byte[] Int64Le(long value)
        {
            ulong raw = unchecked((ulong)value);
            return new byte[]
            {
                (byte)raw,
                (byte)(raw >> 8),
                (byte)(raw >> 16),
                (byte)(raw >> 24),
                (byte)(raw >> 32),
                (byte)(raw >> 40),
                (byte)(raw >> 48),
                (byte)(raw >> 56)
            };
        }

        private static void WriteProfileFrame(
            System.IO.MemoryStream stream,
            byte type,
            byte state,
            string name,
            byte[] payload)
        {
            byte[] nameBytes = new UTF8Encoding(false, true).GetBytes(name);
            if (payload == null) payload = new byte[0];
            stream.WriteByte(type);
            stream.WriteByte(state);
            byte[] length = UInt32Le((uint)nameBytes.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(nameBytes, 0, nameBytes.Length);
            length = UInt32Le((uint)payload.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(payload, 0, payload.Length);
        }

        private static bool ProfileEncoderGoldenMatches()
        {
            var utf8 = new UTF8Encoding(false, true);
            using (var stream = new System.IO.MemoryStream())
            {
                WriteProfileFrame(stream, 1, 2, "schema",
                    utf8.GetBytes("FSL.Stage4.ConversionProfile"));
                WriteProfileFrame(stream, 3, 2, "version", UInt32Le(1));
                WriteProfileFrame(stream, 1, 2, "newline",
                    utf8.GetBytes("a\nb"));
                WriteProfileFrame(stream, 1, 2, "pipe",
                    utf8.GetBytes("a|b"));
                WriteProfileFrame(
                    stream, 1, 2, "empty", new byte[0]);
                WriteProfileFrame(
                    stream, 1, 1, "null", new byte[0]);
                WriteProfileFrame(
                    stream, 1, 0, "absent", new byte[0]);
                WriteProfileFrame(
                    stream, 2, 2, "false", new byte[] { 0 });
                WriteProfileFrame(
                    stream, 3, 2, "zero", UInt32Le(0));
                byte[] digest;
                using (var sha =
                    System.Security.Cryptography.SHA256.Create())
                    digest = sha.ComputeHash(stream.ToArray());
                return String.Equals(
                    ToHex(digest),
                    "0d2589a97eec51dd09f1f23b7de4171e6ec9a1ab1356ad1fbd1ac2011a594e26",
                    StringComparison.Ordinal);
            }
        }

        private static byte[] ConversionProfileBytes(
            System.Collections.Generic.Dictionary<string, string> environment,
            string programFiles,
            string userProfile,
            string home,
            string xdg,
            bool hasProgramW6432,
            string programW6432,
            bool hasAutoCrlf,
            bool autoCrlf,
            System.Collections.Generic.Dictionary<
                string, ConversionSource> records)
        {
            var utf8 = new UTF8Encoding(false, true);
            using (var stream = new System.IO.MemoryStream())
            {
                WriteProfileFrame(stream, 1, 2, "schema",
                    utf8.GetBytes("FSL.Stage4.ConversionProfile"));
                WriteProfileFrame(stream, 3, 2, "version", UInt32Le(1));
                WriteProfileFrame(stream, 1, 2, "resolved.ProgramFiles",
                    utf8.GetBytes(programFiles));
                WriteProfileFrame(stream, 1, 2, "resolved.USERPROFILE",
                    utf8.GetBytes(userProfile));
                WriteProfileFrame(stream, 1, 2, "resolved.HOME",
                    utf8.GetBytes(home));
                WriteProfileFrame(stream, 1, 2, "resolved.XDG_CONFIG_HOME",
                    utf8.GetBytes(xdg));
                WriteProfileFrame(
                    stream, 1, hasProgramW6432 ? (byte)2 : (byte)0,
                    "raw.ProgramW6432",
                    hasProgramW6432
                        ? utf8.GetBytes(environment["ProgramW6432"])
                        : new byte[0]);
                WriteProfileFrame(
                    stream, 1, hasProgramW6432 ? (byte)2 : (byte)0,
                    "resolved.ProgramW6432",
                    hasProgramW6432
                        ? utf8.GetBytes(programW6432)
                        : new byte[0]);
                WriteProfileFrame(stream, 2, 2, "hasAutoCrlf",
                    new byte[] { hasAutoCrlf ? (byte)1 : (byte)0 });
                WriteProfileFrame(
                    stream, 2, hasAutoCrlf ? (byte)2 : (byte)0,
                    "autoCrlf",
                    hasAutoCrlf
                        ? new byte[] { autoCrlf ? (byte)1 : (byte)0 }
                        : new byte[0]);
                foreach (string name in new string[]
                {
                    "ProgramFiles", "USERPROFILE", "HOME", "XDG_CONFIG_HOME"
                })
                {
                    string raw;
                    bool present = environment.TryGetValue(name, out raw);
                    WriteProfileFrame(
                        stream, 1, present ? (byte)2 : (byte)0,
                        "raw." + name,
                        present ? utf8.GetBytes(raw) : new byte[0]);
                }
                var gitNames =
                    new System.Collections.Generic.List<string>();
                foreach (string name in environment.Keys)
                    if (name.StartsWith(
                            "GIT_", StringComparison.OrdinalIgnoreCase))
                        gitNames.Add(name.ToUpperInvariant());
                gitNames.Sort(StringComparer.Ordinal);
                using (var payload = new System.IO.MemoryStream())
                {
                    byte[] count = UInt32Le((uint)gitNames.Count);
                    payload.Write(count, 0, count.Length);
                    foreach (string name in gitNames)
                        WriteProfileFrame(
                            payload, 1, 2, name,
                            utf8.GetBytes(environment[name]));
                    WriteProfileFrame(
                        stream, 5, 2, "gitEnvironment", payload.ToArray());
                }
                var paths = new System.Collections.Generic.List<string>(
                    records.Keys);
                paths.Sort(delegate(string left, string right)
                {
                    int result = String.Compare(
                        left, right, StringComparison.OrdinalIgnoreCase);
                    return result != 0
                        ? result
                        : String.CompareOrdinal(left, right);
                });
                using (var sources = new System.IO.MemoryStream())
                {
                    byte[] count = UInt32Le((uint)paths.Count);
                    sources.Write(count, 0, count.Length);
                    foreach (string path in paths)
                    {
                        ConversionSource source = records[path];
                        using (var record = new System.IO.MemoryStream())
                        {
                            WriteProfileFrame(
                                record, 1, 2, "path",
                                utf8.GetBytes(source.Path));
                            WriteProfileFrame(
                                record, 2, 2, "exists",
                                new byte[] {
                                    source.Exists ? (byte)1 : (byte)0 });
                            WriteProfileFrame(
                                record, 4,
                                source.Exists ? (byte)2 : (byte)0,
                                "length",
                                source.Exists
                                    ? Int64Le(source.Length)
                                    : new byte[0]);
                            WriteProfileFrame(
                                record, 4,
                                source.Exists ? (byte)2 : (byte)0,
                                "creationTicks",
                                source.Exists
                                    ? Int64Le(source.CreationTicks)
                                    : new byte[0]);
                            WriteProfileFrame(
                                record, 4,
                                source.Exists ? (byte)2 : (byte)0,
                                "writeTicks",
                                source.Exists
                                    ? Int64Le(source.WriteTicks)
                                    : new byte[0]);
                            WriteProfileFrame(
                                record, 3,
                                source.Exists ? (byte)2 : (byte)0,
                                "attributes",
                                source.Exists
                                    ? UInt32Le(unchecked((uint)source.Attributes))
                                    : new byte[0]);
                            WriteProfileFrame(
                                record, 7,
                                source.Exists ? (byte)2 : (byte)0,
                                "sha256",
                                source.Exists
                                    ? source.Sha256
                                    : new byte[0]);
                            WriteProfileFrame(
                                record, 1,
                                source.Exists ? (byte)2 : (byte)0,
                                "nativeFileIdentity",
                                source.Exists
                                    ? utf8.GetBytes(source.NativeFileIdentity)
                                    : new byte[0]);
                            WriteProfileFrame(
                                sources, 6, 2, "source", record.ToArray());
                        }
                    }
                    WriteProfileFrame(
                        stream, 5, 2, "sources", sources.ToArray());
                }
                return stream.ToArray();
            }
        }

        private static bool CaptureConversionProfile(
            string gitRoot,
            string gitDirectory,
            System.Collections.Generic.List<GitEntry> entries,
            out ConversionProfile profile)
        {
            profile = null;
            System.Collections.Generic.Dictionary<string, string> environment =
                CaptureEnvironment();
            if (environment == null || !ProfileEncoderGoldenMatches())
                return false;
            string programFiles;
            string userProfile;
            string programW6432 = null;
            bool hasProgramW6432 = environment.ContainsKey("ProgramW6432");
            if (!TryCanonicalEnvironmentPath(
                    environment,
                    "ProgramFiles",
                    true,
                    out programFiles) ||
                !TryCanonicalEnvironmentPath(
                    environment,
                    "USERPROFILE",
                    true,
                    out userProfile) ||
                (hasProgramW6432 && !TryCanonicalEnvironmentPath(
                    environment,
                    "ProgramW6432",
                    true,
                    out programW6432)) ||
                !System.IO.Directory.Exists(programFiles) ||
                !System.IO.Directory.Exists(userProfile) ||
                (hasProgramW6432 &&
                    !System.IO.Directory.Exists(programW6432)))
                return false;
            try
            {
                string knownProgramFiles = System.IO.Path.GetFullPath(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles));
                string knownUserProfile = System.IO.Path.GetFullPath(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile));
                if (!String.Equals(
                        programFiles,
                        knownProgramFiles,
                        StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(
                        userProfile,
                        knownUserProfile,
                        StringComparison.OrdinalIgnoreCase) ||
                    (hasProgramW6432 &&
                        (!String.Equals(
                            programW6432,
                            programFiles,
                            StringComparison.OrdinalIgnoreCase) ||
                         !String.Equals(
                            programW6432,
                            knownProgramFiles,
                            StringComparison.OrdinalIgnoreCase))))
                    return false;
            }
            catch { return false; }
            foreach (string forbidden in new string[]
            {
                "GIT_CONFIG_SYSTEM",
                "GIT_CONFIG_GLOBAL",
                "GIT_CONFIG_NOSYSTEM",
                "GIT_CONFIG_PARAMETERS",
                "GIT_ATTR_NOSYSTEM",
                "GIT_DIR",
                "GIT_WORK_TREE",
                "GIT_COMMON_DIR",
                "GIT_INDEX_FILE",
                "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                "GIT_QUARANTINE_PATH",
                "GIT_NAMESPACE",
                "GIT_SHALLOW_FILE",
                "GIT_GRAFT_FILE",
                "GIT_NO_REPLACE_OBJECTS",
                "GIT_REPLACE_REF_BASE"
            })
                if (environment.ContainsKey(forbidden)) return false;
            if (!ParseCommandConfigEnvironment(environment)) return false;
            string home;
            if (environment.ContainsKey("HOME"))
            {
                if (!TryCanonicalEnvironmentPath(
                        environment, "HOME", true, out home))
                    return false;
            }
            else home = userProfile;
            string xdg;
            if (environment.ContainsKey("XDG_CONFIG_HOME"))
            {
                if (!TryCanonicalEnvironmentPath(
                        environment, "XDG_CONFIG_HOME", true, out xdg))
                    return false;
            }
            else xdg = System.IO.Path.Combine(home, ".config");
            string systemConfig = System.IO.Path.Combine(
                programFiles, "Git", "etc", "gitconfig");
            string systemAttributes = System.IO.Path.Combine(
                programFiles, "Git", "etc", "gitattributes");
            string xdgConfig = System.IO.Path.Combine(
                xdg, "git", "config");
            string userConfig =
                System.IO.Path.Combine(home, ".gitconfig");
            string userAttributes = System.IO.Path.Combine(
                xdg, "git", "attributes");
            string legacyUserAttributes =
                System.IO.Path.Combine(home, ".gitattributes");
            string localConfig =
                System.IO.Path.Combine(gitDirectory, "config");
            string worktreeConfig =
                System.IO.Path.Combine(gitDirectory, "config.worktree");
            string infoAttributes = System.IO.Path.Combine(
                gitDirectory, "info", "attributes");
            var governing = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            governing.Add(System.IO.Path.Combine(
                gitRoot, ".gitattributes"));
            foreach (GitEntry entry in entries)
            {
                string directory = gitRoot;
                string[] parts = entry.Path.Split('/');
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    directory =
                        System.IO.Path.Combine(directory, parts[index]);
                    governing.Add(System.IO.Path.Combine(
                        directory, ".gitattributes"));
                }
            }
            var paths = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in new string[]
            {
                systemConfig, systemAttributes, userConfig, xdgConfig,
                userAttributes, legacyUserAttributes, localConfig,
                worktreeConfig, infoAttributes
            }) paths.Add(System.IO.Path.GetFullPath(path));
            foreach (string path in governing)
                paths.Add(System.IO.Path.GetFullPath(path));
            var records = new System.Collections.Generic.Dictionary<
                string, ConversionSource>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                ConversionSource record = CaptureConversionSource(path);
                if (record == null) return false;
                records.Add(path, record);
            }
            foreach (string forbidden in new string[]
            {
                userAttributes, worktreeConfig, legacyUserAttributes,
                infoAttributes
            })
                if (records[
                    System.IO.Path.GetFullPath(forbidden)].Exists)
                    return false;
            foreach (string forbidden in governing)
                if (records[
                    System.IO.Path.GetFullPath(forbidden)].Exists)
                    return false;
            bool hasAutoCrlf = false;
            bool autoCrlf = false;
            if (!ParseConversionConfig(
                    records[System.IO.Path.GetFullPath(systemConfig)],
                    ref hasAutoCrlf, ref autoCrlf) ||
                !ParseConversionConfig(
                    records[System.IO.Path.GetFullPath(xdgConfig)],
                    ref hasAutoCrlf, ref autoCrlf) ||
                !ParseConversionConfig(
                    records[System.IO.Path.GetFullPath(userConfig)],
                    ref hasAutoCrlf, ref autoCrlf) ||
                !ParseConversionConfig(
                    records[System.IO.Path.GetFullPath(localConfig)],
                    ref hasAutoCrlf, ref autoCrlf) ||
                !ParseSystemAttributes(
                    records[System.IO.Path.GetFullPath(systemAttributes)]))
                return false;
            byte[] fingerprintBytes;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                fingerprintBytes = sha.ComputeHash(
                    ConversionProfileBytes(
                        environment,
                        programFiles,
                        userProfile,
                        home,
                        xdg,
                        hasProgramW6432,
                        programW6432,
                        hasAutoCrlf,
                        autoCrlf,
                        records));
            profile = new ConversionProfile
            {
                AutoCrlf = hasAutoCrlf && autoCrlf,
                Fingerprint = ToHex(fingerprintBytes)
            };
            return true;
        }

        private static byte[] SafeAutoCrlfBytes(byte[] bytes)
        {
            using (var output = new System.IO.MemoryStream())
            {
                bool converted = false;
                for (int index = 0; index < bytes.Length; index++)
                {
                    byte value = bytes[index];
                    if (value == 0) return null;
                    if (value == 0x0D)
                    {
                        if (index + 1 >= bytes.Length ||
                            bytes[index + 1] != 0x0A)
                            return null;
                        output.WriteByte(0x0A);
                        index++;
                        converted = true;
                        continue;
                    }
                    if (value == 0x0A) return null;
                    output.WriteByte(value);
                }
                return converted ? output.ToArray() : null;
            }
        }

        public static bool VerifyGitIndexAndTree(
            string gitRoot, string gitDirectory, string expectedTree)
        {
            try
            {
                byte[] index = System.IO.File.ReadAllBytes(
                    System.IO.Path.Combine(gitDirectory, "index"));
                if (index.Length < 32 ||
                    Encoding.ASCII.GetString(index, 0, 4) != "DIRC" ||
                    ReadUInt32(index, 4) != 2)
                    return false;
                int checksumOffset = index.Length - 20;
                byte[] calculated;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    calculated = sha.ComputeHash(index, 0, checksumOffset);
                for (int i = 0; i < 20; i++)
                    if (calculated[i] != index[checksumOffset + i])
                        return false;
                uint rawCount = ReadUInt32(index, 8);
                if (rawCount > 1000000) return false;
                int count = (int)rawCount;
                int offset = 12;
                byte[] previousPath = null;
                var entries = new System.Collections.Generic.List<GitEntry>();
                string normalizedRoot = System.IO.Path.GetFullPath(
                    gitRoot).TrimEnd('\\');
                string rootPrefix = normalizedRoot + "\\";
                for (int entryIndex = 0; entryIndex < count; entryIndex++)
                {
                    int start = offset;
                    if (start < 12 || start + 63 > checksumOffset)
                        return false;
                    uint mode = ReadUInt32(index, start + 24);
                    if (mode != 0x000081A4 && mode != 0x000081ED)
                        return false;
                    byte[] oid = new byte[20];
                    Buffer.BlockCopy(index, start + 40, oid, 0, 20);
                    bool allZero = true;
                    foreach (byte value in oid)
                        if (value != 0) { allZero = false; break; }
                    if (allZero) return false;
                    ushort flags = ReadUInt16(index, start + 60);
                    if ((flags & 0xF000) != 0) return false;
                    int pathStart = start + 62;
                    int pathEnd = pathStart;
                    while (pathEnd < checksumOffset && index[pathEnd] != 0)
                        pathEnd++;
                    if (pathEnd >= checksumOffset || pathEnd == pathStart)
                        return false;
                    int pathLength = pathEnd - pathStart;
                    if ((flags & 0x0FFF) != Math.Min(pathLength, 0x0FFF))
                        return false;
                    byte[] pathBytes = new byte[pathLength];
                    Buffer.BlockCopy(
                        index, pathStart, pathBytes, 0, pathLength);
                    if (previousPath != null &&
                        CompareBytes(previousPath, pathBytes) >= 0)
                        return false;
                    previousPath = pathBytes;
                    string relative;
                    try
                    {
                        relative = new UTF8Encoding(
                            false, true).GetString(pathBytes);
                    }
                    catch { return false; }
                    if (!IsCanonicalGitPath(relative)) return false;
                    entries.Add(new GitEntry
                    {
                        Path = relative,
                        Mode = mode,
                        ObjectId = oid
                    });
                    int entryLength = (pathEnd - start) + 1;
                    int next = start + ((entryLength + 7) & ~7);
                    if (next <= start || next > checksumOffset) return false;
                    for (int padding = pathEnd + 1; padding < next; padding++)
                        if (index[padding] != 0) return false;
                    offset = next;
                }
                byte[] cacheTree = null;
                while (offset < checksumOffset)
                {
                    if (offset + 8 > checksumOffset) return false;
                    string signature = Encoding.ASCII.GetString(index, offset, 4);
                    uint rawSize = ReadUInt32(index, offset + 4);
                    if (rawSize > Int32.MaxValue) return false;
                    int size = (int)rawSize;
                    offset += 8;
                    if (size < 0 || offset + size < offset ||
                        offset + size > checksumOffset)
                        return false;
                    if (signature != "TREE" || cacheTree != null ||
                        !ValidateCacheTree(index, offset, size))
                        return false;
                    cacheTree = new byte[size];
                    Buffer.BlockCopy(index, offset, cacheTree, 0, size);
                    offset += size;
                }
                if (offset != checksumOffset) return false;
                ConversionProfile conversionProfile;
                if (!CaptureConversionProfile(
                        normalizedRoot,
                        gitDirectory,
                        entries,
                        out conversionProfile))
                    return false;
                foreach (GitEntry entry in entries)
                {
                    string full = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(
                            normalizedRoot,
                            entry.Path.Replace('/', '\\')));
                    if (!full.StartsWith(
                            rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                        !System.IO.File.Exists(full))
                        return false;
                    FormalLauncherNative identity = Read(full, false);
                    if (identity.Reparse || identity.LinkCount != 1 ||
                        !identity.FinalPath.StartsWith(
                            rootPrefix, StringComparison.OrdinalIgnoreCase))
                        return false;
                    byte[] content = System.IO.File.ReadAllBytes(full);
                    if (!EqualBytes(
                            GitObjectId("blob", content), entry.ObjectId))
                    {
                        if (!conversionProfile.AutoCrlf) return false;
                        byte[] canonical = SafeAutoCrlfBytes(content);
                        if (canonical == null ||
                            !EqualBytes(
                                GitObjectId("blob", canonical),
                                entry.ObjectId))
                            return false;
                    }
                }
                GitNode root = new GitNode();
                foreach (GitEntry entry in entries)
                    if (!InsertEntry(root, entry)) return false;
                byte[] actualTreeBytes = BuildTree(root);
                if (cacheTree != null &&
                    !EqualBytes(cacheTree, BuildCacheTree(root)))
                    return false;
                string actualTree = ToHex(actualTreeBytes);
                ConversionProfile recapturedProfile;
                return String.Equals(
                        actualTree, expectedTree, StringComparison.Ordinal) &&
                    CaptureConversionProfile(
                        normalizedRoot,
                        gitDirectory,
                        entries,
                        out recapturedProfile) &&
                    conversionProfile.AutoCrlf ==
                        recapturedProfile.AutoCrlf &&
                    String.Equals(
                        conversionProfile.Fingerprint,
                        recapturedProfile.Fingerprint,
                        StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private sealed class GitEntry
        {
            internal string Path;
            internal uint Mode;
            internal byte[] ObjectId;
        }

        private sealed class GitNode
        {
            internal readonly System.Collections.Generic.Dictionary<
                string, GitNode> Directories =
                new System.Collections.Generic.Dictionary<string, GitNode>(
                    StringComparer.Ordinal);
            internal readonly System.Collections.Generic.Dictionary<
                string, GitEntry> Files =
                new System.Collections.Generic.Dictionary<string, GitEntry>(
                    StringComparer.Ordinal);
        }

        private static bool InsertEntry(GitNode root, GitEntry entry)
        {
            string[] parts = entry.Path.Split('/');
            GitNode node = root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                if (node.Files.ContainsKey(parts[index])) return false;
                GitNode child;
                if (!node.Directories.TryGetValue(parts[index], out child))
                {
                    child = new GitNode();
                    node.Directories.Add(parts[index], child);
                }
                node = child;
            }
            string leaf = parts[parts.Length - 1];
            if (node.Directories.ContainsKey(leaf) ||
                node.Files.ContainsKey(leaf))
                return false;
            node.Files.Add(leaf, entry);
            return true;
        }

        private static byte[] BuildTree(GitNode node)
        {
            var items = new System.Collections.Generic.List<TreeItem>();
            foreach (var pair in node.Files)
                items.Add(new TreeItem
                {
                    Name = pair.Key,
                    Directory = false,
                    Mode = pair.Value.Mode == 0x000081ED
                        ? "100755" : "100644",
                    ObjectId = pair.Value.ObjectId
                });
            foreach (var pair in node.Directories)
                items.Add(new TreeItem
                {
                    Name = pair.Key,
                    Directory = true,
                    Mode = "40000",
                    ObjectId = BuildTree(pair.Value)
                });
            items.Sort(delegate(TreeItem left, TreeItem right)
            {
                return CompareBytes(
                    Encoding.UTF8.GetBytes(
                        left.Name + (left.Directory ? "/" : "")),
                    Encoding.UTF8.GetBytes(
                        right.Name + (right.Directory ? "/" : "")));
            });
            using (var body = new System.IO.MemoryStream())
            {
                foreach (TreeItem item in items)
                {
                    byte[] prefix = Encoding.ASCII.GetBytes(
                        item.Mode + " ");
                    byte[] name = new UTF8Encoding(false, true).GetBytes(
                        item.Name);
                    body.Write(prefix, 0, prefix.Length);
                    body.Write(name, 0, name.Length);
                    body.WriteByte(0);
                    body.Write(item.ObjectId, 0, item.ObjectId.Length);
                }
                return GitObjectId("tree", body.ToArray());
            }
        }

        private static byte[] BuildCacheTree(GitNode root)
        {
            using (var output = new System.IO.MemoryStream())
            {
                WriteCacheTreeNode(output, root, "", true);
                return output.ToArray();
            }
        }

        private static int WriteCacheTreeNode(
            System.IO.Stream output, GitNode node, string name, bool root)
        {
            int entryCount = node.Files.Count;
            foreach (GitNode child in node.Directories.Values)
                entryCount += CountEntries(child);
            byte[] path = new UTF8Encoding(false, true).GetBytes(
                root ? "" : name);
            output.Write(path, 0, path.Length);
            output.WriteByte(0);
            byte[] counts = Encoding.ASCII.GetBytes(
                entryCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + " " +
                node.Directories.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "\n");
            output.Write(counts, 0, counts.Length);
            byte[] oid = BuildTree(node);
            output.Write(oid, 0, oid.Length);
            var names = new System.Collections.Generic.List<string>(
                node.Directories.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (string childName in names)
                WriteCacheTreeNode(
                    output, node.Directories[childName], childName, false);
            return entryCount;
        }

        private static int CountEntries(GitNode node)
        {
            int count = node.Files.Count;
            foreach (GitNode child in node.Directories.Values)
                count += CountEntries(child);
            return count;
        }

        private sealed class TreeItem
        {
            internal string Name;
            internal bool Directory;
            internal string Mode;
            internal byte[] ObjectId;
        }

        private static bool ValidateCacheTree(byte[] bytes, int start, int size)
        {
            try
            {
                int offset = start;
                int end = start + size;
                if (!ParseCacheTreeNode(bytes, ref offset, end, true))
                    return false;
                return offset == end;
            }
            catch { return false; }
        }

        private static bool ParseCacheTreeNode(
            byte[] bytes, ref int offset, int end, bool root)
        {
            int pathStart = offset;
            while (offset < end && bytes[offset] != 0) offset++;
            if (offset >= end) return false;
            string path;
            try
            {
                path = new UTF8Encoding(false, true).GetString(
                    bytes, pathStart, offset - pathStart);
            }
            catch { return false; }
            if ((root && path.Length != 0) ||
                (!root && (
                    path.Length == 0 || path.Contains("/") ||
                    path.Contains("\\") ||
                    path.Normalize(NormalizationForm.FormC) != path)))
                return false;
            offset++;
            int count = ReadAsciiInteger(bytes, ref offset, end, true);
            if (count < -1 || offset >= end || bytes[offset++] != (byte)' ')
                return false;
            int subtreeCount = ReadAsciiInteger(
                bytes, ref offset, end, false);
            if (subtreeCount < 0 || offset >= end ||
                bytes[offset++] != (byte)'\n')
                return false;
            if (count >= 0)
            {
                if (offset + 20 > end) return false;
                offset += 20;
            }
            for (int index = 0; index < subtreeCount; index++)
                if (!ParseCacheTreeNode(bytes, ref offset, end, false))
                    return false;
            return true;
        }

        private static int ReadAsciiInteger(
            byte[] bytes, ref int offset, int end, bool allowNegativeOne)
        {
            bool negative = false;
            if (allowNegativeOne && offset < end && bytes[offset] == (byte)'-')
            {
                negative = true;
                offset++;
            }
            int start = offset;
            long value = 0;
            while (offset < end &&
                bytes[offset] >= (byte)'0' && bytes[offset] <= (byte)'9')
            {
                value = checked(value * 10 + bytes[offset] - (byte)'0');
                offset++;
            }
            if (offset == start || value > Int32.MaxValue) return Int32.MinValue;
            int result = (int)value;
            if (negative) result = -result;
            if (negative && result != -1) return Int32.MinValue;
            return result;
        }

        private static bool IsCanonicalGitPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path[0] == '/' ||
                path.Contains("\\") || path.Contains("\0") ||
                path.Normalize(NormalizationForm.FormC) != path)
                return false;
            string[] parts = path.Split('/');
            foreach (string part in parts)
                if (part.Length == 0 || part == "." || part == "..")
                    return false;
            return true;
        }

        private static byte[] GitObjectId(string type, byte[] content)
        {
            byte[] header = Encoding.ASCII.GetBytes(
                type + " " + content.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "\0");
            byte[] full = new byte[header.Length + content.Length];
            Buffer.BlockCopy(header, 0, full, 0, header.Length);
            Buffer.BlockCopy(content, 0, full, header.Length, content.Length);
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return sha.ComputeHash(full);
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(((uint)bytes[offset] << 8) |
                bytes[offset + 1]);
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) |
                bytes[offset + 3];
        }

        private static int CompareBytes(byte[] left, byte[] right)
        {
            int length = Math.Min(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                int difference = left[index] - right[index];
                if (difference != 0) return difference;
            }
            return left.Length - right.Length;
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace(
                "-", "").ToLowerInvariant();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Information
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out Information information);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle handle, StringBuilder path, int length, uint flags);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError=true)]
        private static extern bool OpenProcessToken(
            IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)]
        private static extern bool GetTokenInformation(
            IntPtr token, int informationClass, IntPtr information,
            int informationLength, out int returnLength);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool LookupAccountSid(
            string systemName, IntPtr sid, StringBuilder name,
            ref uint nameLength, StringBuilder domain, ref uint domainLength,
            out int use);
        [DllImport("advapi32.dll")]
        private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr CommandLineToArgvW(
            string commandLine, out int argumentCount);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
'@ -ReferencedAssemblies @(
        'System.dll',
        'System.Core.dll',
        'System.Security.dll')
}

# Responsibility 1: strict public capability model.
function New-FslFlbException {
    param([string]$Code, [string]$Message, [AllowNull()][Exception]$Inner)
    $exception = if ($null -eq $Inner) {
        [InvalidOperationException]::new($Message)
    }
    else {
        [InvalidOperationException]::new($Message, $Inner)
    }
    $exception.Data['FslFormalLauncherBundleCode'] = $Code
    return $exception
}

function Stop-FslFlb {
    param([string]$Code, [string]$Message, [AllowNull()][Exception]$Inner)
    throw (New-FslFlbException $Code $Message $Inner)
}

function Get-FslFlbNames {
    param($Value)
    if ($Value -is [Collections.IDictionary]) {
        return @($Value.Keys | ForEach-Object { [string]$_ })
    }
    return @($Value.PSObject.Properties | ForEach-Object { $_.Name })
}

function Get-FslFlbValue {
    param($Value, [string]$Name)
    if ($Value -is [Collections.IDictionary]) {
        return $Value[$Name]
    }
    return $Value.PSObject.Properties[$Name].Value
}

function Test-FslFlbNames {
    param($Value, [string[]]$Expected)
    $actual = @(Get-FslFlbNames $Value)
    if ($actual.Count -ne $Expected.Count) { return $false }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -cne $Expected[$index]) { return $false }
    }
    return $true
}

function Test-FslFlbLeaf {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrEmpty($Value) -or
        $Value -in @('.', '..') -or
        [IO.Path]::IsPathRooted($Value) -or
        $Value.IndexOfAny([char[]]"\/:") -ge 0 -or
        $Value -match '[\x00-\x1F<>:"|?*]' -or
        $Value.EndsWith(' ') -or
        $Value.EndsWith('.')) {
        return $false
    }
    $stem = $Value.Split('.')[0]
    if ($stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        return $false
    }
    return $true
}

function Assert-FslFlbModel {
    param([psobject]$Model)
    if (-not (Test-FslFlbNames $Model $script:FlbModelNames) -or
        $Model.schemaVersion -isnot [int] -or
        [int]$Model.schemaVersion -ne 1 -or
        [string]$Model.authorityProfile -cnotin $script:FlbProfiles -or
        [string]::IsNullOrWhiteSpace([string]$Model.contractId) -or
        [string]::IsNullOrWhiteSpace([string]$Model.checkpoint) -or
        [string]::IsNullOrWhiteSpace([string]$Model.attemptId) -or
        [string]$Model.runId -cnotmatch $script:FlbRunIdPattern -or
        -not (Test-FslFlbNames `
            $Model.rootBinding `
            $script:FlbRootBindingNames) -or
        -not (Test-FslFlbNames `
            $Model.recoveryAuthority `
            $script:FlbRecoveryAuthorityNames) -or
        -not (Test-FslFlbLeaf ([string]$Model.rootBinding.sourceLeafName)) -or
        -not (Test-FslFlbLeaf ([string]$Model.rootBinding.bundleLeafName)) -or
        [string]$Model.rootBinding.sourceLeafName -ceq
            [string]$Model.rootBinding.bundleLeafName -or
        [string]::IsNullOrWhiteSpace(
            [string]$Model.recoveryAuthority.contractId) -or
        [string]$Model.recoveryAuthority.contractSha256 -cnotmatch
            $script:FlbShaPattern) {
        Stop-FslFlb `
            'FSL-FLB-V001-MODEL' `
            'The slim formal-launcher capability model is invalid.' `
            $null
    }
    if ([string]$Model.authorityProfile -ceq 'Formal') {
        if ($null -ne $Model.rootBinding.fixtureId) {
            Stop-FslFlb `
                'FSL-FLB-V001-MODEL' `
                'Formal authority requires a null fixtureId.' `
                $null
        }
    }
    else {
        if ($Model.rootBinding.fixtureId -isnot [string]) {
            Stop-FslFlb `
                'FSL-FLB-V001-MODEL' `
                'TestFixture authority requires a canonical Guid-D fixtureId.' `
                $null
        }
        $fixture = [Guid]::Empty
        if (-not [Guid]::TryParseExact(
                [string]$Model.rootBinding.fixtureId,
                'D',
                [ref]$fixture) -or
            $fixture.ToString('D') -cne
                [string]$Model.rootBinding.fixtureId) {
            Stop-FslFlb `
                'FSL-FLB-V001-MODEL' `
                'TestFixture authority requires a canonical Guid-D fixtureId.' `
                $null
        }
    }
}

# Responsibility 2: canonical JSON and cryptographic helpers.
function ConvertTo-FslFlbJsonString {
    param([string]$Value)
    return ($Value | ConvertTo-Json -Compress)
}

function ConvertTo-FslFlbJsonValue {
    param([AllowNull()]$Value, [int]$Depth)
    if ($Depth -gt 64) {
        Stop-FslFlb 'FSL-FLB-V005-CONTRACT-CANONICAL' 'JSON depth exceeded.' $null
    }
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string] -or $Value -is [char]) {
        return ConvertTo-FslFlbJsonString ([string]$Value)
    }
    if ($Value -is [bool]) {
        return $(if ($Value) { 'true' } else { 'false' })
    }
    if ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]) {
        return [Convert]::ToString(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [Collections.IEnumerable] -and
        $Value -isnot [Collections.IDictionary] -and
        $Value -isnot [pscustomobject]) {
        $items = @($Value)
        if ($items.Count -eq 0) { return '[]' }
        $lines = [Collections.Generic.List[string]]::new()
        [void]$lines.Add('[')
        for ($index = 0; $index -lt $items.Count; $index++) {
            $rendered = ConvertTo-FslFlbJsonValue $items[$index] ($Depth + 1)
            $parts = $rendered -split "`n", -1
            for ($part = 0; $part -lt $parts.Count; $part++) {
                $suffix = if (
                    $part -eq $parts.Count - 1 -and
                    $index -lt $items.Count - 1) { ',' } else { '' }
                $prefix = if ($part -eq 0) {
                    ' ' * (($Depth + 1) * 2)
                }
                else { '' }
                [void]$lines.Add($prefix + $parts[$part] + $suffix)
            }
        }
        [void]$lines.Add((' ' * ($Depth * 2)) + ']')
        return $lines -join "`n"
    }
    if ($Value -is [Collections.IDictionary] -or
        $Value -is [pscustomobject]) {
        $names = @(Get-FslFlbNames $Value)
        if ($names.Count -eq 0) { return '{}' }
        $lines = [Collections.Generic.List[string]]::new()
        [void]$lines.Add('{')
        for ($index = 0; $index -lt $names.Count; $index++) {
            $name = $names[$index]
            $rendered = ConvertTo-FslFlbJsonValue (
                Get-FslFlbValue $Value $name) ($Depth + 1)
            $parts = $rendered -split "`n", -1
            for ($part = 0; $part -lt $parts.Count; $part++) {
                $suffix = if (
                    $part -eq $parts.Count - 1 -and
                    $index -lt $names.Count - 1) { ',' } else { '' }
                if ($part -eq 0) {
                    [void]$lines.Add(
                        (' ' * (($Depth + 1) * 2)) +
                        (ConvertTo-FslFlbJsonString $name) +
                        ': ' + $parts[$part] + $suffix)
                }
                else {
                    [void]$lines.Add($parts[$part] + $suffix)
                }
            }
        }
        [void]$lines.Add((' ' * ($Depth * 2)) + '}')
        return $lines -join "`n"
    }
    Stop-FslFlb `
        'FSL-FLB-V005-CONTRACT-CANONICAL' `
        "Unsupported JSON value type: $($Value.GetType().FullName)." `
        $null
}

function ConvertTo-FslFlbCanonicalJson {
    param($Value)
    return (ConvertTo-FslFlbJsonValue $Value 0) + "`n"
}

function Get-FslFlbBytes {
    param([string]$Text)
    return [Text.UTF8Encoding]::new($false, $true).GetBytes($Text)
}

function Get-FslFlbSha256Bytes {
    param([byte[]]$Bytes)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $hash.ComputeHash($Bytes)).Replace('-', '')
    }
    finally { $hash.Dispose() }
}

function Get-FslFlbSha256 {
    param([string]$Path)
    return Get-FslFlbSha256Bytes ([IO.File]::ReadAllBytes($Path))
}

function Get-FslFlbSha1Bytes {
    param([byte[]]$Bytes)
    $hash = [Security.Cryptography.SHA1]::Create()
    try {
        return [BitConverter]::ToString(
            $hash.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

function Get-FslFlbMapHash {
    param([object[]]$Gates)
    $text = @($Gates | ForEach-Object {
        ([string]$_.gateId) + '|' + ([string][int]$_.exitCode)
    }) -join "`n"
    return Get-FslFlbSha256Bytes (Get-FslFlbBytes $text)
}

# Responsibility 3: filesystem identity, ACL, durable writes, and roots.
function Get-FslFlbIdentity {
    param([string]$Path, [bool]$Directory)
    return [FolderSessionLock.Stage4.FormalLauncherNative]::Read(
        $Path,
        $Directory)
}

function Test-FslFlbOrdinary {
    param([string]$Path, [bool]$Directory)
    try {
        $identity = Get-FslFlbIdentity $Path $Directory
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        return -not $identity.Reparse -and
            $identity.LinkCount -eq 1 -and
            $identity.RequestedPath -ceq $full -and
            [string]::Equals(
                $identity.FinalPath,
                $full,
                [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Get-FslFlbAclSddl {
    param([string]$Path, [bool]$Directory)
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $security = if ($Directory) {
        [IO.Directory]::GetAccessControl($Path, $sections)
    }
    else {
        [IO.File]::GetAccessControl($Path, $sections)
    }
    return $security.GetSecurityDescriptorSddlForm($sections)
}

function Test-FslFlbProtectedAcl {
    param(
        [string]$Path,
        [bool]$Directory,
        [string]$UserSid,
        [AllowNull()][string]$ExpectedSddl)
    try {
        $sections =
            [Security.AccessControl.AccessControlSections]::Owner -bor
            [Security.AccessControl.AccessControlSections]::Group -bor
            [Security.AccessControl.AccessControlSections]::Access
        $security = if ($Directory) {
            [IO.Directory]::GetAccessControl($Path, $sections)
        }
        else {
            [IO.File]::GetAccessControl($Path, $sections)
        }
        $owner = ([Security.Principal.NTAccount]$security.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
        if ($owner -cne $UserSid -or -not $security.AreAccessRulesProtected) {
            return $false
        }
        $rules = @($security.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        $principals = @('S-1-5-18', 'S-1-5-32-544', $UserSid)
        $inheritance = if ($Directory) {
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit
        }
        else { [Security.AccessControl.InheritanceFlags]::None }
        if ($rules.Count -ne 3) { return $false }
        for ($index = 0; $index -lt 3; $index++) {
            $rule = $rules[$index]
            if ($rule.IdentityReference.Value -cne $principals[$index] -or
                $rule.AccessControlType -ne
                    [Security.AccessControl.AccessControlType]::Allow -or
                $rule.FileSystemRights -ne
                    [Security.AccessControl.FileSystemRights]::FullControl -or
                $rule.InheritanceFlags -ne $inheritance -or
                $rule.PropagationFlags -ne
                    [Security.AccessControl.PropagationFlags]::None -or
                $rule.IsInherited) {
                return $false
            }
        }
        if (-not [string]::IsNullOrEmpty($ExpectedSddl) -and
            (Get-FslFlbAclSddl $Path $Directory) -cne $ExpectedSddl) {
            return $false
        }
        return $true
    }
    catch { return $false }
}

function Test-FslFlbFormalTokenProofDto {
    param([AllowNull()][psobject]$Proof)
    if ($null -eq $Proof -or
        -not (Test-FslFlbNames $Proof @(
            'machineName',
            'elevationType',
            'currentAccountSid',
            'linkedAccountSid',
            'currentSidType',
            'linkedSidType',
            'currentAdministratorsDenyOnly',
            'currentAdministratorsEnabled',
            'linkedAdministratorsDenyOnly',
            'linkedAdministratorsEnabled',
            'currentAccountDomain',
            'linkedAccountDomain')) -or
        $Proof.machineName -isnot [string] -or
        $Proof.elevationType -isnot [int] -or
        $Proof.currentAccountSid -isnot [string] -or
        $Proof.linkedAccountSid -isnot [string] -or
        $Proof.currentSidType -isnot [int] -or
        $Proof.linkedSidType -isnot [int] -or
        $Proof.currentAdministratorsDenyOnly -isnot [bool] -or
        $Proof.currentAdministratorsEnabled -isnot [bool] -or
        $Proof.linkedAdministratorsDenyOnly -isnot [bool] -or
        $Proof.linkedAdministratorsEnabled -isnot [bool] -or
        $Proof.currentAccountDomain -isnot [string] -or
        $Proof.linkedAccountDomain -isnot [string]) {
        return $false
    }
    return [string]$Proof.machineName -ceq 'FSL-STAGE4-VM' -and
        [int]$Proof.elevationType -eq 3 -and
        [string]$Proof.currentAccountSid -cmatch $script:FlbSidPattern -and
        [string]$Proof.currentAccountSid -ceq
            [string]$Proof.linkedAccountSid -and
        [int]$Proof.currentSidType -eq 1 -and
        [int]$Proof.linkedSidType -eq 1 -and
        [bool]$Proof.currentAdministratorsDenyOnly -and
        -not [bool]$Proof.currentAdministratorsEnabled -and
        -not [bool]$Proof.linkedAdministratorsDenyOnly -and
        [bool]$Proof.linkedAdministratorsEnabled -and
        [string]$Proof.currentAccountDomain -ceq
            [string]$Proof.machineName -and
        [string]$Proof.linkedAccountDomain -ceq
            [string]$Proof.machineName
}

function Set-FslFlbSddl {
    param([string]$Path, [string]$Sddl, [bool]$Directory)
    $security = if ($Directory) {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    else { [Security.AccessControl.FileSecurity]::new() }
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $security.SetSecurityDescriptorSddlForm($Sddl, $sections)
    if ($Directory) {
        [IO.Directory]::SetAccessControl($Path, $security)
    }
    else { [IO.File]::SetAccessControl($Path, $security) }
}

function ConvertTo-FslFlbFileSddl {
    param([string]$DirectorySddl)
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
        $DirectorySddl)
    $owner = $descriptor.Owner.Value
    $group = $descriptor.Group.Value
    return "O:$owner" + "G:$group" +
        "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;$owner)"
}

function Write-FslFlbNew {
    param([string]$Path, [byte[]]$Bytes)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

function Get-FslFlbRoots {
    param([psobject]$Model)
    if ([string]$Model.authorityProfile -ceq 'Formal') {
        $base = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) (
            'FolderSessionLock\Stage4\Recovery\' + [string]$Model.runId)
    }
    else {
        $base = Join-Path ([IO.Path]::GetTempPath()) (
            'FolderSessionLock.Tests\' +
            [string]$Model.rootBinding.fixtureId)
    }
    $base = [IO.Path]::GetFullPath($base).TrimEnd('\')
    $source = [IO.Path]::GetFullPath((
        Join-Path $base ([string]$Model.rootBinding.sourceLeafName))).TrimEnd('\')
    $bundle = [IO.Path]::GetFullPath((
        Join-Path $base ([string]$Model.rootBinding.bundleLeafName))).TrimEnd('\')
    $prefix = $base + '\'
    if (-not $source.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $bundle.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslFlb 'FSL-FLB-V002-ROOT' 'A resolved root escaped its authority base.' $null
    }
    return [pscustomobject][ordered]@{
        baseRoot = $base
        sourceRoot = $source
        bundleRoot = $bundle
    }
}

function Test-FslFlbExactSet {
    param([object[]]$Actual, [string[]]$Expected)
    if ($Actual.Count -ne $Expected.Count) { return $false }
    $set = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($item in $Actual) {
        if (-not $set.Add([string]$item)) { return $false }
    }
    foreach ($item in $Expected) {
        if (-not $set.Contains($item)) { return $false }
    }
    return $true
}

# Responsibility 4: process-free repository authority and recovery authority.
function Read-FslFlbUInt32Be {
    param([byte[]]$Bytes, [int]$Offset)
    return [uint32](
        ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3])
}

function Read-FslFlbLooseObject {
    param(
        [string]$GitDirectory,
        [string]$ObjectId,
        [string]$ExpectedType)
    $path = Join-Path $GitDirectory (
        'objects\' + $ObjectId.Substring(0, 2) + '\' +
        $ObjectId.Substring(2))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The required Git object is not a loose object; authority fails closed.' `
            $null
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 7) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'A Git object is invalid.' $null
    }
    if (-not [FolderSessionLock.Stage4.FormalLauncherNative]::
        ValidateZlibEnvelope($bytes)) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'A Git loose-object zlib envelope is invalid.' `
            $null
    }
    $input = [IO.MemoryStream]::new(
        $bytes,
        2,
        $bytes.Length - 6,
        $false)
    $deflate = [IO.Compression.DeflateStream]::new(
        $input,
        [IO.Compression.CompressionMode]::Decompress)
    $output = [IO.MemoryStream]::new()
    try {
        $deflate.CopyTo($output)
        $uncompressed = $output.ToArray()
        $a = [uint32]1
        $b = [uint32]0
        foreach ($value in $uncompressed) {
            $a = [uint32](($a + $value) % 65521)
            $b = [uint32](($b + $a) % 65521)
        }
        $adler = [uint32](($b -shl 16) -bor $a)
        $storedAdler = Read-FslFlbUInt32Be $bytes ($bytes.Length - 4)
        if ($adler -ne $storedAdler -or
            (Get-FslFlbSha1Bytes $uncompressed) -cne $ObjectId) {
            Stop-FslFlb `
                'FSL-FLB-V010-SOURCE-RECOVERY' `
                'A Git loose-object checksum drifted.' `
                $null
        }
        $nul = [Array]::IndexOf($uncompressed, [byte]0)
        if ($nul -le 0) {
            Stop-FslFlb `
                'FSL-FLB-V010-SOURCE-RECOVERY' `
                'A Git loose-object header is invalid.' `
                $null
        }
        $header = [Text.Encoding]::ASCII.GetString($uncompressed, 0, $nul)
        $match = [regex]::Match(
            $header,
            '^(?<type>commit|tree|blob) (?<length>0|[1-9][0-9]*)$')
        [int64]$declaredLength = 0
        if (-not $match.Success -or
            $match.Groups['type'].Value -cne $ExpectedType -or
            -not [int64]::TryParse(
                $match.Groups['length'].Value,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$declaredLength) -or
            $declaredLength -ne $uncompressed.Length - $nul - 1) {
            Stop-FslFlb `
                'FSL-FLB-V010-SOURCE-RECOVERY' `
                'A Git loose-object type or length drifted.' `
                $null
        }
        return $uncompressed
    }
    finally {
        $output.Dispose()
        $deflate.Dispose()
        $input.Dispose()
    }
}

function Get-FslFlbRepository {
    $projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $candidate = $projectRoot
    $gitRoot = $null
    while (-not [string]::IsNullOrEmpty($candidate)) {
        if (Test-Path -LiteralPath (Join-Path $candidate '.git') -PathType Container) {
            $gitRoot = $candidate
            break
        }
        $parent = Split-Path -Parent $candidate
        if ($parent -ceq $candidate) { break }
        $candidate = $parent
    }
    if ($null -eq $gitRoot) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Git root is unavailable.' $null
    }
    return Get-FslFlbRepositoryAtRoot $projectRoot $gitRoot
}

function Get-FslFlbRepositoryAtRoot {
    param([string]$ProjectRoot, [string]$GitRoot)
    $projectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    $gitRoot = [IO.Path]::GetFullPath($GitRoot).TrimEnd('\')
    if (-not (Test-FslFlbOrdinary $gitRoot $true) -or
        -not $projectRoot.StartsWith(
            $gitRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        $projectRoot -cne $gitRoot) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The Git/project roots are not ordinary and contained.' `
            $null
    }
    $gitDirectory = Join-Path $gitRoot '.git'
    if (-not (Test-FslFlbOrdinary $gitDirectory $true)) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The Git directory identity is unavailable.' `
            $null
    }
    $headText = [IO.File]::ReadAllText(
        (Join-Path $gitDirectory 'HEAD'),
        [Text.UTF8Encoding]::new($false, $true)).Trim()
    if (-not $headText.StartsWith('ref: refs/heads/', [StringComparison]::Ordinal)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'Detached Git HEAD is not allowed.' $null
    }
    $reference = $headText.Substring(5)
    $branch = $reference.Substring('refs/heads/'.Length)
    $referencePath = Join-Path $gitDirectory ($reference.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Git branch ref is unavailable.' $null
    }
    $head = [IO.File]::ReadAllText($referencePath).Trim()
    if ($head -cnotmatch $script:FlbGitPattern) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Git HEAD is invalid.' $null
    }
    $commitBytes = Read-FslFlbLooseObject $gitDirectory $head 'commit'
    $nul = [Array]::IndexOf($commitBytes, [byte]0)
    if ($nul -lt 0) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Git commit object is invalid.' $null
    }
    $commitText = [Text.UTF8Encoding]::new($false, $true).GetString(
        $commitBytes,
        $nul + 1,
        $commitBytes.Length - $nul - 1)
    $match = [regex]::Match($commitText, '^tree (?<tree>[0-9a-f]{40})\n')
    if (-not $match.Success) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Git tree is unavailable.' $null
    }
    $tree = $match.Groups['tree'].Value
    $trackedClean = Test-FslFlbIndexClean $gitRoot $gitDirectory $tree
    return [pscustomobject][ordered]@{
        projectRoot = $projectRoot
        gitRoot = $gitRoot
        gitDirectory = $gitDirectory
        branch = $branch
        head = $head
        tree = $tree
        trackedClean = $trackedClean
    }
}

function Test-FslFlbIndexClean {
    param(
        [string]$GitRoot,
        [string]$GitDirectory,
        [string]$ExpectedTree)
    return [FolderSessionLock.Stage4.FormalLauncherNative]::
        VerifyGitIndexAndTree($GitRoot, $GitDirectory, $ExpectedTree)
}

function Get-FslFlbFileRecord {
    param([string]$Path)
    $identity = Get-FslFlbIdentity $Path $false
    return [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath($Path)
        length = (Get-Item -LiteralPath $Path).Length
        sha256 = Get-FslFlbSha256 $Path
        finalPath = $identity.FinalPath
        fileId = $identity.Identity
        aclSddl = Get-FslFlbAclSddl $Path $false
    }
}

function Get-FslFlbDirectoryRecord {
    param([string]$Path)
    $identity = Get-FslFlbIdentity $Path $true
    return [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        finalPath = $identity.FinalPath
        fileId = $identity.Identity
        aclSddl = Get-FslFlbAclSddl $Path $true
        childCount = @(Get-ChildItem -LiteralPath $Path -Force).Count
    }
}

function Assert-FslFlbBoundFile {
    param([psobject]$Record, [bool]$RequireAcl)
    if (-not (Test-Path -LiteralPath ([string]$Record.path) -PathType Leaf) -or
        -not (Test-FslFlbOrdinary ([string]$Record.path) $false) -or
        (Get-Item -LiteralPath ([string]$Record.path)).Length -ne
            [int64]$Record.length -or
        (Get-FslFlbSha256 ([string]$Record.path)) -cne
            [string]$Record.sha256) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'A bound file drifted.' $null
    }
    if ($RequireAcl -and
        (Get-FslFlbAclSddl ([string]$Record.path) $false) -cne
            [string]$Record.aclSddl) {
        Stop-FslFlb 'FSL-FLB-V013-ACL' 'A bound file ACL drifted.' $null
    }
}

function Test-FslFlbRecoveryAuthorityV3 {
    param([psobject]$Model)
    $modulePath = Join-Path $PSScriptRoot (
        'FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The tracked recovery-authority validator is unavailable.' `
            $null
    }
    $validator = Import-Module $modulePath -Force -PassThru
    $recoveryModel = [pscustomobject][ordered]@{
        schemaVersion = 1
        authorityProfile = [string]$Model.authorityProfile
        contractId = [string]$Model.recoveryAuthority.contractId
        checkpoint =
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR'
        runId = [string]$Model.runId
        rootBinding = [pscustomobject][ordered]@{
            fixtureId = $Model.rootBinding.fixtureId
            sourceLeafName = [string]$Model.rootBinding.sourceLeafName
        }
    }
    $result = Test-FslStage4RecoveryAuthorityBundle -Model $recoveryModel
    if (-not [bool]$result.isValid -or
        $null -eq $result.opaqueAuthority) {
        $codes = @($result.errors | ForEach-Object code) -join ','
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            ('The tracked recovery-authority validator failed closed: ' +
                $codes + '.') `
            $null
    }
    return $result
}

function Resolve-FslFlbAuthority {
    param([psobject]$Model)
    $roots = Get-FslFlbRoots $Model
    if (-not (Test-Path -LiteralPath $roots.baseRoot -PathType Container) -or
        -not (Test-FslFlbOrdinary $roots.baseRoot $true) -or
        -not (Test-Path -LiteralPath $roots.sourceRoot -PathType Container) -or
        -not (Test-FslFlbOrdinary $roots.sourceRoot $true)) {
        Stop-FslFlb 'FSL-FLB-V002-ROOT' 'The internal authority roots are invalid.' $null
    }
    $sourceChildren = @(
        Get-ChildItem -LiteralPath $roots.sourceRoot -Force |
            ForEach-Object { $_.Name })
    if (-not (Test-FslFlbExactSet $sourceChildren $script:FlbSourceNames)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The source root is not exact-two.' $null
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $administrator = $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $tokenProof = $null
    if ([string]$Model.authorityProfile -ceq 'Formal') {
        try {
            $nativeProof =
                [FolderSessionLock.Stage4.FormalLauncherNative]::
                    ReadFormalTokenProof()
            $tokenProof = [pscustomobject][ordered]@{
                machineName = [string]$nativeProof.MachineName
                elevationType = [int]$nativeProof.ElevationType
                currentAccountSid = [string]$nativeProof.CurrentAccountSid
                linkedAccountSid = [string]$nativeProof.LinkedAccountSid
                currentSidType = [int]$nativeProof.CurrentSidType
                linkedSidType = [int]$nativeProof.LinkedSidType
                currentAdministratorsDenyOnly =
                    [bool]$nativeProof.CurrentAdministratorsDenyOnly
                currentAdministratorsEnabled =
                    [bool]$nativeProof.CurrentAdministratorsEnabled
                linkedAdministratorsDenyOnly =
                    [bool]$nativeProof.LinkedAdministratorsDenyOnly
                linkedAdministratorsEnabled =
                    [bool]$nativeProof.LinkedAdministratorsEnabled
                currentAccountDomain =
                    [string]$nativeProof.CurrentAccountDomain
                linkedAccountDomain =
                    [string]$nativeProof.LinkedAccountDomain
            }
        }
        catch {
            Stop-FslFlb `
                'FSL-FLB-V011-FORMAL-TOKEN' `
                'The native formal token proof is unavailable.' `
                $_.Exception
        }
        if (-not (Test-FslFlbFormalTokenProofDto $tokenProof)) {
            Stop-FslFlb `
                'FSL-FLB-V011-FORMAL-TOKEN' `
                'The native formal token proof failed closed.' `
                $null
        }
    }
    $sourceSddl = Get-FslFlbAclSddl $roots.sourceRoot $true
    if (-not (Test-FslFlbProtectedAcl `
        $roots.sourceRoot `
        $true `
        $sid `
        $sourceSddl)) {
        Stop-FslFlb 'FSL-FLB-V013-ACL' 'The source root ACL is invalid.' $null
    }
    $wrapperPath = Join-Path $roots.sourceRoot 'elevated-reconcile.ps1'
    $recoveryPath = Join-Path $roots.sourceRoot 'recovery-contract.json'
    foreach ($path in @($wrapperPath, $recoveryPath)) {
        if (-not (Test-FslFlbOrdinary $path $false) -or
            -not (Test-FslFlbProtectedAcl $path $false $sid $null)) {
            Stop-FslFlb 'FSL-FLB-V013-ACL' 'A source file identity or ACL is invalid.' $null
        }
    }
    $recoveryHash = Get-FslFlbSha256 $recoveryPath
    if ($recoveryHash -cne
            [string]$Model.recoveryAuthority.contractSha256) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The public recovery hash binding drifted.' $null
    }
    $validated = Test-FslFlbRecoveryAuthorityV3 $Model
    $opaque = $validated.opaqueAuthority
    if ([string]$opaque.contractId -cne
            [string]$Model.recoveryAuthority.contractId -or
        [string]$opaque.contractSha256 -cne $recoveryHash -or
        [string]$opaque.executionGitCommit -ceq
            [string]$opaque.recoveryGitCommit) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The opaque schema-v3 dual-authority binding failed.' `
            $null
    }
    $repository = [pscustomobject][ordered]@{
        projectRoot = [string]$opaque.recoveryRepository
        gitRoot = [string]$opaque.recoveryGitRoot
        gitDirectory = [string]$opaque.recoveryGitDirectory
        branch = [string]$opaque.recoveryBranch
        head = [string]$opaque.recoveryGitCommit
        tree = [string]$opaque.recoveryGitTree
        trackedClean = [bool]$opaque.recoveryTrackedClean
    }
    $directoryRecord = Get-FslFlbDirectoryRecord (
        [string]$opaque.systemPrestate.installDirectory)
    $transaction = $opaque.transaction |
        ConvertTo-Json -Depth 32 -Compress |
        ConvertFrom-Json
    $transaction | Add-Member `
        -NotePropertyName directory `
        -NotePropertyValue ([pscustomobject][ordered]@{
            path = $directoryRecord.path
            finalPath = $directoryRecord.finalPath
            fileId = $directoryRecord.fileId
            aclSddl = $directoryRecord.aclSddl
            ordinaryDirectory = $true
            nonReparse = $true
            childCount = $directoryRecord.childCount
        })
    $recovery = [pscustomobject][ordered]@{
        schemaVersion = 3
        contractId = [string]$opaque.contractId
        checkpoint = [string]$Model.checkpoint
        contractStageGates = @($opaque.gates)
        canonical = [pscustomobject][ordered]@{
            evidenceRoot = [string]$opaque.canonicalEvidence.root
            evidenceFiles = @(
                $opaque.executionEvidence.state
                $opaque.executionEvidence.journal
                $opaque.executionEvidence.installWal
                @($opaque.canonicalEvidence.files))
            externalAnchorRoot = [string]$opaque.externalAnchors.root
            externalAnchorFiles = @($opaque.externalAnchors.files)
        }
        transaction = $transaction
        release = $opaque.release
        futureInvocation = $opaque.futureInvocation
        binding = [pscustomobject][ordered]@{
            wrapperSha256 = [string]$opaque.wrapperSha256
        }
    }
    $gates = @($opaque.gates)
    if (-not (Test-FslFlbGateMapDto $gates)) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The recovery gate map drifted.' `
            $null
    }
    if ((Get-FslFlbSha256 $wrapperPath) -cne
        [string]$recovery.binding.wrapperSha256) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The wrapper binding drifted.' $null
    }
    foreach ($record in @($recovery.canonical.evidenceFiles) +
        @($recovery.canonical.externalAnchorFiles)) {
        Assert-FslFlbBoundFile $record $false
    }
    foreach ($root in @(
        [string]$recovery.canonical.evidenceRoot,
        [string]$recovery.canonical.externalAnchorRoot)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container) -or
            -not (Test-FslFlbOrdinary $root $true)) {
            Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'A canonical root drifted.' $null
        }
    }
    $evidenceActual = @(
        Get-ChildItem -LiteralPath ([string]$recovery.canonical.evidenceRoot) -File |
            ForEach-Object { $_.FullName })
    $evidenceExpected = @($recovery.canonical.evidenceFiles |
        ForEach-Object { [string]$_.path })
    if (-not (Test-FslFlbExactSet $evidenceActual $evidenceExpected)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The evidence exact set drifted.' $null
    }
    $anchorActual = @(
        Get-ChildItem -LiteralPath ([string]$recovery.canonical.externalAnchorRoot) -File |
            ForEach-Object { $_.FullName })
    $anchorExpected = @($recovery.canonical.externalAnchorFiles |
        ForEach-Object { [string]$_.path })
    if (-not (Test-FslFlbExactSet $anchorActual $anchorExpected)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The anchor exact set drifted.' $null
    }
    $releaseRoot = [string]$recovery.release.root
    $releaseFiles = @(Get-ChildItem -LiteralPath $releaseRoot -File -Force)
    foreach ($record in @($recovery.release.files)) {
        Assert-FslFlbBoundFile $record $false
    }
    $releaseExpected = @($recovery.release.files |
        ForEach-Object { [string]$_.path })
    if ($releaseFiles.Count -ne [int]$recovery.release.fileCount -or
        -not (Test-FslFlbExactSet (
            @($releaseFiles | ForEach-Object FullName)) $releaseExpected)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Release exact set drifted.' $null
    }
    if ((Get-FslFlbSha256 (
            Join-Path $releaseRoot 'release-descriptor.json')) -cne
            [string]$recovery.release.descriptorSha256 -or
        (Get-FslFlbSha256 (
            Join-Path $releaseRoot 'release-manifest.json')) -cne
            [string]$recovery.release.manifestSha256 -or
        (Get-FslFlbSha256 (
            Join-Path $releaseRoot 'SHA256SUMS.txt')) -cne
            [string]$recovery.release.sumsSha256) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The Release fingerprint drifted.' $null
    }
    $directory = $recovery.transaction.directory
    if (-not (Test-Path -LiteralPath ([string]$directory.path) -PathType Container) -or
        -not (Test-FslFlbOrdinary ([string]$directory.path) $true)) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The transaction directory drifted.' $null
    }
    $directoryIdentity = Get-FslFlbIdentity ([string]$directory.path) $true
    if ($directoryIdentity.FinalPath -cne [string]$directory.finalPath -or
        $directoryIdentity.Identity -cne [string]$directory.fileId -or
        (Get-FslFlbAclSddl ([string]$directory.path) $true) -cne
            [string]$directory.aclSddl -or
        @(Get-ChildItem -LiteralPath ([string]$directory.path) -Force).Count -ne
            [int]$directory.childCount) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The transaction directory binding drifted.' $null
    }
    $powerShell = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::System)) (
        'WindowsPowerShell\v1.0\powershell.exe')
    $expectedRecoveryArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $wrapperPath)
    if ([string]$recovery.futureInvocation.filePath -cne $powerShell -or
        (@($recovery.futureInvocation.arguments) -join [char]0) -cne
            ($expectedRecoveryArguments -join [char]0) -or
        [string]$recovery.futureInvocation.verb -cne 'RunAs' -or
        -not [bool]$recovery.futureInvocation.passThru -or
        -not [bool]$recovery.futureInvocation.wait -or
        [bool]$recovery.futureInvocation.redirectStandardOutput -or
        [bool]$recovery.futureInvocation.redirectStandardError) {
        Stop-FslFlb 'FSL-FLB-V010-SOURCE-RECOVERY' 'The recovery invocation drifted.' $null
    }
    $installDirectory = [string]$directory.path
    $programData = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonApplicationData)) (
        'FolderSessionLock')
    $serviceRegistry =
        'HKLM:\SYSTEM\CurrentControlSet\Services\FolderSessionLockRecovery'
    $productProcesses = @(
        Get-Process -Name (
            'FolderSessionLock.App',
            'FolderSessionLock.Broker',
            'FolderSessionLock.Recovery',
            'FolderSessionLock.Service') -ErrorAction SilentlyContinue)
    $service = Get-Service -Name 'FolderSessionLockRecovery' -ErrorAction SilentlyContinue
    $appInfo = Get-Service -Name 'AppInfo' -ErrorAction SilentlyContinue
    $enableLua = Get-ItemPropertyValue `
        -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
        -Name EnableLUA `
        -ErrorAction Stop
    $formalEligible =
        [string]$Model.authorityProfile -ceq 'Formal' -and
        $null -ne $tokenProof -and
        [Environment]::UserInteractive -and
        $repository.trackedClean
    $sourceRecords = @(
        Get-FslFlbFileRecord $wrapperPath
        Get-FslFlbFileRecord $recoveryPath)
    $evidenceRecords = @($recovery.canonical.evidenceFiles |
        ForEach-Object { Get-FslFlbFileRecord ([string]$_.path) })
    $anchorRecords = @($recovery.canonical.externalAnchorFiles |
        ForEach-Object { Get-FslFlbFileRecord ([string]$_.path) })
    $releaseRecords = @($releaseFiles | Sort-Object Name |
        ForEach-Object { Get-FslFlbFileRecord $_.FullName })
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        formalExecutionEligible = $formalEligible
        profile = [string]$Model.authorityProfile
        roots = $roots
        identity = [pscustomobject][ordered]@{
            machineName = [Environment]::MachineName
            userSid = $sid
            sessionId = $session
            isAdministrator = $administrator
            isInteractive = [Environment]::UserInteractive
            formalTokenProof = $tokenProof
        }
        repository = $repository
        executable = [pscustomobject][ordered]@{
            powerShellPath = $powerShell
            workingDirectory = (
                [Environment]::GetFolderPath(
                    [Environment+SpecialFolder]::System))
        }
        source = [pscustomobject][ordered]@{
            rootSddl = $sourceSddl
            fileSddl = Get-FslFlbAclSddl $wrapperPath $false
            files = $sourceRecords
            recoveryContractId = [string]$recovery.contractId
            recoveryContractSha256 = $recoveryHash
            recoveryGateMapSha256 = Get-FslFlbMapHash $gates
            executionStateAuthoritySha256 = if ($null -eq $opaque) {
                $null
            }
            else { [string]$opaque.executionStateAuthoritySha256 }
            recoveryToolchainAuthoritySha256 = if ($null -eq $opaque) {
                $null
            }
            else { [string]$opaque.recoveryToolchainAuthoritySha256 }
            toolchainRepositorySha256 = if ($null -eq $opaque) {
                $null
            }
            else { [string]$opaque.toolchainRepositorySha256 }
        }
        currentBindings = [pscustomobject][ordered]@{
            sourceRoot = Get-FslFlbDirectoryRecord $roots.sourceRoot
            evidenceRoot = Get-FslFlbDirectoryRecord (
                [string]$recovery.canonical.evidenceRoot)
            evidenceFiles = $evidenceRecords
            externalAnchorRoot = Get-FslFlbDirectoryRecord (
                [string]$recovery.canonical.externalAnchorRoot)
            externalAnchorFiles = $anchorRecords
            releaseRoot = Get-FslFlbDirectoryRecord $releaseRoot
            releaseFiles = $releaseRecords
            transactionDirectory = Get-FslFlbDirectoryRecord (
                [string]$directory.path)
        }
        canonical = $recovery.canonical
        release = $recovery.release
        transaction = $recovery.transaction
        recoveryInvocation = $recovery.futureInvocation
        systemState = [pscustomobject][ordered]@{
            installDirectory = $installDirectory
            programDataRoot = $programData
            programDataAbsent = -not (Test-Path -LiteralPath $programData)
            serviceRegistryPath = $serviceRegistry
            serviceRegistryAbsent = -not (Test-Path -LiteralPath $serviceRegistry)
            serviceAbsent = $null -eq $service
            productProcessCount = $productProcesses.Count
            enableLua = [int]$enableLua
            appInfoStatus = if ($null -eq $appInfo) {
                'Missing'
            }
            else { [string]$appInfo.Status }
        }
    }
}

# Responsibility 5: internal policy and deterministic renderers.
function ConvertTo-FslFlbWindowsArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append('\' * ($backslashes * 2 + 1))
            [void]$builder.Append('"')
        }
        else {
            if ($backslashes -gt 0) {
                [void]$builder.Append('\' * $backslashes)
            }
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append('\' * ($backslashes * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Join-FslFlbWindowsArgumentLine {
    param([string[]]$Arguments)
    return @($Arguments | ForEach-Object {
        ConvertTo-FslFlbWindowsArgument $_
    }) -join ' '
}

function Get-FslFlbPolicy {
    param([psobject]$Model, [psobject]$Authority)
    $bundle = $Authority.roots.bundleRoot
    $observer = Join-Path $bundle 'launch-observer.ps1'
    $outer = Join-Path $bundle 'outer-launcher.ps1'
    $contract = Join-Path $bundle 'launch-observer-contract.json'
    $latch = Join-Path $bundle 'launch-attempt.jsonl'
    $powerShell = $Authority.executable.powerShellPath
    $commandLine = '"' + $powerShell +
        '" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
        $observer + '"'
    $recoveryArgumentLine = Join-FslFlbWindowsArgumentLine (
        [string[]]@($Authority.recoveryInvocation.arguments))
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        files = [pscustomobject][ordered]@{
            outerLauncherPath = $outer
            observerPath = $observer
            contractPath = $contract
        }
        outerInvocation = [pscustomobject][ordered]@{
            filePath = $powerShell
            arguments = @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-ExecutionPolicy',
                'Bypass',
                '-File',
                $observer)
            windowStyle = 'Hidden'
            passThru = $true
            wait = $false
            redirectStandardOutput = $false
            redirectStandardError = $false
        }
        recoveryRunAs = [pscustomobject][ordered]@{
            applicationName = $powerShell
            argumentLine = $recoveryArgumentLine
            verb = 'RunAs'
            passThru = $true
            wait = $true
        }
        nativeOuterLaunch = [pscustomobject][ordered]@{
            primitive = 'CreateProcessW'
            launcherPath = $outer
            applicationName = $powerShell
            commandLine = $commandLine
            workingDirectory = $Authority.executable.workingDirectory
            creationFlags = @(
                'CREATE_BREAKAWAY_FROM_JOB',
                'CREATE_NO_WINDOW')
            numericCreationFlags = '0x09000000'
            inheritHandles = $false
            currentUser = $true
            requireNonElevated = $true
            requireInteractive = $true
            requiredUserSid = $Authority.identity.userSid
            requiredSessionId = $Authority.identity.sessionId
            windowStyle = 'Hidden'
            noWindow = $true
            wait = $false
            fallbackAllowed = $false
        }
        latch = [pscustomobject][ordered]@{
            path = $latch
            schemaVersion = 1
            fileMode = 'CreateNew'
            fileAccess = 'Write'
            fileShare = 'Read'
            fileOptions = 'WriteThrough'
            encoding = 'UTF-8 without BOM'
            records = @(
                'LaunchCommitted',
                'RunAsInvoking',
                'LaunchResult')
        }
        resultSchema = [pscustomobject][ordered]@{
            schemaVersion = 1
            recordCount = 3
            fieldOrder = $script:FlbResultFieldOrder
            temporalFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
            temporalRelation =
                'record1.timestampUtc <= record2.timestampUtc <= record3.timestampUtc'
            preAppendReadOnly = $true
            recoveryGateMappingSchemaVersion = 3
            recoveryGateMappingCount = 56
        }
        exitCodes = [pscustomobject]$script:FlbExitCodes
        allowedWrites = $script:FlbAllowedWrites
        forbiddenActions = $script:FlbForbiddenActions
    }
}

function ConvertTo-FslFlbLiteral {
    param([string]$Value)
    if ($Value.Contains("`r") -or $Value.Contains("`n")) {
        Stop-FslFlb 'FSL-FLB-V001-MODEL' 'A rendered literal contains a newline.' $null
    }
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-FslFlbPredicateTexts {
    return @(
        '[string]$contract.contractId -ceq $fixedContractId',
        '[string]$contract.attemptId -ceq $fixedAttemptId',
        '[string]$contract.authority.identity.userSid -ceq $fixedSid',
        '[int]$contract.authority.identity.sessionId -eq $fixedSessionId',
        '[string]$contract.bindingManifest.observer.sha256 -ceq (Get-Hash $fixedObserverPath)',
        '[string]$contract.bindingManifest.outerLauncher.sha256 -ceq (Get-Hash $fixedLauncherPath)',
        '[string]$contract.policy.nativeOuterLaunch.primitive -ceq ''CreateProcessW''',
        '[string]$contract.policy.nativeOuterLaunch.applicationName -ceq $fixedPowerShell',
        '[string]$contract.policy.nativeOuterLaunch.commandLine -ceq $fixedCommandLine',
        '[string]$contract.policy.nativeOuterLaunch.workingDirectory -ceq $fixedWorkingDirectory',
        '[string]$contract.policy.nativeOuterLaunch.numericCreationFlags -ceq ''0x09000000''',
        '-not [bool]$contract.policy.nativeOuterLaunch.inheritHandles',
        '[bool]$contract.policy.nativeOuterLaunch.currentUser',
        '[bool]$contract.policy.nativeOuterLaunch.requireNonElevated',
        '[bool]$contract.policy.nativeOuterLaunch.requireInteractive',
        '[string]$contract.policy.nativeOuterLaunch.requiredUserSid -ceq $fixedSid',
        '[int]$contract.policy.nativeOuterLaunch.requiredSessionId -eq $fixedSessionId',
        '[string]$contract.policy.nativeOuterLaunch.windowStyle -ceq ''Hidden''',
        '[bool]$contract.policy.nativeOuterLaunch.noWindow',
        '-not [bool]$contract.policy.nativeOuterLaunch.wait',
        '-not [bool]$contract.policy.nativeOuterLaunch.fallbackAllowed',
        'Test-ExactCreationFlags $contract.policy.nativeOuterLaunch.creationFlags')
}

function Test-FslFlbGateMapDto {
    param([object[]]$Gates)
    if ($Gates.Count -ne 56) { return $false }
    $gateIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $exitCodes = [Collections.Generic.HashSet[int]]::new()
    for ($index = 0; $index -lt 56; $index++) {
        $gate = $Gates[$index]
        if ($null -eq $gate -or
            -not (Test-FslFlbNames $gate @('gateId', 'exitCode')) -or
            $gate.gateId -isnot [string] -or
            $gate.exitCode -isnot [int] -or
            -not ([string]$gate.gateId).StartsWith(
                ('FSL-RAB-CG-{0:D3}-' -f ($index + 1)),
                [StringComparison]::Ordinal) -or
            [int]$gate.exitCode -ne 84 + $index -or
            -not $gateIds.Add([string]$gate.gateId) -or
            -not $exitCodes.Add([int]$gate.exitCode)) {
            return $false
        }
    }
    return $true
}

function Resolve-FslFlbGateId {
    param([int]$ExitCode, [object[]]$Gates)
    if ($ExitCode -eq 0) { return $null }
    $matches = @($Gates | Where-Object {
        $_.exitCode -is [int] -and [int]$_.exitCode -eq $ExitCode
    })
    if ($matches.Count -eq 0) { return $null }
    if ($matches.Count -ne 1) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The recovery exit-code map is ambiguous.' `
            $null
    }
    return [string]$matches[0].gateId
}

function New-FslFlbTerminalDto {
    param(
        [string]$Outcome,
        [AllowNull()][object]$TargetPid,
        [AllowNull()][object]$ExitCode,
        [object[]]$Gates)
    if ($Outcome -cin @('UacCancelled', 'LaunchFailed')) {
        return [pscustomobject][ordered]@{
            outcome = $Outcome
            targetPid = $null
            exitCode = $null
            gateId = $null
        }
    }
    if ($Outcome -cne 'Exited' -or
        $TargetPid -isnot [int] -or
        [int]$TargetPid -le 0 -or
        $ExitCode -isnot [int]) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The terminal result DTO is invalid.' `
            $null
    }
    return [pscustomobject][ordered]@{
        outcome = 'Exited'
        targetPid = [int]$TargetPid
        exitCode = [int]$ExitCode
        gateId = Resolve-FslFlbGateId ([int]$ExitCode) $Gates
    }
}

function Render-FslFlbOuter {
    param([psobject]$Model, [psobject]$Authority, [psobject]$Policy)
    $predicates = @(Get-FslFlbPredicateTexts)
    $predicateText = ''
    for ($index = 0; $index -lt $predicates.Count; $index++) {
        $predicateText += '    (' + $predicates[$index] + ')' +
            $(if ($index -lt $predicates.Count - 1) { ' -and' } else { '' }) +
            "`n"
    }
    $template = @'
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixedContractPath = @@CONTRACT@@
$fixedObserverPath = @@OBSERVER@@
$fixedLauncherPath = @@OUTER@@
$fixedPowerShell = @@POWERSHELL@@
$fixedWorkingDirectory = @@WORKING@@
$fixedContractId = @@CONTRACT_ID@@
$fixedAttemptId = @@ATTEMPT_ID@@
$fixedSid = @@SID@@
$fixedSessionId = @@SESSION@@
$fixedCommandLine = @@COMMAND@@
function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Test-ExactCreationFlags([AllowNull()][object]$Value) {
    $values = @($Value)
    return (
        $values.Count -eq 2 -and
        $values[0] -is [string] -and
        [string]$values[0] -ceq 'CREATE_BREAKAWAY_FROM_JOB' -and
        $values[1] -is [string] -and
        [string]$values[1] -ceq 'CREATE_NO_WINDOW')
}
try {
    if ($PSBoundParameters.Count -ne 0 -or $args.Count -ne 0) { exit 64 }
    $contract = [IO.File]::ReadAllText($fixedContractPath) | ConvertFrom-Json
    if (-not [bool]$contract.formalExecutionEligible) { exit 64 }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (
        $identity.User.Value -cne $fixedSid -or
        $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator) -or
        [Diagnostics.Process]::GetCurrentProcess().SessionId -ne
            $fixedSessionId -or
        -not [Environment]::UserInteractive) {
        exit 66
    }
    $contractValid =
@@PREDICATES@@
    if (-not $contractValid) { exit 68 }
    if (Test-Path -LiteralPath $contract.policy.latch.path) { exit 65 }
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FslFormalNativeLauncher {
  public const uint CREATE_BREAKAWAY_FROM_JOB=0x01000000;
  public const uint CREATE_NO_WINDOW=0x08000000;
  public const uint FSL_CREATION_FLAGS=
    CREATE_BREAKAWAY_FROM_JOB|CREATE_NO_WINDOW;
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct STARTUPINFO {
    public int cb; public string lpReserved, lpDesktop, lpTitle;
    public uint dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars;
    public uint dwFillAttribute,dwFlags; public short wShowWindow,cbReserved2;
    public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct PROCESS_INFORMATION {
    public IntPtr hProcess,hThread; public uint dwProcessId,dwThreadId;
  }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool CreateProcessW(
    string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,
    uint flags,IntPtr environment,string directory,ref STARTUPINFO startup,
    out PROCESS_INFORMATION process);
  [DllImport("kernel32.dll",SetLastError=true)]
  public static extern bool CloseHandle(IntPtr handle);
}
"@
    $startup = New-Object FslFormalNativeLauncher+STARTUPINFO
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
    $startup.dwFlags = 1
    $startup.wShowWindow = 0
    $process = New-Object FslFormalNativeLauncher+PROCESS_INFORMATION
    $mutableCommandLine = [Text.StringBuilder]::new($fixedCommandLine)
    $created = [FslFormalNativeLauncher]::CreateProcessW(
        $fixedPowerShell,$mutableCommandLine,[IntPtr]::Zero,[IntPtr]::Zero,
        $false,[FslFormalNativeLauncher]::FSL_CREATION_FLAGS,
        [IntPtr]::Zero,$fixedWorkingDirectory,
        [ref]$startup,[ref]$process)
    if (-not $created) { exit 69 }
    [void][FslFormalNativeLauncher]::CloseHandle($process.hThread)
    [void][FslFormalNativeLauncher]::CloseHandle($process.hProcess)
    [pscustomobject][ordered]@{ processId=[int]$process.dwProcessId }
    exit 0
}
catch { exit 70 }
'@
    $text = $template.
        Replace('@@CONTRACT@@', (ConvertTo-FslFlbLiteral $Policy.files.contractPath)).
        Replace('@@OBSERVER@@', (ConvertTo-FslFlbLiteral $Policy.files.observerPath)).
        Replace('@@OUTER@@', (ConvertTo-FslFlbLiteral $Policy.files.outerLauncherPath)).
        Replace('@@POWERSHELL@@', (ConvertTo-FslFlbLiteral $Authority.executable.powerShellPath)).
        Replace('@@WORKING@@', (ConvertTo-FslFlbLiteral $Authority.executable.workingDirectory)).
        Replace('@@CONTRACT_ID@@', (ConvertTo-FslFlbLiteral ([string]$Model.contractId))).
        Replace('@@ATTEMPT_ID@@', (ConvertTo-FslFlbLiteral ([string]$Model.attemptId))).
        Replace('@@SID@@', (ConvertTo-FslFlbLiteral $Authority.identity.userSid)).
        Replace('@@SESSION@@', ([string][int]$Authority.identity.sessionId)).
        Replace('@@COMMAND@@', (ConvertTo-FslFlbLiteral $Policy.nativeOuterLaunch.commandLine)).
        Replace('@@PREDICATES@@', $predicateText.TrimEnd("`n"))
    return $text.Replace("`r`n", "`n").TrimEnd("`r", "`n") + "`n"
}

function Render-FslFlbObserver {
    param([psobject]$Model, [psobject]$Authority, [psobject]$Policy)
    # The generated script is intentionally self-contained. Its single
    # Assert-FormalPreLatch call precedes the first writable handle and RunAs.
    $template = @'
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixedContractPath = @@CONTRACT@@
$fixedObserverPath = @@OBSERVER@@
$fixedOuterPath = @@OUTER@@
$fixedRecoveryPath = @@RECOVERY@@
$fixedWrapperPath = @@WRAPPER@@
$fixedRecoveryValidatorPath = @@RECOVERY_VALIDATOR@@
$fixedRecoveryAuthorityProfile = @@RECOVERY_PROFILE@@
$fixedRecoveryAuthorityContractId = @@RECOVERY_CONTRACT_ID@@
$fixedRecoveryFixtureId = @@RECOVERY_FIXTURE_ID@@
$fixedRecoverySourceLeaf = @@RECOVERY_SOURCE_LEAF@@
$fixedRepository = @@REPOSITORY@@
$fixedGitRoot = @@GITROOT@@
$fixedGitDirectory = @@GITDIR@@
$fixedGitBranch = @@GITBRANCH@@
$fixedGitHead = @@GITHEAD@@
$fixedGitTree = @@GITTREE@@
$fixedTrackedClean = @@TRACKEDCLEAN@@
$fixedPowerShell = @@POWERSHELL@@
$fixedWorkingDirectory = @@WORKING@@
$fixedCommandLine = @@COMMAND@@
$fixedRecoveryArgumentLine = @@RECOVERY_ARGUMENT_LINE@@
$fixedSid = @@SID@@
$fixedSession = @@SESSION@@
$fixedRunId = @@RUNID@@
$fixedContractId = @@CONTRACT_ID@@
$fixedAttemptId = @@ATTEMPT_ID@@
$fixedCheckpoint = @@CHECKPOINT@@

function Initialize-NativeIdentity {
  if(-not('FslFormalObserverIdentity'-as[type])){
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public sealed class FslFormalObserverTokenProof {
  public string MachineName,CurrentAccountSid,LinkedAccountSid;
  public string CurrentAccountDomain,LinkedAccountDomain;
  public int ElevationType,CurrentSidType,LinkedSidType;
  public bool CurrentAdministratorsDenyOnly,CurrentAdministratorsEnabled;
  public bool LinkedAdministratorsDenyOnly,LinkedAdministratorsEnabled;
}
public sealed class FslFormalObserverIdentity {
  const uint READ=0x80,SR=1,SW=2,SD=4,OPEN=3,REPARSE=0x00200000,
    DIRECTORY=0x02000000;
  public string FinalPath; public string FileId; public uint Links;
  public bool Reparse;
  public static string[] ParseWindowsCommandLine(string commandLine) {
    if(commandLine==null)throw new ArgumentNullException("commandLine");
    int count;IntPtr vector=CommandLineToArgvW(commandLine,out count);
    if(vector==IntPtr.Zero)
      throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
      if(count<1||count>65536)
        throw new InvalidOperationException("Invalid argv count.");
      string[] result=new string[count];
      for(int i=0;i<count;i++) {
        IntPtr value=Marshal.ReadIntPtr(vector,checked(i*IntPtr.Size));
        if(value==IntPtr.Zero)
          throw new InvalidOperationException("Null argv entry.");
        result[i]=Marshal.PtrToStringUni(value);
        if(result[i]==null)
          throw new InvalidOperationException("Invalid argv text.");
      }
      return result;
    } finally {LocalFree(vector);}
  }
  public static bool ValidateZlibEnvelope(byte[] bytes) {
    try {
      if(bytes==null||bytes.Length<7)return false;
      int cmf=bytes[0],flg=bytes[1];
      if((cmf&15)!=8||(cmf>>4)>7||(((cmf<<8)|flg)%31)!=0||
         (flg&32)!=0)return false;
      ObserverBits bits=new ObserverBits(bytes,2,bytes.Length-4);
      long output=0;bool final;
      do {
        final=bits.Read(1)!=0;int kind=bits.Read(2);
        if(kind==0) {
          bits.AlignZero();
          int length=bits.ReadByte()|(bits.ReadByte()<<8);
          int complement=bits.ReadByte()|(bits.ReadByte()<<8);
          if(((length^0xFFFF)&0xFFFF)!=complement)return false;
          bits.SkipBytes(length);output=checked(output+length);
        } else if(kind==1||kind==2) {
          ObserverHuffman literal,distance;
          if(kind==1) {
            int[] ll=new int[288];
            for(int i=0;i<=143;i++)ll[i]=8;
            for(int i=144;i<=255;i++)ll[i]=9;
            for(int i=256;i<=279;i++)ll[i]=7;
            for(int i=280;i<=287;i++)ll[i]=8;
            int[] dl=new int[32];for(int i=0;i<32;i++)dl[i]=5;
            literal=new ObserverHuffman(ll);
            distance=new ObserverHuffman(dl);
          } else ReadDynamicTrees(bits,out literal,out distance);
          ScanCompressed(bits,literal,distance,ref output);
        } else return false;
        if(output>268435456)return false;
      }while(!final);
      bits.Finish();uint a=1,b=0;long decompressed=0;
      using(var input=new System.IO.MemoryStream(
        bytes,2,bytes.Length-6,false))
      using(var deflate=new System.IO.Compression.DeflateStream(
        input,System.IO.Compression.CompressionMode.Decompress)) {
        byte[] buffer=new byte[8192];int count;
        while((count=deflate.Read(buffer,0,buffer.Length))!=0) {
          decompressed=checked(decompressed+count);
          if(decompressed>268435456)return false;
          for(int i=0;i<count;i++) {
            a=(a+buffer[i])%65521;b=(b+a)%65521;
          }
        }
      }
      uint stored=((uint)bytes[bytes.Length-4]<<24)|
        ((uint)bytes[bytes.Length-3]<<16)|
        ((uint)bytes[bytes.Length-2]<<8)|bytes[bytes.Length-1];
      return((b<<16)|a)==stored;
    }catch{return false;}
  }
  static void ReadDynamicTrees(
    ObserverBits bits,out ObserverHuffman literal,
    out ObserverHuffman distance) {
    int lc=bits.Read(5)+257,dc=bits.Read(5)+1,cc=bits.Read(4)+4;
    if(lc>286||dc>32)throw new InvalidOperationException();
    int[] order={16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15};
    int[] codeLengths=new int[19];
    for(int i=0;i<cc;i++)codeLengths[order[i]]=bits.Read(3);
    ObserverHuffman codeTree=new ObserverHuffman(codeLengths);
    int total=lc+dc,offset=0;int[] lengths=new int[total];
    while(offset<total) {
      int symbol=codeTree.Decode(bits);
      if(symbol<=15)lengths[offset++]=symbol;
      else {
        int repeat,value;
        if(symbol==16) {
          if(offset==0)throw new InvalidOperationException();
          repeat=bits.Read(2)+3;value=lengths[offset-1];
        } else if(symbol==17) {repeat=bits.Read(3)+3;value=0;}
        else if(symbol==18) {repeat=bits.Read(7)+11;value=0;}
        else throw new InvalidOperationException();
        if(offset+repeat>total)throw new InvalidOperationException();
        while(repeat-->0)lengths[offset++]=value;
      }
    }
    int[] literals=new int[lc],distances=new int[dc];
    Array.Copy(lengths,0,literals,0,lc);
    Array.Copy(lengths,lc,distances,0,dc);
    if(literals[256]==0)throw new InvalidOperationException();
    literal=new ObserverHuffman(literals);
    distance=new ObserverHuffman(distances);
  }
  static void ScanCompressed(
    ObserverBits bits,ObserverHuffman literal,ObserverHuffman distance,
    ref long output) {
    int[] lb={3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,
      59,67,83,99,115,131,163,195,227,258};
    int[] le={0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,
      4,5,5,5,5,0};
    int[] db={1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,
      513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577};
    int[] de={0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,
      10,11,11,12,12,13,13};
    while(true) {
      int symbol=literal.Decode(bits);
      if(symbol<256)output=checked(output+1);
      else if(symbol==256)return;
      else {
        if(symbol<257||symbol>285)throw new InvalidOperationException();
        int i=symbol-257,length=lb[i]+bits.Read(le[i]);
        int ds=distance.Decode(bits);
        if(ds<0||ds>29)throw new InvalidOperationException();
        int dv=db[ds]+bits.Read(de[ds]);
        if(dv>output)throw new InvalidOperationException();
        output=checked(output+length);
      }
      if(output>268435456)throw new InvalidOperationException();
    }
  }
  sealed class ObserverBits {
    readonly byte[] bytes;readonly int endBit;int bit;
    internal ObserverBits(byte[] value,int startByte,int endByte) {
      bytes=value;bit=checked(startByte*8);endBit=checked(endByte*8);
      if(startByte<0||endByte<startByte||endByte>value.Length)
        throw new InvalidOperationException();
    }
    internal int Read(int count) {
      if(count<0||count>16||bit+count>endBit)
        throw new InvalidOperationException();
      int value=0;
      for(int i=0;i<count;i++,bit++)
        value|=((bytes[bit>>3]>>(bit&7))&1)<<i;
      return value;
    }
    internal int ReadByte() {
      if((bit&7)!=0)throw new InvalidOperationException();return Read(8);
    }
    internal void AlignZero() {
      while((bit&7)!=0)if(Read(1)!=0)throw new InvalidOperationException();
    }
    internal void SkipBytes(int count) {
      if((bit&7)!=0||count<0||bit+checked(count*8)>endBit)
        throw new InvalidOperationException();bit+=count*8;
    }
    internal void Finish() {
      AlignZero();if(bit!=endBit)throw new InvalidOperationException();
    }
  }
  sealed class ObserverHuffman {
    readonly System.Collections.Generic.Dictionary<long,int> symbols=
      new System.Collections.Generic.Dictionary<long,int>();
    readonly int maximum;
    internal ObserverHuffman(int[] lengths) {
      int[] counts=new int[16];
      foreach(int length in lengths) {
        if(length<0||length>15)throw new InvalidOperationException();
        if(length!=0)counts[length]++;
      }
      int code=0;int[] next=new int[16];
      for(int length=1;length<=15;length++) {
        code=(code+counts[length-1])<<1;
        if(code+counts[length]>(1<<length))
          throw new InvalidOperationException();
        next[length]=code;if(counts[length]!=0)maximum=length;
      }
      if(maximum==0)throw new InvalidOperationException();
      for(int symbol=0;symbol<lengths.Length;symbol++) {
        int length=lengths[symbol];if(length==0)continue;
        long key=((long)length<<32)|(uint)next[length]++;
        if(symbols.ContainsKey(key))throw new InvalidOperationException();
        symbols.Add(key,symbol);
      }
    }
    internal int Decode(ObserverBits bits) {
      int code=0;
      for(int length=1;length<=maximum;length++) {
        code=(code<<1)|bits.Read(1);int symbol;
        if(symbols.TryGetValue(((long)length<<32)|(uint)code,out symbol))
          return symbol;
      }
      throw new InvalidOperationException();
    }
  }
  public static FslFormalObserverIdentity Read(string path,bool directory) {
    string full=System.IO.Path.GetFullPath(path).TrimEnd('\\');
    using(SafeFileHandle h=CreateFile(full,READ,SR|SW|SD,IntPtr.Zero,OPEN,
      REPARSE|(directory?DIRECTORY:0),IntPtr.Zero)) {
      if(h.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
      Info i;if(!GetFileInformationByHandle(h,out i))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      StringBuilder b=new StringBuilder(32768);
      uint n=GetFinalPathNameByHandle(h,b,b.Capacity,0);
      if(n==0||n>=b.Capacity)
        throw new Win32Exception(Marshal.GetLastWin32Error());
      string f=b.ToString();if(f.StartsWith(@"\\?\",StringComparison.Ordinal))
        f=f.Substring(4);
      ulong x=((ulong)i.FileIndexHigh<<32)|i.FileIndexLow;
      return new FslFormalObserverIdentity {
        FinalPath=f.TrimEnd('\\'),
        FileId=i.VolumeSerialNumber.ToString("X8")+x.ToString("X16"),
        Links=i.NumberOfLinks,Reparse=(i.FileAttributes&0x400)!=0};
    }
  }
  public static FslFormalObserverTokenProof ReadTokenProof() {
    IntPtr current=IntPtr.Zero,linked=IntPtr.Zero;
    if(!OpenProcessToken(GetCurrentProcess(),0x0008,out current))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
      int elevation=ReadInt(current,18);linked=ReadLinked(current);
      string currentSid,currentDomain,linkedSid,linkedDomain;
      int currentType,linkedType;
      ReadIdentity(current,out currentSid,out currentDomain,out currentType);
      ReadIdentity(linked,out linkedSid,out linkedDomain,out linkedType);
      ObserverGroupState currentGroup=ReadAdministratorsGroup(current);
      ObserverGroupState linkedGroup=ReadAdministratorsGroup(linked);
      return new FslFormalObserverTokenProof {
        MachineName=Environment.MachineName,ElevationType=elevation,
        CurrentAccountSid=currentSid,LinkedAccountSid=linkedSid,
        CurrentSidType=currentType,LinkedSidType=linkedType,
        CurrentAdministratorsDenyOnly=currentGroup.DenyOnly,
        CurrentAdministratorsEnabled=currentGroup.Enabled,
        LinkedAdministratorsDenyOnly=linkedGroup.DenyOnly,
        LinkedAdministratorsEnabled=linkedGroup.Enabled,
        CurrentAccountDomain=currentDomain,LinkedAccountDomain=linkedDomain};
    } finally {
      if(linked!=IntPtr.Zero)CloseHandle(linked);
      if(current!=IntPtr.Zero)CloseHandle(current);
    }
  }
  static int ReadInt(IntPtr token,int kind) {
    IntPtr b=Marshal.AllocHGlobal(4);
    try {int n;if(!GetTokenInformation(token,kind,b,4,out n)||n!=4)
      throw new Win32Exception(Marshal.GetLastWin32Error());
      return Marshal.ReadInt32(b);}finally{Marshal.FreeHGlobal(b);}
  }
  static IntPtr ReadLinked(IntPtr token) {
    IntPtr b=Marshal.AllocHGlobal(IntPtr.Size);
    try {int n;if(!GetTokenInformation(token,19,b,IntPtr.Size,out n)||
      n!=IntPtr.Size)throw new Win32Exception(Marshal.GetLastWin32Error());
      return Marshal.ReadIntPtr(b);}finally{Marshal.FreeHGlobal(b);}
  }
  static void ReadIdentity(
    IntPtr token,out string value,out string domainValue,out int sidType) {
    int size=0;GetTokenInformation(token,1,IntPtr.Zero,0,out size);
    if(size<=0)throw new Win32Exception(Marshal.GetLastWin32Error());
    IntPtr b=Marshal.AllocHGlobal(size);
    try {int n;if(!GetTokenInformation(token,1,b,size,out n))
      throw new Win32Exception(Marshal.GetLastWin32Error());
      IntPtr sid=Marshal.ReadIntPtr(b);
      value=new System.Security.Principal.SecurityIdentifier(sid).Value;
      uint nl=0,dl=0;int use;
      LookupAccountSid(null,sid,null,ref nl,null,ref dl,out use);
      if(nl==0||dl==0)throw new Win32Exception(Marshal.GetLastWin32Error());
      StringBuilder name=new StringBuilder((int)nl);
      StringBuilder domain=new StringBuilder((int)dl);
      if(!LookupAccountSid(null,sid,name,ref nl,domain,ref dl,out use))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      domainValue=domain.ToString();sidType=use;
    }finally{Marshal.FreeHGlobal(b);}
  }
  sealed class ObserverGroupState {internal bool DenyOnly,Enabled;}
  [StructLayout(LayoutKind.Sequential)]struct ObserverSidAndAttributes {
    internal IntPtr Sid;internal uint Attributes;
  }
  [StructLayout(LayoutKind.Sequential)]struct ObserverTokenGroupsLayout {
    internal uint Count;internal ObserverSidAndAttributes First;
  }
  static ObserverGroupState ReadAdministratorsGroup(IntPtr token) {
    var administrators=
      new System.Security.Principal.SecurityIdentifier("S-1-5-32-544");
    byte[] sidBytes=new byte[administrators.BinaryLength];
    administrators.GetBinaryForm(sidBytes,0);
    IntPtr adminSid=Marshal.AllocHGlobal(sidBytes.Length);
    int size=0;GetTokenInformation(token,2,IntPtr.Zero,0,out size);
    if(size<=0)throw new Win32Exception(Marshal.GetLastWin32Error());
    IntPtr b=Marshal.AllocHGlobal(size);
    try {
      Marshal.Copy(sidBytes,0,adminSid,sidBytes.Length);
      int returned;if(!GetTokenInformation(token,2,b,size,out returned)||
        returned<4||returned>size)
        throw new Win32Exception(Marshal.GetLastWin32Error());
      uint count=unchecked((uint)Marshal.ReadInt32(b));
      int offset=checked((int)Marshal.OffsetOf(
        typeof(ObserverTokenGroupsLayout),"First"));
      int stride=Marshal.SizeOf(typeof(ObserverSidAndAttributes));
      if(offset<4||stride<IntPtr.Size+4||count>65536||
         checked(offset+checked((int)count*stride))>returned)
        throw new InvalidOperationException("TOKEN_GROUPS bounds invalid.");
      int matches=0;uint attributes=0;
      for(int i=0;i<(int)count;i++) {
        var entry=(ObserverSidAndAttributes)Marshal.PtrToStructure(
          IntPtr.Add(b,checked(offset+i*stride)),
          typeof(ObserverSidAndAttributes));
        if(entry.Sid==IntPtr.Zero)
          throw new InvalidOperationException("Null group SID.");
        if(EqualSid(entry.Sid,adminSid)){matches++;attributes=entry.Attributes;}
      }
      if(matches!=1)
        throw new InvalidOperationException("Administrators SID count invalid.");
      return new ObserverGroupState {
        Enabled=(attributes&0x4)!=0,DenyOnly=(attributes&0x10)!=0};
    }finally{Marshal.FreeHGlobal(b);Marshal.FreeHGlobal(adminSid);}
  }
  sealed class ObserverConversionSource {
    internal string Path,Text,NativeFileIdentity;internal bool Exists;
    internal long Length,CreationTicks,WriteTicks;
    internal int Attributes;internal byte[] Sha256;
  }
  sealed class ObserverConversionProfile {
    internal bool AutoCrlf;internal string Fingerprint;
  }
  static ObserverConversionSource CaptureConversionSource(string path) {
    string full=System.IO.Path.GetFullPath(path);
    if(!System.IO.File.Exists(full)) {
      if(System.IO.Directory.Exists(full))return null;
      return new ObserverConversionSource {
        Path=full,Exists=false,Text=null};}
    FslFormalObserverIdentity before=Read(full,false);
    if(before.Reparse||before.Links!=1||!String.Equals(
      before.FinalPath,full,StringComparison.OrdinalIgnoreCase))return null;
    var beforeInfo=new System.IO.FileInfo(full);
    long beforeLength=beforeInfo.Length;
    long beforeCreation=beforeInfo.CreationTimeUtc.Ticks;
    long beforeWrite=beforeInfo.LastWriteTimeUtc.Ticks;
    int beforeAttributes=(int)beforeInfo.Attributes;
    if(beforeLength>1024*1024)return null;
    byte[] bytes=System.IO.File.ReadAllBytes(full);string text;
    try{text=new UTF8Encoding(false,true).GetString(bytes);}
    catch{return null;}
    FslFormalObserverIdentity after=Read(full,false);
    var afterInfo=new System.IO.FileInfo(full);
    if(after.Reparse||after.Links!=1||before.FileId!=after.FileId||
       !String.Equals(after.FinalPath,full,StringComparison.OrdinalIgnoreCase)||
       beforeLength!=bytes.Length||afterInfo.Length!=beforeLength||
       afterInfo.CreationTimeUtc.Ticks!=beforeCreation||
       afterInfo.LastWriteTimeUtc.Ticks!=beforeWrite||
       (int)afterInfo.Attributes!=beforeAttributes)return null;
    byte[] digest;using(var sha=System.Security.Cryptography.SHA256.Create())
      digest=sha.ComputeHash(bytes);
    return new ObserverConversionSource {
      Path=full,Exists=true,Text=text,Length=bytes.Length,
      CreationTicks=beforeCreation,WriteTicks=beforeWrite,
      Attributes=beforeAttributes,Sha256=digest,
      NativeFileIdentity=after.FileId};
  }
  static bool IsConfigName(string value) {
    if(String.IsNullOrEmpty(value)||
       !((value[0]>='A'&&value[0]<='Z')||
         (value[0]>='a'&&value[0]<='z')))return false;
    for(int i=1;i<value.Length;i++) {
      char c=value[i];
      if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||
           (c>='0'&&c<='9')||c=='-'))return false;
    }
    return value.Length<=32768;
  }
  static bool DecodeConfigValue(string text,out string value) {
    value=null;var output=new StringBuilder();
    var whitespace=new StringBuilder();bool quoted=false,seen=false;
    for(int i=0;i<text.Length;i++) {
      char c=text[i];
      if(!quoted&&(c=='#'||c==';'))break;
      if(c=='"') {
        quoted=!quoted;seen=true;output.Append(whitespace);
        whitespace.Length=0;continue;
      }
      if(c=='\\') {
        if(++i>=text.Length)return false;
        char escaped=text[i],decoded;
        if(escaped=='"')decoded='"';
        else if(escaped=='\\')decoded='\\';
        else if(escaped=='n')decoded='\n';
        else if(escaped=='t')decoded='\t';
        else if(escaped=='b')decoded='\b';
        else return false;
        output.Append(whitespace);whitespace.Length=0;
        output.Append(decoded);seen=true;
      } else if(!quoted&&(c==' '||c=='\t')) {
        if(seen)whitespace.Append(c);
      } else {
        output.Append(whitespace);whitespace.Length=0;
        output.Append(c);seen=true;
      }
      if(output.Length+whitespace.Length>32768)return false;
    }
    if(quoted)return false;value=output.ToString();return true;
  }
  static bool ParseConversionConfig(
    ObserverConversionSource source,
    ref bool hasAutoCrlf,ref bool autoCrlf) {
    if(!source.Exists)return true;string text=source.Text;
    if(text.IndexOf('\0')>=0||
       new UTF8Encoding(false,true).GetByteCount(text)>1024*1024)return false;
    for(int i=0;i<text.Length;i++)
      if(text[i]=='\r'&&(i+1>=text.Length||text[i+1]!='\n'))return false;
    string[] physical=text.Replace("\r\n","\n").Split('\n');
    if(physical.Length>65536)return false;
    var logical=new System.Collections.Generic.List<string>();
    for(int physicalIndex=0;physicalIndex<physical.Length;physicalIndex++) {
      var lineBuilder=new StringBuilder(physical[physicalIndex]);
      int continuations=0;
      while(lineBuilder.Length>0&&
            lineBuilder[lineBuilder.Length-1]=='\\') {
        int slashCount=0;
        for(int slashIndex=lineBuilder.Length-1;
            slashIndex>=0&&lineBuilder[slashIndex]=='\\';slashIndex--)
          slashCount++;
        if((slashCount&1)==0)break;
        if(++physicalIndex>=physical.Length||++continuations>64)return false;
        lineBuilder.Length--;
        lineBuilder.Append(physical[physicalIndex].TrimStart(' ','\t'));
        if(lineBuilder.Length>32768)return false;
      }
      logical.Add(lineBuilder.ToString());
    }
    string section="",subsection="";int assignments=0;
    var lfs=new System.Collections.Generic.Dictionary<string,string>(
      StringComparer.OrdinalIgnoreCase);
    foreach(string sourceLine in logical) {
      string line=sourceLine.Trim();
      if(line.Length==0||line[0]=='#'||line[0]==';')continue;
      if(line[0]=='[') {
        bool headerQuoted=false,headerEscaped=false;int headerEnd=-1;
        for(int headerIndex=1;headerIndex<line.Length;headerIndex++) {
          char headerCharacter=line[headerIndex];
          if(headerEscaped){headerEscaped=false;continue;}
          if(headerCharacter=='\\'&&headerQuoted) {
            headerEscaped=true;continue;}
          if(headerCharacter=='"')headerQuoted=!headerQuoted;
          else if(headerCharacter==']'&&!headerQuoted) {
            headerEnd=headerIndex;break;}
        }
        if(headerEnd<0)return false;
        string headerRemainder=line.Substring(headerEnd+1).TrimStart(' ','\t');
        if(headerRemainder.Length>0&&headerRemainder[0]!='#'&&
           headerRemainder[0]!=';')return false;
        string header=line.Substring(1,headerEnd-1);
        int separator=header.IndexOfAny(new char[]{' ','\t'});
        string rawSection=separator<0?header:header.Substring(0,separator);
        if(!IsConfigName(rawSection))return false;
        section=rawSection.ToLowerInvariant();subsection="";
        if(separator>=0) {
          string tail=header.Substring(separator).Trim();
          if(tail.Length<2||tail[0]!='"'||tail[tail.Length-1]!='"'||
             !DecodeConfigValue(tail,out subsection)||
             subsection.Length>32768)return false;
        }
        if(section=="include"||section=="includeif")return false;
        continue;
      }
      int nameEnd=0;
      while(nameEnd<line.Length&&line[nameEnd]!=' '&&
            line[nameEnd]!='\t'&&line[nameEnd]!='=')nameEnd++;
      string rawKey=line.Substring(0,nameEnd);
      if(!IsConfigName(rawKey)||section.Length==0||++assignments>4096)
        return false;
      int cursor=nameEnd;
      while(cursor<line.Length&&(line[cursor]==' '||line[cursor]=='\t'))
        cursor++;
      string value="true";
      if(cursor<line.Length) {
        if(line[cursor]=='#'||line[cursor]==';')value="true";
        else {
          if(line[cursor]!='='||
             !DecodeConfigValue(line.Substring(cursor+1),out value))
            return false;
        }
      }
      string key=rawKey.ToLowerInvariant();
      if(section=="core"&&key=="autocrlf") {
        if(!String.Equals(value,"true",StringComparison.OrdinalIgnoreCase)&&
           !String.Equals(value,"false",StringComparison.OrdinalIgnoreCase))
          return false;
        hasAutoCrlf=true;autoCrlf=String.Equals(
          value,"true",StringComparison.OrdinalIgnoreCase);
      } else if(section=="core"&&(key=="eol"||key=="attributesfile"||
        key=="worktree"||key=="safecrlf"||
        key=="checkroundtripencoding"||key=="bigfilethreshold"))return false;
      else if(section=="extensions"&&key=="worktreeconfig")return false;
      else if(section=="filter") {
        if(!String.Equals(subsection,"lfs",StringComparison.Ordinal))return false;
        string expected;
        if(key=="clean")expected="git-lfs clean -- %f";
        else if(key=="smudge")expected="git-lfs smudge -- %f";
        else if(key=="process")expected="git-lfs filter-process";
        else if(key=="required")expected="true";
        else return false;
        if(!String.Equals(value,expected,StringComparison.Ordinal))return false;
        lfs[key]=value;
      }
    }
    return lfs.Count==0||lfs.Count==4;
  }
  static System.Collections.Generic.Dictionary<string,string>
    CaptureEnvironment() {
    var snapshot=new System.Collections.Generic.Dictionary<string,string>(
      StringComparer.OrdinalIgnoreCase);
    System.Collections.IDictionary values=Environment.GetEnvironmentVariables(
      EnvironmentVariableTarget.Process);
    foreach(System.Collections.DictionaryEntry entry in values) {
      string name=entry.Key as string,value=entry.Value as string;
      if(String.IsNullOrEmpty(name)||value==null||
         snapshot.ContainsKey(name))return null;
      snapshot.Add(name,value);
    }
    return snapshot;
  }
  static bool TryCanonicalEnvironmentPath(
    System.Collections.Generic.Dictionary<string,string> environment,
    string name,bool required,out string value) {
    value=null;string raw;
    if(!environment.TryGetValue(name,out raw)) {
      if(required)return false;value="";return true;}
    if(String.IsNullOrWhiteSpace(raw)||raw.IndexOf('\0')>=0)return false;
    try {
      string canonical=System.IO.Path.GetFullPath(raw);
      if(!System.IO.Path.IsPathRooted(raw)||!String.Equals(
        raw,canonical,StringComparison.OrdinalIgnoreCase))return false;
      value=canonical;return true;
    } catch{return false;}
  }
  static bool ParseCommandConfigEnvironment(
    System.Collections.Generic.Dictionary<string,string> environment) {
    var numberedPattern=new System.Text.RegularExpressions.Regex(
      "^GIT_CONFIG_(KEY|VALUE)_(0|[1-9][0-9]*)$",
      System.Text.RegularExpressions.RegexOptions.IgnoreCase|
      System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    foreach(string name in environment.Keys)
      if(name.StartsWith("GIT_CONFIG_",
           StringComparison.OrdinalIgnoreCase)&&
         !String.Equals(name,"GIT_CONFIG_COUNT",
           StringComparison.OrdinalIgnoreCase)&&
         !numberedPattern.IsMatch(name))return false;
    bool hasCount=environment.ContainsKey("GIT_CONFIG_COUNT");int numbered=0;
    foreach(string name in environment.Keys)
      if(numberedPattern.IsMatch(name))numbered++;
    if(!hasCount)return numbered==0;
    string rawCount=environment["GIT_CONFIG_COUNT"];
    if(rawCount.Length==0||(rawCount.Length>1&&rawCount[0]=='0')||
       rawCount.Length>2)return false;
    for(int i=0;i<rawCount.Length;i++)
      if(rawCount[i]<'0'||rawCount[i]>'9')return false;
    int count;
    if(!Int32.TryParse(rawCount,
         System.Globalization.NumberStyles.None,
         System.Globalization.CultureInfo.InvariantCulture,out count)||
       count>64||numbered!=checked(2*count))return false;
    for(int i=0;i<count;i++) {
      string key,value;
      if(!environment.TryGetValue("GIT_CONFIG_KEY_"+i.ToString(
           System.Globalization.CultureInfo.InvariantCulture),out key)||
         !environment.TryGetValue("GIT_CONFIG_VALUE_"+i.ToString(
           System.Globalization.CultureInfo.InvariantCulture),out value)||
         !String.Equals(key,"safe.directory",
           StringComparison.OrdinalIgnoreCase)||value.Length>32768||
         value.IndexOfAny(new char[]{'\0','\r','\n'})>=0)return false;
    }
    return true;
  }
  static bool ParseSystemAttributes(ObserverConversionSource source) {
    if(!source.Exists)return true;string text=source.Text;
    if(text.IndexOf('\0')>=0)return false;
    for(int i=0;i<text.Length;i++)
      if(text[i]=='\r'&&(i+1>=text.Length||text[i+1]!='\n'))return false;
    foreach(string sourceLine in text.Replace("\r\n","\n").Split('\n')) {
      string line=sourceLine.Trim();
      if(line.Length==0||line[0]=='#')continue;
      string[] fields=line.Split(new char[]{' ','\t'},
        StringSplitOptions.RemoveEmptyEntries);
      if(fields.Length!=2||
         !fields[0].StartsWith("*.",StringComparison.Ordinal)||
         fields[0].Length<=2||fields[0].Substring(2).IndexOfAny(
           new char[]{'/','\\','*','?','[',']'})>=0||
         fields[1]!="diff=astextplain")return false;
    }
    return true;
  }
  static byte[] UInt32Le(uint value) {
    return new byte[]{(byte)value,(byte)(value>>8),(byte)(value>>16),
      (byte)(value>>24)};
  }
  static byte[] Int64Le(long value) {
    ulong raw=unchecked((ulong)value);
    return new byte[]{(byte)raw,(byte)(raw>>8),(byte)(raw>>16),
      (byte)(raw>>24),(byte)(raw>>32),(byte)(raw>>40),(byte)(raw>>48),
      (byte)(raw>>56)};
  }
  static void WriteProfileFrame(System.IO.MemoryStream stream,byte type,
    byte state,string name,byte[] payload) {
    byte[] nameBytes=new UTF8Encoding(false,true).GetBytes(name);
    if(payload==null)payload=new byte[0];
    stream.WriteByte(type);stream.WriteByte(state);
    byte[] length=UInt32Le((uint)nameBytes.Length);
    stream.Write(length,0,length.Length);
    stream.Write(nameBytes,0,nameBytes.Length);
    length=UInt32Le((uint)payload.Length);
    stream.Write(length,0,length.Length);
    stream.Write(payload,0,payload.Length);
  }
  static bool ProfileEncoderGoldenMatches() {
    var utf8=new UTF8Encoding(false,true);
    using(var stream=new System.IO.MemoryStream()) {
      WriteProfileFrame(stream,1,2,"schema",
        utf8.GetBytes("FSL.Stage4.ConversionProfile"));
      WriteProfileFrame(stream,3,2,"version",UInt32Le(1));
      WriteProfileFrame(stream,1,2,"newline",utf8.GetBytes("a\nb"));
      WriteProfileFrame(stream,1,2,"pipe",utf8.GetBytes("a|b"));
      WriteProfileFrame(stream,1,2,"empty",new byte[0]);
      WriteProfileFrame(stream,1,1,"null",new byte[0]);
      WriteProfileFrame(stream,1,0,"absent",new byte[0]);
      WriteProfileFrame(stream,2,2,"false",new byte[]{0});
      WriteProfileFrame(stream,3,2,"zero",UInt32Le(0));
      byte[] digest;
      using(var sha=System.Security.Cryptography.SHA256.Create())
        digest=sha.ComputeHash(stream.ToArray());
      return String.Equals(Hex(digest),
        "0d2589a97eec51dd09f1f23b7de4171e6ec9a1ab1356ad1fbd1ac2011a594e26",
        StringComparison.Ordinal);
    }
  }
  static byte[] ConversionProfileBytes(
    System.Collections.Generic.Dictionary<string,string> environment,
    string programFiles,string userProfile,string home,string xdg,
    bool hasProgramW6432,string programW6432,bool hasAutoCrlf,
    bool autoCrlf,
    System.Collections.Generic.Dictionary<
      string,ObserverConversionSource> records) {
    var utf8=new UTF8Encoding(false,true);
    using(var stream=new System.IO.MemoryStream()) {
      WriteProfileFrame(stream,1,2,"schema",
        utf8.GetBytes("FSL.Stage4.ConversionProfile"));
      WriteProfileFrame(stream,3,2,"version",UInt32Le(1));
      WriteProfileFrame(stream,1,2,"resolved.ProgramFiles",
        utf8.GetBytes(programFiles));
      WriteProfileFrame(stream,1,2,"resolved.USERPROFILE",
        utf8.GetBytes(userProfile));
      WriteProfileFrame(stream,1,2,"resolved.HOME",utf8.GetBytes(home));
      WriteProfileFrame(stream,1,2,"resolved.XDG_CONFIG_HOME",
        utf8.GetBytes(xdg));
      WriteProfileFrame(stream,1,hasProgramW6432?(byte)2:(byte)0,
        "raw.ProgramW6432",hasProgramW6432?
          utf8.GetBytes(environment["ProgramW6432"]):new byte[0]);
      WriteProfileFrame(stream,1,hasProgramW6432?(byte)2:(byte)0,
        "resolved.ProgramW6432",hasProgramW6432?
          utf8.GetBytes(programW6432):new byte[0]);
      WriteProfileFrame(stream,2,2,"hasAutoCrlf",
        new byte[]{hasAutoCrlf?(byte)1:(byte)0});
      WriteProfileFrame(stream,2,hasAutoCrlf?(byte)2:(byte)0,"autoCrlf",
        hasAutoCrlf?new byte[]{autoCrlf?(byte)1:(byte)0}:new byte[0]);
      foreach(string name in new string[]{"ProgramFiles","USERPROFILE",
        "HOME","XDG_CONFIG_HOME"}) {
        string raw;bool present=environment.TryGetValue(name,out raw);
        WriteProfileFrame(stream,1,present?(byte)2:(byte)0,"raw."+name,
          present?utf8.GetBytes(raw):new byte[0]);
      }
      var gitNames=new System.Collections.Generic.List<string>();
      foreach(string name in environment.Keys)
        if(name.StartsWith("GIT_",StringComparison.OrdinalIgnoreCase))
          gitNames.Add(name.ToUpperInvariant());
      gitNames.Sort(StringComparer.Ordinal);
      using(var payload=new System.IO.MemoryStream()) {
        byte[] count=UInt32Le((uint)gitNames.Count);
        payload.Write(count,0,count.Length);
        foreach(string name in gitNames)
          WriteProfileFrame(payload,1,2,name,
            utf8.GetBytes(environment[name]));
        WriteProfileFrame(stream,5,2,"gitEnvironment",payload.ToArray());
      }
      var paths=new System.Collections.Generic.List<string>(records.Keys);
      paths.Sort(delegate(string left,string right) {
        int result=String.Compare(
          left,right,StringComparison.OrdinalIgnoreCase);
        return result!=0?result:String.CompareOrdinal(left,right);
      });
      using(var sources=new System.IO.MemoryStream()) {
        byte[] count=UInt32Le((uint)paths.Count);
        sources.Write(count,0,count.Length);
        foreach(string path in paths) {
          ObserverConversionSource source=records[path];
          using(var record=new System.IO.MemoryStream()) {
            WriteProfileFrame(record,1,2,"path",utf8.GetBytes(source.Path));
            WriteProfileFrame(record,2,2,"exists",
              new byte[]{source.Exists?(byte)1:(byte)0});
            WriteProfileFrame(record,4,source.Exists?(byte)2:(byte)0,
              "length",source.Exists?Int64Le(source.Length):new byte[0]);
            WriteProfileFrame(record,4,source.Exists?(byte)2:(byte)0,
              "creationTicks",source.Exists?
                Int64Le(source.CreationTicks):new byte[0]);
            WriteProfileFrame(record,4,source.Exists?(byte)2:(byte)0,
              "writeTicks",source.Exists?
                Int64Le(source.WriteTicks):new byte[0]);
            WriteProfileFrame(record,3,source.Exists?(byte)2:(byte)0,
              "attributes",source.Exists?
                UInt32Le(unchecked((uint)source.Attributes)):new byte[0]);
            WriteProfileFrame(record,7,source.Exists?(byte)2:(byte)0,
              "sha256",source.Exists?source.Sha256:new byte[0]);
            WriteProfileFrame(record,1,source.Exists?(byte)2:(byte)0,
              "nativeFileIdentity",source.Exists?
                utf8.GetBytes(source.NativeFileIdentity):new byte[0]);
            WriteProfileFrame(sources,6,2,"source",record.ToArray());
          }
        }
        WriteProfileFrame(stream,5,2,"sources",sources.ToArray());
      }
      return stream.ToArray();
    }
  }
  static bool CaptureConversionProfile(
    string gitRoot,string gitDirectory,
    System.Collections.Generic.List<ObserverGitEntry> entries,
    out ObserverConversionProfile profile) {
    profile=null;
    System.Collections.Generic.Dictionary<string,string> environment=
      CaptureEnvironment();
    if(environment==null||!ProfileEncoderGoldenMatches())return false;
    string programFiles,userProfile,programW6432=null;
    bool hasProgramW6432=environment.ContainsKey("ProgramW6432");
    if(!TryCanonicalEnvironmentPath(
         environment,"ProgramFiles",true,out programFiles)||
       !TryCanonicalEnvironmentPath(
         environment,"USERPROFILE",true,out userProfile)||
       (hasProgramW6432&&!TryCanonicalEnvironmentPath(
         environment,"ProgramW6432",true,out programW6432))||
       !System.IO.Directory.Exists(programFiles)||
       !System.IO.Directory.Exists(userProfile)||
       (hasProgramW6432&&!System.IO.Directory.Exists(programW6432)))
      return false;
    try {
      string knownProgramFiles=System.IO.Path.GetFullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
      string knownUserProfile=System.IO.Path.GetFullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
      if(!String.Equals(programFiles,knownProgramFiles,
           StringComparison.OrdinalIgnoreCase)||
       !String.Equals(userProfile,knownUserProfile,
           StringComparison.OrdinalIgnoreCase)||
       (hasProgramW6432&&(!String.Equals(programW6432,programFiles,
          StringComparison.OrdinalIgnoreCase)||
        !String.Equals(programW6432,knownProgramFiles,
          StringComparison.OrdinalIgnoreCase))))return false;
    } catch{return false;}
    foreach(string forbidden in new string[]{"GIT_CONFIG_SYSTEM",
      "GIT_CONFIG_GLOBAL","GIT_CONFIG_NOSYSTEM","GIT_CONFIG_PARAMETERS",
      "GIT_ATTR_NOSYSTEM","GIT_DIR","GIT_WORK_TREE","GIT_COMMON_DIR",
      "GIT_INDEX_FILE","GIT_OBJECT_DIRECTORY",
      "GIT_ALTERNATE_OBJECT_DIRECTORIES","GIT_QUARANTINE_PATH",
      "GIT_NAMESPACE","GIT_SHALLOW_FILE","GIT_GRAFT_FILE",
      "GIT_NO_REPLACE_OBJECTS","GIT_REPLACE_REF_BASE"})
      if(environment.ContainsKey(forbidden))return false;
    if(!ParseCommandConfigEnvironment(environment))return false;
    string home;
    if(environment.ContainsKey("HOME")) {
      if(!TryCanonicalEnvironmentPath(
           environment,"HOME",true,out home))return false;
    } else home=userProfile;
    string xdg;
    if(environment.ContainsKey("XDG_CONFIG_HOME")) {
      if(!TryCanonicalEnvironmentPath(
           environment,"XDG_CONFIG_HOME",true,out xdg))return false;
    } else xdg=System.IO.Path.Combine(home,".config");
    string systemConfig=System.IO.Path.Combine(
      programFiles,"Git","etc","gitconfig");
    string systemAttributes=System.IO.Path.Combine(
      programFiles,"Git","etc","gitattributes");
    string xdgConfig=System.IO.Path.Combine(xdg,"git","config");
    string userConfig=System.IO.Path.Combine(home,".gitconfig");
    string userAttributes=System.IO.Path.Combine(
      xdg,"git","attributes");
    string legacyUserAttributes=System.IO.Path.Combine(
      home,".gitattributes");
    string localConfig=System.IO.Path.Combine(gitDirectory,"config");
    string worktreeConfig=System.IO.Path.Combine(
      gitDirectory,"config.worktree");
    string infoAttributes=System.IO.Path.Combine(
      gitDirectory,"info","attributes");
    var governing=new System.Collections.Generic.HashSet<string>(
      StringComparer.OrdinalIgnoreCase);
    governing.Add(System.IO.Path.Combine(gitRoot,".gitattributes"));
    foreach(ObserverGitEntry entry in entries) {
      string directory=gitRoot;string[] parts=entry.Path.Split('/');
      for(int i=0;i<parts.Length-1;i++) {
        directory=System.IO.Path.Combine(directory,parts[i]);
        governing.Add(System.IO.Path.Combine(directory,".gitattributes"));}
    }
    var paths=new System.Collections.Generic.HashSet<string>(
      StringComparer.OrdinalIgnoreCase);
    foreach(string path in new string[]{systemConfig,systemAttributes,
      userConfig,xdgConfig,userAttributes,legacyUserAttributes,localConfig,
      worktreeConfig,infoAttributes})
      paths.Add(System.IO.Path.GetFullPath(path));
    foreach(string path in governing)
      paths.Add(System.IO.Path.GetFullPath(path));
    var records=new System.Collections.Generic.Dictionary<
      string,ObserverConversionSource>(StringComparer.OrdinalIgnoreCase);
    foreach(string path in paths) {
      ObserverConversionSource record=CaptureConversionSource(path);
      if(record==null)return false;records.Add(path,record);
    }
    foreach(string forbidden in new string[]{userAttributes,
      legacyUserAttributes,worktreeConfig,infoAttributes})
      if(records[System.IO.Path.GetFullPath(forbidden)].Exists)return false;
    foreach(string forbidden in governing)
      if(records[System.IO.Path.GetFullPath(forbidden)].Exists)return false;
    bool hasAutoCrlf=false,autoCrlf=false;
    if(!ParseConversionConfig(
         records[System.IO.Path.GetFullPath(systemConfig)],
         ref hasAutoCrlf,ref autoCrlf)||
       !ParseConversionConfig(
         records[System.IO.Path.GetFullPath(xdgConfig)],
         ref hasAutoCrlf,ref autoCrlf)||
       !ParseConversionConfig(
         records[System.IO.Path.GetFullPath(userConfig)],
         ref hasAutoCrlf,ref autoCrlf)||
       !ParseConversionConfig(
         records[System.IO.Path.GetFullPath(localConfig)],
         ref hasAutoCrlf,ref autoCrlf)||
       !ParseSystemAttributes(
         records[System.IO.Path.GetFullPath(systemAttributes)]))return false;
    byte[] fingerprintBytes;
    using(var sha=System.Security.Cryptography.SHA256.Create())
      fingerprintBytes=sha.ComputeHash(
        ConversionProfileBytes(environment,programFiles,userProfile,home,xdg,
          hasProgramW6432,programW6432,hasAutoCrlf,autoCrlf,records));
    profile=new ObserverConversionProfile {
      AutoCrlf=hasAutoCrlf&&autoCrlf,
      Fingerprint=Hex(fingerprintBytes)};
    return true;
  }
  static byte[] SafeAutoCrlfBytes(byte[] bytes) {
    using(var output=new System.IO.MemoryStream()) {
      bool converted=false;
      for(int i=0;i<bytes.Length;i++) {
        byte value=bytes[i];if(value==0)return null;
        if(value==0x0D) {
          if(i+1>=bytes.Length||bytes[i+1]!=0x0A)return null;
          output.WriteByte(0x0A);i++;converted=true;continue;
        }
        if(value==0x0A)return null;output.WriteByte(value);
      }
      return converted?output.ToArray():null;
    }
  }
  public static bool VerifyGitIndexAndTree(
    string rootPath,string gitPath,string expectedTree) {
    try {
      byte[] index=System.IO.File.ReadAllBytes(
        System.IO.Path.Combine(gitPath,"index"));
      if(index.Length<32||Encoding.ASCII.GetString(index,0,4)!="DIRC"||
         U32(index,4)!=2)return false;
      int checksum=index.Length-20;byte[] actual;
      using(var sha=System.Security.Cryptography.SHA1.Create())
        actual=sha.ComputeHash(index,0,checksum);
      for(int i=0;i<20;i++)if(actual[i]!=index[checksum+i])return false;
      uint rawCount=U32(index,8);if(rawCount>1000000)return false;
      int count=(int)rawCount,offset=12;byte[] previous=null;
      var entries=new System.Collections.Generic.List<ObserverGitEntry>();
      string root=System.IO.Path.GetFullPath(rootPath).TrimEnd('\\');
      string prefix=root+"\\";
      for(int entryIndex=0;entryIndex<count;entryIndex++) {
        int start=offset;if(start<12||start+63>checksum)return false;
        uint mode=U32(index,start+24);
        if(mode!=0x000081A4&&mode!=0x000081ED)return false;
        byte[] oid=new byte[20];Buffer.BlockCopy(index,start+40,oid,0,20);
        bool zero=true;foreach(byte value in oid)if(value!=0){zero=false;break;}
        if(zero)return false;
        ushort flags=U16(index,start+60);if((flags&0xF000)!=0)return false;
        int pathStart=start+62,pathEnd=pathStart;
        while(pathEnd<checksum&&index[pathEnd]!=0)pathEnd++;
        if(pathEnd>=checksum||pathEnd==pathStart)return false;
        int pathLength=pathEnd-pathStart;
        if((flags&0x0FFF)!=Math.Min(pathLength,0x0FFF))return false;
        byte[] pathBytes=new byte[pathLength];
        Buffer.BlockCopy(index,pathStart,pathBytes,0,pathLength);
        if(previous!=null&&Compare(previous,pathBytes)>=0)return false;
        previous=pathBytes;string relative;
        try{relative=new UTF8Encoding(false,true).GetString(pathBytes);}
        catch{return false;}
        if(!CanonicalPath(relative))return false;
        entries.Add(new ObserverGitEntry {
          Path=relative,Mode=mode,ObjectId=oid});
        int length=(pathEnd-start)+1;
        int next=start+((length+7)&~7);
        if(next<=start||next>checksum)return false;
        for(int p=pathEnd+1;p<next;p++)if(index[p]!=0)return false;
        offset=next;
      }
      byte[] cacheTree=null;
      while(offset<checksum) {
        if(offset+8>checksum)return false;
        string signature=Encoding.ASCII.GetString(index,offset,4);
        uint rawSize=U32(index,offset+4);
        if(rawSize>Int32.MaxValue)return false;
        int size=(int)rawSize;offset+=8;
        if(size<0||offset+size<offset||offset+size>checksum||
           signature!="TREE"||cacheTree!=null||
           !ValidCacheTree(index,offset,size))return false;
        cacheTree=new byte[size];
        Buffer.BlockCopy(index,offset,cacheTree,0,size);
        offset+=size;
      }
      if(offset!=checksum)return false;
      ObserverConversionProfile conversionProfile;
      if(!CaptureConversionProfile(
        root,gitPath,entries,out conversionProfile))return false;
      foreach(ObserverGitEntry entry in entries) {
        string full=System.IO.Path.GetFullPath(System.IO.Path.Combine(
          root,entry.Path.Replace('/','\\')));
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||
           !System.IO.File.Exists(full))return false;
        FslFormalObserverIdentity identity=Read(full,false);
        if(identity.Reparse||identity.Links!=1||
           !identity.FinalPath.StartsWith(
             prefix,StringComparison.OrdinalIgnoreCase))return false;
        byte[] content=System.IO.File.ReadAllBytes(full);
        if(!Equal(GitOid("blob",content),entry.ObjectId)) {
          if(!conversionProfile.AutoCrlf)return false;
          byte[] canonical=SafeAutoCrlfBytes(content);
          if(canonical==null||
             !Equal(GitOid("blob",canonical),entry.ObjectId))return false;
        }
      }
      var rootNode=new ObserverGitNode();
      foreach(ObserverGitEntry entry in entries)
        if(!Insert(rootNode,entry))return false;
      byte[] actualTree=BuildTree(rootNode);
      if(cacheTree!=null&&!Equal(cacheTree,BuildCacheTree(rootNode)))
        return false;
      ObserverConversionProfile recapturedProfile;
      return String.Equals(Hex(actualTree),expectedTree,
          StringComparison.Ordinal)&&CaptureConversionProfile(
          root,gitPath,entries,out recapturedProfile)&&
        conversionProfile.AutoCrlf==recapturedProfile.AutoCrlf&&
        String.Equals(conversionProfile.Fingerprint,
          recapturedProfile.Fingerprint,StringComparison.Ordinal);
    }catch{return false;}
  }
  sealed class ObserverGitEntry {
    internal string Path;internal uint Mode;internal byte[] ObjectId;
  }
  sealed class ObserverGitNode {
    internal readonly System.Collections.Generic.Dictionary<
      string,ObserverGitNode> Directories=
      new System.Collections.Generic.Dictionary<string,ObserverGitNode>(
        StringComparer.Ordinal);
    internal readonly System.Collections.Generic.Dictionary<
      string,ObserverGitEntry> Files=
      new System.Collections.Generic.Dictionary<string,ObserverGitEntry>(
        StringComparer.Ordinal);
  }
  sealed class ObserverTreeItem {
    internal string Name,Mode;internal bool Directory;internal byte[] ObjectId;
  }
  static bool Insert(ObserverGitNode root,ObserverGitEntry entry) {
    string[] parts=entry.Path.Split('/');ObserverGitNode node=root;
    for(int i=0;i<parts.Length-1;i++) {
      if(node.Files.ContainsKey(parts[i]))return false;
      ObserverGitNode child;
      if(!node.Directories.TryGetValue(parts[i],out child)) {
        child=new ObserverGitNode();node.Directories.Add(parts[i],child);}
      node=child;
    }
    string leaf=parts[parts.Length-1];
    if(node.Directories.ContainsKey(leaf)||node.Files.ContainsKey(leaf))
      return false;
    node.Files.Add(leaf,entry);return true;
  }
  static byte[] BuildTree(ObserverGitNode node) {
    var items=new System.Collections.Generic.List<ObserverTreeItem>();
    foreach(var pair in node.Files)items.Add(new ObserverTreeItem {
      Name=pair.Key,Directory=false,
      Mode=pair.Value.Mode==0x000081ED?"100755":"100644",
      ObjectId=pair.Value.ObjectId});
    foreach(var pair in node.Directories)items.Add(new ObserverTreeItem {
      Name=pair.Key,Directory=true,Mode="40000",
      ObjectId=BuildTree(pair.Value)});
    items.Sort(delegate(ObserverTreeItem left,ObserverTreeItem right){
      return Compare(Encoding.UTF8.GetBytes(
        left.Name+(left.Directory?"/":"")),Encoding.UTF8.GetBytes(
        right.Name+(right.Directory?"/":"")));});
    using(var body=new System.IO.MemoryStream()) {
      foreach(ObserverTreeItem item in items) {
        byte[] mode=Encoding.ASCII.GetBytes(item.Mode+" ");
        byte[] name=new UTF8Encoding(false,true).GetBytes(item.Name);
        body.Write(mode,0,mode.Length);body.Write(name,0,name.Length);
        body.WriteByte(0);body.Write(item.ObjectId,0,item.ObjectId.Length);}
      return GitOid("tree",body.ToArray());
    }
  }
  static byte[] BuildCacheTree(ObserverGitNode root) {
    using(var output=new System.IO.MemoryStream()) {
      WriteCacheNode(output,root,"",true);return output.ToArray();}
  }
  static int WriteCacheNode(
    System.IO.Stream output,ObserverGitNode node,string name,bool root) {
    int count=node.Files.Count;
    foreach(ObserverGitNode child in node.Directories.Values)
      count+=CountEntries(child);
    byte[] path=new UTF8Encoding(false,true).GetBytes(root?"":name);
    output.Write(path,0,path.Length);output.WriteByte(0);
    byte[] counts=Encoding.ASCII.GetBytes(count.ToString(
      System.Globalization.CultureInfo.InvariantCulture)+" "+
      node.Directories.Count.ToString(
        System.Globalization.CultureInfo.InvariantCulture)+"\n");
    output.Write(counts,0,counts.Length);
    byte[] oid=BuildTree(node);output.Write(oid,0,oid.Length);
    var names=new System.Collections.Generic.List<string>(
      node.Directories.Keys);names.Sort(StringComparer.Ordinal);
    foreach(string childName in names)
      WriteCacheNode(output,node.Directories[childName],childName,false);
    return count;
  }
  static int CountEntries(ObserverGitNode node) {
    int count=node.Files.Count;
    foreach(ObserverGitNode child in node.Directories.Values)
      count+=CountEntries(child);
    return count;
  }
  static bool ValidCacheTree(byte[] b,int start,int size) {
    try{int o=start,end=start+size;
      return ParseCacheNode(b,ref o,end,true)&&o==end;}catch{return false;}
  }
  static bool ParseCacheNode(byte[] b,ref int o,int end,bool root) {
    int start=o;while(o<end&&b[o]!=0)o++;if(o>=end)return false;
    string path;try{path=new UTF8Encoding(false,true).GetString(
      b,start,o-start);}catch{return false;}
    if((root&&path.Length!=0)||(!root&&(path.Length==0||
      path.Contains("/")||path.Contains("\\")||
      path.Normalize(NormalizationForm.FormC)!=path)))return false;
    o++;int count=AsciiInt(b,ref o,end,true);
    if(count<-1||o>=end||b[o++]!=(byte)' ')return false;
    int children=AsciiInt(b,ref o,end,false);
    if(children<0||o>=end||b[o++]!=(byte)'\n')return false;
    if(count>=0){if(o+20>end)return false;o+=20;}
    for(int i=0;i<children;i++)
      if(!ParseCacheNode(b,ref o,end,false))return false;
    return true;
  }
  static int AsciiInt(byte[] b,ref int o,int end,bool negativeOne) {
    bool negative=false;if(negativeOne&&o<end&&b[o]==(byte)'-'){
      negative=true;o++;}
    int start=o;long value=0;
    while(o<end&&b[o]>=(byte)'0'&&b[o]<=(byte)'9'){
      value=checked(value*10+b[o]-(byte)'0');o++;}
    if(o==start||value>Int32.MaxValue)return Int32.MinValue;
    int result=(int)value;if(negative)result=-result;
    if(negative&&result!=-1)return Int32.MinValue;return result;
  }
  static bool CanonicalPath(string path) {
    if(String.IsNullOrEmpty(path)||path[0]=='/'||path.Contains("\\")||
       path.Contains("\0")||
       path.Normalize(NormalizationForm.FormC)!=path)return false;
    foreach(string part in path.Split('/'))
      if(part.Length==0||part=="."||part=="..")return false;
    return true;
  }
  static byte[] GitOid(string type,byte[] content) {
    byte[] header=Encoding.ASCII.GetBytes(type+" "+content.Length.ToString(
      System.Globalization.CultureInfo.InvariantCulture)+"\0");
    byte[] full=new byte[header.Length+content.Length];
    Buffer.BlockCopy(header,0,full,0,header.Length);
    Buffer.BlockCopy(content,0,full,header.Length,content.Length);
    using(var sha=System.Security.Cryptography.SHA1.Create())
      return sha.ComputeHash(full);
  }
  static ushort U16(byte[] b,int o) {
    return(ushort)(((uint)b[o]<<8)|b[o+1]);
  }
  static uint U32(byte[] b,int o) {
    return((uint)b[o]<<24)|((uint)b[o+1]<<16)|((uint)b[o+2]<<8)|b[o+3];
  }
  static int Compare(byte[] left,byte[] right) {
    int n=Math.Min(left.Length,right.Length);
    for(int i=0;i<n;i++){int d=left[i]-right[i];if(d!=0)return d;}
    return left.Length-right.Length;
  }
  static bool Equal(byte[] left,byte[] right) {
    if(left.Length!=right.Length)return false;
    for(int i=0;i<left.Length;i++)if(left[i]!=right[i])return false;
    return true;
  }
  static string Hex(byte[] bytes) {
    return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
  }
  [StructLayout(LayoutKind.Sequential)]struct Info {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME
      CreationTime,AccessTime,WriteTime;
    public uint VolumeSerialNumber,FileSizeHigh,FileSizeLow,NumberOfLinks;
    public uint FileIndexHigh,FileIndexLow;
  }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr q,
    uint c,uint f,IntPtr t);
  [DllImport("kernel32.dll",SetLastError=true)]
  static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern uint GetFinalPathNameByHandle(
    SafeFileHandle h,StringBuilder p,int n,uint f);
  [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
  [DllImport("advapi32.dll",SetLastError=true)]
  static extern bool OpenProcessToken(IntPtr p,uint a,out IntPtr t);
  [DllImport("advapi32.dll",SetLastError=true)]
  static extern bool GetTokenInformation(
    IntPtr t,int c,IntPtr b,int n,out int r);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern bool LookupAccountSid(
    string s,IntPtr p,StringBuilder n,ref uint nl,StringBuilder d,
    ref uint dl,out int use);
  [DllImport("advapi32.dll")]
  static extern bool EqualSid(IntPtr a,IntPtr b);
  [DllImport("kernel32.dll",SetLastError=true)]
  static extern bool CloseHandle(IntPtr h);
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern IntPtr CommandLineToArgvW(string line,out int count);
  [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
}
"@ -ReferencedAssemblies @('System.dll','System.Core.dll','System.Security.dll')
  }
}

function Stop-Observer([int]$Code,[string]$Message) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}
function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Get-TextHash([string]$Text) {
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($Text)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','') }
    finally { $sha.Dispose() }
}
function Get-Sha1([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA1]::Create()
    try{return [BitConverter]::ToString($sha.ComputeHash($Bytes)).
      Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Read-U32([byte[]]$Bytes,[int]$Offset) {
    return [uint32](([uint32]$Bytes[$Offset]-shl24)-bor
      ([uint32]$Bytes[$Offset+1]-shl16)-bor
      ([uint32]$Bytes[$Offset+2]-shl8)-bor[uint32]$Bytes[$Offset+3])
}
function Read-Loose([string]$ObjectId) {
    $path=Join-Path $fixedGitDirectory (
      'objects\'+$ObjectId.Substring(0,2)+'\'+$ObjectId.Substring(2))
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){
      Stop-Observer 71 'Required Git loose object is unavailable.'}
    $bytes=[IO.File]::ReadAllBytes($path)
    if($bytes.Length-lt7){Stop-Observer 71 'Git object is invalid.'}
    if(-not[FslFormalObserverIdentity]::ValidateZlibEnvelope($bytes)){
      Stop-Observer 71 'Git loose-object zlib envelope drifted.'}
    $input=[IO.MemoryStream]::new($bytes,2,$bytes.Length-6,$false)
    $deflate=[IO.Compression.DeflateStream]::new(
      $input,[IO.Compression.CompressionMode]::Decompress)
    $output=[IO.MemoryStream]::new()
    try{
      $deflate.CopyTo($output);$uncompressed=$output.ToArray()
      [uint32]$a=1;[uint32]$b=0
      foreach($value in $uncompressed){
        $a=[uint32](($a+$value)%65521)
        $b=[uint32](($b+$a)%65521)}
      $adler=[uint32](($b-shl16)-bor$a)
      if($adler-ne(Read-U32 $bytes ($bytes.Length-4)) -or
         (Get-Sha1 $uncompressed)-cne$ObjectId){
        Stop-Observer 71 'Git loose-object checksum drifted.'}
      $nul=[Array]::IndexOf($uncompressed,[byte]0)
      if($nul-le0){Stop-Observer 71 'Git loose-object header drifted.'}
      $header=[Text.Encoding]::ASCII.GetString($uncompressed,0,$nul)
      $match=[regex]::Match($header,'^commit (?<length>0|[1-9][0-9]*)$')
      [int64]$length=0
      if(-not$match.Success -or -not[int64]::TryParse(
           $match.Groups['length'].Value,
           [Globalization.NumberStyles]::None,
           [Globalization.CultureInfo]::InvariantCulture,
           [ref]$length) -or
         $length-ne$uncompressed.Length-$nul-1){
        Stop-Observer 71 'Git loose-object type/length drifted.'}
      return $uncompressed}
    finally{$output.Dispose();$deflate.Dispose();$input.Dispose()}
}
function Assert-GitAuthority([psobject]$Contract) {
    $headText=[IO.File]::ReadAllText(
      (Join-Path $fixedGitDirectory 'HEAD'),
      [Text.UTF8Encoding]::new($false,$true)).Trim()
    $expectedRef='ref: refs/heads/'+$fixedGitBranch
    if($headText-cne$expectedRef){Stop-Observer 71 'Git branch drifted.'}
    $head=[IO.File]::ReadAllText((Join-Path $fixedGitDirectory (
      'refs\heads\'+$fixedGitBranch.Replace('/','\')))).Trim()
    $commit=Read-Loose $head
    $nul=[Array]::IndexOf($commit,[byte]0)
    if($nul-lt0){Stop-Observer 71 'Git commit object drifted.'}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString(
      $commit,$nul+1,$commit.Length-$nul-1)
    $match=[regex]::Match($text,'^tree (?<tree>[0-9a-f]{40})\n')
    $clean=[FslFormalObserverIdentity]::VerifyGitIndexAndTree(
      $fixedGitRoot,$fixedGitDirectory,$fixedGitTree)
    if($head-cne$fixedGitHead -or -not$match.Success -or
       $match.Groups['tree'].Value-cne$fixedGitTree -or
       $clean-ne[bool]$fixedTrackedClean -or
       [string]$Contract.authority.repository.projectRoot-cne$fixedRepository -or
       [string]$Contract.authority.repository.gitRoot-cne$fixedGitRoot -or
       [string]$Contract.authority.repository.gitDirectory-cne$fixedGitDirectory -or
       [string]$Contract.authority.repository.branch-cne$fixedGitBranch -or
       [string]$Contract.authority.repository.head-cne$fixedGitHead -or
       [string]$Contract.authority.repository.tree-cne$fixedGitTree -or
       [bool]$Contract.authority.repository.trackedClean-ne[bool]$fixedTrackedClean){
      Stop-Observer 71 'Current Git authority drifted.'}
}
function Assert-ExactNames([string]$Root,[string[]]$Names,[int]$Code) {
    $actual=@(Get-ChildItem -LiteralPath $Root -Force|ForEach-Object{$_.Name})
    if($actual.Count-ne$Names.Count){Stop-Observer $Code 'Exact set count drifted.'}
    foreach($name in $Names){if(@($actual|Where-Object{$_-ceq$name}).Count-ne1){
        Stop-Observer $Code 'Exact set drifted.'}}
}
function Assert-File([psobject]$Record,[int]$Code) {
    if(-not(Test-Path -LiteralPath $Record.path -PathType Leaf) -or
       (Get-Item -LiteralPath $Record.path).Length-ne[int64]$Record.length -or
       (Get-Hash $Record.path)-cne[string]$Record.sha256){
        Stop-Observer $Code 'Bound file drifted.'}
}
function Get-Sddl([string]$Path,[bool]$Directory) {
    $sections=[Security.AccessControl.AccessControlSections]::Owner -bor
      [Security.AccessControl.AccessControlSections]::Group -bor
      [Security.AccessControl.AccessControlSections]::Access
    $security=if($Directory){
      [IO.Directory]::GetAccessControl($Path,$sections)
    }else{[IO.File]::GetAccessControl($Path,$sections)}
    return $security.GetSecurityDescriptorSddlForm($sections)
}
function Assert-Identity(
  [string]$Path,[bool]$Directory,[AllowNull()][psobject]$Record,
  [AllowNull()][object]$ExpectedSddl,[int]$Code) {
    $exists=if($Directory){
      Test-Path -LiteralPath $Path -PathType Container
    }else{Test-Path -LiteralPath $Path -PathType Leaf}
    if(-not$exists){
      Stop-Observer $Code 'Bound object is absent.'}
    $identity=[FslFormalObserverIdentity]::Read($Path,$Directory)
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if($identity.Reparse -or (-not$Directory -and $identity.Links-ne1) -or
       $identity.FinalPath-cne$full){
      Stop-Observer $Code 'Bound object identity drifted.'}
    if($null-ne$Record -and
       ($identity.FinalPath-cne[string]$Record.finalPath -or
        $identity.FileId-cne[string]$Record.fileId)){
      Stop-Observer $Code 'Bound object current authority drifted.'}
    $sddl=Get-Sddl $Path $Directory
    if($null-ne$Record -and $sddl-cne[string]$Record.aclSddl){
      Stop-Observer $Code 'Bound object ACL authority drifted.'}
    if($null-ne$ExpectedSddl -and $sddl-cne[string]$ExpectedSddl){
      Stop-Observer $Code 'Bound object ACL drifted.'}
}
function Assert-FormalTokenProof([psobject]$Contract) {
    $native=[FslFormalObserverIdentity]::ReadTokenProof()
    $bound=$Contract.authority.identity.formalTokenProof
    if([Environment]::MachineName-cne'FSL-STAGE4-VM' -or
       $native.MachineName-cne'FSL-STAGE4-VM' -or
       $native.ElevationType-ne3 -or
       $native.CurrentAccountSid-cne$fixedSid -or
       $native.LinkedAccountSid-cne$fixedSid -or
       $native.CurrentSidType-ne1 -or $native.LinkedSidType-ne1 -or
       -not$native.CurrentAdministratorsDenyOnly -or
       $native.CurrentAdministratorsEnabled -or
       $native.LinkedAdministratorsDenyOnly -or
       -not$native.LinkedAdministratorsEnabled -or
       $native.CurrentAccountDomain-cne'FSL-STAGE4-VM' -or
       $native.LinkedAccountDomain-cne'FSL-STAGE4-VM' -or
       $null-eq$bound -or
       [string]$bound.machineName-cne$native.MachineName -or
       [int]$bound.elevationType-ne$native.ElevationType -or
       [string]$bound.currentAccountSid-cne$native.CurrentAccountSid -or
       [string]$bound.linkedAccountSid-cne$native.LinkedAccountSid -or
       [int]$bound.currentSidType-ne$native.CurrentSidType -or
       [int]$bound.linkedSidType-ne$native.LinkedSidType -or
       [bool]$bound.currentAdministratorsDenyOnly-ne
         $native.CurrentAdministratorsDenyOnly -or
       [bool]$bound.currentAdministratorsEnabled-ne
         $native.CurrentAdministratorsEnabled -or
       [bool]$bound.linkedAdministratorsDenyOnly-ne
         $native.LinkedAdministratorsDenyOnly -or
       [bool]$bound.linkedAdministratorsEnabled-ne
         $native.LinkedAdministratorsEnabled -or
       [string]$bound.currentAccountDomain-cne$native.CurrentAccountDomain -or
       [string]$bound.linkedAccountDomain-cne$native.LinkedAccountDomain){
      Stop-Observer 66 'Formal native token proof drifted.'}
}
function Assert-CurrentFile([psobject]$Record,[int]$Code) {
    Assert-File $Record $Code
    Assert-Identity ([string]$Record.path) $false $Record $null $Code
}
function Assert-CurrentRoot([psobject]$Record,[int]$Code) {
    Assert-Identity ([string]$Record.path) $true $Record $null $Code
    if(@(Get-ChildItem -LiteralPath $Record.path -Force).Count-ne
       [int]$Record.childCount){
      Stop-Observer $Code 'Bound root child count drifted.'}
}
function Get-Utc {
    [DateTime]::UtcNow.ToString(
      "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
      [Globalization.CultureInfo]::InvariantCulture)
}
@@LATCH_HELPERS@@
function Write-Record([IO.FileStream]$Stream,[psobject]$Record) {
    $json=ConvertTo-FslFlbLatchCanonicalLine $Record
    if($null-eq$json){Stop-Observer 81 'Latch record shape drifted.'}
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($json+"`n")
    $Stream.Write($bytes,0,$bytes.Length);$Stream.Flush($true)
}
function Assert-Latch([string]$Path,[object[]]$ExpectedRecords) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    if(-not(Test-FslFlbLatchBytes $bytes $ExpectedRecords)){
      Stop-Observer 81 'Latch canonical bytes or semantics drifted.'}
}
function Assert-Temporal([string]$Earlier,[string]$Later) {
    $format="yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
    $culture=[Globalization.CultureInfo]::InvariantCulture
    $styles=[Globalization.DateTimeStyles]::AssumeUniversal -bor
      [Globalization.DateTimeStyles]::AdjustToUniversal
    $a=[DateTimeOffset]::MinValue;$b=[DateTimeOffset]::MinValue
    if(-not[DateTimeOffset]::TryParseExact(
         $Earlier,$format,$culture,$styles,[ref]$a) -or
       -not[DateTimeOffset]::TryParseExact(
         $Later,$format,$culture,$styles,[ref]$b) -or $b-lt$a){
      Stop-Observer 81 'Temporal gate drifted.'}
}
function Assert-FormalPreLatch([psobject]$Contract,[string]$Raw) {
    if(-not[bool]$Contract.formalExecutionEligible -or
       [string]$Contract.authority.profile-cne'Formal'){
      Stop-Observer 64 'Test fixtures are never formal-execution eligible.'}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if(-not[Environment]::Is64BitOperatingSystem -or
       -not[Environment]::Is64BitProcess -or
       $PSVersionTable.PSVersion.Major-ne5 -or
       [Environment]::MachineName-cne$Contract.authority.identity.machineName -or
       $identity.User.Value-cne$fixedSid -or
       [Diagnostics.Process]::GetCurrentProcess().SessionId-ne$fixedSession -or
       $principal.IsInRole(
         [Security.Principal.WindowsBuiltInRole]::Administrator) -or
       -not[Environment]::UserInteractive){
      Stop-Observer 66 'Identity/environment gate drifted.'}
    Initialize-NativeIdentity
    Assert-FormalTokenProof $Contract
    $rawLength=[Text.UTF8Encoding]::new($false,$true).GetByteCount($Raw)
    if($rawLength-ne[int]$Contract.bindingManifest.contractLength){
      Stop-Observer 67 'Contract length drifted.'}
    $pattern='(?m)^    "contractCanonicalSha256": "([0-9A-F]{64})",$'
    $matches=[regex]::Matches($Raw,$pattern)
    if($matches.Count-ne1){Stop-Observer 67 'Contract self-hash field drifted.'}
    $zeroed=[regex]::Replace(
      $Raw,$pattern,
      '    "contractCanonicalSha256": "'+('0'*64)+'",')
    if((Get-TextHash $zeroed)-cne
       [string]$Contract.bindingManifest.contractCanonicalSha256){
      Stop-Observer 67 'Contract self-hash drifted.'}
    Assert-ExactNames $Contract.authority.roots.bundleRoot @(
      'outer-launcher.ps1','launch-observer.ps1',
      'launch-observer-contract.json') 68
    Assert-Identity $Contract.authority.roots.bundleRoot $true $null (
      [string]$Contract.authority.source.rootSddl) 68
    $bundleFileSddl=[string]$Contract.authority.source.fileSddl
    foreach($path in @($fixedOuterPath,$fixedObserverPath,$fixedContractPath)){
      Assert-Identity $path $false $null $bundleFileSddl 69}
    if((Get-Hash $fixedObserverPath)-cne
         [string]$Contract.bindingManifest.observer.sha256 -or
       (Get-Hash $fixedOuterPath)-cne
         [string]$Contract.bindingManifest.outerLauncher.sha256){
      Stop-Observer 69 'Bundle binding drifted.'}
    Assert-ExactNames $Contract.authority.roots.sourceRoot @(
      'elevated-reconcile.ps1','recovery-contract.json') 70
    Assert-CurrentRoot $Contract.authority.currentBindings.sourceRoot 70
    foreach($file in @($Contract.authority.source.files)){
      Assert-CurrentFile $file 70}
    $recoveryRaw=[IO.File]::ReadAllText(
      $fixedRecoveryPath,[Text.UTF8Encoding]::new($false,$true))
    if((Get-Hash $fixedRecoveryPath)-cne
       [string]$Contract.bindingManifest.recoveryContract.sha256){
      Stop-Observer 70 'Recovery hash drifted.'}
    $recovery=$recoveryRaw|ConvertFrom-Json
    if([int]$recovery.schemaVersion-ne3){
      Stop-Observer 70 'Recovery schema drifted.'}
    Import-Module $fixedRecoveryValidatorPath -Force
    $recoveryModel=[pscustomobject][ordered]@{
      schemaVersion=1;authorityProfile=$fixedRecoveryAuthorityProfile
      contractId=$fixedRecoveryAuthorityContractId
      checkpoint='CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR'
      runId=$fixedRunId
      rootBinding=[pscustomobject][ordered]@{
        fixtureId=$fixedRecoveryFixtureId
        sourceLeafName=$fixedRecoverySourceLeaf}}
    $validated=Test-FslStage4RecoveryAuthorityBundle -Model $recoveryModel
    if(-not[bool]$validated.isValid -or $null-eq$validated.opaqueAuthority){
      Stop-Observer 70 'Recovery authority validation failed.'}
    $opaque=$validated.opaqueAuthority
    if([string]$opaque.executionStateAuthoritySha256-cne
         [string]$Contract.bindingManifest.executionStateAuthoritySha256 -or
       [string]$opaque.recoveryToolchainAuthoritySha256-cne
         [string]$Contract.bindingManifest.recoveryToolchainAuthoritySha256 -or
       [string]$opaque.toolchainRepositorySha256-cne
         [string]$Contract.bindingManifest.toolchainRepositorySha256 -or
       [string]$opaque.recoveryGateMapSha256-cne
         [string]$Contract.bindingManifest.recoveryGateMapSha256){
      Stop-Observer 70 'Opaque recovery bindings drifted.'}
    $gates=@($opaque.gates);$gatePrefix='FSL-RAB-CG-';$gateCount=56
    if($gates.Count-ne$gateCount){Stop-Observer 70 'Recovery gate count drifted.'}
    $map=@();$exitMap=[Collections.Generic.Dictionary[int,string]]::new()
    for($i=0;$i-lt$gateCount;$i++){
      if(@($gates[$i].PSObject.Properties).Count-ne2 -or
         $gates[$i].PSObject.Properties[0].Name-cne'gateId' -or
         $gates[$i].PSObject.Properties[1].Name-cne'exitCode' -or
         $gates[$i].exitCode-isnot[int] -or
         -not([string]$gates[$i].gateId).StartsWith(
           ($gatePrefix+('{0:D3}-'-f($i+1))),[StringComparison]::Ordinal) -or
         [int]$gates[$i].exitCode-ne84+$i -or
         $exitMap.ContainsKey([int]$gates[$i].exitCode)){
         Stop-Observer 70 'Recovery gate map drifted.'}
      $exitMap.Add([int]$gates[$i].exitCode,[string]$gates[$i].gateId)
      $map+=([string]$gates[$i].gateId)+'|'+[string][int]$gates[$i].exitCode}
    if((Get-TextHash($map-join"`n"))-cne
       [string]$Contract.authority.source.recoveryGateMapSha256){
      Stop-Observer 70 'Recovery gate-map hash drifted.'}
    if([string]$opaque.recoveryRepository-cne$fixedRepository -or
       [string]$opaque.recoveryGitCommit-cne$fixedGitHead -or
       [string]$opaque.recoveryGitTree-cne$fixedGitTree -or
       [string]$opaque.executionGitCommit-ceq
         [string]$opaque.recoveryGitCommit){
      Stop-Observer 71 'Dual recovery authority drifted.'}
    Assert-GitAuthority $Contract
    Assert-CurrentRoot $Contract.authority.currentBindings.evidenceRoot 72
    foreach($file in @($Contract.authority.currentBindings.evidenceFiles)){
      Assert-CurrentFile $file 72}
    Assert-CurrentRoot $Contract.authority.currentBindings.externalAnchorRoot 73
    foreach($file in @($Contract.authority.currentBindings.externalAnchorFiles)){
      Assert-CurrentFile $file 73}
    Assert-ExactNames $Contract.authority.canonical.evidenceRoot @(
      $Contract.authority.canonical.evidenceFiles|ForEach-Object{
        [IO.Path]::GetFileName($_.path)}) 72
    Assert-ExactNames $Contract.authority.canonical.externalAnchorRoot @(
      $Contract.authority.canonical.externalAnchorFiles|ForEach-Object{
        [IO.Path]::GetFileName($_.path)}) 73
    $release=@(Get-ChildItem -LiteralPath $Contract.authority.release.root -File)
    if($release.Count-ne[int]$Contract.authority.release.fileCount){
      Stop-Observer 74 'Release exact set drifted.'}
    $releaseLines=@($release|Sort-Object Name|ForEach-Object{
      $_.Name+'|'+$_.Length+'|'+(Get-Hash $_.FullName)})
    if((Get-TextHash($releaseLines-join"`n"))-cne
       [string]$Contract.authority.release.fingerprintSha256){
      Stop-Observer 74 'Release fingerprint drifted.'}
    Assert-CurrentRoot $Contract.authority.currentBindings.releaseRoot 74
    foreach($file in @($Contract.authority.currentBindings.releaseFiles)){
      Assert-CurrentFile $file 74}
    $installExists=Test-Path `
      -LiteralPath $Contract.authority.systemState.installDirectory `
      -PathType Container
    if(-not$installExists){
      Stop-Observer 75 'Install directory drifted.'}
    Assert-CurrentRoot $Contract.authority.currentBindings.transactionDirectory 75
    $service=Get-Service -Name 'FolderSessionLockRecovery' -ErrorAction SilentlyContinue
    $appInfo=Get-Service -Name 'AppInfo' -ErrorAction SilentlyContinue
    $enableLua=Get-ItemPropertyValue `
      -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
      -Name EnableLUA -ErrorAction Stop
    $processes=@(Get-Process -Name @(
      'FolderSessionLock.App','FolderSessionLock.Broker',
      'FolderSessionLock.Recovery','FolderSessionLock.Service') `
      -ErrorAction SilentlyContinue)
    if(-not[bool]$Contract.authority.systemState.programDataAbsent -or
       $(Test-Path -LiteralPath (
          $Contract.authority.systemState.programDataRoot)) -or
       -not[bool]$Contract.authority.systemState.serviceRegistryAbsent -or
       $(Test-Path -LiteralPath (
          $Contract.authority.systemState.serviceRegistryPath)) -or
       -not[bool]$Contract.authority.systemState.serviceAbsent -or
       $null-ne$service -or $processes.Count-ne0 -or
       [int]$Contract.authority.systemState.productProcessCount-ne$processes.Count -or
       [int]$Contract.authority.systemState.enableLua-ne[int]$enableLua -or
       [int]$enableLua-ne1 -or $null-eq$appInfo -or
       [string]$Contract.authority.systemState.appInfoStatus-cne
         [string]$appInfo.Status -or [string]$appInfo.Status-cne'Running'){
      Stop-Observer 75 'System-state gate drifted.'}
    if(Test-Path -LiteralPath $Contract.policy.latch.path){
      Stop-Observer 65 'Latch already exists.'}
    if([string]$Contract.policy.nativeOuterLaunch.commandLine-cne
         $fixedCommandLine -or
       [string]$Contract.policy.nativeOuterLaunch.applicationName-cne
         $fixedPowerShell -or
       [string]$Contract.policy.nativeOuterLaunch.workingDirectory-cne
         $fixedWorkingDirectory){
      Stop-Observer 67 'Canonical command line drifted.'}
    $creationFlags=@($Contract.policy.nativeOuterLaunch.creationFlags)
    if($creationFlags.Count-ne2 -or
       $creationFlags[0]-isnot[string] -or
       [string]$creationFlags[0]-cne'CREATE_BREAKAWAY_FROM_JOB' -or
       $creationFlags[1]-isnot[string] -or
       [string]$creationFlags[1]-cne'CREATE_NO_WINDOW' -or
       [string]$Contract.policy.nativeOuterLaunch.numericCreationFlags-cne
         '0x09000000'){
      Stop-Observer 67 'Native creation flags drifted.'}
    $runAs=$Contract.policy.recoveryRunAs
    $runAsNames=@($runAs.PSObject.Properties|ForEach-Object Name)
    if($runAsNames.Count-ne5 -or
       ($runAsNames-join'|')-cne
         'applicationName|argumentLine|verb|passThru|wait' -or
       [string]$runAs.applicationName-cne$fixedPowerShell -or
       [string]$runAs.argumentLine-cne$fixedRecoveryArgumentLine -or
       [string]$runAs.verb-cne'RunAs' -or
       $runAs.passThru-isnot[bool] -or -not[bool]$runAs.passThru -or
       $runAs.wait-isnot[bool] -or -not[bool]$runAs.wait){
      Stop-Observer 67 'Recovery RunAs policy drifted.'}
    $expectedArgv=@(
      'dummy.exe','-NoLogo','-NoProfile','-NonInteractive',
      '-ExecutionPolicy','Bypass','-File',$fixedWrapperPath)
    $parsedArgv=@([FslFormalObserverIdentity]::ParseWindowsCommandLine(
      '"dummy.exe" '+$fixedRecoveryArgumentLine))
    if($parsedArgv.Count-ne$expectedArgv.Count){
      Stop-Observer 67 'Recovery argv cardinality drifted.'}
    for($argvIndex=0;$argvIndex-lt$expectedArgv.Count;$argvIndex++){
      if([string]$parsedArgv[$argvIndex]-cne[string]$expectedArgv[$argvIndex]){
        Stop-Observer 67 'Recovery argv drifted.'}}
    return [pscustomobject][ordered]@{
      wrapperSha256=Get-Hash $fixedWrapperPath
      recoveryContractSha256=Get-Hash $fixedRecoveryPath
      exitCodeToGateId=$exitMap}
}

function New-Terminal(
  [string]$Outcome,[AllowNull()][object]$TargetPid,
  [AllowNull()][object]$ExitCode,[Collections.Generic.Dictionary[int,string]]$Map){
    if($Outcome-cin@('UacCancelled','LaunchFailed')){
      return [pscustomobject][ordered]@{
        outcome=$Outcome;targetPid=$null;exitCode=$null;gateId=$null}}
    if($Outcome-cne'Exited' -or $TargetPid-isnot[int] -or
       [int]$TargetPid-le0 -or $ExitCode-isnot[int]){
      Stop-Observer 78 'Terminal result is invalid.'}
    $gate=$null
    if([int]$ExitCode-ne0 -and $Map.ContainsKey([int]$ExitCode)){
      $gate=$Map[[int]$ExitCode]}
    return [pscustomobject][ordered]@{
      outcome='Exited';targetPid=[int]$TargetPid;exitCode=[int]$ExitCode
      gateId=$gate}
}

try {
  if($PSBoundParameters.Count-ne0 -or $args.Count-ne0){
    Stop-Observer 64 'No runtime metadata is allowed.'}
  if(Test-Path -LiteralPath (Join-Path(
       (Split-Path -Parent $fixedObserverPath),'launch-attempt.jsonl'))){
    Stop-Observer 65 'Latch already exists.'}
  $raw=[IO.File]::ReadAllText(
    $fixedContractPath,[Text.UTF8Encoding]::new($false,$true))
  $contract=$raw|ConvertFrom-Json
  $contexts=@(Assert-FormalPreLatch $contract $raw)
  if($contexts.Count-ne1){Stop-Observer 67 'Pre-latch context cardinality drifted.'}
  $context=$contexts[0]
  $wrapperHash=[string]$context.wrapperSha256
  $recoveryHash=[string]$context.recoveryContractSha256
  $record1=[pscustomobject][ordered]@{
    schemaVersion=1;recordOrdinal=1;attemptId=$fixedAttemptId;runId=$fixedRunId
    checkpoint=$fixedCheckpoint;wrapperSha256=$wrapperHash
    recoveryContractSha256=$recoveryHash;phase='LaunchCommitted';status='Pending'
    outcome=$null;observerPid=[int]$PID;targetPid=$null;exitCode=$null
    gateId=$null;timestampUtc=Get-Utc}
  $stream=[IO.FileStream]::new(
    $contract.policy.latch.path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,
    [IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
  try{Write-Record $stream $record1}finally{$stream.Dispose()}
  Assert-Latch $contract.policy.latch.path @($record1)
  $time2=Get-Utc;Assert-Temporal $record1.timestampUtc $time2
  $record2=[pscustomobject][ordered]@{
    schemaVersion=1;recordOrdinal=2;attemptId=$fixedAttemptId;runId=$fixedRunId
    checkpoint=$fixedCheckpoint;wrapperSha256=$wrapperHash
    recoveryContractSha256=$recoveryHash;phase='RunAsInvoking';status='Pending'
    outcome=$null;observerPid=[int]$PID;targetPid=$null;exitCode=$null
    gateId=$null;timestampUtc=$time2}
  $stream=[IO.FileStream]::new(
    $contract.policy.latch.path,[IO.FileMode]::Append,[IO.FileAccess]::Write,
    [IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
  try{Write-Record $stream $record2}finally{$stream.Dispose()}
  Assert-Latch $contract.policy.latch.path @($record1,$record2)
  $child=$null;$childExit=$null;$target=$null;$outcome='LaunchFailed'
  try{
    $child=Start-Process -FilePath $fixedPowerShell `
      -ArgumentList $fixedRecoveryArgumentLine -Verb RunAs -PassThru -Wait
    $target=[int]$child.Id;$childExit=[int]$child.ExitCode;$outcome='Exited'
  }
  catch [ComponentModel.Win32Exception] {
    if($_.Exception.NativeErrorCode-eq1223){$outcome='UacCancelled'}
  }
  catch {$outcome='LaunchFailed'}
  Assert-Latch $contract.policy.latch.path @($record1,$record2)
  $time3=Get-Utc;Assert-Temporal $record2.timestampUtc $time3
  $terminal=New-Terminal $outcome $target $childExit $context.exitCodeToGateId
  $record3=[pscustomobject][ordered]@{
    schemaVersion=1;recordOrdinal=3;attemptId=$fixedAttemptId;runId=$fixedRunId
    checkpoint=$fixedCheckpoint;wrapperSha256=$wrapperHash
    recoveryContractSha256=$recoveryHash;phase='LaunchResult'
    status='Completed';outcome=$terminal.outcome;observerPid=[int]$PID
    targetPid=$terminal.targetPid;exitCode=$terminal.exitCode
    gateId=$terminal.gateId;timestampUtc=$time3}
  $stream=[IO.FileStream]::new(
    $contract.policy.latch.path,[IO.FileMode]::Append,[IO.FileAccess]::Write,
    [IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
  try{Write-Record $stream $record3}finally{$stream.Dispose()}
  Assert-Latch $contract.policy.latch.path @($record1,$record2,$record3)
  if($outcome-cne'Exited'){Stop-Observer 77 'RunAs failed.'}
  if($childExit-ne0){Stop-Observer 79 'Recovery returned nonzero.'}
  exit 0
}
catch {Stop-Observer 80 'Observer failed closed.'}
'@
    $source = @($Authority.source.files)
    $recoveryValidatorPath = Join-Path $PSScriptRoot (
        'FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
    $fixtureLiteral = if ($null -eq $Model.rootBinding.fixtureId) {
        '$null'
    }
    else {
        ConvertTo-FslFlbLiteral ([string]$Model.rootBinding.fixtureId)
    }
    $trackedCleanLiteral = if ([bool]$Authority.repository.trackedClean) {
        '$true'
    }
    else {
        '$false'
    }
    $text = $template.
        Replace('@@CONTRACT@@', (ConvertTo-FslFlbLiteral $Policy.files.contractPath)).
        Replace('@@OBSERVER@@', (ConvertTo-FslFlbLiteral $Policy.files.observerPath)).
        Replace('@@OUTER@@', (ConvertTo-FslFlbLiteral $Policy.files.outerLauncherPath)).
        Replace('@@RECOVERY@@', (ConvertTo-FslFlbLiteral $source[1].path)).
        Replace('@@WRAPPER@@', (ConvertTo-FslFlbLiteral $source[0].path)).
        Replace('@@RECOVERY_VALIDATOR@@', (
            ConvertTo-FslFlbLiteral $recoveryValidatorPath)).
        Replace('@@RECOVERY_PROFILE@@', (
            ConvertTo-FslFlbLiteral ([string]$Model.authorityProfile))).
        Replace('@@RECOVERY_CONTRACT_ID@@', (
            ConvertTo-FslFlbLiteral (
                [string]$Model.recoveryAuthority.contractId))).
        Replace('@@RECOVERY_FIXTURE_ID@@', $fixtureLiteral).
        Replace('@@RECOVERY_SOURCE_LEAF@@', (
            ConvertTo-FslFlbLiteral (
                [string]$Model.rootBinding.sourceLeafName))).
        Replace('@@REPOSITORY@@', (ConvertTo-FslFlbLiteral $Authority.repository.projectRoot)).
        Replace('@@GITROOT@@', (ConvertTo-FslFlbLiteral $Authority.repository.gitRoot)).
        Replace('@@GITDIR@@', (ConvertTo-FslFlbLiteral $Authority.repository.gitDirectory)).
        Replace('@@GITBRANCH@@', (ConvertTo-FslFlbLiteral $Authority.repository.branch)).
        Replace('@@GITHEAD@@', (ConvertTo-FslFlbLiteral $Authority.repository.head)).
        Replace('@@GITTREE@@', (ConvertTo-FslFlbLiteral $Authority.repository.tree)).
        Replace('@@TRACKEDCLEAN@@', $trackedCleanLiteral).
        Replace('@@POWERSHELL@@', (ConvertTo-FslFlbLiteral $Authority.executable.powerShellPath)).
        Replace('@@WORKING@@', (ConvertTo-FslFlbLiteral $Authority.executable.workingDirectory)).
        Replace('@@COMMAND@@', (ConvertTo-FslFlbLiteral $Policy.nativeOuterLaunch.commandLine)).
        Replace('@@RECOVERY_ARGUMENT_LINE@@', (ConvertTo-FslFlbLiteral $Policy.recoveryRunAs.argumentLine)).
        Replace('@@SID@@', (ConvertTo-FslFlbLiteral $Authority.identity.userSid)).
        Replace('@@SESSION@@', ([string][int]$Authority.identity.sessionId)).
        Replace('@@RUNID@@', (ConvertTo-FslFlbLiteral ([string]$Model.runId))).
        Replace('@@CONTRACT_ID@@', (ConvertTo-FslFlbLiteral ([string]$Model.contractId))).
        Replace('@@ATTEMPT_ID@@', (ConvertTo-FslFlbLiteral ([string]$Model.attemptId))).
        Replace('@@CHECKPOINT@@', (ConvertTo-FslFlbLiteral ([string]$Model.checkpoint)))
    $text = $text.Replace(
        '@@LATCH_HELPERS@@',
        $script:FlbLatchHelperTemplate.TrimEnd("`r", "`n"))
    return $text.Replace("`r`n", "`n").TrimEnd("`r", "`n") + "`n"
}

# Responsibility 6: canonical manifest with non-circular self hash.
function Get-FslFlbRecoveryAuthorityGateMapSha256 {
    param([psobject]$Authority)
    $record = @($Authority.source.files)[1]
    try {
        $bytes = [IO.File]::ReadAllBytes([string]$record.path)
        if ([int64]$record.length -ne [int64]$bytes.Length -or
            [string]$record.sha256 -cne
                (Get-FslFlbSha256Bytes $bytes)) {
            Stop-FslFlb `
                'FSL-FLB-V010-SOURCE-RECOVERY' `
                'The recovery contract binding drifted.' `
                $null
        }
        $raw = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $contract = $raw | ConvertFrom-Json
    }
    catch {
        if ($_.Exception.Data.Contains('FslFormalLauncherBundleCode')) {
            throw
        }
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The recovery contract binding could not be read.' `
            $_.Exception
    }
    $hash = [string]$contract.bindingManifest.recoveryGateMapSha256
    if ($hash -cnotmatch $script:FlbShaPattern) {
        Stop-FslFlb `
            'FSL-FLB-V010-SOURCE-RECOVERY' `
            'The recovery authority gate-map binding is invalid.' `
            $null
    }
    return $hash
}

function New-FslFlbContractBase {
    param(
        [psobject]$Model,
        [psobject]$Authority,
        [psobject]$Policy,
        [byte[]]$OuterBytes,
        [byte[]]$ObserverBytes)
    $recoveryAuthorityGateMapSha256 =
        Get-FslFlbRecoveryAuthorityGateMapSha256 $Authority
    return [ordered]@{
        schemaVersion = 1
        authorityProfile = [string]$Model.authorityProfile
        contractId = [string]$Model.contractId
        checkpoint = [string]$Model.checkpoint
        attemptId = [string]$Model.attemptId
        runId = [string]$Model.runId
        formalExecutionEligible = [bool]$Authority.formalExecutionEligible
        authority = $Authority
        policy = $Policy
        bindingManifest = [ordered]@{
            schemaVersion = 1
            fileOrder = $script:FlbBundleNames
            outerLauncher = [ordered]@{
                name = 'outer-launcher.ps1'
                length = $OuterBytes.Length
                sha256 = Get-FslFlbSha256Bytes $OuterBytes
            }
            observer = [ordered]@{
                name = 'launch-observer.ps1'
                length = $ObserverBytes.Length
                sha256 = Get-FslFlbSha256Bytes $ObserverBytes
            }
            contractName = 'launch-observer-contract.json'
            contractLength = 0
            contractCanonicalSha256 = $script:FlbZeros
            hashRule = $script:FlbSelfHashRule
            recoveryWrapper = [ordered]@{
                name = 'elevated-reconcile.ps1'
                length = [int64]$Authority.source.files[0].length
                sha256 = [string]$Authority.source.files[0].sha256
            }
            recoveryContract = [ordered]@{
                name = 'recovery-contract.json'
                length = [int64]$Authority.source.files[1].length
                sha256 =
                    [string]$Authority.source.recoveryContractSha256
            }
            recoveryGateMapSha256 =
                $recoveryAuthorityGateMapSha256
            executionStateAuthoritySha256 =
                $Authority.source.executionStateAuthoritySha256
            recoveryToolchainAuthoritySha256 =
                $Authority.source.recoveryToolchainAuthoritySha256
            toolchainRepositorySha256 =
                $Authority.source.toolchainRepositorySha256
            currentAuthorityCanonicalSha256 = Get-FslFlbSha256Bytes (
                Get-FslFlbBytes (
                    ConvertTo-FslFlbCanonicalJson $Authority))
        }
    }
}

function Complete-FslFlbContract {
    param([Collections.IDictionary]$Contract)
    for ($iteration = 0; $iteration -lt 8; $iteration++) {
        $Contract.bindingManifest.contractCanonicalSha256 = $script:FlbZeros
        $bytes = Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Contract)
        if ([int]$Contract.bindingManifest.contractLength -eq $bytes.Length) {
            break
        }
        $Contract.bindingManifest.contractLength = $bytes.Length
    }
    $zeroBytes = Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Contract)
    if ($zeroBytes.Length -ne [int]$Contract.bindingManifest.contractLength) {
        Stop-FslFlb 'FSL-FLB-V006-BINDING' 'Contract length did not stabilize.' $null
    }
    $Contract.bindingManifest.contractCanonicalSha256 =
        Get-FslFlbSha256Bytes $zeroBytes
    $actual = Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Contract)
    if ($actual.Length -ne [int]$Contract.bindingManifest.contractLength) {
        Stop-FslFlb 'FSL-FLB-V006-BINDING' 'Contract self-hash changed its length.' $null
    }
    return ,$actual
}

# Responsibility 7: AST/static validator and stable errors.
function New-FslFlbError {
    param([string]$Code, [string]$Target, [string]$Detail)
    return [pscustomobject][ordered]@{
        code = $Code
        target = $Target
        detail = $Detail
    }
}

function Sort-FslFlbErrors {
    param([Collections.IList]$Errors)
    $array = @($Errors)
    [Array]::Sort($array, [Comparison[object]]{
        param($left, $right)
        $value = [string]::Compare(
            [string]$left.code,
            [string]$right.code,
            [StringComparison]::Ordinal)
        if ($value -eq 0) {
            $value = [string]::Compare(
                [string]$left.target,
                [string]$right.target,
                [StringComparison]::Ordinal)
        }
        if ($value -eq 0) {
            $value = [string]::Compare(
                [string]$left.detail,
                [string]$right.detail,
                [StringComparison]::Ordinal)
        }
        return $value
    })
    return $array
}

function Get-FslFlbAst {
    param([string]$Path)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    return [pscustomobject]@{ ast = $ast; errors = @($errors) }
}

function Get-FslFlbAndLeaves {
    param($Ast)
    if ($Ast -is [Management.Automation.Language.ParenExpressionAst]) {
        return @(Get-FslFlbAndLeaves $Ast.Pipeline)
    }
    if ($Ast -is [Management.Automation.Language.PipelineAst] -and
        $Ast.PipelineElements.Count -eq 1) {
        return @(Get-FslFlbAndLeaves $Ast.PipelineElements[0])
    }
    if ($Ast -is [Management.Automation.Language.CommandExpressionAst]) {
        return @(Get-FslFlbAndLeaves $Ast.Expression)
    }
    if ($Ast -is [Management.Automation.Language.BinaryExpressionAst] -and
        $Ast.Operator -eq [Management.Automation.Language.TokenKind]::And) {
        return @(
            Get-FslFlbAndLeaves $Ast.Left
            Get-FslFlbAndLeaves $Ast.Right)
    }
    return @($Ast)
}

function Get-FslFlbPredicateClassification {
    param([string]$ActualPath, [string]$ExpectedText)
    $parsed = Get-FslFlbAst $ActualPath
    if ($parsed.errors.Count -ne 0) { return $null }
    $assignment = @($parsed.ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -ceq 'contractValid'
    }, $true))
    if ($assignment.Count -ne 1) {
        return [pscustomobject]@{ kind = 'set'; ordinal = 0 }
    }
    $leaves = @(Get-FslFlbAndLeaves $assignment[0].Right)
    if ($leaves.Count -ne 22) {
        return [pscustomobject]@{ kind = 'set'; ordinal = 0 }
    }
    $expected = @(Get-FslFlbPredicateTexts)
    $different = @()
    for ($index = 0; $index -lt 22; $index++) {
        $actual = $leaves[$index].Extent.Text.Trim()
        while ($actual.StartsWith('(') -and $actual.EndsWith(')')) {
            $actual = $actual.Substring(1, $actual.Length - 2).Trim()
        }
        if ($actual -cne $expected[$index]) { $different += $index }
    }
    if ($different.Count -eq 0) { return $null }
    if ($different.Count -eq 1) {
        $index = $different[0]
        return [pscustomobject]@{
            kind = 'single'
            ordinal = $index + 1
        }
    }
    return [pscustomobject]@{ kind = 'set'; ordinal = 0 }
}

function Get-FslFlbObservedFiles {
    param([string]$Root)
    $records = @()
    foreach ($name in $script:FlbBundleNames) {
        $path = Join-Path $Root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $records += [pscustomobject][ordered]@{
                name = $name
                path = $path
                length = (Get-Item -LiteralPath $path).Length
                sha256 = Get-FslFlbSha256 $path
            }
        }
        else {
            $records += [pscustomobject][ordered]@{
                name = $name
                path = $path
                length = $null
                sha256 = $null
            }
        }
    }
    return $records
}

function Remove-FslFlbPartial {
    param([string]$Root, [hashtable]$Hashes)
    if (-not (Test-Path -LiteralPath $Root -PathType Container) -or
        -not (Test-FslFlbOrdinary $Root $true)) { return }
    $children = @(Get-ChildItem -LiteralPath $Root -Force)
    foreach ($child in $children) {
        if ($child.PSIsContainer -or
            -not $Hashes.ContainsKey($child.Name) -or
            -not (Test-FslFlbOrdinary $child.FullName $false) -or
            (Get-FslFlbSha256 $child.FullName) -cne $Hashes[$child.Name]) {
            return
        }
    }
    foreach ($child in $children) { [IO.File]::Delete($child.FullName) }
    if (@(Get-ChildItem -LiteralPath $Root -Force).Count -eq 0) {
        [IO.Directory]::Delete($Root, $false)
    }
}

# Responsibility 8: exact two public commands.
function New-FslStage4FormalLauncherBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [psobject]$Model)
    Assert-FslFlbModel $Model
    $authority = Resolve-FslFlbAuthority $Model
    $policy = Get-FslFlbPolicy $Model $authority
    $root = $authority.roots.bundleRoot
    if (Test-Path -LiteralPath $root) {
        Stop-FslFlb 'FSL-FLB-V002-ROOT' 'The internal bundle root must not exist.' $null
    }
    $parent = Split-Path -Parent $root
    if (-not (Test-FslFlbOrdinary $parent $true)) {
        Stop-FslFlb 'FSL-FLB-V002-ROOT' 'The bundle parent identity is invalid.' $null
    }
    $outerBytes = Get-FslFlbBytes (
        Render-FslFlbOuter $Model $authority $policy)
    $observerBytes = Get-FslFlbBytes (
        Render-FslFlbObserver $Model $authority $policy)
    $contract = New-FslFlbContractBase `
        $Model `
        $authority `
        $policy `
        $outerBytes `
        $observerBytes
    $contractBytes = Complete-FslFlbContract $contract
    $hashes = @{
        'outer-launcher.ps1' = Get-FslFlbSha256Bytes $outerBytes
        'launch-observer.ps1' = Get-FslFlbSha256Bytes $observerBytes
        'launch-observer-contract.json' = Get-FslFlbSha256Bytes $contractBytes
    }
    $fileSddl = [string]$authority.source.fileSddl
    try {
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Set-FslFlbSddl $root $authority.source.rootSddl $true
        if (-not (Test-FslFlbOrdinary $root $true) -or
            -not (Test-FslFlbProtectedAcl `
                $root `
                $true `
                $authority.identity.userSid `
                $authority.source.rootSddl)) {
            Stop-FslFlb 'FSL-FLB-V013-ACL' 'The bundle-root ACL failed.' $null
        }
        foreach ($item in @(
            @('outer-launcher.ps1', $outerBytes),
            @('launch-observer.ps1', $observerBytes),
            @('launch-observer-contract.json', $contractBytes))) {
            $path = Join-Path $root $item[0]
            Write-FslFlbNew $path ([byte[]]$item[1])
            Set-FslFlbSddl $path $fileSddl $false
            if (-not (Test-FslFlbOrdinary $path $false) -or
                -not (Test-FslFlbProtectedAcl `
                    $path `
                    $false `
                    $authority.identity.userSid `
                    $fileSddl)) {
                Stop-FslFlb `
                    'FSL-FLB-V013-ACL' `
                    ('A bundle-file identity or ACL failed: ' + $item[0] + '.') `
                    $null
            }
        }
    }
    catch {
        Remove-FslFlbPartial $root $hashes
        if ($_.Exception.Data.Contains('FslFormalLauncherBundleCode')) { throw }
        Stop-FslFlb 'FSL-FLB-V002-ROOT' 'Bundle generation failed.' $_.Exception
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        bundleRoot = $root
        contractCanonicalSha256 =
            [string]$contract.bindingManifest.contractCanonicalSha256
        observedFiles = @(Get-FslFlbObservedFiles $root)
    }
}

function Test-FslStage4FormalLauncherBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [psobject]$Model)
    $errors = [Collections.Generic.List[object]]::new()
    $root = $null
    $observed = @()
    try {
        Assert-FslFlbModel $Model
        $authority = Resolve-FslFlbAuthority $Model
        $policy = Get-FslFlbPolicy $Model $authority
        $root = $authority.roots.bundleRoot
        if (-not (Test-Path -LiteralPath $root -PathType Container) -or
            -not (Test-FslFlbOrdinary $root $true)) {
            [void]$errors.Add((New-FslFlbError `
                'FSL-FLB-V002-ROOT' `
                'bundleRoot' `
                'The internal bundle root identity is invalid.'))
        }
        else {
            $observed = @(Get-FslFlbObservedFiles $root)
            $actualNames = @(
                Get-ChildItem -LiteralPath $root -Force |
                    ForEach-Object { $_.Name })
            if (-not (Test-FslFlbExactSet $actualNames $script:FlbBundleNames)) {
                [void]$errors.Add((New-FslFlbError `
                    'FSL-FLB-V003-FILESET' `
                    'bundleRoot' `
                    'The bundle is not exact-three with exact case.'))
            }
            $outerBytes = Get-FslFlbBytes (
                Render-FslFlbOuter $Model $authority $policy)
            $observerBytes = Get-FslFlbBytes (
                Render-FslFlbObserver $Model $authority $policy)
            $expectedContract = New-FslFlbContractBase `
                $Model `
                $authority `
                $policy `
                $outerBytes `
                $observerBytes
            $contractBytes = Complete-FslFlbContract $expectedContract
            $outerPath = Join-Path $root 'outer-launcher.ps1'
            $observerPath = Join-Path $root 'launch-observer.ps1'
            $contractPath = Join-Path $root 'launch-observer-contract.json'
            $predicate = if (Test-Path -LiteralPath $outerPath -PathType Leaf) {
                Get-FslFlbPredicateClassification `
                    $outerPath `
                    ([Text.UTF8Encoding]::new($false, $true).GetString($outerBytes))
            }
            else { $null }
            if ($null -ne $predicate) {
                if ($predicate.kind -ceq 'single') {
                    [void]$errors.Add((New-FslFlbError `
                        ('FSL-FLB-V009-PREDICATE-{0:D2}' -f $predicate.ordinal) `
                        'outer-launcher.ps1' `
                        'Exactly one exit-68 predicate changed.'))
                }
                else {
                    [void]$errors.Add((New-FslFlbError `
                        'FSL-FLB-V009-PREDICATE-SET' `
                        'outer-launcher.ps1' `
                        'The exact ordered 22-predicate set changed.'))
                }
            }
            else {
                foreach ($item in @(
                    @($outerPath, $outerBytes),
                    @($observerPath, $observerBytes),
                    @($contractPath, $contractBytes))) {
                    if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf) -or
                        -not (Test-FslFlbOrdinary $item[0] $false) -or
                        (Get-FslFlbSha256 $item[0]) -cne
                            (Get-FslFlbSha256Bytes ([byte[]]$item[1]))) {
                        [void]$errors.Add((New-FslFlbError `
                            'FSL-FLB-V004-FILE-BYTES' `
                            ([IO.Path]::GetFileName($item[0])) `
                            'A bundle file is not its canonical byte sequence.'))
                    }
                }
                if (Test-Path -LiteralPath $contractPath -PathType Leaf) {
                    try {
                        $raw = [IO.File]::ReadAllText(
                            $contractPath,
                            [Text.UTF8Encoding]::new($false, $true))
                        $parsed = $raw | ConvertFrom-Json
                        $expectedText = [Text.UTF8Encoding]::new(
                            $false,
                            $true).GetString($contractBytes)
                        if ($raw -cne $expectedText -or
                            [int]$parsed.bindingManifest.contractLength -ne
                                [Text.UTF8Encoding]::new(
                                    $false,
                                    $true).GetByteCount($raw)) {
                            [void]$errors.Add((New-FslFlbError `
                                'FSL-FLB-V005-CONTRACT-CANONICAL' `
                                'launch-observer-contract.json' `
                                'The canonical manifest or self hash drifted.'))
                        }
                    }
                    catch {
                        [void]$errors.Add((New-FslFlbError `
                            'FSL-FLB-V005-CONTRACT-CANONICAL' `
                            'launch-observer-contract.json' `
                            'The contract JSON is invalid.'))
                    }
                }
            }
            $fileSddl = [string]$authority.source.fileSddl
            if (-not (Test-FslFlbProtectedAcl `
                $root `
                $true `
                $authority.identity.userSid `
                $authority.source.rootSddl)) {
                [void]$errors.Add((New-FslFlbError `
                    'FSL-FLB-V013-ACL' `
                    'bundleRoot' `
                    'The bundle-root ACL drifted.'))
            }
            foreach ($name in $script:FlbBundleNames) {
                $path = Join-Path $root $name
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and
                    -not (Test-FslFlbProtectedAcl `
                        $path `
                        $false `
                        $authority.identity.userSid `
                        $fileSddl)) {
                    [void]$errors.Add((New-FslFlbError `
                        'FSL-FLB-V013-ACL' `
                        $name `
                        'A bundle-file ACL drifted.'))
                }
            }
            if (Test-Path -LiteralPath (
                Join-Path $root 'launch-attempt.jsonl')) {
                [void]$errors.Add((New-FslFlbError `
                    'FSL-FLB-V012-LATCH' `
                    'launch-attempt.jsonl' `
                    'The pre-execution latch must be absent.'))
            }
            foreach ($path in @($outerPath, $observerPath)) {
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    $parsed = Get-FslFlbAst $path
                    if ($parsed.errors.Count -ne 0) {
                        [void]$errors.Add((New-FslFlbError `
                            'FSL-FLB-V014-NONEXECUTION' `
                            ([IO.Path]::GetFileName($path)) `
                            ('Windows PowerShell 5.1 AST parsing failed: ' +
                                $parsed.errors[0].Message + ' @ ' +
                                $parsed.errors[0].Extent.StartLineNumber + ':' +
                                $parsed.errors[0].Extent.StartColumnNumber + '.')))
                    }
                }
            }
            if (Test-Path -LiteralPath $observerPath -PathType Leaf) {
                $observerAst = (Get-FslFlbAst $observerPath).ast
                $runAs = @($observerAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -ceq 'Start-Process'
                }, $true))
                $writes = @($observerAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $node.Member.Value -ceq 'new' -and
                    $node.Expression.Extent.Text -match 'FileStream'
                }, $true))
                $gate = @($observerAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -ceq 'Assert-FormalPreLatch'
                }, $true))
                if ($runAs.Count -ne 1 -or
                    $writes.Count -lt 1 -or
                    $gate.Count -ne 1 -or
                    $gate[0].Extent.StartOffset -gt
                        $writes[0].Extent.StartOffset -or
                    $gate[0].Extent.StartOffset -gt
                        $runAs[0].Extent.StartOffset) {
                    [void]$errors.Add((New-FslFlbError `
                        'FSL-FLB-V014-NONEXECUTION' `
                        'launch-observer.ps1' `
                        'The full pre-latch gate is not before every write and RunAs.'))
                }
            }
        }
    }
    catch {
        $code = [string]$_.Exception.Data['FslFormalLauncherBundleCode']
        if ([string]::IsNullOrEmpty($code)) { $code = 'FSL-FLB-V001-MODEL' }
        [void]$errors.Add((New-FslFlbError `
            $code `
            'authority' `
            $_.Exception.Message))
    }
    $sorted = @(Sort-FslFlbErrors $errors)
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        isValid = $sorted.Count -eq 0
        bundleRoot = $root
        errors = $sorted
        observedFiles = $observed
    }
}

Export-ModuleMember -Function @(
    'New-FslStage4FormalLauncherBundle',
    'Test-FslStage4FormalLauncherBundle')
