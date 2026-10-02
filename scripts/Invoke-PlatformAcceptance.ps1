[CmdletBinding()]
param(
    [string] $DotnetPath = 'dotnet',
    [string] $PythonPath,
    [string] $EvidenceDirectory,
    [ValidatePattern('^\d+$')]
    [string] $BuildRevision = '0',
    [switch] $SkipRdpAxImp
)

# Runs only automated local acceptance. It never loads a saved profile, connects
# to a pool, publishes a release, or launches an update with elevation.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$windowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$linuxPlatform = [bool](Get-Variable IsLinux -ValueOnly -ErrorAction SilentlyContinue)
if (-not $windowsPlatform -and -not $linuxPlatform) { throw 'Acceptance supports Windows and Linux only.' }
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repoRoot ('artifacts/platform-acceptance/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidence directory for each acceptance pass.' }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$manifest = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $null
    workingTree = @()
    os = [Environment]::OSVersion.VersionString
    dotnet = $null
    python = $null
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    buildRevision = $BuildRevision
    reusedRdpInterop = [bool]$SkipRdpAxImp
    rdpInteropHashes = @{}
    portableLocksPreserved = $null
    checks = $checks
    result = 'running'
    pending = @(
        'Physical Windows/Linux desktop behavior and display scaling',
        'Writable and protected installation update, UAC, rollback, and unelevated restart',
        'Live pool networking, console reboot diagnosis, and HA failover/recovery',
        'Live AD domain/role recovery and DR storage/VM recovery and cleanup'
    )
}
$originalPath = $env:PATH
$originalDotnetRoot = $env:DOTNET_ROOT
$locks = @{}
$locksCaptured = $false
$locationPushed = $false

. (Join-Path $PSScriptRoot 'PlatformAcceptanceChecks.ps1')
if (-not $windowsPlatform) {
    Add-SkippedUiProbeChecks
    $manifest.pending += 'Windows-only networking, connection, beta and AD/DR editor probes were not run on this platform'
}

function Update-LockEvidence {
    $changed = @()
    foreach ($path in $locks.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $locks[$path]) {
            $changed += $path
        }
    }
    $manifest.portableLockChanges = $changed
    $manifest.portableLocksPreserved = $changed.Count -eq 0
    if ($changed.Count -ne 0) { throw ('Portable lockfiles changed during acceptance: ' + ($changed -join ', ')) }
}

try {
    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    if ($linuxPlatform) {
        # Distribution SDK launchers commonly live at /usr/bin/dotnet as a
        # symlink; apphosts need the actual installation directory in DOTNET_ROOT.
        $dotnetFile = [IO.FileInfo]::new($dotnet)
        if (-not $dotnetFile.PSObject.Methods['ResolveLinkTarget']) {
            throw 'Use PowerShell 7.2 or newer on Linux to resolve the SDK installation path.'
        }
        $dotnetTarget = $dotnetFile.ResolveLinkTarget($true)
        if ($null -ne $dotnetTarget) { $dotnet = $dotnetTarget.FullName }
    }
    if (-not $PythonPath) { $PythonPath = if ($windowsPlatform) { 'python' } else { 'python3' } }
    $python = (Get-Command $PythonPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $manifest.dotnet = $dotnet
    $manifest.python = $python
    Push-Location $repoRoot
    $locationPushed = $true
    if (-not $windowsPlatform -and $SkipRdpAxImp) { throw 'SkipRdpAxImp applies only to Windows builds.' }
    if ($SkipRdpAxImp) {
        foreach ($interop in @('AxMSTSCLib.dll', 'MSTSCLib.dll')) {
            $interopPath = Join-Path $repoRoot "XenAdmin/RDP/$interop"
            if (-not (Test-Path -LiteralPath $interopPath -PathType Leaf)) {
                throw "SkipRdpAxImp requires existing trusted RDP interop: $interop"
            }
            $manifest.rdpInteropHashes[$interop] = (Get-FileHash -LiteralPath $interopPath -Algorithm SHA256).Hash
        }
    }
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $originalPath
    $manifest.sourceCommit = (git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $manifest.workingTree = @(git status --short)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source changes.' }
    Invoke-Check 'redistribution-notice-fixtures' $python @((Join-Path $PSScriptRoot 'test-redistribution-notices.py'))
    $lockPaths = @(git ls-files '*/packages.lock.json')
    if ($LASTEXITCODE -ne 0 -or $lockPaths.Count -eq 0) { throw 'Cannot enumerate portable lockfiles.' }
    foreach ($path in $lockPaths) { $locks[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    $locksCaptured = $true

    Invoke-Check 'sdk' $dotnet @('--info')
    Invoke-Check 'python' $python @('-c', 'import sys; print(sys.version); sys.exit(0 if sys.version_info >= (3, 12) else 1)')
    if (-not $windowsPlatform) {
        foreach ($dependency in @('xvfb-run', 'xauth', 'xdotool', 'openbox', 'xprop', 'xwininfo', 'tar', 'chmod')) {
            $null = Get-Command $dependency -CommandType Application -ErrorAction Stop
        }
    }
    if ($windowsPlatform) {
        Invoke-Check 'restore' $dotnet @('restore', 'XenAdmin.sln', '--locked-mode')
        $rdpArgs = @()
        if ($SkipRdpAxImp) { $rdpArgs = @('-p:SkipRdpAxImp=true') }
        foreach ($configuration in @('Release', 'Debug')) {
            Invoke-Check "build-$configuration" $dotnet (@('build', 'XenAdmin.sln', '-c', $configuration, '--no-restore', '--nologo', "-p:BuildRevision=$BuildRevision") + $rdpArgs)
        }
    }
    foreach ($configuration in @('Release', 'Debug')) {
        Invoke-Check "shell-$configuration" $dotnet @('test', 'XcpNgCenter.Shell.Tests/XcpNgCenter.Shell.Tests.csproj', '-c', $configuration,
            '-p:RestoreLockedMode=true', '--logger', "trx;LogFileName=shell-$configuration.trx", '--results-directory', $evidence)
    }
    $frameworks = @('net10.0')
    if ($windowsPlatform) { $frameworks += 'net481' }
    foreach ($framework in $frameworks) {
        Invoke-Check "shared-$framework" $dotnet @('test', 'XenCenterLib.Tests/XenCenterLib.Tests.csproj', '-c', 'Release', '-f', $framework,
            '-p:RestoreLockedMode=true', '--logger', "trx;LogFileName=shared-$framework.trx", '--results-directory', $evidence)
    }
    if ($windowsPlatform) {
        foreach ($configuration in @('Release', 'Debug')) {
            Invoke-Check "winforms-$configuration" $dotnet @('run', '--project', 'tools/WinForms.CompatibilityProbe/WinForms.CompatibilityProbe.csproj',
                '-c', 'Release', '-p:RestoreLockedMode=true', '--', (Join-Path $repoRoot "XenAdmin/bin/$configuration/net10.0-windows/XCP-ng Center.dll"))
            Invoke-Check "winforms-lifecycle-designer-$configuration" $dotnet @('run', '--project', 'tools/WinForms.CompatibilityProbe/WinForms.CompatibilityProbe.csproj',
                '-c', 'Release', '--no-build', '--no-restore', '--', (Join-Path $repoRoot "XenAdmin/bin/$configuration/net10.0-windows/XCP-ng Center.dll"), '--lifecycle-designer')
            Invoke-Check "winforms-plugin-archive-$configuration" $dotnet @('run', '--project', 'tools/WinForms.CompatibilityProbe/WinForms.CompatibilityProbe.csproj',
                '-c', 'Release', '--no-build', '--no-restore', '--', (Join-Path $repoRoot "XenAdmin/bin/$configuration/net10.0-windows/XCP-ng Center.dll"), '--plugin-archive')
        }
        Invoke-Check 'proxy-auth' $dotnet @('run', '--project', 'tools/WinForms.CompatibilityProbe/WinForms.CompatibilityProbe.csproj',
            '-c', 'Release', '--no-build', '--no-restore', '--', (Join-Path $repoRoot 'XenAdmin/bin/Release/net10.0-windows/XCP-ng Center.dll'), '--proxy-auth')
        foreach ($mode in @('networking', 'connection-settings', 'beta-settings', 'access-recovery', 'modernization', 'remote-desktop')) {
            $probeArgs = @('run', '--project', 'tools/AdvancedNetworking.UiProbe/AdvancedNetworking.UiProbe.csproj', '-c', 'Release', '-p:RestoreLockedMode=true', '--')
            if ($mode -ne 'networking') { $probeArgs += "--$mode" }
            $destination = Join-Path $evidence "ui-$mode"
            $probeArgs += @('--evidence-directory', $destination)
            Invoke-Check "ui-$mode" $dotnet $probeArgs (Join-Path $destination 'results.log')
        }
    }
    $rid = if ($windowsPlatform) { 'win-x64' } else { 'linux-x64' }
    $extension = if ($windowsPlatform) { 'zip' } else { 'tar.gz' }
    $archive = Join-Path $evidence "XcpNgCenter.Shell-$rid.$extension"
    # The hosting process may be powershell_ise.exe; publish needs the CLI.
    $shellName = if ($windowsPlatform -and $PSVersionTable.PSEdition -ne 'Core') { 'powershell.exe' }
        elseif ($windowsPlatform) { 'pwsh.exe' } else { 'pwsh' }
    $shell = Join-Path $PSHOME $shellName
    if (-not (Test-Path -LiteralPath $shell -PathType Leaf)) { throw 'Cannot locate the current PowerShell CLI.' }
    Invoke-Check 'publish' $shell @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Publish-Shell.ps1'), '-RuntimeIdentifier', $rid, '-ArchivePath', $archive, '-BuildRevision', $BuildRevision)
    $manifest.archive = $archive
    $manifest.archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $smokeArgs = @((Join-Path $PSScriptRoot 'verify-shell-package.py'), '--archive', $archive, '--rid', $rid, '--evidence-directory', (Join-Path $evidence 'package-smoke'))
    if ($windowsPlatform) { Invoke-Check 'package-smoke' $python $smokeArgs }
    else {
        $xvfb = (Get-Command xvfb-run -ErrorAction Stop).Source
        Invoke-Check 'package-smoke' $xvfb (@('-a', $python) + $smokeArgs + @('--desktop', '--window-manager'))
    }
    Update-LockEvidence
    $manifest.result = Get-AcceptanceSuccessResult $windowsPlatform
}
catch {
    $manifest.result = 'failed'
    $manifest.error = $_.Exception.Message
    throw
}
finally {
    try {
        if ($locksCaptured -and $manifest.result -eq 'failed') {
            try { Update-LockEvidence }
            catch { $manifest.lockVerificationError = $_.Exception.Message }
        }
        $manifest.finishedUtc = [DateTime]::UtcNow.ToString('o')
        [IO.File]::WriteAllText((Join-Path $evidence 'acceptance.json'),
            ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    }
    finally {
        $env:PATH = $originalPath
        $env:DOTNET_ROOT = $originalDotnetRoot
        if ($locationPushed) { Pop-Location }
        Write-Host "Acceptance evidence: $evidence"
    }
}
