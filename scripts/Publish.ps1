param([switch] $IncludeMobile)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($IncludeMobile) { & (Join-Path $PSScriptRoot 'Build-Mobile.ps1') }
$mobileTemplate = Join-Path $repositoryRoot 'VibeCode.Desktop\Assets\vibecode-mobile.apk'
if (Test-Path -LiteralPath $mobileTemplate) {
    & (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $mobileTemplate
}
$project = Join-Path $repositoryRoot 'VibeCode.Desktop\VibeCode.Desktop.csproj'
$output = Join-Path $repositoryRoot 'artifacts\windows-x64'
& dotnet publish $project -c Release -r win-x64 --self-contained true --output $output `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugSymbols=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Write-Output "Published VibeCode to $output"
