# Loaded by the isolated Windows GUI fixture.
function UnbindButton($path) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,"解除绑定 $path")
    $main.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function OpenUnbind($path) {
    $button = Wait-Ui "解除 $path" { $b=UnbindButton $path; if ($b -and $b.Current.IsEnabled) { $b } }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $processMatch = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    Wait-Ui '解绑确认框' {
        $notice = ById $main 'Notice'
        if ($notice -and $notice.Current.Name) { throw $notice.Current.Name }
        $nested = ById $main 'ConfirmUnbind'
        if ($nested) {
            while ($nested -and $nested.Current.ControlType -ne [Windows.Automation.ControlType]::Window) {
                $nested = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($nested)
            }
            if ($nested) { return $nested }
        }
        [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$processMatch) |
            Where-Object { $_.Current.Name -eq '解除目录绑定' } | Select-Object -First 1
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UnbindKeys {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowStyle(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point origin);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static int ClientTopOffset(IntPtr hwnd) {
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        try {
            var origin = new Point();
            if (!ClientToScreen(hwnd, ref origin) || !GetWindowRect(hwnd, out var rect))
                throw new InvalidOperationException("Cannot measure the non-client area.");
            return origin.Y - rect.Top;
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@
function Save-DialogWithOwner($dialogHandle,$ownerHandle,[int]$thickness) {
    $before = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
    try {
        $frame = [EdgeQaCapture+Rect]::new()
        if ([EdgeQaCapture]::DwmGetWindowAttribute($dialogHandle,9,[ref]$frame,16)) { throw '无法取得确认框的真实可见边界。' }
        $area = [Drawing.Rectangle]::FromLTRB($frame.Left,$frame.Top,$frame.Right,$frame.Bottom)
        $area.Inflate(12,12)
        # Only include this test dialog and the strip of its test owner immediately around it.
        foreach ($x in ($area.Left+1),($area.Right-2),($frame.Left+2),([int]($area.Left+$area.Width/2))) {
            foreach ($y in ($area.Top+1),($area.Bottom-2),($frame.Top+2),([int]($area.Top+$area.Height/2))) {
                $point = [EdgeQaCapture+Point]::new($x,$y)
                $visible = [EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint($point),2)
                if ($visible -notin $dialogHandle,$ownerHandle) { throw '确认框或测试所有者被其他窗口遮挡。' }
            }
        }
        $image = [Drawing.Bitmap]::new($area.Width,$area.Height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($image)
            try { $graphics.CopyFromScreen($area.Left,$area.Top,0,0,$area.Size) } finally { $graphics.Dispose() }
            $image.Save((Join-Path $ScreenshotDirectory 'confirmation.png'),[Drawing.Imaging.ImageFormat]::Png)
            foreach ($side in 'top','bottom','left','right') {
                $outside,$border,$inside = foreach ($d in 11,12,(13+$thickness)) {
                    switch ($side) {
                        top    { $image.GetPixel([int]($image.Width/2),$d) }
                        bottom { $image.GetPixel([int]($image.Width/2),$image.Height-1-$d) }
                        left   { $image.GetPixel($d,[int]($image.Height/2)) }
                        right  { $image.GetPixel($image.Width-1-$d,[int]($image.Height/2)) }
                    }
                }
                foreach ($near in $outside,$inside) {
                    if ([Math]::Abs([int]$border.R-$near.R)+[Math]::Abs([int]$border.G-$near.G)+[Math]::Abs([int]$border.B-$near.B) -lt 12) {
                        throw "$side 边的屏幕实图与相邻背景或客户区无可辨识的边缘。"
                    }
                }
            }
        } finally { $image.Dispose() }
    } finally { $null = [EdgeQaCapture]::SetThreadDpiAwarenessContext($before) }
}
function DialogKey($dialog,[int]$key) {
    if ($dialog.Current.ProcessId -ne $app.Id -or $dialog.Current.Name -ne '解除目录绑定') { throw 'Wrong test window' }
    $target = [intptr]$dialog.Current.NativeWindowHandle
    $null = [UnbindKeys]::PostMessage($target,0x100,[intptr]$key,[intptr]1)
    $null = [UnbindKeys]::PostMessage($target,0x101,[intptr]$key,[intptr]0xC0000001L)
}
function Check-UnbindLayout($dialog) {
    $scale = [EdgeQaCapture]::GetDpiForWindow([intptr]$dialog.Current.NativeWindowHandle)/96
    $window = $dialog.Current.BoundingRectangle
    $bounds = @{}
    foreach ($id in 'UnbindHeading','UnbindProjectLabel','UnbindDirectoryLabel','UnbindProjectName','UnbindPath','UnbindNote','CancelUnbind','ConfirmUnbind') {
        $element = ById $dialog $id
        AssertContained $dialog $element $id
        $bounds[$id] = $element.Current.BoundingRectangle
    }
    foreach ($id in 'UnbindHeading','UnbindProjectLabel','UnbindDirectoryLabel','UnbindNote') {
        if ([math]::Abs($bounds[$id].Left-$window.Left-20*$scale) -gt 1) { throw "$id 未对齐确认框左侧 20 DIP。" }
    }
    foreach ($id in 'UnbindProjectName','UnbindPath','ConfirmUnbind') {
        if ([math]::Abs($window.Right-$bounds[$id].Right-20*$scale) -gt 1) { throw "$id 未对齐确认框右侧 20 DIP。" }
    }
    $project = $bounds.UnbindProjectName
    $pathBox = $bounds.UnbindPath
    if ([math]::Abs($project.Left-$pathBox.Left) -gt 1 -or
        [math]::Abs($bounds.UnbindProjectLabel.Top-$project.Top) -gt 1 -or
        [math]::Abs($bounds.UnbindDirectoryLabel.Top-$pathBox.Top) -gt 1 -or
        $bounds.UnbindProjectLabel.Right -gt $project.Left -or $bounds.UnbindDirectoryLabel.Right -gt $pathBox.Left -or
        $project.Bottom -gt $pathBox.Top -or
        $pathBox.Height -lt 1.5*$bounds.UnbindNote.Height) {
        throw "项目和目录两列未对齐、内容重叠或长路径未换行。项目标签 $($bounds.UnbindProjectLabel)，值 $project；目录标签 $($bounds.UnbindDirectoryLabel)，值 $pathBox。"
    }
    if ($pathBox.Bottom -gt $bounds.UnbindNote.Top) {
        # UIA returns the complete wrapped text bounds even when its last lines are outside the viewport.
        $ancestor = ById $dialog 'UnbindPath'
        $scroll = $null
        while ($ancestor -and $ancestor -ne $dialog) {
            if ($ancestor.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scroll) -and
                $scroll.Current.VerticallyScrollable) { break }
            $ancestor = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($ancestor)
        }
        if (!$scroll -or $ancestor.Current.BoundingRectangle.Bottom -gt $bounds.UnbindNote.Top) { throw '长路径缺少独立于下方提示的滚动区域。' }
        $scroll.SetScrollPercent(-1,100)
        $null = Wait-Ui '长路径末行在详情区内可读' {
            (ById $dialog 'UnbindPath').Current.BoundingRectangle.Bottom -le $ancestor.Current.BoundingRectangle.Bottom+1
        }
        $scroll.SetScrollPercent(-1,0)
        $null = Wait-Ui '详情恢复初始位置' { (ById $dialog 'UnbindPath').Current.BoundingRectangle.Top -eq $pathBox.Top }
    }
    $cancel = $bounds.CancelUnbind
    $confirm = $bounds.ConfirmUnbind
    foreach ($button in $cancel,$confirm) {
        if ([math]::Abs($button.Width-88*$scale) -gt 1 -or [math]::Abs($button.Height-32*$scale) -gt 1) {
            throw "确认框按钮尺寸应为 88 × 32 DIP，实测 $($button.Width) × $($button.Height) px。"
        }
    }
    $gap = $confirm.Left-$cancel.Right
    if ([math]::Abs($cancel.Top-$confirm.Top) -gt 1 -or [math]::Abs($cancel.Bottom-$confirm.Bottom) -gt 1 -or
        [math]::Abs($gap-8*$scale) -gt 1) { throw "确认框按钮上下边缘或净距异常，实测间距 $gap px。" }
    Pass "长路径换行后两列各自左齐、标签和值逐行顶齐，正文与确认按钮共用 20 DIP 边距；按钮均为 $($confirm.Width) × $($confirm.Height) px，实测净距 $gap px"
}
$first = [IO.Directory]::CreateDirectory((Join-Path $root ('目录-'+'workspace-'*8))).FullName
$second = [IO.Directory]::CreateDirectory((Join-Path $root 'another-workspace')).FullName
$kept = Join-Path $first 'keep.txt'
[IO.File]::WriteAllText($kept,'keep this file')
foreach($dir in $first,$second) {
    & $cli --state $state bind --project $ProjectId --path $dir | Out-Null
    if ($LASTEXITCODE) { throw '临时目录绑定失败' }
}
Click $main 'RefreshButton'
$button = Wait-Ui '目录行按钮' { UnbindButton $first }
$button.SetFocus()
Snapshot 'directories'
if ($ScreenshotDirectory) {
    $handle = [intptr]$main.Current.NativeWindowHandle
    $testedDpi = [EdgeQaCapture]::GetDpiForWindow($handle)
    $scale = $testedDpi/96
    if (![EdgeQaCapture]::Resize($handle,460*$scale,560*$scale)) { throw '无法缩至窄窗' }
    $button.SetFocus()
    Start-Sleep -Milliseconds 250
    AssertContained $main $button '窄窗解除绑定'
    Check-DirectoryActions $scale '双目录最窄窗口'
    $machineName = ById $main 'MachineName'
    $projectName = HasText (ById $main 'ProjectList') '跨平台客户端与共享素材协作验收项目'
    AssertContained $main $machineName '窄窗长设备名'
    AssertContained $main $projectName '窄窗长项目名'
    AssertContained $main (UnbindButton $second) '另一个目录的解绑操作'
    $projectRect = $projectName.Current.BoundingRectangle
    $deviceRect = $machineName.Current.BoundingRectangle
    if ($machineName.Current.Name -notlike '*设计协作工作站-Windows-11-演示设备*' -or
        $deviceRect.Top -lt (ById $main 'CoreApiStatus').Current.BoundingRectangle.Bottom -or
        $projectRect.Right -gt (ById $main 'AccessMode').Current.BoundingRectangle.Left) {
        throw '窄窗未保留完整设备名、辅助信息未在 Core 状态下方或长项目名遮盖权限。'
    }
    Snapshot 'compact-directories'
    Pass '最窄窗口内长设备名和项目名均未遮住操作或权限；两个绑定目录分别有独立的解绑按钮'
}
$dialog = OpenUnbind $first
if (!(HasText $dialog 'ProjectHub') -or !(HasText $dialog '本机文件会保留') -or (ById $dialog 'UnbindPath').Current.Name -ne $first) {
    throw '解绑提示未显示所选项目、完整路径或保留文件说明'
}
Check-UnbindLayout $dialog
$null=Wait-Ui '默认焦点取消' { (ById $dialog 'CancelUnbind').Current.HasKeyboardFocus }
$dialogHandle = [intptr]$dialog.Current.NativeWindowHandle
if ($dialog.Current.ProcessId -ne $app.Id -or $dialog.Current.Name -ne '解除目录绑定' -or !$dialogHandle) {
    throw '待检查窗口不是本次临时设备的解绑确认框。'
}
$style = [UnbindKeys]::GetWindowStyle($dialogHandle,-16)
$topPx = [UnbindKeys]::ClientTopOffset($dialogHandle)
$ownerHandle = [intptr]$main.Current.NativeWindowHandle
if (($style -band 0x00C00000) -eq 0x00C00000 -or ($style -band 0x000C0000) -or $topPx -ge 15) { # WS_CAPTION, WS_SYSMENU, WS_THICKFRAME
    throw "解绑确认框未呈现独立的原生无标题栏边框；样式 $($style.ToString('X8'))，客户区上方 $topPx px。"
}
if ([UnbindKeys]::IsWindowEnabled($ownerHandle)) { throw '解绑确认框打开后所有者窗口仍可操作。' }
$frameEnabled=0
$frameWidth=0
$renderResult = [UnbindKeys]::DwmGetWindowAttribute($dialogHandle,1,[ref]$frameEnabled,4)
$widthResult = [UnbindKeys]::DwmGetWindowAttribute($dialogHandle,37,[ref]$frameWidth,4)
if ($widthResult -eq 0 -and $frameWidth -le 0) { throw 'DWM 报告的可见边框宽度为零。' }
if ($ScreenshotDirectory) { Save-DialogWithOwner $dialogHandle $ownerHandle ([Math]::Max(1,$frameWidth)) }
if ($renderResult -ne 0) {throw '无法读取 DWM 的非客户区渲染状态。'}
Pass "样式 0x$($style.ToString('X8'))，没有 Windows 标题栏、系统菜单或可调大小边框；DWM 非客户区渲染标志 $frameEnabled，客户区上方 $topPx px，模态窗口已禁用所有者"
if ($ScreenshotDirectory) { Pass "物理屏幕截图四边均与内侧客户区、外侧所有者背景可区分，DWM 报告可见边框宽 $frameWidth px" }
DialogKey $dialog 27
$null=Wait-Ui 'Esc 后焦点回到原行' { (UnbindButton $first).Current.HasKeyboardFocus }
if ((& $cli --state $state bindings | ConvertFrom-Json -NoEnumerate).Count -ne 2) { throw '取消后目录绑定丢失' }
Pass '长路径与项目名称在确认框可读，默认聚焦取消，Esc 返回原行'
$dialog = OpenUnbind $first
DialogKey $dialog 9
$null=Wait-Ui 'Tab 到确认' { (ById $dialog 'ConfirmUnbind').Current.HasKeyboardFocus }
DialogKey $dialog 13
$null=Wait-Ui '解除指定行' { !(UnbindButton $first) -and (UnbindButton $second) }
if ([IO.File]::ReadAllText($kept) -ne 'keep this file') { throw '解绑影响本机文件' }
Pass 'Tab 和 Enter 可确认，保留其他目录和本机文件'
$dialog = OpenUnbind $second
Click $dialog 'CancelUnbind'
$null=Wait-Ui '取消后行可访问' { (UnbindButton $second).Current.IsEnabled }
$dialog = OpenUnbind $second
$previous = (& $cli --state $state bindings | ConvertFrom-Json)[0]
& $cli --state $state unbind --project $ProjectId --workspace $previous.workspaceId --path $second | Out-Null
if ($LASTEXITCODE) { throw '并发解绑准备失败' }
$replacement = & $cli --state $state bind --project $ProjectId --path $second | ConvertFrom-Json
if ($LASTEXITCODE -or $replacement.workspaceId -eq $previous.workspaceId) { throw '重新绑定准备失败' }
Click $dialog 'ConfirmUnbind'
$null=Wait-Ui '旧确认不能删除新绑定' { (ById $main 'Notice').Current.Name -like '*绑定已变化*' -and (UnbindButton $second).Current.IsEnabled }
Pass '确认期间同一路径被改绑时，旧确认不会删除新绑定'
$dialog = OpenUnbind $second
$offline = Join-Path $root 'core-offline'
[IO.File]::WriteAllText($offline,'503')
try {
    Click $dialog 'ConfirmUnbind'
    $null=Wait-Ui '空目录提示' { (HasText $main '尚未绑定目录') -and (HasText $main '0 个目录') -and (ById $main 'BrowseButton').Current.IsEnabled }
} finally { Remove-Item -LiteralPath $offline }
Click $main 'RefreshButton'
$null=Wait-Ui '刷新后绑定仍为空' { !(UnbindButton $second) -and (ById $main 'RefreshButton').Current.IsEnabled }
Snapshot 'empty-directories'
Pass '点击取消保留绑定；确认时掉线仍可解除最后一行，恢复并刷新后显示 0 个目录，可继续添加目录'
