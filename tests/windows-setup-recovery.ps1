# Loaded by windows-auto-update.ps1 -Recovery with two real NSIS installers and a temporary paired user.
$oldPath = $old.Path
Stop-EdgeTestDesktop $old
$menuPath = Join-Path $scope 'Programs/Nodicarta Edge.lnk'
$startupPath = Join-Path $scope 'Startup/Nodicarta Edge.lnk'
[IO.File]::Delete($startupPath)
$menuHash = (Get-FileHash -LiteralPath $menuPath).Hash
$receiptHash = (Get-FileHash -LiteralPath (Join-Path $install 'public-install.json')).Hash
$journal = Join-Path $install '.nodicarta-install-transaction.json'
[IO.File]::WriteAllText((Join-Path $scope "withhold-ready-$NewVersion"),'')
[IO.File]::WriteAllText((Join-Path $fixture 'serve-valid'),'ready')
$old = Start-Local $oldPath @('--background','--state',$state)
$candidate = Wait-Check 'new desktop spawned but withheld its ready signal' {
    @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $NewVersion }
}
Check ((Test-Path -LiteralPath $journal) -and $old.HasExited -and $old.ExitCode -eq 0) 'Unhealthy desktop was not in a real transaction'
$newPath = $candidate.Path
function Restored-Resident {
    if ((Test-Path -LiteralPath $journal) -or [IO.File]::ReadAllText($active) -cne $oldBuild) { return }
    @(In-Test) | Where-Object Path -eq $oldPath
}
function Check-Preserved {
    Check ((Get-FileHash -LiteralPath $menuPath).Hash -eq $menuHash -and !(Test-Path -LiteralPath $startupPath) -and
        (Get-FileHash -LiteralPath (Join-Path $install 'public-install.json')).Hash -eq $receiptHash -and
        (Get-FileHash -LiteralPath $config).Hash -eq $configHash -and (Database-Fingerprint) -ceq $before -and
        [IO.File]::ReadAllText($skillsPreference) -ceq 'false' -and (Skills-Fingerprint) -ceq $skills) 'Rollback lost state or preferences'
}
$restored = Wait-Check 'old resident returned after failed health check' { Restored-Resident }
Check ($candidate.HasExited -and $restored.MainWindowHandle -eq 0) 'Failed candidate outlived recovery'
Check-Preserved
$time = [datetime]::UtcNow
$null = Wait-Check 'fresh feed poll after the rollback' { (Get-Item -LiteralPath (Join-Path $fixture 'last-probe')).LastWriteTimeUtc -gt $time }
Start-Sleep -Seconds 2
Check ((Get-Content -Raw -LiteralPath (Join-Path $fixture 'download-count')) -ceq '1' -and !$restored.HasExited) 'Same failed signed release was retried automatically'

# Replay the already verified installer. Terminate the candidate and installer worker at once to model a process loss.
$cached = @(Get-ChildItem -LiteralPath (Join-Path $install 'updates') -File -Filter 'setup-*.exe')
Check ($cached.Count -eq 1) 'Expected one authenticated cached installer'
$info = [Diagnostics.ProcessStartInfo]::new($cached[0].FullName)
$info.UseShellExecute = $false
$info.Arguments = "/S /UPDATE /D=$install"
$secondSetup = [Diagnostics.Process]::Start($info)
try {
    $candidate2 = Wait-Check 'second update reached its health check' { @(In-Test) | Where-Object Path -eq $newPath }
    Check ((Test-Path -LiteralPath $journal) -and $restored.HasExited) 'No persistent rollback record'
    $workers = @(Get-CimInstance Win32_Process -Filter "Name='ProjectHub.Edge.Desktop.exe'" | Where-Object {
        $_.ParentProcessId -eq $secondSetup.Id -and $_.CommandLine.Contains('--install-release') -and $_.CommandLine.Contains($install) })
    Check ($workers.Count -eq 1) 'Could not uniquely identify the isolated installer worker'
    $worker = Get-Process -Id $workers[0].ProcessId
    $worker.Kill(); $worker.WaitForExit()
    $candidate2.Kill(); $candidate2.WaitForExit()
    Check ($secondSetup.WaitForExit(30000) -and $secondSetup.ExitCode -ne 0) 'Installer reported success after losing its worker'
    $request = Start-Local $newPath @('--background','--state',$state)
    $afterLoss = Wait-Check 'desktop recovered persistent transaction on next launch' { Restored-Resident }
    Check-Preserved
    Check ($request.WaitForExit(5000) -and !$afterLoss.HasExited) 'Next launch did not redirect to the old resident'
} finally { $secondSetup.Dispose() }

$unknown = Join-Path $install 'user-file.txt'
[IO.File]::WriteAllText($unknown,'keep')
$uninstall = Start-Local (Join-Path $install 'Uninstall.exe') @('/S')
$null = Wait-Check 'uninstall removed its managed programs and shortcuts' {
    !(Test-Path -LiteralPath $active) -and !(Test-Path -LiteralPath $menuPath) -and !(Test-Path -LiteralPath (Join-Path $scope 'install-location'))
}
Check ((Database-Fingerprint) -ceq $before -and (Get-Content -Raw -LiteralPath $unknown) -ceq 'keep') 'Uninstall altered user files or the paired state'
[ordered]@{
    oldVersion=$OldVersion; attemptedVersion=$NewVersion; windowsBuild=[Environment]::OSVersion.Version.ToString();
    healthTimeoutRolledBack=$true; repeatedSignedPackageSuppressed=$true; interruptedWorkerRecoveredOnNextLaunch=$true;
    originalShortcutsAndActiveReleaseRecovered=$true; loginStartupStayedDisabled=$true; retainedPairedDatabaseAndSkills=$true;
    uninstallRetainedStateAndUnknownFiles=$true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'verified.json')
Write-Host "PASS: failed health check and interrupted installation rolled back, a repeated signed update was suppressed, user files survived uninstall."
