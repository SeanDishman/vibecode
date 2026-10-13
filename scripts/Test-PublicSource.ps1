$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @(& git -C $repositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard) | Sort-Object -Unique
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate public source.' }
$failures = [Collections.Generic.List[string]]::new()
$privateFeature = '(?i:grok|daybreak|shannon|jailbr|uncensored|attacktactics|safetyrefus|safetyrewind|\bmitre\b)|Mitre'
$binaryExtensions = @('.exe','.dll','.so','.lib','.apk','.jar','.zip','.7z','.msi','.nupkg','.pdb','.dmp','.pem','.key','.p12','.pfx','.keystore','.jks','.jsonl')
$textExtensions = @('.cs','.csproj','.xaml','.xml','.json','.md','.txt','.kt','.kts','.ps1','.py','.mjs','.js','.yml','.yaml','.toml','.props','.targets','.config')
foreach ($relative in $paths) {
    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $relative))
    if (-not $path.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add("Path escapes repository: $relative"); continue
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue } # staged deletions
    $name = [IO.Path]::GetFileName($path)
    $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
    if ($binaryExtensions -contains $extension -or $name -match '^\.env(?:\.|$)|^(auth|oauth_creds|google_accounts|settings|local\.properties|keystore\.properties)(?:\.json)?$') {
        $failures.Add("Local data or compiled payload: $relative")
    }
    if ($relative -cmatch $privateFeature) { $failures.Add("Private feature path: $relative") }
    if ($textExtensions -notcontains $extension) { continue }
    $text = [IO.File]::ReadAllText($path)
    if ($relative -ne 'scripts/Test-PublicSource.ps1' -and $text -cmatch $privateFeature) {
        $failures.Add("Private feature reference: $relative")
    }
    # Report file names only; never echo a credential match.
    if ($relative -ne 'scripts/Test-PublicSource.ps1' -and $text -match '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|sk-(?:proj-|ant-)?[A-Za-z0-9_-]{32,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)') {
        $failures.Add("Potential credential: $relative")
    }
    if ($extension -eq '.csproj') {
        [xml] $project = $text
        foreach ($node in $project.SelectNodes('//*[@Include]')) {
            $include = $node.GetAttribute('Include')
            if ($node.Name -eq 'PackageReference' -or $include -match '\$|\*|;') { continue }
            $target = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($path)) $include))
            if (-not $target.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add("Project dependency escapes repository: $relative")
            }
        }
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output "PASS: public source policy ($($paths.Count) paths checked). Run a full secret scan before publishing."
