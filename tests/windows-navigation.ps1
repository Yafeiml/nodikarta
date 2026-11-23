# Loaded by windows-access-gui.ps1 with a disposable paired state.
if (!$ScreenshotDirectory) { throw 'Navigation checks require an evidence directory.' }
. (Join-Path $PSScriptRoot 'WindowsLanguageMenu.ps1')
. (Join-Path $PSScriptRoot 'WindowsPointerInput.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'WindowNavigationProbe.cs')
$folder = [IO.Directory]::CreateDirectory((Join-Path $root 'navigation workspace')).FullName
$null = & $cli --state $state bind --project $ProjectId --path $folder
if ($LASTEXITCODE) { throw 'Could not bind the fixture directory.' }
Click $main 'RefreshButton'
$null = Wait-Ui '临时目录可见' { HasText $main $folder }
$scratch = [IO.Directory]::CreateDirectory((Join-Path $root 'navigation captures')).FullName
Save-WindowsTestWindow $main $scratch 'initial'
$hwnd = [intptr]$main.Current.NativeWindowHandle
$testedDpi = [EdgeQaCapture]::GetDpiForWindow($hwnd)
$scale = $testedDpi/96
$previousDpi = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
$probe = [WindowNavigationProbe]::new($hwnd,$app.Id)
$records = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
$capabilities = [Collections.Generic.List[object]]::new()

function InkBounds($pixels, [Drawing.Rectangle]$area) {
    $left=$area.Right; $top=$area.Bottom; $right=-1; $bottom=-1
    for ($y=$area.Top;$y -lt $area.Bottom;$y++) {
        for ($x=$area.Left;$x -lt $area.Right;$x++) {
            $c=$pixels.GetPixel($x,$y)
            if ($c.R -gt 155 -or $c.G -gt 165 -or $c.B -gt 180) { continue }
            $left=[Math]::Min($left,$x); $top=[Math]::Min($top,$y)
            $right=[Math]::Max($right,$x); $bottom=[Math]::Max($bottom,$y)
        }
    }
    if ($right -lt 0) { throw "No painted title pixels in $area" }
    return "$left,$top,$($right-$left+1),$($bottom-$top+1)"
}
function Record-Page($case,$page,$name) {
    $mainNow = MainWindow
    if (!$mainNow -or [intptr]$mainNow.Current.NativeWindowHandle -ne $hwnd) { throw 'Page navigation replaced the test HWND.' }
    $prefix = if ($page -eq 'overview') { 'Main' } else { 'Access' }
    $logo = Wait-Ui "$page 图标" { ById $mainNow ($prefix+'BrandMark') }
    $brand = ById $mainNow ($prefix+'BrandName')
    if (!$brand) {
        $brand = $mainNow.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'脉络 Edge')) |
            Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text } | Select-Object -First 1
    }
    if (!$brand) { throw 'No actual title brand text peer.' }
    $bar = ById $mainNow ($prefix+'TitleBar')
    Save-WindowsTestWindow $mainNow $scratch $name
    $png = Join-Path $scratch "$name.png"
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw 'DWM bounds unavailable.' }
    function RelativeBox($el) {
        $r = $el.Current.BoundingRectangle
        [Drawing.Rectangle]::FromLTRB([int][Math]::Round($r.Left)-$frame.Left,[int][Math]::Round($r.Top)-$frame.Top,
            [int][Math]::Round($r.Right)-$frame.Left,[int][Math]::Round($r.Bottom)-$frame.Top)
    }
    $logoBox = RelativeBox $logo
    $brandBox = RelativeBox $brand
    $bmp = [Drawing.Bitmap]::FromFile($png)
    try {
        $x = [int]($bmp.Width * 0.51)
        # The maximized HWND extends beyond the monitor. Sample well inside the
        # visible header, above its lower rule even after DWM clips the top frame.
        $scanStart = [int]($scale*22)
        $background=$bmp.GetPixel($x,$scanStart)
        $barEnd = $null
        for ($y=$scanStart+1;$y -lt $scale*52;$y++) {
            if ($bmp.GetPixel($x,$y).ToArgb() -ne $background.ToArgb()) { $barEnd=$y;break }
        }
        if ($null -eq $barEnd) { throw 'No painted header boundary found.' }
        $rule = $bmp.GetPixel($x,$barEnd).ToArgb()
        $bodyTop = $barEnd + 1
        while ($bodyTop -lt $bmp.Height -and $bmp.GetPixel($x,$bodyTop).ToArgb() -eq $rule) { $bodyTop++ }
        if ($bodyTop-$barEnd -gt [Math]::Ceiling(2*$scale)) { throw 'Expected a thin, continuous header rule.' }
        $graphic=InkBounds $bmp $logoBox
        $letters=InkBounds $bmp $brandBox
        $crop = $bmp.Clone([Drawing.Rectangle]::new(0,0,$brandBox.Right+3,$bodyTop),$bmp.PixelFormat)
        $stream = [IO.MemoryStream]::new()
        try {
            $crop.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray()))
            if (($case -eq 'default' -or $case -eq 'maximized') -and $name -eq "$case-$page") {
                $crop.Save((Join-Path $ScreenshotDirectory "$name-title.png"),[Drawing.Imaging.ImageFormat]::Png)
                if ($case -eq 'default') { [IO.File]::Copy($png,(Join-Path $ScreenshotDirectory "$name.png"),$true) }
            }
        } finally { $stream.Dispose(); $crop.Dispose() }
    } finally { $bmp.Dispose() }
    $row = [ordered]@{case=$case;name=$name;page=$page;hwnd=$hwnd.ToInt64();bounds=$probe.Bounds();
        maximized=[WindowNavigationProbe]::IsZoomed($hwnd);minimized=[WindowNavigationProbe]::IsIconic($hwnd);
        dwm="$($frame.Left),$($frame.Top),$($frame.Right),$($frame.Bottom)";
        iconPixels=$graphic;brandTextPixels=$letters;titleHeightPx=if ($bar) { (RelativeBox $bar).Height } else { $null };
        dividerTopPx=$barEnd;visibleTitleHeightPx=$bodyTop;brandPixelsSha256=$hash;locationEvents=@($probe.Drain())}
    $records.Add($row)
    return $row
}
function Check-Navigation($reference,$row) {
    foreach ($field in 'hwnd','bounds','dwm','maximized','minimized','iconPixels','brandTextPixels','dividerTopPx','brandPixelsSha256') {
        if ($reference[$field] -ne $row[$field]) { $failures.Add("$($row.name) $field") }
    }
    foreach ($rect in $row.locationEvents) {
        if ($rect -ne $reference.bounds) { $failures.Add("$($row.name) transient-bounds"); break }
    }
}
function Open-Page($button,[bool]$keyboard) {
    if ($keyboard) {
        $entry = ById $main $button
        $entry.SetFocus(); $null=Wait-Ui '入口焦点' { $entry.Current.HasKeyboardFocus }
        Send-EdgeLanguageKey $hwnd 0x0d
    } else { Click $main $button }
    return Wait-Ui '接入页面可见' { AccessWindow }
}
function Back($access,$back,$entry,[bool]$keyboard) {
    if ($keyboard) { Send-EdgeLanguageKey $hwnd 0x1b } else { Click $access $back }
    $null=Wait-Ui '返回并恢复对应入口焦点' {
        $next = ById $main $entry
        $next -and $next.Current.HasKeyboardFocus
    }
}
function Test-DeviceWindowLimits($access,$case) {
    foreach ($page in 'Clients','Stdio','Skills') {
        Click $access ("ConfigFormat"+$page)
        $before = $probe.Bounds()
        $zoomed = [WindowNavigationProbe]::IsZoomed($hwnd)
        $buttons = @($access.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)) |
            Where-Object { $_.Current.Name -match '最小化|最大化|还原|Minimi[sz]e|Maximi[sz]e|Restore' -and !$_.Current.IsOffscreen })
        if ($buttons.Count -or $probe.CanMinimize -or $probe.CanMaximize) {
            $failures.Add("$case/$page exposes min/max")
        }
        $caption=(ById $access 'AccessBrandMark').Current.BoundingRectangle
        $null=$probe.Drain()
        $actions = @('SC_MINIMIZE','SC_MAXIMIZE','client-double-click','caption-double-click')
        if ($zoomed) { $actions += 'SC_RESTORE' }
        foreach ($action in $actions) {
            switch ($action) {
                'SC_MINIMIZE' { $probe.SystemCommand(0xf020) }
                'SC_MAXIMIZE' { $probe.SystemCommand(0xf030) }
                'SC_RESTORE'  { $probe.SystemCommand(0xf120) }
                'client-double-click' { $probe.DoubleClickTitle([int]($caption.Left+$caption.Width/2),[int]($caption.Top+$caption.Height/2)) }
                'caption-double-click' { $probe.CaptionDoubleClick([int]($caption.Left+$caption.Width/2),[int]($caption.Top+$caption.Height/2)) }
            }
            Start-Sleep -Milliseconds 125
            if ($probe.Bounds() -ne $before -or [WindowNavigationProbe]::IsIconic($hwnd) -or
                [WindowNavigationProbe]::IsZoomed($hwnd) -ne $zoomed) {
                throw "$case/$page did not block $action"
            }
        }
        $capabilities.Add(@{case=$case;tab=$page;visibleMinMax=$buttons.Count;canMinimize=$probe.CanMinimize;
            canMaximize=$probe.CanMaximize;doubleClickStable=$true;nativeCommandsBlocked=$true})
    }
}
function Page-Cycle($case) {
    $null = $probe.Drain()
    $first = Record-Page $case 'overview' "$case-overview"
    foreach ($page in 'clients','folders') {
        $entry = if ($page -eq 'clients') { 'GlobalAccessButton' } else { 'AccessSettingsButton' }
        $back = if ($page -eq 'clients') { 'CloseGlobalAccessButton' } else { 'CloseAccessButton' }
        $access = Open-Page $entry ($page -eq 'folders')
        $current = Record-Page $case $page "$case-$page"
        Check-Navigation $first $current
        Back $access $back $entry ($page -eq 'folders')
        $returned = Record-Page $case 'overview' "$case-return-$page"
        Check-Navigation $first $returned
    }
}
function Drag-Point([int]$x,[int]$y,[int]$dx,[int]$dy) {
    $point = [EdgeQaCapture+Point]::new($x,$y)
    if ([EdgeQaInput]::GetForegroundWindow() -ne $hwnd -or
        [EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint($point),2) -ne $hwnd) {
        throw "鼠标起点 ($x,$y) 不属于本次隔离窗口。"
    }
    if (![EdgeQaInput]::SetPhysicalCursorPos($x,$y)) { throw '无法定位指针。' }
    Start-Sleep -Milliseconds 100
    [EdgeQaInput]::mouse_event(2,0,0,0,0)
    try {
        foreach ($step in 1..4) {
            Start-Sleep -Milliseconds 65
            if (![EdgeQaInput]::SetPhysicalCursorPos(($x+$dx*$step/4),($y+$dy*$step/4))) { throw '无法拖动指针。' }
        }
    } finally { [EdgeQaInput]::mouse_event(4,0,0,0,0) }
    Start-Sleep -Milliseconds 160
}
function Test-FixedPage($view,$page,$prefix) {
    $before = $probe.Bounds()
    if ($probe.CanResize -or $probe.CanMaximize) { throw "$page exposes resizing or maximizing in Windows" }
    if (!$probe.RefusesInteractiveResize()) { throw "$page entered a native sizing loop" }
    $null=[EdgeQaInput]::SetForegroundWindow($hwnd)
    $caption=(ById $view ($prefix+'BrandName')).Current.BoundingRectangle
    $probe.SystemCommand(0xf030)
    $probe.CaptionDoubleClick([int]$caption.Left,[int]$caption.Top)
    Start-Sleep -Milliseconds 160
    if ($probe.Bounds() -ne $before -or [WindowNavigationProbe]::IsZoomed($hwnd)) { throw "$page allows maximization" }
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw 'DWM bounds unavailable.' }
    $right = $frame.Right-2
    $middleY = ($frame.Bottom+$frame.Top)/2
    Drag-Point $right $middleY -36 0
    Drag-Point ($frame.Right-5) ($frame.Bottom-5) -36 -24
    if ($probe.Bounds() -ne $before) { throw "$page resized after dragging the edge or corner" }
    $capabilities.Add(@{page=$page;bounds=$before;resizeFrame=$probe.CanResize;maximizeBox=$probe.CanMaximize;systemSizeBlocked=$true;edgeAndCornerDragStable=$true})
}
function Test-CoreButton {
    $button=(ById $main 'ManageCoreConnection').Current.BoundingRectangle
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Core')
    $label=$main.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition).Current.BoundingRectangle
    $badge=(ById $main 'CoreApiStatus').Current.BoundingRectangle
    if ($label.IsEmpty -or $button.Right -ge $label.Left -or $label.Right -ge $badge.Left -or
        [Math]::Abs(($button.Top+$button.Height/2)-($label.Top+$label.Height/2)) -gt 2*$scale) {
        throw "Expected switch button before Core and connection status on the same line"
    }
    Click $main 'ManageCoreConnection'
    $byProcess=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    $dialog=Wait-Ui '更换 Core 对话框' {
        foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byProcess)) {
            if ($window.Current.AutomationId -eq 'CoreConnectionWindow') { return $window }
            if ($nested=ById $window 'CoreConnectionWindow') { return $nested }
        }
    }
    Click $dialog 'CancelCoreChange'
    $null=Wait-Ui '返回原有设备概览' { (ById $main 'GlobalAccessButton').Current.IsEnabled }
    Pass ('更换按钮、Core、连接状态从左到右，按钮与 Core 文字间距 {0} px；点击打开更换 Core 窗口并成功取消。' -f ($label.Left-$button.Right))
}
try {
    Page-Cycle 'default'
    if (!$NavigationBaseline) {
        Test-CoreButton
        Test-FixedPage $main 'overview' 'Main'
        $initial = [int[]]($probe.Bounds().Split(','))
        $caption = (ById $main 'MainBrandName').Current.BoundingRectangle
        Drag-Point ($caption.Left+$caption.Width/2) ($caption.Top+$caption.Height/2) 36 24
        $moved = [int[]]($probe.Bounds().Split(','))
        if ($moved[0]-$initial[0] -ne 36 -or $moved[1]-$initial[1] -ne 24 -or
            $moved[2]-$initial[2] -ne 36 -or $moved[3]-$initial[3] -ne 24) { throw '标题栏拖动未能保持窗口大小并移动到预期位置' }
        $access = Open-Page 'GlobalAccessButton' $false
        Test-FixedPage $access 'clients' 'Access'
        Test-DeviceWindowLimits $access 'fixed'
        Back $access 'CloseGlobalAccessButton' 'GlobalAccessButton' $true
        $access = Open-Page 'AccessSettingsButton' $false
        Test-FixedPage $access 'folders' 'Access'
        Back $access 'CloseAccessButton' 'AccessSettingsButton' $true
        Page-Cycle 'moved'
        if ($failures.Count) { throw "Window chrome changed: $($failures -join ', ')" }
        Pass '三页实测边缘和角落拖动、最大化命令及标题栏双击均保持固定尺寸；标题栏拖动可移动位置。'
        return
    }
    $null = [EdgeQaCapture]::Resize($hwnd,1,1)
    Start-Sleep -Milliseconds 220
    Page-Cycle 'minimum'
    # Resize and move the normal window while on a child page, then return.
    $access = Open-Page 'GlobalAccessButton' $false
    $null = [EdgeLanguageInput]::SetWindowPos($hwnd,0,172,183,[int](733*$scale),[int](520*$scale),0x14)
    Start-Sleep -Milliseconds 220
    $manual = Record-Page 'custom' 'clients' 'manual-resize-on-clients'
    Back $access 'CloseGlobalAccessButton' 'GlobalAccessButton' $true
    $returned = Record-Page 'custom' 'overview' 'custom-return'
    Check-Navigation $manual $returned
    Page-Cycle 'custom'
    $entry, $back, $page = 'GlobalAccessButton','CloseGlobalAccessButton','clients'
    $access = Open-Page $entry $false
    $max = Wait-Ui '用户可用的最大化按钮' {
        $el = HasText $access '最大化'
        if ($el -and $el.Current.IsEnabled) { $el }
    }
    $max.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-Ui '用户操作后原生窗口最大化' { [WindowNavigationProbe]::IsZoomed($hwnd) }
    $null=$probe.Drain()
    $maximized = Record-Page 'maximized' $page "maximized-on-$page"
    Back $access $back $entry $true
    $returned = Record-Page 'maximized' 'overview' 'maximized-return'
    Check-Navigation $maximized $returned
    Page-Cycle 'maximized'
    if ([WindowNavigationProbe]::IsZoomed($hwnd)) {
        $access = Open-Page 'AccessSettingsButton' $true
        $caption = (ById $access 'AccessBrandMark').Current.BoundingRectangle
        $probe.CaptionDoubleClick([int]($caption.Left+$caption.Width/2),[int]($caption.Top+$caption.Height/2))
        $null = Wait-Ui '同一双击确实将目录页还原，以验证智能体页上的双击测试有效' { ![WindowNavigationProbe]::IsZoomed($hwnd) }
        $restored = Record-Page 'restored' 'folders' 'restored-folders'
        if ($restored.bounds -ne $manual.bounds) { $failures.Add('Restore lost manual position/size') }
        Back $access 'CloseAccessButton' 'AccessSettingsButton' $true
        $returned = Record-Page 'restored' 'overview' 'restored-return'
        Check-Navigation $restored $returned
    }
    Pass "$($records.Count) 次原生窗体与标题像素取样；$($failures.Count) 项差异；键盘 Enter／Esc 和返回入口焦点均已验证。"
} finally {
    $summary = [ordered]@{version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion;
        windowDpi=$testedDpi;measurements=$records;clientWindowCapabilities=$capabilities;differences=$failures}
    $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ScreenshotDirectory 'navigation.json')
    $probe.Dispose()
    $null=[EdgeQaCapture]::SetThreadDpiAwarenessContext($previousDpi)
}
