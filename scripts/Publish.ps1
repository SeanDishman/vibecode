param(
    [switch] $IncludeMobile,
    [ValidatePattern('^[a-zA-Z0-9-]*$')][string] $BuildSlot = '',
    [string] $OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-PublicSource.ps1')
if ($IncludeMobile) { & (Join-Path $PSScriptRoot 'Build-Mobile.ps1') }
$mobileTemplate = Join-Path $repositoryRoot 'VibeCode.Desktop\Assets\vibecode-mobile.apk'
if (Test-Path -LiteralPath $mobileTemplate) {
    & (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $mobileTemplate
}
$project = Join-Path $repositoryRoot 'VibeCode.Desktop\VibeCode.Desktop.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repositoryRoot 'artifacts\windows-x64' }
$buildArguments = @()
if ($BuildSlot) { $buildArguments = @("-p:OutputPath=bin-$BuildSlot/", "-p:BaseIntermediateOutputPath=obj-$BuildSlot/") }
& dotnet publish $project -c Release -r win-x64 --self-contained true --output $output `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugSymbols=false -p:DebugType=None @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Write-Output "Published VibeCode to $output"
