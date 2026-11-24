#requires -Version 7.0
# Smoke test for a signed candidate installed in a disposable test user's shell folders.
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$TestHome, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
. (Join-Path $PSScriptRoot '../deploy/windows/Stop-EdgeTestDesktop.ps1')
. (Join-Path $PSScriptRoot 'Save-WindowsTestWindow.ps1')
$exe = (Get-Item -LiteralPath $Executable).FullName
$homePath = (Get-Item -LiteralPath $TestHome).FullName
$tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) + '\'
if (!$exe.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or
    !$homePath.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Use a disposable installed candidate and test home.' }
$startup = Join-Path $homePath 'Startup/Nodicarta Edge.lnk'
$shell = New-Object -ComObject WScript.Shell
$existing = $shell.CreateShortcut($startup)
if (!(Test-Path -LiteralPath $startup) -or $existing.TargetPath -ne $exe -or $existing.Arguments -notmatch '--background') {
    throw 'The fixture installer must have registered the expected quiet startup shortcut.'
}
$original = [IO.File]::ReadAllBytes($startup)
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-preferences-qa-')
$state = Join-Path $work.FullName 'device state 中文'
$testProfileDirectory = [IO.Directory]::CreateDirectory((Join-Path $work.FullName 'profile')).FullName
$autoPref = Join-Path $testProfileDirectory 'LOCALAPPDATA/Nodicarta/application-auto-update'
$checks = [Collections.Generic.List[string]]::new()
function Pass($message) { $checks.Add($message); Write-Host "PASS $message" }
function Until($name,[scriptblock]$condition) {
    $end = [datetime]::UtcNow.AddSeconds(15)
    do { $value=& $condition; if ($value) { return $value }; Start-Sleep -Milliseconds 100 } while ([datetime]::UtcNow -lt $end)
    if ($app -and $app.HasExited) { throw "Timed out: $name (candidate exited with code $($app.ExitCode))" }
    throw "Timed out: $name (candidate $($app.Id), window '$($app.MainWindowTitle)')"
}
function Launch-Edge([string]$target, [string]$arguments) {
    if ($target -ne $exe) { throw 'Unexpected candidate executable.' }
    $info = [Diagnostics.ProcessStartInfo]::new($target)
    $info.UseShellExecute = $false
    $info.Arguments = $arguments
    $info.WorkingDirectory = Split-Path $exe
    $info.Environment['NODICARTA_EDGE_SETUP_SCOPE'] = 'fixture'
    $info.Environment['NODICARTA_EDGE_SETUP_TEST_HOME'] = $homePath
    $info.Environment['HOME'] = $info.Environment['USERPROFILE'] = $testProfileDirectory
    foreach ($key in 'APPDATA','LOCALAPPDATA','XDG_CONFIG_HOME','CLAUDE_CONFIG_DIR','DSH_HOME','PI_CODING_AGENT_DIR','CODEX_HOME') {
        $info.Environment[$key] = Join-Path $testProfileDirectory $key
    }
    foreach ($key in 'OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT') { $null=$info.Environment.Remove($key) }
    [Diagnostics.Process]::Start($info)
}
function Window($id) {
    $pidCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    foreach ($win in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition)) {
        if ($win.Current.AutomationId -eq $id -and !$win.Current.IsOffscreen) { return $win }
        if ($nested = Control $win $id) { if (!$nested.Current.IsOffscreen) { return $nested } }
    }
}
function Control($parent, $id) {
    $cond = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$cond)
}
function Push($parent, $id) {
    $control = Until $id { Control $parent $id }
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Toggle($parent,$id) {
    $control = Until $id { $c=Control $parent $id; if($c -and $c.Current.IsEnabled){$c} }
    $control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
}
function Value($parent,$id) { (Control $parent $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Wake-Resident {
    $wake=Launch-Edge $exe "--state ""$state"""
    try { if (!$wake.WaitForExit(6500) -or $wake.ExitCode -ne 0) { throw 'A repeat launch did not wake the existing resident.' } }
    finally { $wake.Dispose() }
    Until 'resident overview' { Window 'EdgeMainWindow' }
}
$app=$null
try {
    if (@(Get-Process ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue).Count) { throw 'Release the real desktop before this isolated test.' }
    $app=Launch-Edge $exe "--state ""$state"""
    $main=Until 'overview' { Window 'EdgeMainWindow' }
    $null=Until 'unpaired setup' { Control $main 'PairButton' }
    Push $main 'AppPreferencesButton'
    $settings=Until 'settings' { Window 'AppPreferencesWindow' }
    $firstLabel=(Control $settings 'StartOnLogin').Current.Name
    $firstLanguage=if($firstLabel -eq '登录时启动 Edge'){'zh-CN'}else{'en'}
    $otherLanguage=if($firstLanguage -eq 'zh-CN'){'en'}else{'zh-CN'}
    $otherLabel=if($otherLanguage -eq 'en'){'Start Edge when I sign in'}else{'登录时启动 Edge'}
    if ($firstLabel -notin '登录时启动 Edge','Start Edge when I sign in' -or
        (Value $settings 'StartOnLogin') -ne 'On' -or (Value $settings 'AutomaticAppUpdates') -ne 'On') { throw 'First install preferences differ.' }
    if ($EvidenceDirectory) { Save-WindowsTestWindow $settings $EvidenceDirectory "preferences-$firstLanguage" }
    Toggle $settings 'StartOnLogin'
    $null=Until 'disabled login' { !(Test-Path -LiteralPath $startup) }
    Toggle $settings 'AutomaticAppUpdates'
    $null=Until 'saved update opt-out' { (Test-Path -LiteralPath $autoPref) -and [IO.File]::ReadAllText($autoPref) -ceq 'false' }
    Push $settings 'CloseAppPreferences'
    Stop-EdgeTestDesktop $app
    $app.Dispose()
    $app=$null
    Pass 'Disabling login removed the user Startup entry; app updates were switched off independently.'
    [IO.File]::WriteAllText((Join-Path $state 'ui-language.txt'),$otherLanguage)
    $app=Launch-Edge $exe "--state ""$state"""
    $main=Until 'restarted overview' { Window 'EdgeMainWindow' }
    Push $main 'AppPreferencesButton'
    $settings=Until 'reopened settings' { Window 'AppPreferencesWindow' }
    if ((Control $settings 'StartOnLogin').Current.Name -ne $otherLabel -or
        (Value $settings 'StartOnLogin') -ne 'Off' -or (Value $settings 'AutomaticAppUpdates') -ne 'Off') { throw 'Restart did not retain both choices.' }
    if ($EvidenceDirectory) { Save-WindowsTestWindow $settings $EvidenceDirectory "preferences-$otherLanguage" }
    Toggle $settings 'AutomaticAppUpdates'
    $null=Until 'saved update opt-in' { [IO.File]::ReadAllText($autoPref) -ceq 'true' }
    Toggle $settings 'StartOnLogin'
    $null=Until 'registered login' { Test-Path -LiteralPath $startup }
    $link=$shell.CreateShortcut($startup)
    if ($link.Arguments -ne "--background --state ""$state""" -or $link.TargetPath -ne $exe) { throw 'Re-enabling did not register the current state and executable.' }
    Pass 'Reopening in the other language retained both choices; enabling login saved the custom state path.'
    Push $settings 'CloseAppPreferences'
    Stop-EdgeTestDesktop $app
    $app.Dispose()
    $app=Launch-Edge $link.TargetPath $link.Arguments
    $null=Until 'silent initialization' { $app.WaitForInputIdle(250) }
    Start-Sleep -Milliseconds 850
    if ($app.HasExited -or (Window 'EdgeMainWindow')) { throw 'The startup command did not stay in the tray.' }
    $originalPid=$app.Id
    $null=Wake-Resident
    $residents=@(Get-Process ProjectHub.Edge.Desktop | Where-Object Path -eq $exe)
    if ($residents.Count -ne 1 -or $residents[0].Id -ne $originalPid) { throw 'A second login instance was created.' }
    Pass 'The registered command launched the unpaired device silently. A foreground launch opened the same process.'
    if ($EvidenceDirectory) {
        $null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
        [ordered]@{version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion;time=[DateTimeOffset]::Now.ToString('o');
            desktopImageSha256=(Get-EdgeDesktopImageHash $exe);checks=$checks} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'preferences.json')
    }
} finally {
    if($app){ if(!$app.HasExited -and $app.Path -eq $exe){
            try { Stop-EdgeTestDesktop $app } catch { if(!$app.HasExited){$app.Kill();$app.WaitForExit()} }
        }; $app.Dispose() }
    [IO.File]::WriteAllBytes($startup,$original)
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('nodicarta-preferences-qa-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
