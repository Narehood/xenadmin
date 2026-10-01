# Dot-sourced by the acceptance runner and its isolated failure fixtures.
# The caller owns $evidence and the mutable $checks list.
function Assert-UiProbeEvidence([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "UI probe did not write current evidence: $Path"
    }
    $lines = @(Get-Content -LiteralPath $Path -ErrorAction Stop)
    if ($lines.Count -eq 0 -or -not ($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        throw "UI probe wrote empty evidence: $Path"
    }
    if ($lines | Where-Object { $_ -match '^\s*FAIL:' }) {
        throw "UI probe evidence contains a FAIL: result: $Path"
    }
}

function Invoke-Check([string] $Name, [string] $Executable, [string[]] $Arguments, [string] $UiResultsLog) {
    $log = Join-Path $evidence ($Name + '.log')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Running $Name"
    $nativeExit = -1
    $passed = $false
    $checkError = $null
    try {
        # Tee-Object creates no file when a native process emits no output.
        [IO.File]::WriteAllText($log, '', [Text.UTF8Encoding]::new($false))
        # Windows PowerShell treats redirected native stderr as ErrorRecords.
        # The native exit status, checked immediately below, is authoritative.
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $PSNativeCommandUseErrorActionPreference = $false
            $global:LASTEXITCODE = $null
            & $Executable @Arguments 2>&1 | Tee-Object -FilePath $log -ErrorAction Stop | Out-Host
            if ($null -ne $global:LASTEXITCODE) { $nativeExit = $global:LASTEXITCODE }
        }
        finally { $ErrorActionPreference = $savedPreference }
        if ($nativeExit -ne 0) { throw "$Name failed with exit code $nativeExit. See $log" }
        if ($UiResultsLog) { Assert-UiProbeEvidence $UiResultsLog }
        $passed = $true
    }
    catch {
        $checkError = $_.Exception.Message
        throw
    }
    finally {
        $checks.Add([ordered]@{
            name = $Name; exitCode = $nativeExit; seconds = $watch.Elapsed.TotalSeconds
            result = $(if ($passed) { 'pass' } else { 'fail' }); log = $log
            executable = $Executable; arguments = $Arguments; error = $checkError
            uiResultsLog = $UiResultsLog
        })
    }
}

function Add-SkippedUiProbeChecks {
    foreach ($mode in @('networking', 'connection-settings', 'beta-settings', 'access-recovery', 'modernization')) {
        $checks.Add([ordered]@{
            name = "ui-$mode"; result = 'skipped'; exitCode = $null; log = $null; uiResultsLog = $null
            reason = 'These editor probes run on Windows only; they were not exercised on this platform.'
        })
    }
}

function Get-AcceptanceSuccessResult([bool] $WindowsPlatform) {
    if (-not $WindowsPlatform) {
        return 'native automated checks passed; Windows-only editor probes skipped; manual acceptance pending'
    }
    foreach ($mode in @('networking', 'connection-settings', 'beta-settings', 'access-recovery', 'modernization')) {
        $record = @($checks | Where-Object { $_.name -eq "ui-$mode" })
        if ($record.Count -ne 1 -or $record[0].result -ne 'pass' -or -not $record[0].uiResultsLog) {
            throw "Missing successful UI probe evidence for $mode."
        }
        Assert-UiProbeEvidence $record[0].uiResultsLog
    }
    return 'automated checks passed; manual acceptance pending'
}
