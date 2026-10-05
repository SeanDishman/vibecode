param(
    [int[]]$Cases = (0..15),
    [string]$Executable = 'BuildOutput\VibeCode.BridgeTerminalTests\bin-orchestration-v2-final\Release\net8.0-windows\VibeCode.BridgeTerminalTests.exe',
    [string]$OutputDirectory = 'artifacts\orchestration-v2',
    [int]$GroupRuns = 2
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$results = @()
foreach ($caseIndex in $Cases) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $OutputDirectory ('luna-{0:00}.log' -f $caseIndex)
    & $Executable --live-simulations --workflow-case $caseIndex *> $log
    $result = [pscustomobject]@{ case = $caseIndex; exit_code = $LASTEXITCODE; seconds = $timer.Elapsed.TotalSeconds; log = $log }
    $results += $result
    if ($result.exit_code -eq 0) {
        $caseDirectory = Join-Path $OutputDirectory ('workflow-{0:00}' -f $caseIndex)
        New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath 'artifacts\shared-agent-panel\live-simulations' -Filter ('workflow-{0:00}*' -f $caseIndex) -File | Copy-Item -Destination $caseDirectory
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'luna-results.json') -Encoding UTF8
    Write-Output ('Luna case {0:00}: exit={1}; {2:N1}s' -f $caseIndex, $result.exit_code, $result.seconds)
}
for ($groupIndex = 1; $groupIndex -le $GroupRuns; $groupIndex++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $OutputDirectory ('luna-groups-{0}.log' -f $groupIndex)
    & $Executable --live-simulations --two-groups *> $log
    $result = [pscustomobject]@{ case = "groups-$groupIndex"; exit_code = $LASTEXITCODE; seconds = $timer.Elapsed.TotalSeconds; log = $log }
    $results += $result
    $caseDirectory = Join-Path $OutputDirectory ('groups-{0}' -f $groupIndex)
    New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null
    if ($result.exit_code -eq 0) {
        Get-ChildItem -LiteralPath 'artifacts\shared-agent-panel\live-simulations' -Filter 'two-orchestrators*' -File | Copy-Item -Destination $caseDirectory
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'luna-results.json') -Encoding UTF8
    Write-Output ('Luna group run {0}: exit={1}; {2:N1}s' -f $groupIndex, $result.exit_code, $result.seconds)
}
if ($results.Where({ $_.exit_code -ne 0 }).Count -gt 0) { exit 1 }
