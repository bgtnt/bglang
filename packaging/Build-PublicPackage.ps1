[CmdletBinding()]
param(
    [string] $InputDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [switch] $Test,
    [switch] $TestWeb,
    [string] $ResultsDirectory,
    [string] $CompareWith
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$callerDirectory = (Get-Location).ProviderPath
$lock = Get-Content -LiteralPath (Join-Path $root 'data/dictionary/public-input-lock.v1.json') -Raw | ConvertFrom-Json
if (!$lock.contractComplete) { throw 'Public input contract is incomplete.' }
if (!$InputDirectory) { $InputDirectory = Join-Path $root $lock.authoringInput.releaseRelativeDirectory }
$InputDirectory = [IO.Path]::GetFullPath($InputDirectory, $callerDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $callerDirectory)
if ($ResultsDirectory) { $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory, $callerDirectory) }
if ($CompareWith) { $CompareWith = [IO.Path]::GetFullPath($CompareWith, $callerDirectory) }
if ($TestWeb -and !$Test) { throw 'TestWeb requires Test.' }
if ($Test -and !$ResultsDirectory) { throw 'Test requires an explicit ResultsDirectory.' }
$evidenceDirectory = if ($ResultsDirectory) { $ResultsDirectory } else { $OutputDirectory + '.validation' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }
if (Test-Path -LiteralPath $evidenceDirectory) { throw 'The native evidence directory must be new.' }
if ([string]::IsNullOrWhiteSpace($env:TEMP) -or [string]::IsNullOrWhiteSpace($env:TMP)) { throw 'Set explicit process TEMP and TMP before launching the recipe.' }

function Assert-NoReparsePath([string] $path) {
    $current = [IO.Path]::GetFullPath($path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path is not allowed: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Assert-Separate([string] $left, [string] $right) {
    $a = $left.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $b = $right.TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($a -ieq $b -or $a.StartsWith($b + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith($a + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Directories must be separate: $left; $right" }
}
foreach ($directory in @($InputDirectory, $OutputDirectory, $evidenceDirectory)) { Assert-NoReparsePath $directory }
Assert-Separate $InputDirectory $OutputDirectory
Assert-Separate $InputDirectory $evidenceDirectory
Assert-Separate $OutputDirectory $evidenceDirectory
if ($CompareWith) { Assert-NoReparsePath $CompareWith; Assert-Separate $CompareWith $OutputDirectory; Assert-Separate $CompareWith $evidenceDirectory }

function File-Pin([string] $path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular file: $path" }
    return [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Write-JsonNew([string] $path, $value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 30) + [char]10)
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes) } finally { $stream.Dispose() }
    return File-Pin $path
}
function Assert-Files([string] $directory, $expected) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $expected) {
        if ([string]::IsNullOrWhiteSpace($file.name) -or $file.name -cne [IO.Path]::GetFileName($file.name) -or
            $file.name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or !$names.Add($file.name)) { throw 'Invalid or duplicate locked file name.' }
    }
    $items = @(Get-ChildItem -LiteralPath $directory -Force)
    if ((@($items | ForEach-Object { $_.Name } | Sort-Object) -join "\n") -cne (@($expected.name | Sort-Object) -join "\n")) { throw "Unexpected package/input file set: $directory" }
    foreach ($file in $expected) {
        $pin = File-Pin (Join-Path $directory $file.name)
        if ($pin.bytes -ne $file.bytes -or $pin.sha256 -cne $file.sha256) { throw "RAW bytes/SHA differ: $($pin.path)" }
    }
}
function Assert-SameBytes([string] $leftPath, [string] $rightPath) {
    $left = [IO.File]::OpenRead($leftPath); $right = [IO.File]::OpenRead($rightPath)
    try {
        if ($left.Length -ne $right.Length) { throw 'RAW twin lengths differ.' }
        $a = [byte[]]::new(65536); $b = [byte[]]::new(65536)
        while ($left.Position -lt $left.Length) {
            $count = [int][Math]::Min($a.Length, $left.Length - $left.Position)
            $left.ReadExactly($a, 0, $count); $right.ReadExactly($b, 0, $count)
            if ([Convert]::ToHexString($a, 0, $count) -cne [Convert]::ToHexString($b, 0, $count)) { throw "RAW twin mismatch: $leftPath" }
        }
    } finally { $left.Dispose(); $right.Dispose() }
}

Assert-Files $InputDirectory $lock.authoringInput.files
if (@($lock.authoringInput.files | Where-Object { $_.name -ceq $lock.authoringInput.entryFile }).Count -ne 1) { throw 'Locked authoring entry file is missing.' }
$graph = [IO.Path]::GetFullPath($lock.graph.releaseRelativePath, $root)
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ([IO.Path]::IsPathFullyQualified($lock.graph.releaseRelativePath) -or !$graph.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Locked graph must be inside the source snapshot.' }
Assert-NoReparsePath $graph
$graphPin = File-Pin $graph
if ($graphPin.bytes -ne $lock.graph.bytes -or $graphPin.sha256 -cne $lock.graph.sha256) { throw 'Current graph RAW bytes/SHA differ.' }
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
$nativeDirectory = Join-Path $evidenceDirectory 'native'
[IO.Directory]::CreateDirectory($nativeDirectory) | Out-Null
$script:nativeCommands = [Collections.Generic.List[object]]::new()
$script:nativeOrdinal = 0
$dotnet = (Get-Command dotnet -CommandType Application).Source

# Held direct processes only. Actual child GetTempPath proofs may be captured by the owner's startup hook;
# explicit PSI TEMP/TMP and supervisor GetTempPath are not relabeled as child or descendant proof.
function Invoke-Dotnet([string] $label, [string[]] $arguments) {
    $script:nativeOrdinal++
    $prefix = Join-Path $nativeDirectory ($script:nativeOrdinal.ToString('D2') + '-' + $label)
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $root
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['TEMP'] = $env:TEMP; $start.Environment['TMP'] = $env:TMP
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $state = [ordered]@{ startAttempted = $false; started = $false; startRejectedBeforeNative = $false; pid = $null; birthUtc = $null; nativeExitCode = $null;
        waitCompleted = $false; nativeExitObserved = $false; stdoutDrained = $false; stderrDrained = $false;
        stdoutTaskTerminal = $null; stderrTaskTerminal = $null; stdoutDisposed = $false; stderrDisposed = $false; processDisposed = $false }
    $child = $null; $stdout = $null; $stderr = $null; $outTask = $null; $errTask = $null; $environmentProof = $null
    $errors = [Collections.Generic.List[string]]::new()
    try {
        $stdout = [IO.File]::Open($prefix + '.stdout.bin', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $stderr = [IO.File]::Open($prefix + '.stderr.bin', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $bindings = @(foreach ($environmentName in @('TEMP','TMP','BGLANG_PUBLIC_PACKAGE_MANIFEST','BGLANG_LEXICAL_MANIFEST','BGLANG_LEXICAL_REQUIRE_MANIFEST','BGLANG_STRESS_MANIFEST','BGLANG_STRESS_REQUIRE_MANIFEST','BGLANG_PREVIEW6_REPAIR_MANIFEST','BGLANG_PREVIEW6_REPAIR_REQUIRE_MANIFEST','BGLANG_WEB_TEST_MANIFEST','BGLANG_WEB_TEST_MANIFEST_SHA256')) {
            $present = $start.Environment.ContainsKey($environmentName)
            [ordered]@{ name = $environmentName; present = $present; value = $(if ($present) { $start.Environment[$environmentName] } else { $null }) }
        })
        $environmentProof = Write-JsonNew ($prefix + '.process-environment.json') ([ordered]@{
            createdBeforeNativeStart = $true; nativeStartAttempted = $state.startAttempted; bindings = $bindings;
            qualification = 'Actual ProcessStartInfo.Environment dictionary before Process.Start; absent differs from empty. Child GetTempPath is a separate proof.' })
        $child = [Diagnostics.Process]::new(); $child.StartInfo = $start
        $state.startAttempted = $true; $state.started = $null; $state.started = $child.Start()
        if ($state.started -ne $true) { throw 'Native start returned false.' }
        try { $outTask = $child.StandardOutput.BaseStream.CopyToAsync($stdout) } catch { $errors.Add('stdout-start: ' + $_.Exception) }
        try { $errTask = $child.StandardError.BaseStream.CopyToAsync($stderr) } catch { $errors.Add('stderr-start: ' + $_.Exception) }
        try { $state.pid = $child.Id } catch { $errors.Add('pid: ' + $_.Exception) }
        try { $state.birthUtc = $child.StartTime.ToUniversalTime().ToString('o') } catch { $errors.Add('birth: ' + $_.Exception) }
    } catch {
        if ($state.startAttempted -and $null -eq $state.started -and $_.Exception.GetBaseException() -is [ComponentModel.Win32Exception]) { $state.startRejectedBeforeNative = $true }
        $errors.Add('setup-start: ' + $_.Exception)
    } finally {
        if ($state.startAttempted -and $state.started -ne $false -and !$state.startRejectedBeforeNative) {
            try { $child.WaitForExit(); $state.waitCompleted = $true } catch { $errors.Add('wait: ' + $_.Exception) }
            try { $code = $child.ExitCode; if ($null -eq $code) { throw 'Exit unavailable.' }; $state.nativeExitCode = [int]$code; $state.nativeExitObserved = $true } catch { $errors.Add('exit: ' + $_.Exception) }
        }
        $nativeClosed = !$state.startAttempted -or $state.started -eq $false -or $state.startRejectedBeforeNative -or $state.waitCompleted -or $state.nativeExitObserved
        # Keep the process, streams and copy tasks owned while a launch/exit remains unknown.
        if ($nativeClosed) {
            if ($outTask) { try { $null = $outTask.GetAwaiter().GetResult(); $state.stdoutDrained = $true } catch { $errors.Add('stdout-drain: ' + $_.Exception) }; $state.stdoutTaskTerminal = $outTask.IsCompleted }
            if ($errTask) { try { $null = $errTask.GetAwaiter().GetResult(); $state.stderrDrained = $true } catch { $errors.Add('stderr-drain: ' + $_.Exception) }; $state.stderrTaskTerminal = $errTask.IsCompleted }
            if ($stdout) { try { $stdout.Dispose(); $state.stdoutDisposed = $true } catch { $errors.Add('stdout-dispose: ' + $_.Exception) } }
            if ($stderr) { try { $stderr.Dispose(); $state.stderrDisposed = $true } catch { $errors.Add('stderr-dispose: ' + $_.Exception) } }
            if ($child) { try { $child.Dispose(); $state.processDisposed = $true } catch { $errors.Add('process-dispose: ' + $_.Exception) } }
        } else {
            if ($outTask) { $state.stdoutTaskTerminal = $outTask.IsCompleted }
            if ($errTask) { $state.stderrTaskTerminal = $errTask.IsCompleted }
        }
    }
    $closed = $nativeClosed -and ($null -eq $outTask -or $state.stdoutTaskTerminal) -and ($null -eq $errTask -or $state.stderrTaskTerminal)
    $outPin = $null; $errPin = $null
    try { if (Test-Path -LiteralPath ($prefix + '.stdout.bin')) { $outPin = File-Pin ($prefix + '.stdout.bin') } } catch { $errors.Add('stdout-pin: ' + $_.Exception) }
    try { if (Test-Path -LiteralPath ($prefix + '.stderr.bin')) { $errPin = File-Pin ($prefix + '.stderr.bin') } } catch { $errors.Add('stderr-pin: ' + $_.Exception) }
    $record = [ordered]@{ label = $label; executable = $dotnet; arguments = $arguments; explicitTEMP = $start.Environment['TEMP']; explicitTMP = $start.Environment['TMP'];
        actualSupervisorGetTempPath = [IO.Path]::GetTempPath(); state = $state; errors = $errors.ToArray(); finishedUtc = [DateTime]::UtcNow.ToString('o');
        processEnvironment = $environmentProof; stdout = $outPin; stderr = $errPin; streamPinsFinal = [bool]($state.stdoutDisposed -and $state.stderrDisposed); allJobsClosed = [bool]$closed }
    $script:nativeCommands.Add($record)
    $saved = $false
    try { $null = Write-JsonNew ($prefix + '.terminal.json') $record; $saved = $true } catch { $errors.Add('terminal-receipt: ' + $_.Exception) }
    if (!$closed) {
        [Console]::Error.WriteLine('UNRESOLVED native supervision; preserving job and supervisor, no return/kill/retry. Evidence: ' + $prefix)
        [Threading.Thread]::Sleep([Threading.Timeout]::Infinite)
    }
    if (!$saved -or $errors.Count -or !$state.nativeExitObserved -or $state.nativeExitCode -ne 0 -or !$state.stdoutDrained -or !$state.stderrDrained -or !$state.stdoutDisposed -or !$state.stderrDisposed -or !$state.processDisposed) { throw "dotnet $label failed; native evidence: $prefix" }
    Write-Host "dotnet $label exit0; evidence: $prefix"
    return $record
}

function Assert-PassedTrx([string] $path, [string] $label) {
    $pin = File-Pin $path
    $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($path)
    $leaves = @($document.SelectNodes("//*[local-name()='UnitTestResult' and not(.//*[local-name()='UnitTestResult'])]"))
    $notPassed = @($leaves | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' })
    $check = [ordered]@{ label = $label; trx = $pin; leafCount = $leaves.Count; passed = ($leaves.Count - $notPassed.Count);
        notPassed = @($notPassed | ForEach-Object { [ordered]@{ testId = $_.GetAttribute('testId'); testName = $_.GetAttribute('testName'); outcome = $_.GetAttribute('outcome'); exactXml = $_.OuterXml } });
        status = $(if ($leaves.Count -gt 0 -and $notPassed.Count -eq 0) { 'PASS_ALL_LEAVES' } else { 'FAIL_NOT_ACCEPTED' }) }
    $null = Write-JsonNew (Join-Path $evidenceDirectory ($label + '-trx-check.json')) $check
    $script:trxChecks.Add($check)
    if ($leaves.Count -eq 0 -or $notPassed.Count -ne 0) { throw "Requested test suite has no leaves or a non-Passed outcome: $label" }
}

$previousLocation = Get-Location
$changedSelectors = @('BGLANG_PUBLIC_PACKAGE_MANIFEST', 'BGLANG_LEXICAL_MANIFEST', 'BGLANG_LEXICAL_REQUIRE_MANIFEST', 'BGLANG_STRESS_MANIFEST', 'BGLANG_STRESS_REQUIRE_MANIFEST', 'BGLANG_PREVIEW6_REPAIR_MANIFEST', 'BGLANG_PREVIEW6_REPAIR_REQUIRE_MANIFEST')
if ($TestWeb) { $changedSelectors += @('BGLANG_WEB_TEST_MANIFEST', 'BGLANG_WEB_TEST_MANIFEST_SHA256') }
$originalEnvironment = [Environment]::GetEnvironmentVariables('Process')
$consumerChecks = [Collections.Generic.List[object]]::new()
$script:trxChecks = [Collections.Generic.List[object]]::new()
try {
    Set-Location -LiteralPath $root
    $sdkRun = Invoke-Dotnet 'sdk-version' @('--version')
    $version = [IO.File]::ReadAllText($sdkRun.stdout.path).Trim()
    if ($version -cne $lock.sdk.effectiveVersion) { throw "Expected SDK $($lock.sdk.effectiveVersion); found $version" }
    $null = Invoke-Dotnet 'sourcebuilder-build' @('build', 'tools/BgLang.SourceBuilder/BgLang.SourceBuilder.csproj', '-c', 'Release', '-nr:false', '-maxCpuCount:1', '-p:UseSharedCompilation=false')
    [IO.Directory]::CreateDirectory((Split-Path $OutputDirectory -Parent)) | Out-Null
    $null = Invoke-Dotnet 'package-build' @('tools/BgLang.SourceBuilder/bin/Release/net10.0/BgLang.SourceBuilder.dll', 'build',
        (Join-Path $InputDirectory $lock.authoringInput.entryFile), $graph, $OutputDirectory)
    Assert-Files $OutputDirectory $lock.expectedCandidate.files
    if ($CompareWith) {
        Assert-Files $CompareWith $lock.expectedCandidate.files
        foreach ($file in $lock.expectedCandidate.files) { Assert-SameBytes (Join-Path $OutputDirectory $file.name) (Join-Path $CompareWith $file.name) }
    }
    if ($Test) {
        $manifest = Join-Path $OutputDirectory 'manifest.json'
        [Environment]::SetEnvironmentVariable('BGLANG_PUBLIC_PACKAGE_MANIFEST', $manifest, 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_LEXICAL_MANIFEST', $manifest, 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_LEXICAL_REQUIRE_MANIFEST', '1', 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_STRESS_MANIFEST', $manifest, 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_STRESS_REQUIRE_MANIFEST', '1', 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_PREVIEW6_REPAIR_MANIFEST', $manifest, 'Process')
        [Environment]::SetEnvironmentVariable('BGLANG_PREVIEW6_REPAIR_REQUIRE_MANIFEST', '1', 'Process')
        $null = Invoke-Dotnet 'public-restore' @('restore', 'BgLang.Public.slnx', '--locked-mode', '--disable-parallel', '-nr:false')
        $null = Invoke-Dotnet 'public-build' @('build', 'BgLang.Public.slnx', '-c', 'Release', '--no-restore', '-nr:false', '-maxCpuCount:1', '-p:UseSharedCompilation=false')
        $null = Invoke-Dotnet 'public-test' @('test', 'tests/BgLang.Public.Tests/BgLang.Public.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
            '-nr:false', '--logger', 'trx;LogFileName=public.trx', '--results-directory', $ResultsDirectory)
        Assert-PassedTrx (Join-Path $ResultsDirectory 'public.trx') 'public'
        $null = Invoke-Dotnet 'example-build' @('build', 'examples/BgLang.PublicExample/BgLang.PublicExample.csproj', '-c', 'Release', '-nr:false', '-maxCpuCount:1', '-p:UseSharedCompilation=false')
        $consumerDirectory = Join-Path $ResultsDirectory 'consumer'
        [IO.Directory]::CreateDirectory($consumerDirectory) | Out-Null
        foreach ($sample in @(@{ id = 'known-repeat'; text = 'знаме знаме.' }, @{ id = 'empty'; text = '' }, @{ id = 'context-default'; text = 'книга.' })) {
            $samplePath = Join-Path $consumerDirectory ($sample.id + '.utf8.txt')
            [IO.File]::WriteAllText($samplePath, $sample.text, [Text.UTF8Encoding]::new($false))
            $run = Invoke-Dotnet ('consumer-' + $sample.id) @('examples/BgLang.PublicExample/bin/Release/net10.0/BgLang.PublicExample.dll', $manifest, '--json', '--text-file', $samplePath)
            $check = & (Join-Path $root 'packaging/Verify-PublicConsumerJson.ps1') -InputJsonPath $run.stdout.path -SampleId $sample.id -SamplePath $samplePath
            $consumerChecks.Add($check)
        }
        $null = Write-JsonNew (Join-Path $consumerDirectory 'checks.json') $consumerChecks.ToArray()
        if ($TestWeb) {
            [Environment]::SetEnvironmentVariable('BGLANG_WEB_TEST_MANIFEST', $manifest, 'Process')
            $manifestFile = @($lock.expectedCandidate.files | Where-Object { $_.name -ceq 'manifest.json' })
            if ($manifestFile.Count -ne 1) { throw 'Exact manifest hash is required for HTTP tests.' }
            [Environment]::SetEnvironmentVariable('BGLANG_WEB_TEST_MANIFEST_SHA256', $manifestFile[0].sha256, 'Process')
            # Existing pinned Web test versions; intentionally no Web package lock or locked-restore claim.
            $null = Invoke-Dotnet 'web-build' @('build', 'tests/BgLang.Web.Tests/BgLang.Web.Tests.csproj', '-c', 'Release', '-nr:false', '-maxCpuCount:1', '-p:UseSharedCompilation=false')
            $null = Invoke-Dotnet 'web-test' @('test', 'tests/BgLang.Web.Tests/BgLang.Web.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
                '-nr:false', '--logger', 'trx;LogFileName=web.trx', '--results-directory', (Join-Path $ResultsDirectory 'web'))
            Assert-PassedTrx (Join-Path $ResultsDirectory 'web/web.trx') 'web'
        }
    }
    $null = Write-JsonNew (Join-Path $evidenceDirectory 'recipe-receipt.json') ([ordered]@{ schema = 'bglang.public-package-recipe.v2'; status = 'PASS_LOCAL_REPRODUCTION_AND_REQUESTED_CHECKS';
        inputLock = File-Pin (Join-Path $root 'data/dictionary/public-input-lock.v1.json'); graph = $graphPin; expectedPackageId = $lock.expectedCandidate.packageId;
        outputFiles = @($lock.expectedCandidate.files | ForEach-Object { File-Pin (Join-Path $OutputDirectory $_.name) }); nativeCommands = $script:nativeCommands.ToArray();
        publicAndConsumerTestsRequested = [bool]$Test; webTestsRequested = [bool]$TestWeb; trxChecks = $script:trxChecks.ToArray(); consumerChecks = $consumerChecks.ToArray(); publication = $false;
        nativeEvidenceQualification = 'Direct held native exits and drained/disposed streams only. Child self-temp requires the external owner startup-hook proof; no descendant exits are inferred.' })
} catch {
    $null = Write-JsonNew (Join-Path $evidenceDirectory 'recipe-failure.json') @{ schema = 'bglang.public-package-recipe-failure.v2'; status = 'FAIL_NOT_ACCEPTED';
        error = $_.Exception.ToString(); scriptStack = $_.ScriptStackTrace; nativeCommands = $script:nativeCommands.ToArray(); trxChecks = $script:trxChecks.ToArray(); consumerChecks = $consumerChecks.ToArray(); publication = $false }
    throw
} finally {
    foreach ($name in $changedSelectors) {
        $value = if ($originalEnvironment.Contains($name)) { [string]$originalEnvironment[$name] } else { [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    Set-Location -LiteralPath $previousLocation
}
