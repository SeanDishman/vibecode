param([switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet build VibeCode.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }
    function Invoke-Regression {
        param([string] $ProjectName, [string[]] $ProjectArguments = @())
        $project = Join-Path $repositoryRoot "tests\$ProjectName\$ProjectName.csproj"
        & dotnet run --project $project -c Release --no-build -- @ProjectArguments
        if ($LASTEXITCODE -ne 0) { throw "$ProjectName failed." }
    }
    Invoke-Regression VibeCode.PublicTests
    Invoke-Regression VibeCode.AutoScrollTests
    Invoke-Regression VibeCode.BridgeTerminalTests
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-capabilities-only')
    Invoke-Regression VibeCode.BridgeTerminalTests @('--jarvis-desktop-only')
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--groups')
    Invoke-Regression VibeCode.OrchestratorGroupTests @('--review-settings')
    & (Join-Path $repositoryRoot 'tests\Test-MobileTemplate.ps1')
    Write-Output 'All public regression checks passed. No live model calls were made.'
}
finally { Pop-Location }
