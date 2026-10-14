param([switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testEnvironment = @{}
$accountRoot = Join-Path $repositoryRoot ('artifacts\test-accounts-' + [Guid]::NewGuid().ToString('N'))
foreach ($name in @('VIBECODE_CODEX_ACCOUNT_STORE','VIBECODE_CODEX_LEGACY_HOME','VIBECODE_CLAUDE_ACCOUNT_STORE','CLAUDE_CONFIG_DIR','GEMINI_CLI_HOME')) {
    $testEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    $directory = Join-Path $accountRoot $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
}
Push-Location -LiteralPath $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'Test-PublicSource.ps1')
    if (-not $NoBuild) {
        & dotnet build VibeCode.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }
    function Invoke-Regression {
        param([string] $ProjectName, [string[]] $ProjectArguments = @())
        $project = Join-Path $repositoryRoot "tests\$ProjectName\$ProjectName.csproj"
        $ErrorActionPreference = 'Continue' # Windows PowerShell otherwise truncates native stderr on failure.
        & dotnet run --project $project -c Release --no-build -- @ProjectArguments
        $result = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($result -ne 0) { throw "$ProjectName failed (exit $result)." }
    }
    Invoke-Regression VibeCode.PublicTests
    Invoke-Regression VibeCode.ChatModelPickerTests
    Invoke-Regression VibeCode.CliInstallTests
    Invoke-Regression VibeCode.GeminiTests @('--fixtures-only')
    Invoke-Regression VibeCode.UsageAccountingTests
    Invoke-Regression VibeCode.BridgeUsageTests
    Invoke-Regression VibeCode.AutoScrollTests
    Invoke-Regression VibeCode.BridgeTerminalTests
    Invoke-Regression VibeCode.BridgeTerminalTests @('--cli-setup-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--gemini-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--ultrafast-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--bridge-accounts-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-capabilities-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-desktop-only')
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--groups')
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--review-settings')
    & (Join-Path $repositoryRoot 'tests\Test-MobileTemplate.ps1')
    Write-Output 'All public regression checks passed. No live model calls were made.'
}
finally {
    Pop-Location
    foreach ($name in $testEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $testEnvironment[$name], 'Process') }
}
