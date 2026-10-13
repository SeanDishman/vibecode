param([switch] $Offline)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$mobileRoot = Join-Path $repositoryRoot 'mobile\VibeCodeMobile'
$properties = [IO.File]::ReadAllText((Join-Path $mobileRoot 'gradle\wrapper\gradle-wrapper.properties'))
$match = [regex]::Match($properties, 'gradle-(\d+\.\d+(?:\.\d+)?)-bin\.zip')
if (-not $match.Success) { throw 'Invalid Gradle distribution version.' }
$version = $match.Groups[1].Value
$toolsRoot = Join-Path $repositoryRoot 'artifacts\gradle'
$gradle = Join-Path $toolsRoot "gradle-$version\bin\gradle.bat"
if (-not (Test-Path -LiteralPath $gradle)) {
    if ($Offline) { throw 'Gradle is not cached. Run once without -Offline to download it.' }
    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
    $url = "https://services.gradle.org/distributions/gradle-$version-bin.zip"
    $expected = (Invoke-RestMethod -Uri "$url.sha256").Trim()
    if ($expected -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid Gradle checksum metadata.' }
    $archive = Join-Path $toolsRoot "gradle-$version-bin.zip"
    Invoke-WebRequest -Uri $url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Gradle download checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsRoot -Force
}
$slotPath = Join-Path $mobileRoot 'app\src\main\assets\vibecode-enroll.vcenroll'
& (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $slotPath -EnrollmentSlot
Push-Location -LiteralPath $mobileRoot
try {
    $arguments = @('--no-daemon', '--console=plain', '-PvibecodeTemplate=true', ':app:assembleRelease')
    if ($Offline) { $arguments += '--offline' }
    & $gradle @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    $apk = Get-Item -LiteralPath 'app\build\outputs\apk\release\app-release-unsigned.apk'
    & (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $apk.FullName
    Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $repositoryRoot 'VibeCode.Desktop\Assets\vibecode-mobile.apk')
    Write-Output 'The mobile template is ready for the desktop build.'
}
finally { Pop-Location }
