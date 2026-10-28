param(
    [ValidateRange(2, 4)][int]$Players = 4,
    [string]$Executable = '',
    [ValidateRange(0, 15)][int]$LateJoinSeconds = 0
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Executable) { $Executable = Join-Path $projectRoot 'Builds/Windows/ParkChaos.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$proofRoot = Join-Path $projectRoot ('_previews/lan-proof/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
[IO.Directory]::CreateDirectory($proofRoot) | Out-Null
$runs = @()
$failures = [Collections.Generic.List[string]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()

try {
    for ($i = 0; $i -lt $Players; $i++) {
        if ($i -gt 0) { Start-Sleep -Seconds 2 }
        if ($i -eq ($Players - 1) -and $LateJoinSeconds -gt 0) {
            # LateJoinSeconds is measured from host launch, not added to every client.
            while ($clock.Elapsed.TotalSeconds -lt $LateJoinSeconds) { Start-Sleep -Milliseconds 200 }
        }
        $name = if ($i -eq 0) { 'host' } else { "client$i" }
        $proof = Join-Path $proofRoot "$name.jsonl"
        $log = Join-Path $proofRoot "$name.log"
        $mode = if ($i -eq 0) { '-parkchaos-host' } else { '-parkchaos-join 127.0.0.1' }
        $role = if ($i -eq 1) { 'Manager' } elseif ($i -eq 2) { 'Technician' } else { 'Operator' }
        $arguments = "$mode -parkchaos-physics-proof -parkchaos-role $role -parkchaos-proof `"$proof`" -logFile `"$log`" -batchmode -force-d3d11 -screen-fullscreen 0 -screen-width 1280 -screen-height 720"
        $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $runs += [pscustomobject]@{ Name = $name; Role = $role; Process = $process; Proof = $proof; Log = $log }
        Write-Host "Started $name (PID $($process.Id))"
    }
    while (@($runs | Where-Object { -not $_.Process.HasExited }).Count -gt 0 -and $clock.Elapsed.TotalSeconds -lt 120) {
        Start-Sleep -Seconds 1
    }
    foreach ($run in $runs) {
        if (-not $run.Process.HasExited) { $failures.Add("$($run.Name): process exceeded 120 second timeout") }
    }
}
finally {
    # Stop only executable instances this invocation started if launch or timeout failed.
    foreach ($run in $runs) {
        if (-not $run.Process.HasExited) { Stop-Process -Id $run.Process.Id -ErrorAction SilentlyContinue }
    }
}

$hostResult = $null
$hostSamples = @()
foreach ($run in $runs) {
    if ($run.Process.ExitCode -ne 0) { $failures.Add("$($run.Name): exit code $($run.Process.ExitCode)") }
    if (-not (Test-Path -LiteralPath $run.Proof)) {
        $failures.Add("$($run.Name): missing proof file; use a Development build with FISHNET")
        continue
    }
    try { $samples = @(Get-Content -LiteralPath $run.Proof | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) }
    catch { $failures.Add("$($run.Name): invalid JSONL: $_"); continue }
    if ($samples.Count -eq 0) { $failures.Add("$($run.Name): empty proof"); continue }
    if ($run.Name -eq 'host') { $hostSamples = $samples }
    else {
        $deadlineMatches = 0
        foreach ($sample in $samples) {
            foreach ($fault in @($sample.faults)) {
                if ($null -eq $fault -or $fault.deadline -le 0) { continue }
                # Probe cadence is one real second / four sim seconds. Match the same
                # fault instance on either adjacent host sample rather than an event edge.
                $nearby = @($hostSamples | Where-Object { [Math]::Abs($_.simTime - $sample.simTime) -le 4.5 })
                $matching = @($nearby | ForEach-Object { $_.faults } | Where-Object {
                    $_.type -eq $fault.type -and [Math]::Abs($_.deadline - $fault.deadline) -lt 0.05 -and $_.missed -eq $fault.missed
                })
                if ($matching.Count -gt 0) { $deadlineMatches++ }
            }
        }
        if ($deadlineMatches -eq 0) { $failures.Add("$($run.Name): no timed fault snapshot matched host (type/deadline/overdue)") }
        else { Write-Host "$($run.Name): $deadlineMatches timed fault snapshots matched host" }
    }
    if (@($samples | Where-Object { $_.connected }).Count -eq 0) { $failures.Add("$($run.Name): never connected") }
    $localSamples = @($samples | Where-Object { $_.role -and $_.role -ne 'none' })
    if ($localSamples.Count -eq 0) { $failures.Add("$($run.Name): no local player spawned") }
    if (@($localSamples | Where-Object { $_.role -eq $run.Role }).Count -eq 0) {
        $failures.Add("$($run.Name): requested role $($run.Role) was not observed")
    }
    if (@($localSamples | Where-Object { $_.cameras -ne 1 -or $_.listeners -ne 1 }).Count -gt 0) {
        $failures.Add("$($run.Name): local player did not consistently have exactly one camera and listener")
    }
    $maxPlayers = ($samples | Measure-Object -Property players -Maximum).Maximum
    if ($maxPlayers -ne $Players) { $failures.Add("$($run.Name): maximum spawned players $maxPlayers, expected $Players") }
    if ($run.Name -eq 'host' -and ($samples | Measure-Object -Property peers -Maximum).Maximum -ne $Players) {
        $failures.Add("host: connected peer count never reached $Players")
    }
    if (@($samples | Where-Object { $_.physicsChecked -and $_.lapCheckCount -eq 4 -and $_.minimumLapHeight -ge 0.9 }).Count -eq 0) {
        $failures.Add("$($run.Name): generated lap-bar interaction volumes failed physical audit")
    }
    $errors = @($samples | ForEach-Object { $_.errors } | Where-Object { $_ } | Select-Object -Unique)
    if ($errors.Count -gt 0) { $failures.Add("$($run.Name): $($errors.Count) runtime errors; see JSONL and log") }
    if (@($samples | Where-Object { $_.final }).Count -eq 0) { $failures.Add("$($run.Name): missing final probe sample") }
    $result = @($samples | Where-Object { $_.state -eq 'EndOfShift' }) | Select-Object -Last 1
    if ($null -eq $result) { $failures.Add("$($run.Name): no EndOfShift snapshot"); continue }
    if ($run.Name -eq 'host') {
        $hostResult = $result
        if (@($samples | Where-Object { $_.admissionsPaused }).Count -eq 0 -or $result.admissionsPaused) {
            $failures.Add('host: manager admissions pause/resume was not observed')
        }
        if ($result.guestCareCount -lt 1) { $failures.Add('host: manager guest care was not observed') }
    }
    elseif ($null -eq $hostResult) { $failures.Add("$($run.Name): cannot compare results because host EndOfShift is missing") }
    elseif ($result.score -ne $hostResult.score -or $result.riders -ne $hostResult.riders -or $result.dispatches -ne $hostResult.dispatches) {
        $failures.Add("$($run.Name): end results differ from host (score/riders/dispatches)")
    }
    elseif ($result.budget -ne $hostResult.budget -or $result.guestCareCount -ne $hostResult.guestCareCount -or $result.admissionsPaused -ne $hostResult.admissionsPaused) {
        $failures.Add("$($run.Name): final manager state differs from host")
    }
}

Write-Host "Proof, screenshots, and logs: $proofRoot"
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL: $failure" -ForegroundColor Red }
    throw "LAN verification failed ($($failures.Count) checks)."
}
Write-Host "PASS: $Players LAN players; one camera/listener each; no runtime errors; matching end-of-shift results." -ForegroundColor Green
