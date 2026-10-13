[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $BaseUrl
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
if (!$BaseUrl) {
    $delivery = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'public-delivery.json') -Raw | ConvertFrom-Json
    $BaseUrl = $delivery.inputBaseUrl
}
$base = [Uri]$BaseUrl
if (!$base.IsAbsoluteUri -or $base.Scheme -cne 'https' -or $base.UserInfo -or $base.Query -or $base.Fragment) {
    throw 'BaseUrl must be an absolute HTTPS directory without credentials, query or fragment.'
}
$target = [IO.Path]::GetFullPath($Destination, (Get-Location).ProviderPath)
if (Test-Path -LiteralPath $target) { throw 'Destination must be new; existing files are never overwritten.' }
foreach ($file in $lock.storedInput.files) {
    if ([IO.Path]::GetFileName($file.name) -cne $file.name -or $file.name -in @('.', '..')) { throw 'Unsafe locked file name.' }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($file in $lock.storedInput.files) {
    $path = Join-Path $target $file.name
    Invoke-WebRequest -Uri ($base.AbsoluteUri.TrimEnd('/') + '/' + [Uri]::EscapeDataString($file.name)) -OutFile $path
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $file.bytes -or $hash -cne $file.sha256) {
        throw "Locked input bytes/SHA mismatch: $($file.name). Partial download retained in $target."
    }
    Write-Output "Verified $($file.name): $($file.bytes) bytes / $hash"
}
Write-Output "Ready: $target"
