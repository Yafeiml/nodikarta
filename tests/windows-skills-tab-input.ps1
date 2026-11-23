# Send navigation keys only to this disposable window.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SkillsTabInput {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint message, IntPtr wp, IntPtr lp);
}
'@
$hwnd = [intptr]$main.Current.NativeWindowHandle
function Send-SkillsKey([int]$key) {
    if ($main.Current.ProcessId -ne $app.Id -or [Windows.Automation.AutomationElement]::FocusedElement.Current.ProcessId -ne $app.Id) {
        throw '焦点不在本次测试客户端。'
    }
    if (![SkillsTabInput]::PostMessage($hwnd,0x100,[intptr]$key,[intptr]1) -or
        ![SkillsTabInput]::PostMessage($hwnd,0x101,[intptr]$key,[intptr]0xC0000001L)) { throw "按键 $key 发送失败。" }
}
Click $access 'ConfigFormatClients'
Check-DevicePage $access 'Clients'
$button = ById $access 'ConfigFormatSkills'
(ById $access 'ConfigFormatStdio').SetFocus()
Send-SkillsKey 9
$null = Wait-Ui 'Tab 到通用 Skills' { $button.Current.HasKeyboardFocus }
Send-SkillsKey 13
Check-DevicePage $access 'Skills'
Check-SkillsCount $access 5
$frame = [EdgeQaCapture+Rect]::new()
if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw '无法读取实图范围。' }
$scale = [EdgeQaCapture]::GetDpiForWindow($hwnd)/96
Snapshot 'skills-selected'
$png = [Drawing.Bitmap]::new((Join-Path $ScreenshotDirectory 'skills-selected.png'))
try {
    $bounds = $button.Current.BoundingRectangle
    $color = $png.GetPixel([int]($bounds.Left-$frame.Left+5*$scale),[int]($bounds.Top-$frame.Top+$bounds.Height/2))
    $rgb = '{0:X2}{1:X2}{2:X2}' -f $color.R,$color.G,$color.B
    if ($rgb -notin 'DBE9F8','D2E4F5') { throw "第三页签无选中底色，实测 $rgb。" }
} finally { $png.Dispose() }
Send-SkillsKey 27
$null = Wait-Ui 'Esc 返回设备概览并恢复入口焦点' {
    [bool](ById $main 'LanguageButton') -and (ById $main 'GlobalAccessButton').Current.HasKeyboardFocus
}
Send-SkillsKey 13
$access = Wait-Ui '重新打开智能体接入' { AccessWindow }
Check-DevicePage $access 'Clients'
Pass "Tab 聚焦第三个页签，Enter 打开 Skills，实图选中底色 #$rgb。Esc 返回概览并恢复焦点，Enter 可再次进入客户端列表。"
