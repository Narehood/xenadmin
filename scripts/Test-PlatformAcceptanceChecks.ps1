[CmdletBinding()]
param([string] $EvidenceDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PlatformAcceptanceChecks.ps1')
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/acceptance-fixtures/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new fixture evidence directory.' }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[string]]::new()
$windowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$shellName = if ($windowsPlatform -and $PSVersionTable.PSEdition -ne 'Core') { 'powershell.exe' }
    elseif ($windowsPlatform) { 'pwsh.exe' } else { 'pwsh' }
$shell = Join-Path $PSHOME $shellName

function Native-Args([string] $Command) {
    return @('-NoProfile', '-NonInteractive', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command)))
}
function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([string] $Name, [scriptblock] $Action, [int] $ExitCode, [string] $ErrorText) {
    $checks.Clear()
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Require ($null -ne $failure -and $failure.Contains($ErrorText)) "$Name did not report the expected failure: $failure"
    Require ($checks.Count -eq 1 -and $checks[0].result -eq 'fail' -and $checks[0].exitCode -eq $ExitCode) "$Name was incorrectly recorded as a pass."
    Require (Test-Path -LiteralPath $checks[0].log -PathType Leaf) "$Name lost its native log."
    $results.Add("PASS: $Name")
}

try {
    Expect-Failure 'nonzero-native-exit' { Invoke-Check 'nonzero-native-exit' $shell (Native-Args 'exit 7') } 7 'exit code 7'
    $missing = Join-Path $evidence 'missing-results.log'
    Expect-Failure 'missing-ui-evidence' { Invoke-Check 'missing-ui-evidence' $shell (Native-Args 'exit 0') $missing } 0 'did not write current evidence'
    $failed = Join-Path $evidence 'failed-results.log'
    [IO.File]::WriteAllText($failed, "earlier assertion passed`nFAIL: binding did not clear the password`n")
    Expect-Failure 'failed-ui-evidence' { Invoke-Check 'failed-ui-evidence' $shell (Native-Args 'exit 0') $failed } 0 'contains a FAIL:'
    $empty = Join-Path $evidence 'empty-results.log'
    [IO.File]::WriteAllText($empty, '')
    Expect-Failure 'empty-ui-evidence' { Invoke-Check 'empty-ui-evidence' $shell (Native-Args 'exit 0') $empty } 0 'empty evidence'

    $checks.Clear()
    $success = Join-Path $evidence 'passed-results.log'
    [IO.File]::WriteAllText($success, "All fixture assertions passed`n")
    foreach ($mode in @('networking', 'connection-settings', 'beta-settings', 'access-recovery')) {
        Invoke-Check "ui-$mode" $shell (Native-Args '[Console]::Error.WriteLine("benign native diagnostic"); exit 0') $success
    }
    Require ((Get-AcceptanceSuccessResult $true) -eq 'automated checks passed; manual acceptance pending') 'Windows success summary did not require all four probes.'
    Require (@($checks | Where-Object result -ne 'pass').Count -eq 0) 'Benign native stderr caused a false failure.'
    $results.Add('PASS: successful UI evidence and benign native stderr')
    [IO.File]::WriteAllText($success, 'FAIL: evidence changed before finalization')
    $failure = $null
    try { Get-AcceptanceSuccessResult $true | Out-Null } catch { $failure = $_.Exception.Message }
    Require ($null -ne $failure -and $failure.Contains('FAIL:')) 'Finalization accepted failed UI evidence.'
    $results.Add('PASS: finalization rechecks UI evidence')

    $checks.Clear()
    Add-SkippedUiProbeChecks
    $manifest = @{ checks = $checks; result = (Get-AcceptanceSuccessResult $false) } | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    Require ($manifest.checks.Count -eq 4 -and @($manifest.checks | Where-Object result -ne 'skipped').Count -eq 0) 'Linux omitted explicit UI skips.'
    Require ($manifest.result.Contains('editor probes skipped')) 'Linux success summary claimed Windows UI coverage.'
    $failure = $null
    try { Get-AcceptanceSuccessResult $true | Out-Null } catch { $failure = $_.Exception.Message }
    Require ($null -ne $failure) 'Skipped probes satisfied Windows acceptance.'
    $results.Add('PASS: skipped probes stay distinct from successful Windows acceptance')
    Write-Host "Passed $($results.Count) acceptance failure/coverage fixtures."
}
catch { $results.Add('FAIL: ' + $_.Exception.Message); throw }
finally { [IO.File]::WriteAllLines((Join-Path $evidence 'results.log'), $results) }
