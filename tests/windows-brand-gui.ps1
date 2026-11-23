# Invoked by windows-access-fixture.cjs --brand for an isolated branded release.
if (!$ScreenshotDirectory) { throw 'Specify an evidence directory for the pixel comparisons.' }
Add-Type -Path (Join-Path $PSScriptRoot 'WindowsBrandShell.cs')
$logos = [Collections.Generic.List[object]]::new()
$null = Wait-Ui '设备概览' { ById $main 'GlobalAccessButton' }
Snapshot 'overview'
$priorDpi = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
try {
    function Save-BrandMark($window, $name, $automationId, $shape) {
        $mark = Wait-Ui $automationId {
            $m = ById $window $automationId
            if ($m -and !$m.Current.IsOffscreen -and $m.Current.BoundingRectangle.Width -gt 8) { $m }
        }
        $area = $mark.Current.BoundingRectangle
        $frame = [EdgeQaCapture+Rect]::new()
        if ([EdgeQaCapture]::DwmGetWindowAttribute($window.Current.NativeWindowHandle,9,[ref]$frame,16)) { throw 'Window frame unavailable.' }
        $source = [Drawing.Bitmap]::FromFile((Join-Path $ScreenshotDirectory "$name.png"))
        try {
            $crop = [Drawing.Rectangle]::new([Math]::Round($area.Left)-$frame.Left,[Math]::Round($area.Top)-$frame.Top,
                [Math]::Round($area.Width),[Math]::Round($area.Height))
            if ($crop.Left -lt 0 -or $crop.Top -lt 0 -or $crop.Right -gt $source.Width -or $crop.Bottom -gt $source.Height) {
                throw "$automationId crop $crop is outside screenshot $($source.Width)x$($source.Height)"
            }
            $markBitmap = $source.Clone($crop,$source.PixelFormat)
            try { $markBitmap.Save((Join-Path $ScreenshotDirectory "$name-mark.png"),[Drawing.Imaging.ImageFormat]::Png) }
            finally { $markBitmap.Dispose() }
        } finally { $source.Dispose() }
        $logos.Add(@{id=$automationId;file="$name-mark.png";shape=$shape;
            leftPx=$crop.X;topPx=$crop.Y;widthPx=$crop.Width;heightPx=$crop.Height})
    }
    Save-BrandMark $main 'overview' 'MainBrandMark' 'micro'
    Click $main 'GlobalAccessButton'
    $access = Wait-Ui '智能体接入' { AccessWindow }
    Snapshot 'access'
    Save-BrandMark $main 'access' 'AccessBrandMark' 'micro'
    Click $access 'CloseGlobalAccessButton'
    $null = Wait-Ui '返回概览' { ById $main 'GlobalAccessButton' }

    $trayWindow = [WindowsBrandShell]::TrayWindow($app.Id)
    $byProcess = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    function Get-BrandWindow([string]$id) {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byProcess)) {
            if ($w.Current.AutomationId -like $id) { return $w }
        }
    }
    [WindowsBrandShell]::OpenMenu($trayWindow)
    $popup = Wait-Ui '测试托盘菜单' { Get-BrandWindow 'AvaloniaTrayPopupRoot_*' }
    $item = Wait-Ui '关于' { HasText $popup '关于脉络 Edge' }
    $box = $item.Current.BoundingRectangle
    [WindowsBrandShell]::ChooseMenuItem($popup.Current.NativeWindowHandle,$app.Id,$box.Left+$box.Width/2,$box.Top+$box.Height/2)
    $about = Wait-Ui '关于窗口' {
        $window = Get-BrandWindow 'CoreConnectionWindow'
        if ($window) { $window } else { ById $main 'CoreConnectionWindow' }
    }
    Start-Sleep -Milliseconds 300
    Save-WindowsTestWindow $about $ScreenshotDirectory 'about'
    Save-BrandMark $about 'about' 'AboutBrandMark' 'standard'
    Click $about 'CancelCoreChange'
    if ($TitleMarksOnly) {
        $testedDpi = [EdgeQaCapture]::GetDpiForWindow($main.Current.NativeWindowHandle)
        @{scope='titlesOnly';logos=$logos;windowDpi=$testedDpi;osVersion=[Environment]::OSVersion.Version.ToString()} |
            ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ScreenshotDirectory 'capture.json')
        Pass "已从实际 Windows 标题栏截取设备概览和智能体接入的标志，DPI $testedDpi。"
        continue
    }
    $trayRect = [WindowsBrandShell+Rect]::new()
    function Get-NotificationElement {
        if (![WindowsBrandShell]::GetTrayRect($trayWindow,[ref]$trayRect)) { return }
        $point = [Windows.Point]::new(($trayRect.Left+$trayRect.Right)/2,($trayRect.Top+$trayRect.Bottom)/2)
        foreach ($class in 'Shell_TrayWnd','TopLevelWindowForOverflowXamlIsland','NotifyIconOverflowWindow') {
            $shell = [WindowsBrandShell]::ShellWindow($class)
            if (![WindowsBrandShell]::IsWindowVisible($shell)) { continue }
            $container = [Windows.Automation.AutomationElement]::FromHandle($shell)
            if ($class -ne 'Shell_TrayWnd' -and $container.Current.BoundingRectangle.Contains($point)) {
                # The public Shell API already identified the icon by this process's HWND and notify ID.
                return $container
            }
            foreach ($el in $container.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
                if (($el.Current.Name -match '脉络 Edge|Nodicarta Edge') -or ($el.Current.HelpText -match '脉络 Edge|Nodicarta Edge')) {
                    if ($el.Current.BoundingRectangle.Contains($point)) { return $el }
                }
            }
        }
    }
    $shellWindow = [WindowsBrandShell]::ShellWindow('Shell_TrayWnd')
    $shellElement = [Windows.Automation.AutomationElement]::FromHandle($shellWindow)
    $shellDpi = [EdgeQaCapture]::GetDpiForWindow($shellWindow)
    $openedOverflow = $false
    $physicalClick = $false
    function Test-BrandOverflowOpen {
        [WindowsBrandShell]::IsWindowVisible([WindowsBrandShell]::ShellWindow('NotifyIconOverflowWindow')) -or
        [WindowsBrandShell]::IsWindowVisible([WindowsBrandShell]::ShellWindow('TopLevelWindowForOverflowXamlIsland'))
    }
    try {
        $notification = Get-NotificationElement
        if (!$notification -and !(Test-BrandOverflowOpen)) {
            $chevron = ById $shellElement '1502'
            if (!$chevron) {
                $chevron = $shellElement.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
                    Where-Object { $_.Current.Name -match '显示隐藏|Show hidden icons' -and !$_.Current.IsOffscreen } | Select-Object -First 1
            }
            if ($chevron) {
                function Invoke-BrandChevron {
                    $pattern = $null
                    if ($chevron.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { $pattern.Invoke(); return }
                    $r = $chevron.Current.BoundingRectangle
                    [WindowsBrandShell]::ChooseMenuItem($chevron.Current.NativeWindowHandle,$chevron.Current.ProcessId,$r.Left+$r.Width/2,$r.Top+$r.Height/2)
                }
                Invoke-BrandChevron
                for ($i=0;$i -lt 20 -and !(Test-BrandOverflowOpen);$i++) { Start-Sleep -Milliseconds 50 }
                $openedOverflow = Test-BrandOverflowOpen
                if (!$openedOverflow -and $env:NODICARTA_QA_TRAY_CLICK -eq '1') {
                    $chevronHandle = [WindowsBrandShell]::NotificationChevron()
                    $box = [EdgeQaCapture+Rect]::new()
                    if ($chevronHandle.ToInt64() -and [EdgeQaCapture]::GetWindowRect($chevronHandle,[ref]$box)) {
                        $physicalClick = [WindowsBrandShell]::ClickStableChevron($chevronHandle,($box.Left+$box.Right)/2,($box.Top+$box.Bottom)/2)
                    }
                    Write-Host "Physical notification chevron: $([WindowsBrandShell]::ClickResult)."
                    for ($i=0;$physicalClick -and $i -lt 20 -and !(Test-BrandOverflowOpen);$i++) { Start-Sleep -Milliseconds 50 }
                    $openedOverflow = Test-BrandOverflowOpen
                }
                if ($openedOverflow) { Start-Sleep -Milliseconds 150 }
                $notification = Get-NotificationElement
            }
        }
        $trayCaptured = $false
        if ($notification) {
            # Limit screen sampling to the public Shell rectangle for this app's individual icon.
            $side = [int][Math]::Round(16*$shellDpi/96)
            $rect = [WindowsBrandShell+Rect]::new()
            if (![WindowsBrandShell]::GetTrayRect($trayWindow,[ref]$rect)) { throw 'Test notification icon disappeared.' }
            $x = [int][Math]::Round(($rect.Right+$rect.Left-$side)/2)
            $y = [int][Math]::Round(($rect.Bottom+$rect.Top-$side)/2)
            $tile = [Drawing.Bitmap]::new($side,$side)
            $graphics = [Drawing.Graphics]::FromImage($tile)
            try {
                $graphics.CopyFromScreen($x,$y,0,0,$tile.Size)
                $tile.Save((Join-Path $ScreenshotDirectory 'tray-mark.png'),[Drawing.Imaging.ImageFormat]::Png)
                $trayCaptured = $true
            } finally { $graphics.Dispose(); $tile.Dispose() }
        }
    } finally {
        if ($physicalClick) { [WindowsBrandShell]::RestoreChevron($openedOverflow -and (Test-BrandOverflowOpen)) }
        elseif ($openedOverflow -and (Test-BrandOverflowOpen)) { Invoke-BrandChevron }
    }

    $trayCaptureMethod = if ($trayCaptured) { 'Visible notification area' } else { 'Unavailable' }
    Write-Host "Isolated notification icon: shell DPI $shellDpi, overflow opened $openedOverflow, matched shell tile $trayCaptured."
    $testedDpi = [EdgeQaCapture]::GetDpiForWindow($main.Current.NativeWindowHandle)
    $shortcut = Join-Path $root 'Nodicarta Edge.lnk'
    $wsh = New-Object -ComObject WScript.Shell
    $link = $wsh.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.IconLocation = "$exe,0"
    $link.Save()
    if ($wsh.CreateShortcut($shortcut).IconLocation -ne "$exe,0") { throw 'Shortcut did not retain its icon source.' }
    foreach ($entry in @{key='exe';path=$exe},@{key='link';path=$shortcut}) {
        foreach ($size in 'small','large') {
            $hIcon = [WindowsBrandShell]::FileIcon($entry.path,$size -eq 'small')
            try {
                $bitmap = [Drawing.Icon]::FromHandle($hIcon).ToBitmap()
                try { $bitmap.Save((Join-Path $ScreenshotDirectory "$($entry.key)-$size.png"),[Drawing.Imaging.ImageFormat]::Png) }
                finally { $bitmap.Dispose() }
            } finally { $null = [WindowsBrandShell]::DestroyIcon($hIcon) }
        }
    }
    @{logos=$logos;windowDpi=$testedDpi;shellDpi=$shellDpi;trayCaptured=$trayCaptured;trayCaptureMethod=$trayCaptureMethod;
      shellThemeLight=(Get-ItemPropertyValue 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Themes/Personalize' 'SystemUsesLightTheme');
      osVersion=[Environment]::OSVersion.Version.ToString()} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ScreenshotDirectory 'capture.json')
    Pass "概览、智能体接入和托盘关于窗口均已获取实际标志；临时快捷方式和 EXE 均由 Windows Shell 取图。桌面 DPI $testedDpi，托盘 DPI $shellDpi，实拍托盘 $trayCaptured。"
} finally { $null = [EdgeQaCapture]::SetThreadDpiAwarenessContext($priorDpi) }
