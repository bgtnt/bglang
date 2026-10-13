[CmdletBinding()]
param(
    [string] $InputDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [switch] $Test,
    [string] $ResultsDirectory,
    [string] $CompareWith
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$callerDirectory = (Get-Location).ProviderPath
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
if (!$lock.contractComplete) { throw 'Public input contract is incomplete.' }
if (!$InputDirectory) { $InputDirectory = Join-Path $root $lock.storedInput.releaseRelativeDirectory }
$InputDirectory = [IO.Path]::GetFullPath($InputDirectory, $callerDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $callerDirectory)
if ($ResultsDirectory) { $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory, $callerDirectory) }
if ($CompareWith) { $CompareWith = [IO.Path]::GetFullPath($CompareWith, $callerDirectory) }
if ($Test -and !$ResultsDirectory) { throw 'Test requires an explicit ResultsDirectory.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }

function Assert-Files([string] $directory, $expected) {
    $items = @(Get-ChildItem -LiteralPath $directory -Force)
    if ((@($items | ForEach-Object { $_.Name } | Sort-Object) -join "`n") -cne (@($expected.name | Sort-Object) -join "`n")) {
        throw "Unexpected package file set: $directory"
    }
    foreach ($file in $expected) {
        $item = Get-Item -LiteralPath (Join-Path $directory $file.name)
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular file: $($item.FullName)" }
        if ($item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "RAW bytes/SHA differ: $($item.FullName)"
        }
    }
}

function Invoke-Dotnet([string[]] $arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($arguments -join ' ')" }
}

$version = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $version -cne $lock.sdk.effectiveVersion) { throw "Expected SDK $($lock.sdk.effectiveVersion); found $version" }
Write-Output "Child temp: $([IO.Path]::GetTempPath())"
Assert-Files $InputDirectory $lock.storedInput.files
$graph = Join-Path $root $lock.graph.releaseRelativePath
$graphFile = Get-Item -LiteralPath $graph
if ($graphFile.Length -ne $lock.graph.bytes -or (Get-FileHash -LiteralPath $graph -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.graph.sha256) {
    throw 'Current graph RAW bytes/SHA differ. Use the declared Windows checkout attributes or exact release snapshot.'
}

$previousLocation = Get-Location
$previousManifest = $env:BGLANG_PUBLIC_PACKAGE_MANIFEST
try {
    Set-Location -LiteralPath $root
    Invoke-Dotnet -arguments @('build', 'tools/BgLang.SourceBuilder/BgLang.SourceBuilder.csproj', '-c', 'Release')
    New-Item -ItemType Directory -Path (Split-Path $OutputDirectory -Parent) -Force | Out-Null
    Invoke-Dotnet -arguments @('tools/BgLang.SourceBuilder/bin/Release/net10.0/BgLang.SourceBuilder.dll', 'promote-native',
        (Join-Path $InputDirectory 'manifest.json'), $graph, $OutputDirectory)
    Assert-Files $OutputDirectory $lock.expectedCandidate.files
    if ($CompareWith) {
        Assert-Files $CompareWith $lock.expectedCandidate.files
        foreach ($file in $lock.expectedCandidate.files) {
            & "$env:WINDIR/System32/fc.exe" /b (Join-Path $OutputDirectory $file.name) (Join-Path $CompareWith $file.name) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "RAW twin mismatch: $($file.name)" }
        }
    }
    if ($Test) {
        $env:BGLANG_PUBLIC_PACKAGE_MANIFEST = Join-Path $OutputDirectory 'manifest.json'
        Invoke-Dotnet -arguments @('restore', 'BgLang.Public.slnx', '--locked-mode')
        Invoke-Dotnet -arguments @('build', 'BgLang.Public.slnx', '-c', 'Release', '--no-restore')
        Invoke-Dotnet -arguments @('test', 'tests/BgLang.Public.Tests/BgLang.Public.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
            '--logger', 'trx;LogFileName=public.trx', '--results-directory', $ResultsDirectory)
        Invoke-Dotnet -arguments @('build', 'examples/BgLang.PublicExample/BgLang.PublicExample.csproj', '-c', 'Release')
        Invoke-Dotnet -arguments @('run', '--project', 'examples/BgLang.PublicExample/BgLang.PublicExample.csproj', '-c', 'Release', '--no-build', '--no-restore',
            '--', $env:BGLANG_PUBLIC_PACKAGE_MANIFEST)
    }
} finally {
    $env:BGLANG_PUBLIC_PACKAGE_MANIFEST = $previousManifest
    Set-Location -LiteralPath $previousLocation
}
