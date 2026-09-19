param(
    [Parameter(Mandatory = $true)] [string] $Path,
    [switch] $EnrollmentSlot
)
$ErrorActionPreference = 'Stop'

function Assert-Enrollment([byte[]] $Bytes) {
    if ($Bytes.Length -ne 2048) { throw 'Enrollment templates must contain exactly 2048 bytes.' }
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    # Match the complete schema, including duplicate fields that JSON parsers otherwise discard.
    if ($json -cnotmatch '\A\s*\{\s*"configured"\s*:\s*false\s*\}\s*\z') {
        throw 'The enrollment template must contain only the boolean configured: false.'
    }
}

if ($EnrollmentSlot) {
    Assert-Enrollment ([IO.File]::ReadAllBytes($Path))
    return
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($Path)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'assets/vibecode-enroll.vcenroll' })
    if ($entries.Count -ne 1 -or $entries[0].Length -ne 2048 -or $entries[0].CompressedLength -ne 2048) {
        throw 'The APK must contain one uncompressed 2048-byte enrollment template.'
    }
    $stream = $entries[0].Open()
    $buffer = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($buffer)
        Assert-Enrollment ($buffer.ToArray())
    }
    finally { $stream.Dispose(); $buffer.Dispose() }
    if ($archive.Entries | Where-Object { $_.FullName -match '^META-INF/.*\.(RSA|DSA|EC)$' }) {
        throw 'Use an unsigned APK as the desktop template.'
    }
}
finally { $archive.Dispose() }

# v2/v3 signing certificates sit outside ZIP entries, just before the central directory.
$apkBytes = [IO.File]::ReadAllBytes($Path)
$endRecord = -1
for ($offset = $apkBytes.Length - 22; $offset -ge [Math]::Max(0, $apkBytes.Length - 65557); $offset--) {
    if ([BitConverter]::ToUInt32($apkBytes, $offset) -eq 0x06054b50 -and
        $offset + 22 + [BitConverter]::ToUInt16($apkBytes, $offset + 20) -eq $apkBytes.Length) {
        $endRecord = $offset
        break
    }
}
if ($endRecord -lt 0) { throw 'The APK has no valid ZIP end record.' }
$directoryOffset = [BitConverter]::ToUInt32($apkBytes, $endRecord + 16)
if ($directoryOffset -gt $endRecord) { throw 'The APK central directory is outside the package.' }
if ($directoryOffset -ge 16 -and
    [Text.Encoding]::ASCII.GetString($apkBytes, $directoryOffset - 16, 16) -ceq 'APK Sig Block 42') {
    throw 'Use an unsigned APK as the desktop template.'
}
