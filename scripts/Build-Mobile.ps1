param([switch] $Offline)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$mobileRoot = Join-Path $repositoryRoot 'mobile\VibeCodeMobile'
$slotPath = Join-Path $mobileRoot 'app\src\main\assets\vibecode-enroll.vcenroll'
& (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $slotPath -EnrollmentSlot
Push-Location -LiteralPath $mobileRoot
try {
    $arguments = @('--no-daemon', '--console=plain', '-PvibecodeTemplate=true', ':app:assembleRelease')
    if ($Offline) { $arguments += '--offline' }
    & .\gradlew.bat @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    $apk = Get-Item -LiteralPath 'app\build\outputs\apk\release\app-release-unsigned.apk'
    & (Join-Path $PSScriptRoot 'Assert-MobileTemplate.ps1') -Path $apk.FullName
    Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $repositoryRoot 'VibeCode.Desktop\Assets\vibecode-mobile.apk')
    Write-Output 'The mobile template is ready for the desktop build.'
}
finally { Pop-Location }
