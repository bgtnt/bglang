[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InputDirectory,
    [Parameter(Mandatory)] [string] $WorkDirectory,
    [switch] $TestConsumers = $true
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$InputDirectory = [IO.Path]::GetFullPath($InputDirectory)
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Reproduction WorkDirectory must be new.' }
if ([string]::IsNullOrWhiteSpace($env:TEMP) -or [string]::IsNullOrWhiteSpace($env:TMP)) { throw 'Set explicit TEMP/TMP before this check.' }
foreach ($source in @($root, $InputDirectory)) {
    $prefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $workPrefix = $WorkDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($source -ieq $WorkDirectory -or $WorkDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep check outputs separate from source/input.' }
}
for ($parent = [IO.DirectoryInfo]::new((Split-Path $WorkDirectory -Parent)); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'WorkDirectory ancestry must be regular.' }
}
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
$current = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/current-editorial-reproduction.v1.json') -Raw | ConvertFrom-Json
function File-Pin([string] $path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected a regular evidence file.' }
    return [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Assert-Package([string] $path, $expected) {
    $actualNames = @(Get-ChildItem -LiteralPath $path -Force | ForEach-Object { $_.Name } | Sort-Object)
    if (($actualNames -join '|') -cne (@($expected.files.name | Sort-Object) -join '|')) { throw 'The exact four-file set differs.' }
    foreach ($file in $expected.files) {
        $pin = File-Pin (Join-Path $path $file.name)
        if ($pin.bytes -ne $file.bytes -or $pin.sha256 -cne $file.sha256) { throw 'Predeclared package RAW pin differs.' }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $path 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.packageId -cne $expected.packageId) { throw 'Predeclared package identity differs.' }
}
$inputBefore = @($lock.authoringInput.files | ForEach-Object { File-Pin (Join-Path $InputDirectory $_.name) })
New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
$full = Join-Path $WorkDirectory 'full-source'
$sourceOnly = Join-Path $WorkDirectory 'source-only'
& (Join-Path $root 'packaging/Export-PublicSource.ps1') -InputDirectory $InputDirectory -OutputDirectory $full
& (Join-Path $root 'packaging/Export-PublicSource.ps1') -SourceOnly -OutputDirectory $sourceOnly
$onlyManifestPath = Join-Path $sourceOnly 'public-source-manifest.json'
$onlyManifest = Get-Content -LiteralPath $onlyManifestPath -Raw | ConvertFrom-Json
if (!$onlyManifest.sourceOnly -or (Test-Path -LiteralPath (Join-Path $sourceOnly (Join-Path $lock.authoringInput.releaseRelativeDirectory $lock.authoringInput.entryFile)))) { throw 'SourceOnly must omit the immutable authoring payload.' }
foreach ($file in $lock.expectedCandidate.files) {
    if (Test-Path -LiteralPath (Join-Path $sourceOnly (Join-Path $lock.expectedCandidate.releaseRelativeDirectory $file.name))) { throw 'SourceOnly must omit runtime package payload.' }
}
foreach ($snapshot in @($full, $sourceOnly)) {
    if ((Get-Content -LiteralPath (Join-Path $snapshot 'global.json') -Raw | ConvertFrom-Json).sdk.version -cne '10.0.403') { throw 'The revised reproduction SDK selection was not exported.' }
    foreach ($relative in @('data/dictionary/current-editorial-reproduction.v1.json', 'data/dictionary/cheta-editorial-stress-overlay.v1.json', 'tools/BgLang.SourceBuilder/AuthoringStressOverlay.cs')) {
        if ((File-Pin (Join-Path $root $relative)).sha256 -cne (File-Pin (Join-Path $snapshot $relative)).sha256) { throw 'An exact current-profile producer input was not exported.' }
    }
}
$results = [Collections.Generic.List[object]]::new()
$currentFullManifest = $null
foreach ($profile in @('Current', 'Baseline')) {
    $expected = if ($profile -ceq 'Baseline') { $lock.expectedCandidate } else { $current.expectedCandidate }
    $fullPackage = Join-Path $WorkDirectory ($profile + '-full-package')
    foreach ($mode in @('Full', 'SourceOnly')) {
        $snapshot = if ($mode -ceq 'Full') { $full } else { $sourceOnly }
        $package = if ($mode -ceq 'Full') { $fullPackage } else { Join-Path $WorkDirectory ($profile + '-sourceonly-package') }
        $evidence = Join-Path $WorkDirectory ($profile + '-' + $mode + '-evidence')
        $parameters = @{ OutputDirectory = $package; ResultsDirectory = $evidence }
        if ($mode -ceq 'SourceOnly') { $parameters.InputDirectory = $InputDirectory }
        # Full deliberately omits -InputDirectory: use the exact delivered immutable payload.
        if ($profile -ceq 'Baseline') { $parameters.Profile = $(if ($mode -ceq 'SourceOnly') { 'baseline' } else { 'Baseline' }) }
        elseif ($mode -ceq 'SourceOnly') { $parameters.Profile = 'current' }
        # Current Full omits -Profile; SourceOnly also checks case-insensitive ValidateSet canonicalization.
        if ($mode -ceq 'SourceOnly') { $parameters.CompareWith = $fullPackage }
        if ($TestConsumers) {
            $parameters.Test = $true; $parameters.TestWeb = $true
            if ($profile -ceq 'Baseline') {
                if ([string]::IsNullOrWhiteSpace($currentFullManifest)) { throw 'Build and verify the actual Current Full package before Baseline consumer checks.' }
                $parameters.ArticleCurrentManifest = $currentFullManifest
            }
        }
        & (Join-Path $snapshot 'packaging/Build-PublicPackage.ps1') @parameters
        Assert-Package $package $expected
        if ($profile -ceq 'Current' -and $mode -ceq 'Full') { $currentFullManifest = Join-Path $package 'manifest.json' }
        $receiptPath = Join-Path $evidence 'recipe-receipt.json'
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $factory = if ($profile -ceq 'Current') { 'locked-base-authoring/editorial-stress-overlay/existing-build' } else { 'locked-base-authoring/existing-build' }
        if ($receipt.reproductionProfile -cne $profile -or $receipt.factoryPath -cne $factory -or $receipt.expectedPackageId -cne $expected.packageId) { throw 'Reproduction factory/profile metadata differs.' }
        if ($profile -ceq 'Current' -and ($receipt.derivedAuthoring.bytes -ne $current.derivedAuthoring.bytes -or $receipt.derivedAuthoring.sha256 -cne $current.derivedAuthoring.sha256)) { throw 'Derived authoring pin differs.' }
        if ($TestConsumers) {
            $articleManifest = if ($profile -ceq 'Current') { Join-Path $package 'manifest.json' } else { $currentFullManifest }
            $articlePin = File-Pin $articleManifest
            if ($receipt.articleCurrentManifest.path -ine $articlePin.path -or $receipt.articleCurrentManifest.bytes -ne $articlePin.bytes -or
                $receipt.articleCurrentManifest.sha256 -cne $articlePin.sha256) { throw 'Recipe article Current binding differs from the actual reviewed Current package.' }
        }
        $results.Add([ordered]@{ profile = $profile; mode = $mode; defaultProfileExercised = ($profile -ceq 'Current' -and $mode -ceq 'Full'); casefoldProfileExercised = ($mode -ceq 'SourceOnly'); packageId = $expected.packageId; articleCurrentManifest = $receipt.articleCurrentManifest; receipt = File-Pin $receiptPath })
    }
}
$inputAfter = @($lock.authoringInput.files | ForEach-Object { File-Pin (Join-Path $InputDirectory $_.name) })
if (($inputBefore | ConvertTo-Json -Depth 5 -Compress) -cne ($inputAfter | ConvertTo-Json -Depth 5 -Compress)) { throw 'Immutable public authoring input changed.' }
$summary = [ordered]@{ schema = 'bglang.editorial-reproduction-check.v1'; status = 'PASS_EXACT_PREDECLARED_FOUR_FILE_RECONSTRUCTION'; results = $results.ToArray(); sourceOnlyManifest = File-Pin $onlyManifestPath; publicAndWebRequested = [bool]$TestConsumers; batchAccepted = $false; qualification = 'Actual recipe receipts and exact RAW package comparisons, including current default and explicit baseline. This is not private Full/52-row linguistic acceptance or anonymous hosted-asset readback.' }
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $WorkDirectory 'reproduction-check.json') -Encoding utf8NoBOM
$summary
