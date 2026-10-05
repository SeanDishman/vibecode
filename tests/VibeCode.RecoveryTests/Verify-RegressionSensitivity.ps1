param([switch]$SkipHealthyRun)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $PSScriptRoot 'VibeCode.RecoveryTests.csproj'
# Directory.Build.props re-roots the relative bin-*/obj-* paths below under BuildOutput\<project>.
$buildOutput = Join-Path $workspace 'BuildOutput\VibeCode.RecoveryTests'
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $workspace "artifacts\test-sensitivity\$runId"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Push-Location $workspace
try {
    $cases = @(
        @{ Name = 'compact'; Source = 'Protocol\CodexSession.Compaction.cs';
           Before = 'RequestAsync("thread/compact/start",'; After = 'RequestAsync("turn/start",';
           Failure = 'FAIL: Codex sends native thread compaction instead of turn/start' },
        @{ Name = 'orb'; Source = 'UI\BridgeSharedTerminalViewModel.cs';
           Before = 'if (e.PropertyName is nameof(ChatViewModel.ShowWorkingText) or null or "") Raise(nameof(HasWorkingAgents));';
           After = '// Deliberate regression: no activity notification for shared-terminal bindings.';
           Failure = 'FAIL: shared terminal orb notifies bindings when a child starts' }
    )
    if (-not $SkipHealthyRun) {
        & dotnet build $project --nologo -v:q "-p:BaseIntermediateOutputPath=obj-sensitivity-$runId/" "-p:OutputPath=bin-sensitivity-$runId/" -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Healthy build failed; regression sensitivity cannot be assessed.' }
        & (Join-Path $buildOutput "bin-sensitivity-$runId\VibeCode.RecoveryTests.exe") *> (Join-Path $evidence 'healthy.log')
        if ($LASTEXITCODE -ne 0) { throw 'Healthy tests failed; regression sensitivity cannot be assessed.' }
    }
    foreach ($case in $cases) {
        $sourcePath = Join-Path $workspace ("SRC\VibeCode.Desktop\" + $case.Source)
        $original = [IO.File]::ReadAllText($sourcePath)
        if (($original.Split([string[]]@($case.Before), [StringSplitOptions]::None)).Length -ne 2) {
            throw "Expected one mutation location in $sourcePath. Update this probe for the current source."
        }
        $replacementPath = Join-Path $evidence ($case.Name + '.cs')
        [IO.File]::WriteAllText($replacementPath, $original.Replace($case.Before, $case.After))
        $relativeSource = [Security.SecurityElement]::Escape($case.Source)
        $replacementXml = [Security.SecurityElement]::Escape($replacementPath)
        $targetsPath = Join-Path $evidence ($case.Name + '.targets')
        # Import only replaces Compile items in Desktop, including WPF's temporary project. Workspace source is untouched.
        $targets = @'
<Project>
  <ItemGroup Condition="$([System.String]::Copy('$(MSBuildProjectName)').StartsWith('VibeCode.Desktop'))">
    <Compile Remove="SOURCE;$(MSBuildProjectDirectory)\SOURCE" />
    <Compile Include="REPLACEMENT" />
  </ItemGroup>
</Project>
'@
        [IO.File]::WriteAllText($targetsPath, $targets.Replace('SOURCE', $relativeSource).Replace('REPLACEMENT', $replacementXml))
        $variant = "sensitivity-$runId-$($case.Name)"
        & dotnet build $project --nologo -v:q "-p:BaseIntermediateOutputPath=obj-$variant/" "-p:OutputPath=bin-$variant/" "-p:CustomAfterMicrosoftCommonTargets=$targetsPath" -p:UseSharedCompilation=false *> (Join-Path $evidence ($case.Name + '-build.log'))
        if ($LASTEXITCODE -ne 0) { throw "Mutation $($case.Name) did not compile. This is not a detected behavioral regression; inspect $evidence." }
        $log = Join-Path $evidence ($case.Name + '.log')
        # Windows PowerShell wraps stderr from a deliberately failing native test as NativeCommandError.
        # Assess its exit code and assertion text ourselves; do not confuse that wrapper with a script failure.
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & (Join-Path $buildOutput "bin-$variant\VibeCode.RecoveryTests.exe") *> $log
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $savedPreference }
        $output = [IO.File]::ReadAllText($log)
        if ($code -ne 1 -or -not $output.Contains($case.Failure)) {
            throw "Mutation $($case.Name) was not rejected by its intended check (exit $code). Inspect $log."
        }
        Write-Output "PASS: $($case.Name) regression rejected by the intended behavioral check."
    }
    Write-Output "Evidence: $evidence"
}
finally { Pop-Location }
