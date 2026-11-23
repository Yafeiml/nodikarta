Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EdgeQaInput {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll")] public static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out long point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@
function PointAt($element, [switch]$MouseClick, [scriptblock]$WhilePressed) {
    AssertContained $main $element $element.Current.AutomationId
    $r = $element.Current.BoundingRectangle
    $point = [EdgeQaCapture+Point]::new($r.X+$r.Width/2,$r.Y+$r.Height/2)
    $pointProcess = [uint32]0
    $null = [EdgeQaInput]::GetWindowThreadProcessId([EdgeQaCapture]::WindowFromPoint($point),[ref]$pointProcess)
    if ($pointProcess -ne $app.Id -or [EdgeQaInput]::GetForegroundWindow() -ne $hwnd) { throw "测试窗口未独占输入目标 $($element.Current.AutomationId)。" }
    if (![EdgeQaInput]::SetPhysicalCursorPos($point.X,$point.Y)) { throw '无法定位指针。' }
    Start-Sleep -Milliseconds 120
    if ($MouseClick -or $WhilePressed) {
        $null = Wait-Ui "指针可点击 $($element.Current.AutomationId)" {
            $packed = [long]0
            if (![EdgeQaInput]::GetPhysicalCursorPos([ref]$packed)) { throw '无法读取物理鼠标位置。' }
            $actual = [EdgeQaCapture+Point]::new(($packed -shl 32 -shr 32), ($packed -shr 32))
            if ([Math]::Abs($actual.X - $point.X) -gt 1 -or [Math]::Abs($actual.Y - $point.Y) -gt 1 -or
                !$r.Contains($actual.X, $actual.Y) -or [EdgeQaInput]::GetForegroundWindow() -ne $hwnd) {
                throw "点击 $($element.Current.AutomationId) 前输入焦点或指针已移开。目标 ($($point.X),$($point.Y))，当前 ($($actual.X),$($actual.Y))。"
            }
            [EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint($actual),2) -eq $hwnd
        }
        [EdgeQaInput]::mouse_event(2,0,0,0,0)
        try {
            Start-Sleep -Milliseconds 60
            if ($WhilePressed) { & $WhilePressed }
        } finally { [EdgeQaInput]::mouse_event(4,0,0,0,0) }
        Start-Sleep -Milliseconds 180
    }
}
