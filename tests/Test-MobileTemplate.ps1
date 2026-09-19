param([string] $TemplateApk, [string] $SignedApk)
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '..\scripts\Assert-MobileTemplate.ps1'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('vibecode-template-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$files = [Collections.Generic.List[string]]::new()
$cases = @(
    @{ Json = '{"configured":false}'; Accepted = $true; Size = 2048 },
    @{ Json = '{"configured":true}'; Accepted = $false; Size = 2048 },
    @{ Json = '{"configured":"false"}'; Accepted = $false; Size = 2048 },
    @{ Json = '{"configured":false,"pcName":"Example"}'; Accepted = $false; Size = 2048 },
    @{ Json = '{"Configured":false}'; Accepted = $false; Size = 2048 },
    @{ Json = '{"configured":"example","configured":false}'; Accepted = $false; Size = 2048 },
    @{ Json = '{}'; Accepted = $false; Size = 2048 },
    @{ Json = '{"configured":false}'; Accepted = $false; Size = 2047 }
)
try {
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $path = Join-Path $testDirectory ($index.ToString() + '.vcenroll')
        $files.Add($path)
        [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($case.Json.PadRight($case.Size, ' ')))
        $accepted = $true
        try { & $validator -Path $path -EnrollmentSlot }
        catch { $accepted = $false }
        if ($accepted -ne $case.Accepted) { throw "Enrollment validation failed for case $index." }
    }
    if ($TemplateApk) { & $validator -Path ([IO.Path]::GetFullPath($TemplateApk)) }
    if ($SignedApk) {
        $signedPath = (Resolve-Path -LiteralPath $SignedApk).Path
        $accepted = $true
        try { & $validator -Path $signedPath }
        catch { $accepted = $false }
        if ($accepted) { throw 'The validator accepted a signed template.' }
    }
    Write-Output ('PASS: {0} enrollment validation cases and supplied APK checks' -f $cases.Count)
}
finally {
    foreach ($file in $files) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($testDirectory)
}
