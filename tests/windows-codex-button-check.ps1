# Called on the disposable UIA window in windows-codex-copies.ps1.
. (Join-Path $PSScriptRoot 'Save-WindowsTestWindow.ps1')

function Test-CodexButtonAppearance($Window, [ValidateSet('available','connected')]$Phase, [string]$EvidenceDirectory) {
    $connected = $Phase -eq 'connected'
    $caption = if ($connected) { '断开接入' } else { '接入' }
    $name = if ($connected) { '断开 Codex 接入' } else { '接入 Codex' }
    $hwnd = [intptr]$Window.Current.NativeWindowHandle
    $size = $Window.Current.BoundingRectangle.Size
    Add-Type -AssemblyName System.Windows.Forms
    Save-WindowsTestWindow $Window $EvidenceDirectory "$Phase-default"
    $scale = [EdgeQaCapture]::GetDpiForWindow($hwnd) / 96
    $font = [Drawing.Font]::new('Microsoft YaHei UI', [single](13*$scale), [Drawing.GraphicsUnit]::Pixel)
    try {
        foreach ($layout in 'default','narrow') {
            if ($layout -eq 'narrow') {
                if (![EdgeQaCapture]::Resize($hwnd, [int](506*$scale), [int]$size.Height)) { throw 'Cannot resize the test window.' }
                Start-Sleep -Milliseconds 350
                if ($Window.Current.BoundingRectangle.Width -gt 522*$scale) { throw 'Narrow window size was not applied.' }
                Save-WindowsTestWindow $Window $EvidenceDirectory "$Phase-narrow"
            }
            $action = ById $Window 'HarnessInstall_codex'
            $rect = $action.Current.BoundingRectangle
            $other = (ById $Window 'HarnessInstall_claude').Current.BoundingRectangle
            $copy = (ById $Window 'HarnessCopy_codex').Current.BoundingRectangle
            $textWidth = [Windows.Forms.TextRenderer]::MeasureText($caption,$font,[Drawing.Size]::Empty,[Windows.Forms.TextFormatFlags]::NoPadding).Width
            if ($action.Current.Name -ne $name -or !$action.Current.IsEnabled -or
                $action.Current.IsOffscreen -or $rect.Width -lt $textWidth+12*$scale -or
                [Math]::Abs($rect.Left-$other.Left) -gt 1 -or [Math]::Abs($rect.Width-$other.Width) -gt 1 -or
                [Math]::Abs($copy.Left-$rect.Right-8*$scale) -gt 1 -or [Math]::Abs($copy.Top-$rect.Top) -gt 1) {
                throw "$Phase/$layout 接入按钮状态、文字空间或按钮对齐不符。"
            }
            if ($connected) {
                $frame = [EdgeQaCapture+Rect]::new()
                $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
                $image = [Drawing.Bitmap]::new((Join-Path $EvidenceDirectory "$Phase-$layout.png"))
                try {
                    $color = $image.GetPixel([int]($rect.Left-$frame.Left+5*$scale), [int]($rect.Top-$frame.Top+$rect.Height/2))
                    if ($color.ToArgb() -eq [Drawing.Color]::FromArgb(224,243,233).ToArgb()) {
                        throw "$layout 断开接入操作仍然使用旧的绿色成功底色。"
                    }
                    if ((ById $Window 'HarnessStatus_codex').Current.Name -notlike '已接入*' -or (ById $Window 'HarnessDisconnect_codex')) {
                        throw "$layout 连接状态或按钮数量不符。"
                    }
                    foreach ($id in 'claude','cursor','opencode','deepseek','pi') {
                        $inactive = (ById $Window "HarnessInstall_$id").Current
                        if ($inactive.IsEnabled -or $inactive.Name -notlike '接入 *' -or $inactive.IsOffscreen) { continue }
                        $r = $inactive.BoundingRectangle
                        $fill = $image.GetPixel([int]($r.Left-$frame.Left+5*$scale), [int]($r.Top-$frame.Top+$r.Height/2))
                        if ($fill.ToArgb() -ne [Drawing.Color]::FromArgb(237,241,245).ToArgb()) { throw '未接入的禁用按钮应保留灰色。' }
                        break
                    }
                } finally { $image.Dispose() }
            }
        }
    } finally {
        $font.Dispose()
        $null = [EdgeQaCapture]::Resize($hwnd, [int]$size.Width, [int]$size.Height)
    }
    if (!('CodexButtonKeyboard' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CodexButtonKeyboard {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
    }
    (ById $Window 'ConfigFormatSkills').SetFocus()
    if ([CodexButtonKeyboard]::GetForegroundWindow() -ne $hwnd) { throw '键盘输入目标不是本次测试窗口。' }
    [Windows.Forms.SendKeys]::SendWait('{TAB}')
    $null = Wait-For "$Phase 主操作焦点" { [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'HarnessInstall_codex' }
    [Windows.Forms.SendKeys]::SendWait('{TAB}')
    $null = Wait-For "$Phase 复制焦点" { [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'HarnessCopy_codex' }
}
