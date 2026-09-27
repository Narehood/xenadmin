[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,
    [ValidatePattern('^\d+$')]
    [string] $BuildRevision = '0',
    [string] $Codename = 'Awa',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $ReleaseVersion
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "artifacts/publish/$RuntimeIdentifier/$([Guid]::NewGuid().ToString('N'))"
$archive = [IO.Path]::GetFullPath($ArchivePath)
$lockName = "packages.$RuntimeIdentifier.lock.json"
$versionProperties = @()
if ($ReleaseVersion) {
    # Pin all projects to the release job's date, even if publishing crosses UTC midnight.
    $parsedVersion = [Version]::Parse($ReleaseVersion)
    if ($parsedVersion.Revision -ne [int]$BuildRevision) { throw 'ReleaseVersion and BuildRevision disagree.' }
    $versionProperties = @("-p:BuildYear=$($parsedVersion.Major)", "-p:BuildMonth=$($parsedVersion.Minor)", "-p:BuildDay=$($parsedVersion.Build)")
}

Push-Location $repoRoot
try {
    # The portable graph is checked in. RID restores need additional runtime-pack
    # graphs, so seed per-project obj locks without rewriting the reviewed locks.
    $lockFiles = @(git ls-files '*/packages.lock.json')
    if ($LASTEXITCODE -ne 0 -or $lockFiles.Count -eq 0) {
        throw 'Could not enumerate the checked-in NuGet lockfiles.'
    }
    foreach ($lockFile in $lockFiles) {
        $obj = Join-Path (Split-Path -Parent $lockFile) 'obj'
        New-Item -ItemType Directory -Force -Path $obj | Out-Null
        Copy-Item -LiteralPath $lockFile -Destination (Join-Path $obj $lockName) -Force
    }

    dotnet publish XcpNgCenter.Shell/XcpNgCenter.Shell.csproj -c Release `
        -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=false `
        "-p:NuGetLockFilePath=obj/$lockName" -p:RestoreLockedMode=false `
        "-p:BuildRevision=$BuildRevision" "-p:Codename=$Codename" @versionProperties `
        -o $publishRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $RuntimeIdentifier." }

    Copy-Item -LiteralPath 'XcpNgCenter.Shell/packaging/INSTALL.TXT' -Destination $publishRoot -Force
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
    # Existing updaters require the shell and runtime files at the archive root.
    if ($RuntimeIdentifier -eq 'win-x64') {
        Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archive -Force
    }
    else {
        if ($env:OS -eq 'Windows_NT') { throw 'Package Linux releases on Linux to preserve executable permissions.' }
        chmod 755 (Join-Path $publishRoot 'XcpNgCenter.Shell')
        if ($LASTEXITCODE -ne 0) { throw 'Could not set the Linux executable mode.' }
        tar -C $publishRoot -czf $archive .
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the Linux archive.' }
    }
}
finally {
    Pop-Location
}
