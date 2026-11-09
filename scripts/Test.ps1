param([switch] $NoBuild, [ValidatePattern('^[a-zA-Z0-9-]*$')][string] $BuildSlot = '')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testEnvironment = @{}
$accountRoot = Join-Path $repositoryRoot ('artifacts\test-accounts-' + [Guid]::NewGuid().ToString('N'))
foreach ($name in @('VIBECODE_DATA_DIR','CODEX_HOME','VIBECODE_CODEX_ACCOUNT_STORE','VIBECODE_CODEX_LEGACY_HOME','VIBECODE_CLAUDE_ACCOUNT_STORE','CLAUDE_CONFIG_DIR','GEMINI_CLI_HOME')) {
    $testEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    $directory = Join-Path $accountRoot $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
}
Push-Location -LiteralPath $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'Test-PublicSource.ps1')
    $buildArguments = @()
    if ($BuildSlot) { $buildArguments = @("-p:OutputPath=bin-$BuildSlot/", "-p:BaseIntermediateOutputPath=obj-$BuildSlot/") }
    $builtProjects = [Collections.Generic.HashSet[string]]::new()
    function Invoke-Regression {
        param([string] $ProjectName, [string[]] $ProjectArguments = @())
        $project = Join-Path $repositoryRoot "tests\$ProjectName\$ProjectName.csproj"
        if (-not $NoBuild -and $builtProjects.Add($ProjectName)) {
            & dotnet build $project -c Release --nologo @buildArguments
            if ($LASTEXITCODE -ne 0) { throw "$ProjectName build failed." }
        }
        $ErrorActionPreference = 'Continue'  # Windows PowerShell otherwise truncates native stderr on failure.
        & dotnet run --project $project -c Release --no-build @buildArguments -- @ProjectArguments
        $result = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($result -ne 0) { throw "$ProjectName failed (exit $result)." }
    }
    Invoke-Regression VibeCode.PublicTests
    Invoke-Regression VibeCode.ChatModelPickerTests
    Invoke-Regression VibeCode.CliInstallTests
    Invoke-Regression VibeCode.UsageAccountingTests
    Invoke-Regression VibeCode.BridgeUsageTests
    Invoke-Regression VibeCode.McpHostTests @('--only=bridge', '--quick')
    Invoke-Regression VibeCode.McpHostTests @('--only=fallback')
    Invoke-Regression VibeCode.McpHostTests @('--only=shared')
    Invoke-Regression VibeCode.McpHostTests @('--only=ui')
    Invoke-Regression VibeCode.AutoScrollTests
    Invoke-Regression VibeCode.BridgeTerminalTests
    Invoke-Regression VibeCode.BridgeTerminalTests @('--cli-setup-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--removed-gemini-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--worker-controls-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--ultrafast-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--bridge-accounts-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-capabilities-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-desktop-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--terminal-input-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--terminal-rendering-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--terminal-reuse-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--terminal-windows-only')
    Invoke-Regression VibeCode.TelemetryTests
    Invoke-Regression VibeCode.RewindTests
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--groups')
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--review-settings')
    & (Join-Path $repositoryRoot 'tests\Test-MobileTemplate.ps1')
    Write-Output 'All public regression checks passed. No live model calls were made.'
}
finally {
    Pop-Location
    foreach ($name in $testEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $testEnvironment[$name], 'Process') }
}
