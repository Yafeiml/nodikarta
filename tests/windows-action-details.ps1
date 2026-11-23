# Exercise the visible controls on the disposable desktop instance with real pointer and keyboard input.
$workspace = [IO.Directory]::CreateDirectory((Join-Path $root 'ProjectHub')).FullName
$null = & $cli --state $state bind --project $ProjectId --path $workspace
if ($LASTEXITCODE) { throw 'Could not bind the disposable workspace.' }
Click $main 'RefreshButton'
$null = Wait-Ui '目录已绑定' { HasText $main $workspace }
$scratch = Join-Path $root 'captures'
Save-WindowsTestWindow $main $scratch 'initial'
$hwnd = [intptr]$main.Current.NativeWindowHandle
$testedDpi = [EdgeQaCapture]::GetDpiForWindow($hwnd)
$scale = $testedDpi/96
. (Join-Path $PSScriptRoot 'WindowsPointerInput.ps1')
function Send-TestKey([byte]$key) {
    if ([EdgeQaInput]::GetForegroundWindow() -ne $hwnd) { throw '焦点已离开测试窗口。' }
    [EdgeQaInput]::keybd_event($key,0,0,0)
    [EdgeQaInput]::keybd_event($key,0,2,0)
    Start-Sleep -Milliseconds 75
}
function Capture-Action([string]$name) {
    $dest = if ($ScreenshotDirectory) { $ScreenshotDirectory } else { $scratch }
    Start-Sleep -Milliseconds 200
    Save-WindowsTestWindow $main $dest $name
    Join-Path $dest ($name+'.png')
}
function Check-ButtonSurface($element,[string]$name) {
    PointAt $element
    $photo = Capture-Action $name
    $frame = [EdgeQaCapture+Rect]::new()
    $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
    $r = $element.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new($photo)
    try {
        $c = $bitmap.GetPixel([int]($r.Left-$frame.Left+4*$scale),[int]($r.Top-$frame.Top+$r.Height/2))
        if ($c.B-$c.R -lt 24 -or $c.R -gt 230) { throw "$name 选中背景在悬停时消失 ($c)。" }
        elseif ($name -in 'catalog-hover','config-hover') {
            $ink = [Collections.Generic.List[int]]::new()
            $fill = [Collections.Generic.List[int]]::new()
            $left = [int]($r.Left-$frame.Left)
            $top = [int]($r.Top-$frame.Top)
            for ($y=0; $y -lt [int]$r.Height; $y++) {
                if ($bitmap.GetPixel($left+[int](6*$scale),$top+$y).ToArgb() -eq $c.ToArgb()) { $fill.Add($y) }
                for ($x=[int](5*$scale); $x -lt $r.Width-5*$scale; $x++) {
                    $inkColor = $bitmap.GetPixel($left+$x,$top+$y)
                    if ($inkColor.R -lt 115 -and $inkColor.G -lt 145) { $ink.Add($y); break }
                }
            }
            if (!$ink.Count -or !$fill.Count) { throw "$name 无法测量标签字形或背景。" }
            $offset = [Math]::Abs(($ink[0]+$ink[-1]-$fill[0]-$fill[-1])/2)
            if ($offset -gt 1.7*$scale) { $visualFailures.Add("$name 标签文字与选中底色中心相差 $offset px。") }
            else { Pass "$name 选中态文字与可视底色中心相差 $offset px" }
        }
    } finally { $bitmap.Dispose() }
}
function Assert-ButtonPaint($element,[string]$photo,[string]$expected) {
    $frame = [EdgeQaCapture+Rect]::new()
    $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
    $r = $element.Current.BoundingRectangle
    $left = [int]($r.Left-$frame.Left)
    $top = [int]($r.Top-$frame.Top)
    $bitmap = [Drawing.Bitmap]::new($photo)
    try {
        $color = $bitmap.GetPixel($left+[int](4*$scale),$top+[int]($r.Height/2))
        $rgb = '{0:X2}{1:X2}{2:X2}' -f $color.R,$color.G,$color.B
        if ($rgb -ne $expected) { throw "$($element.Current.AutomationId) 实图底色 $rgb，预期 $expected。" }
        for ($y=[int]($r.Height/2); $y -lt $r.Height-3*$scale; $y++) {
            $run = 0
            for ($x=[int](4*$scale); $x -lt $r.Width-4*$scale; $x++) {
                $p = $bitmap.GetPixel($left+$x,$top+$y)
                if ($p.R -lt 135 -and $p.G -lt 160 -and $p.B -lt 180) { $run++ } else { $run = 0 }
                if ($run -gt $r.Width*0.48) { throw "$($element.Current.AutomationId) 文字有连续下划线。" }
            }
        }
    } finally { $bitmap.Dispose() }
}
function Check-NeutralButton($button,$park,[string]$stem,[string]$idleColor) {
    PointAt $park
    Save-WindowsTestWindow $main $scratch 'neutral-idle'
    Assert-ButtonPaint $button (Join-Path $scratch 'neutral-idle.png') $idleColor
    PointAt $button
    Assert-ButtonPaint $button (Capture-Action "$stem-hover") 'EDF3F7'
    PointAt $button -WhilePressed {
        Assert-ButtonPaint $button (Capture-Action "$stem-pressed") 'DFE9F2'
        PointAt $park
    }
    Pass "$stem 实图正常 / 悬停 / 按下底色为 $idleColor / EDF3F7 / DFE9F2，文字无下划线"
}
function Check-GroupSpacing($page,[string[]]$ids,[string]$name) {
    $previous = $null
    $horizontal = @()
    $vertical = @()
    foreach ($id in $ids) {
        $button = Wait-Ui $id { ById $page $id }
        AssertContained $main $button $id
        $r = $button.Current.BoundingRectangle
        if ($previous) {
            if ([Math]::Abs($r.Top-$previous.Top) -lt 1) { $horizontal += $r.Left-$previous.Right }
            else { $vertical += $r.Top-$previous.Bottom }
        }
        $previous = $r
    }
    foreach ($gap in (@($horizontal)+@($vertical))) {
        # UI Automation returns integer outer bounds for fractional-DIP glyph widths.
        if ($gap -le 0 -or [Math]::Abs($gap-2*$scale) -gt 1) { throw "$name 相邻按钮间距 $gap px，预期约 $(2*$scale) px。" }
    }
    Pass "$name 实测按钮横向间距 [$($horizontal -join ', ')] px，换行间距 [$($vertical -join ', ')] px"
    return $vertical.Count
}
function Check-PaintedGap($page,[string[]]$ids,[string]$name,[switch]$Temporary) {
    Click $page $ids[0]
    $first = (ById $page $ids[0]).Current.BoundingRectangle
    $next = ById $page $ids[1]
    PointAt $next
    if ($Temporary) {
        Start-Sleep -Milliseconds 200
        Save-WindowsTestWindow $main $scratch $name
        $photo = Join-Path $scratch ($name+'.png')
    } else { $photo = Capture-Action $name }
    $frame = [EdgeQaCapture+Rect]::new()
    $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
    $bitmap = [Drawing.Bitmap]::new($photo)
    try {
        $right = [int]($first.Right-$frame.Left)
        $y = [int]($first.Top-$frame.Top+$first.Height/2)
        $gap = 0
        for ($x=$right-[int](5*$scale); $x -lt $right+[int](8*$scale); $x++) {
            $p = $bitmap.GetPixel($x,$y)
            if ($p.R -eq 255 -and $p.G -eq 255 -and $p.B -eq 255) { $gap++ }
        }
        $selected = $bitmap.GetPixel($right-[int](5*$scale),$y)
        $hovered = $bitmap.GetPixel($right+[int](8*$scale),$y)
        if ($selected.B-$selected.R -lt 20 -or $hovered.R -ne 237 -or $hovered.G -ne 243) { throw "$name 未呈现相邻选中和悬停背景。" }
        if ($gap -ne [Math]::Round(2*$scale)) { throw "$name 两按钮可见色块间实际有 $gap 个白色像素，预期 $(2*$scale)。" }
        Pass "$name $($ids[0]) 选中、$($ids[1]) 悬停，背景边缘之间实测 $gap 个白色像素"
    } finally { $bitmap.Dispose() }
}
function Check-AccessButton([string]$name) {
    $link = ById $main 'AccessSettingsButton'
    $bounds = $link.Current.BoundingRectangle
    Check-DirectoryActions $scale $name
    PointAt (ById $main 'CoreApiStatus')
    Start-Sleep -Milliseconds 200
    Save-WindowsTestWindow $main $scratch 'idle'
    PointAt $link
    $hover = Capture-Action $name
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16) -ne 0) { throw '无法获取实图边界。' }
    $idle = [Drawing.Bitmap]::new((Join-Path $scratch 'idle.png'))
    $bitmap = [Drawing.Bitmap]::new($hover)
    try {
        $left = [int]($bounds.Left-$frame.Left)
        $top = [int]($bounds.Top-$frame.Top)
        $padding = [int](2*$scale)
        $columns = [Collections.Generic.List[int]]::new()
        $tops = @{}
        $bottoms = @{}
        $changes = 0
        for ($x=$padding; $x -lt $bounds.Width-$padding; $x++) {
            for ($y=$padding; $y -lt $bounds.Height-$padding; $y++) {
                $c = $bitmap.GetPixel($left+$x,$top+$y)
                if ($c.ToArgb() -ne $idle.GetPixel($left+$x,$top+$y).ToArgb()) { $changes++ }
                if ($c.R -gt 145 -or $c.G -gt 160 -or $c.B -gt 190) { continue }
                if (!$tops.ContainsKey($x)) { $columns.Add($x); $tops[$x] = $y }
                $bottoms[$x] = $y
            }
        }
        if ($changes -lt $bounds.Height*$bounds.Width*0.48) { throw "按钮悬停底色未正常出现 ($changes px)。" }
        $split = $columns.Count-1
        while ($split -gt 0 -and $columns[$split]-$columns[$split-1] -lt 4*$scale) { $split-- }
        if ($split -le 0) { throw '箭头与文字没有独立间距。' }
        $labelXs = $columns.GetRange(0,$split)
        $arrowXs = $columns.GetRange($split,$columns.Count-$split)
        $arrowRange = $arrowXs | ForEach-Object { $tops[$_]; $bottoms[$_] } | Measure-Object -Minimum -Maximum
        $labelRange = $labelXs | ForEach-Object { $tops[$_]; $bottoms[$_] } | Measure-Object -Minimum -Maximum
        $arrowHeight = $arrowRange.Maximum-$arrowRange.Minimum+1
        $centering = [Math]::Abs(($labelRange.Maximum+$labelRange.Minimum-$arrowRange.Maximum-$arrowRange.Minimum)/2)
        $heading = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'本机目录绑定')) |
            Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text } | Select-Object -First 1
        AssertContained $main $heading '本机目录绑定标题'
        $headingBox = $heading.Current.BoundingRectangle
        $inkRows = for ($y=[int]$headingBox.Top; $y -lt $headingBox.Bottom; $y++) {
            for ($x=[int]$headingBox.Left; $x -lt [Math]::Min($headingBox.Right,$headingBox.Left+90*$scale); $x++) {
                if ($bitmap.GetPixel($x-$frame.Left,$y-$frame.Top).R -lt 100) { $y; break }
            }
        }
        if (!$inkRows) { throw '未测到行标题文字。' }
        $rowOffset = [Math]::Abs(($inkRows[0]+$inkRows[-1])/2 - $bounds.Top-($labelRange.Minimum+$labelRange.Maximum)/2)
        if ($arrowHeight -lt 6*$scale -or $centering -gt 3*$scale -or $labelRange.Minimum -lt $padding -or
            $bounds.Height-1-$labelRange.Maximum -lt $padding -or $rowOffset -gt 3*$scale) {
            throw "文字或箭头大小、垂直对齐异常 (箭头高 $arrowHeight px，文字与箭头差 $centering px，与行标题差 $rowOffset px)。"
        }
        $gap = $arrowXs[0]-$labelXs[$labelXs.Count-1]-1
        Pass "$name 悬停底色完整，文字与箭头间距 $gap px、中心相差 $centering px，与行标题中心相差 $rowOffset px"
    } finally { $bitmap.Dispose(); $idle.Dispose() }
}
$priorDpi = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
$priorPointer = [long]0
$null = [EdgeQaInput]::GetPhysicalCursorPos([ref]$priorPointer)
try {
    $null = [EdgeQaInput]::SetForegroundWindow($hwnd)
    $null = Wait-Ui '测试窗口获得前台输入' { [EdgeQaInput]::GetForegroundWindow() -eq $hwnd }
    PointAt (ById $main 'CoreApiStatus') -MouseClick
    Start-Sleep -Milliseconds 300
    $oldSize = $main.Current.BoundingRectangle.Size
    $visualFailures = [Collections.Generic.List[string]]::new()
    try { Check-AccessButton 'overview-hover' } catch { $visualFailures.Add($_.Exception.Message) }
    Check-NeutralButton (ById $main 'AccessSettingsButton') (ById $main 'CoreApiStatus') 'overview' 'F4F7FA'
    $null = [EdgeQaCapture]::Resize($hwnd,500*$scale,520*$scale)
    Start-Sleep -Milliseconds 300
    try { Check-AccessButton 'compact-overview-hover' } catch { $visualFailures.Add($_.Exception.Message) }
    $null = [EdgeQaCapture]::Resize($hwnd,$oldSize.Width,$oldSize.Height)
    Start-Sleep -Milliseconds 300
    PointAt (ById $main 'AccessSettingsButton') -MouseClick
    $access = Wait-Ui '鼠标进入接入管理' { AccessWindow }
    $null = Wait-Ui '自检可用' { (ById $access 'AccessCheckButton').Current.IsEnabled }
    Check-NeutralButton (ById $access 'AdjustAccessScope') (ById $access 'AccessProject') 'client' 'F4F7FA'
    Check-ButtonSurface (ById $access 'AccessClientTab') 'client-navigation-hover'
    Click $access 'AccessCatalogTab'
    $categories = @('项目与知识','共享素材','反馈','事件','任务','Agent') | ForEach-Object { 'ToolGroup_'+$_ }
    $formats = @('ConfigFormatClients','ConfigFormatStdio','ConfigFormatSkills')
    $null = Check-GroupSpacing $access $categories '默认工具分类'
    Check-PaintedGap $access $categories 'catalog-spacing'
    Check-NeutralButton (ById $access 'ToolGroup_共享素材') (ById $access 'AccessProject') 'category-idle' 'FFFFFF'
    Click $access 'ToolGroup_共享素材'
    $null = Wait-Ui '选择共享素材' { HasText $access 'download_asset' }
    Check-ButtonSurface (ById $access 'ToolGroup_共享素材') 'catalog-hover'
    Pass '实际鼠标点击进入目录设置，工具分类及导航选中项在悬停时仍保持蓝底'
    $accessSize = $main.Current.BoundingRectangle.Size
    $null = [EdgeQaCapture]::Resize($hwnd,506*$scale,540*$scale)
    Start-Sleep -Milliseconds 250
    $null = Check-GroupSpacing $access $categories '最窄窗口工具分类'
    Check-PaintedGap $access $categories 'compact-catalog-spacing' -Temporary
    $null = [EdgeQaCapture]::Resize($hwnd,$accessSize.Width,$accessSize.Height)
    Start-Sleep -Milliseconds 250
    Click $access 'AccessClientTab'
    [IO.File]::WriteAllText((Join-Path $root 'core-offline'),'403')
    Click $access 'RefreshAccessButton'
    $grant = Wait-Ui '权限未确认徽标' { $el = ById $access 'AccessGrant'; if ($el.Current.Name -eq '授权待确认') { $el } }
    $denied = Capture-Action 'authorization-unconfirmed'
    $frame = [EdgeQaCapture+Rect]::new()
    $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
    $bitmap = [Drawing.Bitmap]::new($denied)
    try {
        $r = $grant.Current.BoundingRectangle
        $color = $bitmap.GetPixel([int]($r.Left-$frame.Left-3*$scale),[int]($r.Top-$frame.Top+$r.Height/2))
        if ($color.B -le $color.G -or $color.G-$color.R -gt 9 -or $color.R -gt 248) { throw "未确认授权徽标底色异常 ($color)。" }
        foreach ($id in 'AdjustAccessScope','AccessCheckButton') {
            $control = ById $access $id
            if ($control.Current.IsEnabled) { throw "$id 仍可操作。" }
            $r = $control.Current.BoundingRectangle
            $left = [int]($r.Left-$frame.Left)
            $top = [int]($r.Top-$frame.Top)
            $color = $bitmap.GetPixel($left+[int](4*$scale),$top+[int]($r.Height/2))
            if ($id -eq 'AdjustAccessScope' -and ($color.R -ne 237 -or $color.G -ne 241 -or $color.B -ne 245)) {
                throw "禁用的调整范围未使用中性按钮禁用底色。($color)"
            }
            if ($id -eq 'AccessCheckButton') {
                if ($color.R -lt 222 -or $color.R -gt 250) { throw "自检禁用态底色异常 ($color)。" }
                for ($x=[int](10*$scale); $x -lt 30*$scale; $x++) {
                    for ($y=[int](6*$scale); $y -lt $r.Height-6*$scale; $y++) {
                        $stroke = $bitmap.GetPixel($left+$x,$top+$y)
                        if ($stroke.R -lt 130 -and $stroke.B-$stroke.R -gt 65) { throw "禁用盾牌图标仍在使用活动颜色 ($stroke)。" }
                    }
                }
            }
        }
    } finally { $bitmap.Dispose() }
    [IO.File]::Delete((Join-Path $root 'core-offline'))
    Click $access 'RefreshAccessButton'
    $null = Wait-Ui '授权徽标恢复' { (ById $access 'AccessGrant').Current.Name -eq '项目读写' }
    Pass 'Core 拒绝授权后显示中性授权待确认徽标，调整范围和自检均禁用并呈灰色，恢复后显示项目读写'
    Click $access 'CloseAccessButton'
    $null = Wait-Ui '返回概览' { ById $main 'AccessSettingsButton' }
    (ById $main 'OpenCoreButton').SetFocus()
    $focused = [Collections.Generic.List[string]]::new()
    for ($tab=0; $tab -lt 16; $tab++) {
        Send-TestKey 9
        $focused.Add([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId)
        if ($focused[$tab] -eq 'AccessSettingsButton') { break }
    }
    if ($focused[$focused.Count-1] -ne 'AccessSettingsButton') { throw "Tab 未聚焦接入管理。经过控件：$($focused -join ' → ')" }
    $null = Capture-Action 'overview-focus'
    Send-TestKey 13
    $access = Wait-Ui 'Enter 进入接入管理' { AccessWindow }
    $null = Wait-Ui '接入页已就绪且接收键盘焦点' {
        (ById $access 'AccessCheckButton').Current.IsEnabled -and
            [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'AccessClientTab'
    }
    Send-TestKey 27
    $null = Wait-Ui 'Esc 返回设备概览并恢复入口焦点' {
        [bool](ById $main 'LanguageButton') -and [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'AccessSettingsButton'
    }
    Pass '键盘 Tab 聚焦目录设置，Enter 进入目录页并聚焦导航，Esc 返回概览并恢复入口焦点'
    $globalButton = ById $main 'GlobalAccessButton'
    Check-NeutralButton $globalButton (ById $main 'CoreApiStatus') 'global-entry' 'F4F7FA'
    $globalButton.SetFocus()
    Send-TestKey 13
    $access = Wait-Ui 'Enter 进入设备级智能体接入' { AccessWindow }
    $null = Wait-Ui '全局接入可操作' { (ById $access 'HarnessInstall_codex').Current.IsEnabled }
    Check-AccessMethods $access
    Click $access 'ConfigFormatStdio'
    Check-ButtonSurface (ById $access 'ConfigFormatStdio') 'method-hover'
    $null = Check-GroupSpacing $access $formats '默认设备接入方式'
    Check-PaintedGap $access $formats 'method-spacing'
    $deviceSize = $main.Current.BoundingRectangle.Size
    $null = [EdgeQaCapture]::Resize($hwnd,506*$scale,540*$scale)
    Start-Sleep -Milliseconds 250
    $null = Check-GroupSpacing $access $formats '最窄窗口设备接入方式'
    Check-PaintedGap $access $formats 'compact-method-spacing'
    $null = [EdgeQaCapture]::Resize($hwnd,$deviceSize.Width,$deviceSize.Height)
    Pass '设备接入方式在窄窗和默认窗口共用工具分类的 2 DIP 按钮间距'
    Send-TestKey 27
    $null = Wait-Ui '设备级接入返回原入口焦点' {
        [bool](ById $main 'LanguageButton') -and [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'GlobalAccessButton'
    }
    Pass '设备级智能体接入支持 Enter 进入、Esc 返回并恢复入口焦点'
    if ($visualFailures.Count) { throw ($visualFailures -join [Environment]::NewLine) }
} finally {
    [IO.File]::Delete((Join-Path $root 'core-offline'))
    $current = [long]0
    $null = [EdgeQaInput]::GetPhysicalCursorPos([ref]$current)
    $where = [EdgeQaCapture+Point]::new(($current -shl 32 -shr 32),($current -shr 32))
    if ([EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint($where),2) -eq $hwnd) {
        $null = [EdgeQaInput]::SetPhysicalCursorPos(($priorPointer -shl 32 -shr 32),($priorPointer -shr 32))
    }
    $null = [EdgeQaCapture]::SetThreadDpiAwarenessContext($priorDpi)
}
