[CmdletBinding()]
param(
    [string] $InputDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$caller = (Get-Location).ProviderPath
$allow = Get-Content -LiteralPath (Join-Path $root 'packaging/public-source-allowlist.v1.json') -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
if (!$lock.contractComplete) { throw 'Public input contract is incomplete.' }
if (!$InputDirectory) { $InputDirectory = Join-Path $root $lock.storedInput.releaseRelativeDirectory }
$InputDirectory = [IO.Path]::GetFullPath($InputDirectory, $caller)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $caller)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }
$inputPrefix = $InputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($OutputDirectory.StartsWith($inputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be outside immutable inputs.' }
if ($allow.inputDirectory -cne $lock.storedInput.releaseRelativeDirectory) { throw 'Input directory contracts differ.' }
$inputNames = @(Get-ChildItem -LiteralPath $InputDirectory -Force | ForEach-Object { $_.Name } | Sort-Object)
if (($inputNames -join "`n") -cne (@($lock.storedInput.files.name | Sort-Object) -join "`n")) { throw 'Unexpected input file set.' }

function File-Pin([string] $path) {
    $item = Get-Item -LiteralPath $path
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular file: $path" }
    return @{ bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$copies = [System.Collections.Generic.List[object]]::new()
$outputPrefix = $OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($relative in $allow.files) {
    $source = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if ([IO.Path]::IsPathFullyQualified($relative) -or !$source.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        !$paths.Add($relative)) { throw "Invalid or duplicate source path: $relative" }
    $pin = File-Pin $source
    if ($relative -ceq $lock.graph.releaseRelativePath -and
        ($pin.bytes -ne $lock.graph.bytes -or $pin.sha256 -cne $lock.graph.sha256)) { throw 'Graph RAW bytes/SHA differ.' }
    $copies.Add(@{ path = $relative; source = $source; bytes = $pin.bytes; sha256 = $pin.sha256 })
}
foreach ($file in $lock.storedInput.files) {
    $source = Join-Path $InputDirectory $file.name
    $pin = File-Pin $source
    if ($pin.bytes -ne $file.bytes -or $pin.sha256 -cne $file.sha256) { throw "Input RAW bytes/SHA differ: $($file.name)" }
    $relative = $allow.inputDirectory.TrimEnd('/') + '/' + $file.name
    if (![IO.Path]::GetFullPath((Join-Path $OutputDirectory $relative)).StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Input delivery path escapes the export.' }
    if (!$paths.Add($relative)) { throw "Duplicate input path: $relative" }
    $copies.Add(@{ path = $relative; source = $source; bytes = $pin.bytes; sha256 = $pin.sha256 })
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
    files = $rows.ToArray()
    publication = $false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'public-source-manifest.json') -Encoding utf8NoBOM
Write-Output "Child temp: $([IO.Path]::GetTempPath())"
Write-Output "Exported $($allow.files.Count) source files and $($lock.storedInput.files.Count) locked input files."
