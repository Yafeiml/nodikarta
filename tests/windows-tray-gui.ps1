#requires -Version 7.0
# Pipe an unused *test* offer as JSON {"core":"http://127.0.0.1:...","code":"..."}.
# The executable uses a fresh per-run state directory. Never use a real user's offer.
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory,ValueFromPipeline)][string]$OfferJson,
    [string]$EvidenceDirectory,
    [scriptblock]$SetTestGrant
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$offer = $OfferJson | ConvertFrom-Json
if (!$offer.code -or !([uri]$offer.core).IsLoopback) { throw 'An isolated loopback Core and a test offer are required.' }
$exe = (Resolve-Path -LiteralPath $Executable).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('projecthub-tray-test-' + [guid]::NewGuid().ToString('N'))
$folder = [IO.Directory]::CreateDirectory((Join-Path $testRoot '中文项目 目录')).FullName
$state = Join-Path $testRoot 'state'
$checks = [Collections.Generic.List[string]]::new()
function Pass($s) {$checks.Add($s); Write-Host "PASS $s"}
function Until($purpose, [scriptblock]$condition) {
    $end = [datetime]::UtcNow.AddSeconds(18)
    do { $value = & $condition; if ($value) { return $value }; Start-Sleep -Milliseconds 150 } while ([datetime]::UtcNow -lt $end)
    throw "Timed out: $purpose"
}
function Open-Edge([switch]$Silent) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    foreach ($arg in @('--state',$state)) { $info.ArgumentList.Add($arg) }
    if ($Silent) { $info.ArgumentList.Add('--background') }
    [Diagnostics.Process]::Start($info)
}
function Element($id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function TextOf($el) {
    if (!$el) { return '' }
    $el.Current.Name
}
function Close-ToTray($wnd) {
    $pattern = $null
    if ($wnd.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern,[ref]$pattern)) { $pattern.Close(); return }
    if (![EdgeQaPicker]::PostMessage([intptr]$wnd.Current.NativeWindowHandle,16,0,0)) { throw 'Cannot close the test window.' }
}
function SetField($id,$value) {
    $el = Until $id { Element $id }
    $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Click($id) {
    $el = Until $id { $target = Element $id; if ($target -and $target.Current.IsEnabled) {$target} }
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function FindWindow {
    $filter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
    $wins = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$filter)
    foreach ($win in $wins) { if ($win.Current.AutomationId -eq 'EdgeMainWindow' -and !$win.Current.IsOffscreen) { return $win } }
}
function Wake {
    $again = Open-Edge
    if (!$again.WaitForExit(8000) -or $again.ExitCode -ne 0) { throw 'Second launch did not hand over to the first process.' }
    Until 'visible primary window' { FindWindow }
}
function NameElement($parent, $name) {
    $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))
}
function Pick-Folder {
    Click 'BrowseButton'
    $dialog = Until 'native Windows folder picker' {
        $owned = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty,'#32770'))
        if ($owned) { return $owned }
        $filter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
        foreach ($item in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$filter)) {
            if ($item.Current.ClassName -eq '#32770' -and !$item.Current.IsOffscreen) { return $item }
        }
    }
    function PickerControl($id, $class) {
        $filter = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty,$class))
        $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
    }
    # Win11 exposes these native child windows without Value/Invoke UIA patterns on this shell.
    if (!('EdgeQaPicker' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EdgeQaPicker {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wp, string lp);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
}
'@
    }
    $edit = Until 'folder entry' { PickerControl '1152' 'Edit' }
    $ok = Until 'confirm folder' { PickerControl '1' 'Button' }
    if (![EdgeQaPicker]::SendMessageW([intptr]$edit.Current.NativeWindowHandle,12,0,$folder) -or
        ![EdgeQaPicker]::PostMessage([intptr]$ok.Current.NativeWindowHandle,245,0,0)) {
        throw 'Native folder picker could not select the test directory.'
    }
}
. (Join-Path $PSScriptRoot 'Save-WindowsTestWindow.ps1')
function Capture($name) { Save-WindowsTestWindow $window $EvidenceDirectory $name }
try {
    $process = Open-Edge -Silent
    $null = Until 'background worker heartbeat' { Test-Path (Join-Path $state 'connection.json') }
    if (FindWindow) { throw 'Silent launch displayed a window.' }
    Pass '登录参数启动后台连接，无可见主窗口'
    $window = Wake
    $null = Until 'pairing form' { Element 'CoreAddress' }
    SetField 'CoreAddress' $offer.core
    SetField 'DeviceName' 'Windows GUI 验收'
    Capture 'pair'
    SetField 'PairingCode' $offer.code
    Click 'PairButton'
    $null = Until 'read-only grant' { (TextOf (Element 'AccessMode')) -match '只读' }
    Pass '从 Avalonia 窗口用一次性配对码接入 Core，读取实际只读授权'
    Pick-Folder
    $null = Until 'bound directory' { NameElement $window $folder }
    Capture 'projects'
    Pass '通过 Windows 文件夹选择器绑定中文带空格的现有目录'
    Close-ToTray $window
    $null = Until 'hide without exiting' { !(FindWindow) -and !$process.HasExited }
    $window = Wake
    $null = Until 'same grant on wake' { (TextOf (Element 'AccessMode')) -match '只读' }
    Pass '关闭主窗口仍驻留，手动重复打开唤醒相同进程'
    $process.Kill()
    $process.WaitForExit()
    $process = Open-Edge
    $window = Until 'fresh process restores window' { FindWindow }
    $null = Until 'stored grant and folder' { (TextOf (Element 'AccessMode')) -match '只读' -and (NameElement $window $folder) }
    Pass '结束验收进程后重新打开，恢复既有配对、只读项目和目录绑定'
    if ($SetTestGrant) {
        # The public device ID is the only value passed to the isolated administrator fixture.
        $device = & python -c 'import pathlib, sqlite3, sys; db=sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri()+"?mode=ro",uri=True); print(db.execute("SELECT DISTINCT device_id FROM workspaces").fetchone()[0])' (Join-Path $state 'edge.sqlite3')
        if ($LASTEXITCODE -or $device -notmatch '^[a-fA-F0-9-]{36}$') { throw 'No current QA device ID.' }
        & $SetTestGrant 'grant' $device
        Click 'RefreshButton'
        $null = Until 'updated permission' { (TextOf (Element 'AccessMode')) -match '读写' }
        & $SetTestGrant 'revoke' $device
        Click 'RefreshButton'
        $null = Until 'revoked project disappears' { !(Element 'AccessMode') -and !(Element 'ProjectList') -and !(Element 'BrowseButton') }
        Pass 'Core 改为读写和撤权后，刷新窗口即时生效，无需重新配对'
    }
    if ($EvidenceDirectory) {
        [ordered]@{ checkedAtUtc=[datetime]::UtcNow.ToString('o'); executableHash=(Get-FileHash -LiteralPath $exe).Hash; checks=$checks } | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'checks.json')
    }
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $full = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($full) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and [IO.Path]::GetFileName($full) -match '^projecthub-tray-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
