[CmdletBinding()]
param(
    [string] $InputDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [switch] $SourceOnly,
    [string] $ValidationDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$caller = (Get-Location).ProviderPath
$allow = Get-Content -LiteralPath (Join-Path $root 'packaging/public-source-allowlist.v1.json') -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
if (!$lock.contractComplete) { throw 'Public input contract is incomplete.' }
if ($SourceOnly -and $InputDirectory) { throw 'SourceOnly does not deliver InputDirectory.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $caller)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }
if (!$ValidationDirectory) { $ValidationDirectory = $OutputDirectory + '.validation' }
$ValidationDirectory = [IO.Path]::GetFullPath($ValidationDirectory, $caller)
if (Test-Path -LiteralPath $ValidationDirectory) { throw 'ValidationDirectory must be new.' }
$outputPrefix = $OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$validationPrefix = $ValidationDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($OutputDirectory -ieq $ValidationDirectory -or $ValidationDirectory.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($validationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and validation directories must be separate.' }
if (!$SourceOnly) {
    if (!$InputDirectory) { $InputDirectory = Join-Path $root $lock.storedInput.releaseRelativeDirectory }
    $InputDirectory = [IO.Path]::GetFullPath($InputDirectory, $caller)
    $inputPrefix = $InputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory -ieq $InputDirectory -or $ValidationDirectory -ieq $InputDirectory -or
        $OutputDirectory.StartsWith($inputPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $ValidationDirectory.StartsWith($inputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Outputs must be outside immutable inputs.' }
}
if ($allow.inputDirectory -cne $lock.storedInput.releaseRelativeDirectory) { throw 'Input directory contracts differ.' }

function File-Pin([string] $path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular file: $path" }
    return @{ bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$copies = [System.Collections.Generic.List[object]]::new()
function Source-Path([string] $relative) {
    $full = [IO.Path]::GetFullPath($relative, $root)
    if ([IO.Path]::IsPathFullyQualified($relative) -or !$full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid source path: $relative"
    }
    return [IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
}
foreach ($declared in $allow.files) {
    $relative = Source-Path $declared
    if (!$paths.Add($relative)) { throw "Duplicate source path: $relative" }
    $source = Join-Path $root $relative
    $pin = File-Pin $source
    if ($relative -ceq $lock.graph.releaseRelativePath -and
        ($pin.bytes -ne $lock.graph.bytes -or $pin.sha256 -cne $lock.graph.sha256)) { throw 'Graph RAW bytes/SHA differ.' }
    $copies.Add(@{ path = $relative; source = $source; bytes = $pin.bytes; sha256 = $pin.sha256 })
}

# Evaluate the source checkout, not a copied tree whose missing globs/imports can disappear.
# Keep raw MSBuild evidence outside the public source tree, including on a guard failure.
New-Item -ItemType Directory -Path $ValidationDirectory | Out-Null
$evaluations = [System.Collections.Generic.List[object]]::new()
function Evaluate-Project([string] $project, [string] $kind, [string[]] $query) {
    $prefix = Join-Path $ValidationDirectory (([IO.Path]::GetFileNameWithoutExtension($project)) + '.' + $kind)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $root
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['TEMP'] = $env:TEMP; $start.Environment['TMP'] = $env:TMP
    $arguments = @('msbuild', $project, '-nologo', '-nodeReuse:false', '-maxCpuCount:1', '-p:Configuration=Release',
        '-p:UseSharedCompilation=false', ('-p:ArtifactsPath=' + (Join-Path $ValidationDirectory 'artifacts'))) + $query
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $stdout = [IO.File]::Create($prefix + '.stdout.bin'); $stderr = [IO.File]::Create($prefix + '.stderr.bin')
    $child = [Diagnostics.Process]::Start($start)
    $childId = $child.Id; $birth = $child.StartTime.ToUniversalTime().ToString('o')
    try {
        $outTask = $child.StandardOutput.BaseStream.CopyToAsync($stdout); $errTask = $child.StandardError.BaseStream.CopyToAsync($stderr)
        $child.WaitForExit(); $code = $child.ExitCode
        $null = $outTask.GetAwaiter().GetResult(); $null = $errTask.GetAwaiter().GetResult()
    } finally { $child.Dispose(); $stdout.Dispose(); $stderr.Dispose() }
    [ordered]@{ project = $project; arguments = $arguments; pid = $childId; nativeCreationUtc = $birth; actualExitCode = $code;
        waitedDrainedDisposed = $true; TEMP = $env:TEMP; TMP = $env:TMP;
        stdout = File-Pin ($prefix + '.stdout.bin'); stderr = File-Pin ($prefix + '.stderr.bin') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix + '.terminal.json') -Encoding utf8NoBOM
    if ($code -ne 0) { throw "MSBuild evaluation failed ($code): $project; evidence: $prefix" }
    return [IO.File]::ReadAllText($prefix + '.stdout.bin')
}
function Require-Input([string] $full, [string] $kind) {
    $full = [IO.Path]::GetFullPath($full)
    if ($full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = [IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
        if (!$paths.Contains($relative)) { throw "Missing evaluated $kind input: $relative" }
        $null = $required.Add($kind + '|' + $relative)
    } elseif ($kind -notin @('Import', 'Analyzer') -or !$full.StartsWith($sdkPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "External build input is outside the SDK installation: $full"
    } else {
        $null = $sdkInputs.Add([IO.Path]::GetRelativePath($sdkPrefix, $full).Replace('\', '/'))
    }
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) { throw "Unresolved evaluated $kind input: $full" }
}
$projects = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($project in $allow.projects) {
    $relative = Source-Path $project
    if (!$projects.Add($relative) -or !$paths.Contains($relative)) { throw "Missing or duplicate export project: $relative" }
}
$solution = Source-Path $allow.defaultSolution
if (!$paths.Contains($solution)) { throw 'Default solution is not exported.' }
[xml] $solutionXml = Get-Content -LiteralPath (Join-Path $root $solution) -Raw
foreach ($entry in $solutionXml.SelectNodes('//Project')) {
    if (!$projects.Contains((Source-Path $entry.Path))) { throw "Solution project is not declared: $($entry.Path)" }
}
$sdk = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
if (!$paths.Contains('global.json')) { throw 'SDK selection input global.json is not exported.' }
foreach ($project in $projects) {
    $items = Evaluate-Project $project 'items' @('-getItem:Compile,ProjectReference,Content,None,EmbeddedResource,AdditionalFiles,Analyzer,PackageReference,FrameworkReference',
        '-getProperty:TargetFramework,TargetFrameworks,NETCoreSdkVersion,MSBuildToolsPath,MSBuildAllProjects,BaseIntermediateOutputPath') | ConvertFrom-Json
    if ($items.Properties.NETCoreSdkVersion -cne $sdk) { throw "Unexpected SDK for $project" }
    if ($items.Properties.TargetFrameworks) { throw "Evaluate each target framework before exporting multi-target project: $project" }
    $sdkRoot = Split-Path (Split-Path $items.Properties.MSBuildToolsPath -Parent) -Parent
    $sdkPrefix = $sdkRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $sdkInputs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $required = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Require-Input (Join-Path $root $project) 'Project'
    foreach ($kind in @('Compile', 'ProjectReference', 'Content', 'EmbeddedResource', 'AdditionalFiles', 'Analyzer')) {
        foreach ($item in $items.Items.$kind) { Require-Input $item.FullPath $kind }
    }
    foreach ($reference in $items.Items.ProjectReference) {
        $relative = [IO.Path]::GetRelativePath($root, $reference.FullPath).Replace('\', '/')
        if (!$projects.Contains($relative)) { throw "Referenced project is not evaluated: $relative" }
    }
    foreach ($item in $items.Items.None) {
        foreach ($metadata in @('CopyToOutputDirectory', 'CopyToPublishDirectory')) {
            $copy = $item.PSObject.Properties[$metadata]
            if ($copy -and $copy.Value -and $copy.Value -cne 'Never') { Require-Input $item.FullPath 'CopiedNone' }
        }
    }
    $packageLock = Join-Path (Split-Path (Join-Path $root $project) -Parent) 'packages.lock.json'
    if (Test-Path -LiteralPath $packageLock) { Require-Input $packageLock 'PackageLock' }
    $preprocessed = Join-Path $ValidationDirectory (([IO.Path]::GetFileNameWithoutExtension($project)) + '.preprocessed.xml')
    $null = Evaluate-Project $project 'imports' @(('-preprocess:' + $preprocessed))
    [xml] $expanded = Get-Content -LiteralPath $preprocessed -Raw
    foreach ($comment in $expanded.SelectNodes('//comment()')) {
        $lines = @($comment.Value.Split("`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($lines.Count -ge 3 -and $lines[0] -match '^=+$' -and $lines[-1] -match '^=+$') {
            $import = $lines[-2]
            if ([IO.Path]::IsPathFullyQualified($import)) {
                # MSBuild escapes -- as __ inside comments; ambiguous names cannot prove an import identity.
                if ($import.Contains('__')) { throw "Ambiguous preprocessed import path: $import" }
                Require-Input $import 'Import'
            }
        }
    }
    $evaluations.Add([ordered]@{ project = $project; targetFramework = $items.Properties.TargetFramework; sdk = $sdk;
        requiredSourceInputs = @($required | Sort-Object); externalSdkInputs = @($sdkInputs | Sort-Object); compileCount = $items.Items.Compile.Count;
        projectReferenceCount = $items.Items.ProjectReference.Count })
}
$evaluations | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ValidationDirectory 'evaluated-build-closure.json') -Encoding utf8NoBOM

if (!$SourceOnly) {
  $inputNames = @(Get-ChildItem -LiteralPath $InputDirectory -Force | ForEach-Object { $_.Name } | Sort-Object)
  if (($inputNames -join "`n") -cne (@($lock.storedInput.files.name | Sort-Object) -join "`n")) { throw 'Unexpected input file set.' }
  foreach ($file in $lock.storedInput.files) {
    $source = Join-Path $InputDirectory $file.name
    $pin = File-Pin $source
    if ($pin.bytes -ne $file.bytes -or $pin.sha256 -cne $file.sha256) { throw "Input RAW bytes/SHA differ: $($file.name)" }
    $relative = $allow.inputDirectory.TrimEnd('/') + '/' + $file.name
    if (![IO.Path]::GetFullPath((Join-Path $OutputDirectory $relative)).StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Input delivery path escapes the export.' }
    if (!$paths.Add($relative)) { throw "Duplicate input path: $relative" }
    $copies.Add(@{ path = $relative; source = $source; bytes = $pin.bytes; sha256 = $pin.sha256 })
  }
}

# Only declared RAW files are copied; bin/obj, private Docs and Git state are not enumerated.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($copy in $copies) {
    $destination = Join-Path $OutputDirectory $copy.path
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $copy.source -Destination $destination
    $pin = File-Pin $destination
    $sourcePin = File-Pin $copy.source
    if ($pin.bytes -ne $copy.bytes -or $pin.sha256 -cne $copy.sha256 -or
        $sourcePin.bytes -ne $copy.bytes -or $sourcePin.sha256 -cne $copy.sha256) { throw "RAW copy mismatch: $($copy.path)" }
    $rows.Add([ordered]@{ path = $copy.path; bytes = $copy.bytes; sha256 = $copy.sha256 })
}
$manifest = [ordered]@{
    schema = 'bglang.public-source-manifest.v1'
    sourceBaseCommit = $allow.sourceBaseCommit
    authority = 'Accepted runtime/producer commit anchor; exact recipe/docs snapshot is the RAW file manifest.'
    sourceOnly = [bool] $SourceOnly
    releaseBuildClosure = $evaluations.ToArray()
    files = $rows.ToArray()
    publication = $false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'public-source-manifest.json') -Encoding utf8NoBOM
Write-Output "Child temp: $([IO.Path]::GetTempPath())"
Write-Output "Exported $($allow.files.Count) source files; source-only: $SourceOnly. Build evidence: $ValidationDirectory"
