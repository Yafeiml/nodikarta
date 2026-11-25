# Loaded by windows-auto-update.ps1 -Recovery with two real NSIS installers and a temporary paired user.
$oldPath = $old.Path
Stop-EdgeTestDesktop $old
$menuPath = Join-Path $scope 'Programs/Nodicarta Edge.lnk'
$startupPath = Join-Path $scope 'Startup/Nodicarta Edge.lnk'
[IO.File]::Delete($startupPath)
$menuHash = (Get-FileHash -LiteralPath $menuPath).Hash
$receiptHash = (Get-FileHash -LiteralPath (Join-Path $install 'public-install.json')).Hash
$journal = Join-Path $install '.nodicarta-install-transaction.json'
$backup = Join-Path $install '.nodicarta-install-backup'
[IO.File]::WriteAllText((Join-Path $scope "withhold-ready-$NewVersion"),'')
[IO.File]::WriteAllText((Join-Path $fixture 'serve-valid'),'ready')
$old = Start-Local $oldPath @('--background','--state',$state)
$candidate = Wait-Check 'new desktop spawned but withheld its ready signal' {
    @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $NewVersion }
}
Check ((Test-Path -LiteralPath $journal) -and $old.HasExited -and $old.ExitCode -eq 0) 'Unhealthy desktop was not in a real transaction'
function Restored-Resident {
    if ((Test-Path -LiteralPath $journal) -or (Test-Path -LiteralPath $backup) -or
        (Get-FileHash -LiteralPath $manifest).Hash -cne $oldBuild) { return }
    @(In-Test) | Where-Object { $_.Path -eq $oldPath -and $_.MainModule.FileVersionInfo.ProductVersion -eq $OldVersion }
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

# Replay the same installer by its signed digest to model process loss during the update.
Check ($ReplaySha256 -match '^[0-9a-fA-F]{64}$' -and (Get-FileHash -LiteralPath $ReplaySetup).Hash -eq $ReplaySha256) 'Unexpected recovery test installer digest'
$info = [Diagnostics.ProcessStartInfo]::new($ReplaySetup)
$info.UseShellExecute = $false
$info.Arguments = "/S /UPDATE /D=$install"
$secondSetup = [Diagnostics.Process]::Start($info)
try {
    $candidate2 = Wait-Check 'second update reached its health check' {
        @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $NewVersion }
    }
    Check ((Test-Path -LiteralPath $journal) -and $restored.HasExited) 'No persistent rollback record'
    $workers = @(Get-CimInstance Win32_Process -Filter "Name='ProjectHub.Edge.Desktop.exe'" | Where-Object {
        $_.ParentProcessId -eq $secondSetup.Id -and $_.CommandLine.Contains('--install-release') -and $_.CommandLine.Contains($install) })
    Check ($workers.Count -eq 1) 'Could not uniquely identify the isolated installer worker'
    $worker = Get-Process -Id $workers[0].ProcessId
    $worker.Kill(); $worker.WaitForExit()
    $candidate2.Kill(); $candidate2.WaitForExit()
    Check ($secondSetup.WaitForExit(30000) -and $secondSetup.ExitCode -ne 0) 'Installer reported success after losing its worker'
    $request = Start-Local $cli @('--version')
    Check ($request.WaitForExit(5000) -and $request.ExitCode -ne 0) 'The fixed launcher allowed a partial installation'
    $request.Dispose()
    $info.FileName = $OldSetup
    $repair = [Diagnostics.Process]::Start($info)
    try { Check ($repair.WaitForExit(90000) -and $repair.ExitCode -eq 0) 'The verified prior setup could not recover the transaction' }
    finally { $repair.Dispose() }
    $afterLoss = Wait-Check 'the prior installer recovered the interrupted transaction' { Restored-Resident }
    Check-Preserved
    Check (!$afterLoss.HasExited) 'The recovered resident could not stay open'
} finally { $secondSetup.Dispose() }

$unknown = Join-Path $install 'user-file.txt'
[IO.File]::WriteAllText($unknown,'keep')
$uninstall = Start-Local (Join-Path $install 'Uninstall.exe') @('/S')
$null = Wait-Check 'uninstall removed its managed programs and shortcuts' {
    !(Test-Path -LiteralPath $manifest) -and !(Test-Path -LiteralPath $menuPath) -and !(Test-Path -LiteralPath (Join-Path $scope 'install-location'))
}
Check ((Database-Fingerprint) -ceq $before -and (Get-Content -Raw -LiteralPath $unknown) -ceq 'keep') 'Uninstall altered user files or the paired state'
[ordered]@{
    oldVersion=$OldVersion; attemptedVersion=$NewVersion; windowsBuild=[Environment]::OSVersion.Version.ToString();
    healthTimeoutRolledBack=$true; repeatedSignedPackageSuppressed=$true; interruptedWorkerRecoveredByVerifiedSetup=$true;
    originalShortcutsAndRuntimeRecovered=$true; loginStartupStayedDisabled=$true; retainedPairedDatabaseAndSkills=$true;
    uninstallRetainedStateAndUnknownFiles=$true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'verified.json')
Write-Host "PASS: failed health check and interrupted installation rolled back, a repeated signed update was suppressed, user files survived uninstall."
