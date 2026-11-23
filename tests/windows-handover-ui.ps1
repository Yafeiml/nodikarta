#requires -Version 7.0
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Fixture)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$offer = [Console]::ReadLine() | ConvertFrom-Json
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
function Wait-Ui([string]$description, [scriptblock]$read, [int]$seconds = 22) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        try { $value = & $read }
        catch [Windows.Automation.ElementNotAvailableException] { $value = $null }
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $until)
    throw "Expected desktop state: $description"
}
function Start-Edge($name) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $profile = [IO.Directory]::CreateDirectory((Join-Path $Fixture "$name-home")).FullName
    $info.Environment['USERPROFILE'] = $info.Environment['HOME'] = $profile
    $info.Environment['CODEX_HOME'] = Join-Path $profile '.codex'
    $info.Environment['APPDATA'] = Join-Path $profile 'AppData/Roaming'
    $info.Environment['LOCALAPPDATA'] = Join-Path $profile 'AppData/Local'
    $info.Environment['XDG_CONFIG_HOME'] = Join-Path $profile '.config'
    $info.ArgumentList.Add('--state')
    $info.ArgumentList.Add((Join-Path $Fixture $name))
    $app = [Diagnostics.Process]::Start($info)
    $processes.Add($app)
    return $app
}
function Window($app, $id) {
    $match = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    foreach ($item in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$match)) {
        if ($item.Current.AutomationId -eq $id) { return $item }
        if ($nested = Control $item $id) { return $nested }
    }
}
function Control($win, $id) {
    $match = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $win.FindFirst([Windows.Automation.TreeScope]::Descendants,$match)
}
function Click($win, $id) {
    $control = Wait-Ui $id { $item = Control $win $id; if ($item.Current.IsEnabled) { $item } }
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Fill($win, $id, $value) {
    $field = Wait-Ui $id { Control $win $id }
    $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Online($win) { (Control $win 'CoreApiStatus').Current.Name -in '在线','Online' }
try {
    $old = Start-Edge 'desktop-old'
    $oldWindow = Wait-Ui 'original desktop opens' { Window $old 'EdgeMainWindow' }
    $null = Wait-Ui 'original device online' { Online $oldWindow }
    $new = Start-Edge 'desktop-new'
    $win = Wait-Ui 'recipient desktop opens' { Window $new 'EdgeMainWindow' }
    $null = Wait-Ui 'ordinary pair action' { (Control $win 'PairButton').Current.Name -in '配对设备','Pair device' }
    Fill $win 'CoreAddress' $offer.core
    Fill $win 'PairingCode' $offer.code
    $null = Wait-Ui 'confirm takeover action and old-identity warning' {
        (Control $win 'PairButton').Current.Name -in '确认接替','Take over' -and
        (Control $win 'PairingHint').Current.Name -match '旧身份随即停用|credentials will stop working'
    }
    Click $win 'PairButton'
    $null = Wait-Ui 'interrupted confirmation shown' { (Control $win 'Notice').Current.Name -match '自动继续|automatically resume' }
    Click $oldWindow 'RefreshButton'
    $null = Wait-Ui 'original still online during interrupted confirmation' { Online $oldWindow }
    $new.Kill()
    $new.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $Fixture 'desktop-confirm-enabled'),'resume')
    $new = Start-Edge 'desktop-new'
    $win = Wait-Ui 'restarted recipient opens' { Window $new 'EdgeMainWindow' }
    $null = Wait-Ui 'recipient recovers into online overview without reentering code' { Online $win } 85
    Click $oldWindow 'RefreshButton'
    $null = Wait-Ui 'old device shows authorization lost' { (Control $oldWindow 'CoreApiStatus').Current.Name -in '未授权','Unauthorized' }
    Click $oldWindow 'ManageCoreConnection'
    $dialog = Wait-Ui 'old device can open the re-pair dialog' { Window $old 'CoreConnectionWindow' }
    Fill $dialog 'NewCorePairingCode' $offer.code
    $null = Wait-Ui 'paired installation recognizes an update code' {
        (Control $dialog 'ConfirmCoreChange').Current.Name -in '确认接替','Take over'
    }
    Click $dialog 'CancelCoreChange'
} finally {
    foreach ($app in $processes) {
        if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
        $app.Dispose()
    }
}
