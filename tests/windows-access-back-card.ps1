# Run against one temporary, paired Windows instance via --back-card.
$scratch = Join-Path $root 'back-card-captures'
Save-WindowsTestWindow $main $scratch 'overview'
$hwnd = [intptr]$main.Current.NativeWindowHandle
$testedDpi = [EdgeQaCapture]::GetDpiForWindow($hwnd)
$scale = $testedDpi/96
$descendants = [Windows.Automation.TreeScope]::Descendants
$backName = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'返回设备概览')
$textType = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
foreach ($layout in 'default','narrow') {
    $palette = @{}
    foreach ($page in 'Clients','Stdio','Skills') {
        Click $main 'GlobalAccessButton'
        $access = Wait-Ui '设备接入页' { AccessWindow }
        $original = Wait-Ui '标题左侧的返回按钮' { ById $access 'CloseGlobalAccessButton' }
        if ($layout -eq 'narrow') {
            if (![EdgeQaCapture]::Resize($hwnd,506*$scale,540*$scale)) { throw '无法缩放测试窗口。' }
            $null = Wait-Ui '窄窗口已生效' { $main.Current.BoundingRectangle.Width -le 523*$scale }
        }
        if ($page -ne 'Clients') { Click $access "ConfigFormat$page" }
        Check-DevicePage $access $page
        $matches = $access.FindAll($descendants,$backName)
        if ($matches.Count -ne 1 -or $matches[0].Current.AutomationId -ne 'CloseGlobalAccessButton' -or
            ($matches[0].GetRuntimeId() -join ',') -ne ($original.GetRuntimeId() -join ',') -or
            $matches[0].FindAll($descendants,$textType).Count -ne 0) { throw "$layout/$page 未使用同一个纯图标返回按钮。" }
        $heading = ById $access 'DeviceAccessHeading'
        AssertContained $main $heading '智能体接入标题'
        AssertContained $main $matches[0] '返回箭头'
        $title = $heading.Current.BoundingRectangle
        $back = $matches[0].Current.BoundingRectangle
        if ($back.Right -gt $title.Left -or [Math]::Abs($back.Top+$back.Height/2-$title.Top-$title.Height/2) -gt $scale) {
            throw "$layout/$page 返回箭头未在标题左侧垂直居中。"
        }

        $destination = if ($ScreenshotDirectory -and $page -eq 'Clients') { $ScreenshotDirectory } else { $scratch }
        $name = "$($page.ToLowerInvariant())-$layout"
        Save-WindowsTestWindow $main $destination $name
        $frame = [EdgeQaCapture+Rect]::new()
        if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw '无法定位窗口实图。' }
        $image = [Drawing.Bitmap]::new((Join-Path $destination "$name.png"))
        try {
            $x = [int]((ById $access 'ConfigFormatClients').Current.BoundingRectangle.Left-$frame.Left)
            $markers = switch ($page) {
                'Clients' { 'codex','claude','antigravity','cursor','opencode','deepseek','pi' | ForEach-Object { "HarnessIcon_$_" } }
                'Stdio' { 'CopyConfigButton' }
                'Skills' { 'CheckSkillsButton' }
            }
            foreach ($id in $markers) {
                $r = (ById $access $id).Current.BoundingRectangle
                $y = [int]($r.Top+$r.Height/2-$frame.Top)
                if ($r.Top-$frame.Top -lt 0 -or $r.Bottom-$frame.Top -gt $image.Height) { throw "$layout/$id 未完整显示在截图内。" }
                $inside = $image.GetPixel($x+[int](4*$scale),$y)
                $edge = 0..2 | ForEach-Object { $image.GetPixel($x+$_,$y) } | Sort-Object R | Select-Object -First 1
                $colors = ('{0:X6}/{1:X6}' -f ($edge.ToArgb() -band 0xFFFFFF),($inside.ToArgb() -band 0xFFFFFF))
                $expectedFill = if ($page -eq 'Clients') { 'ffffffff' } else { 'fff5f8fc' }
                if ($inside.Name -cne $expectedFill -or $inside.ToArgb() -eq $edge.ToArgb() -or
                    ($palette.ContainsKey($page) -and $colors -cne $palette[$page]) -or
                    ($page -eq 'Skills' -and $colors -cne $palette['Stdio'])) {
                    throw "$layout/$id 卡片边线和底色异常：$colors。"
                }
                $palette[$page] = $colors
                if ($page -eq 'Clients') {
                    $rowColor = $image.GetPixel([int]($r.Right-$frame.Left+9*$scale),$y)
                    if ($rowColor.ToArgb() -ne $inside.ToArgb()) { throw "$layout/$id 列表行与外卡的底色不同。" }
                    if ($id -eq 'HarnessIcon_codex') {
                        # A square row painted over the card can leave only a faint, broken arc.
                        $arcY = $y - [int](24*$scale)
                        $arc = ([int](2*$scale)..[int](6*$scale) | ForEach-Object { $image.GetPixel($x+$_,$arcY).R } |
                                Measure-Object -Minimum).Minimum
                        if ($arc -gt $edge.R + 6) { throw "$layout 列表左上圆角的轮廓被第一行遮挡。" }
                    }
                    $dark = 0
                    for ($py=[int][Math]::Ceiling($r.Top-$frame.Top); $py -lt [int]($r.Bottom-$frame.Top); $py++) {
                        for ($px=[int][Math]::Ceiling($r.Left-$frame.Left); $px -lt [int]($r.Right-$frame.Left); $px++) {
                            $pixel = $image.GetPixel($px,$py)
                            if ([Math]::Abs([int]$pixel.R-$pixel.G) -gt 9 -or [Math]::Abs([int]$pixel.R-$pixel.B) -gt 9) {
                                throw "$layout/$id 图标含彩色像素 $pixel。"
                            }
                            if ($pixel.R -lt 96) { $dark++ }
                        }
                    }
                    if ($dark -lt 8) { throw "$layout/$id 没有可辨认的深色图形。" }
                }
            }
        } finally { $image.Dispose() }
        $matches[0].SetFocus()
        $null = Wait-Ui "$layout/$page 返回按钮收到焦点" { $matches[0].Current.HasKeyboardFocus }
        Click $access 'CloseGlobalAccessButton'
        try {
            $null = Wait-Ui "$layout/$page 返回概览并恢复入口焦点" {
                [bool](ById $main 'LanguageButton') -and (ById $main 'GlobalAccessButton').Current.HasKeyboardFocus
            }
        } catch {
            $focused = [Windows.Automation.AutomationElement]::FocusedElement.Current
            $current = if ($focused.ProcessId -eq $app.Id) { $focused.AutomationId } else { '其他进程' }
            throw "$layout/$page 点击后窗口为「$($main.Current.Name)」，焦点位置：$current。"
        }
    }
    Pass "$layout：三个页签的返回按钮及焦点恢复均通过。七个黑白品牌图标所在列表的边线和底色为 $($palette['Clients'])，其余两页为 $($palette['Stdio'])。"
}
