# Called by windows-access-gui.ps1 with an isolated paired Edge and loopback Core.
$workspace = [IO.Directory]::CreateDirectory((Join-Path $root 'ProjectHub')).FullName
$binding = & $cli --state $state bind --project $ProjectId --path $workspace | ConvertFrom-Json
if ($LASTEXITCODE -or !$binding.workspaceId) { throw 'Could not bind test workspace.' }
$reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$reservation.Start()
$port = $reservation.LocalEndpoint.Port
$reservation.Stop()
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Interface([string]$transport) {
    $info = [Diagnostics.ProcessStartInfo]::new($cli)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    foreach ($arg in @('--state',$state,$transport,'--project',$ProjectId,'--binding',$binding.bindingId,'--mode','readOnly','--workspace',$binding.workspaceId)) {
        $info.ArgumentList.Add([string]$arg)
    }
    if ($transport -eq 'mcp-http') {
        $info.Environment['PROJECTHUB_MCP_TOKEN'] = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        $info.ArgumentList.Add('--port')
        $info.ArgumentList.Add([string]$port)
    }
    $child = [Diagnostics.Process]::Start($info)
    $children.Add($child)
    return $child
}
function UiText([string]$id) { (ById $main $id).Current.Name }
function Check-OverviewInterfaces {
    if ((ById $main 'McpHttpStatus') -or (ById $main 'EdgeApiStatus') -or
        (HasText $main 'MCP · HTTP') -or (HasText $main 'Edge REST API')) {
        throw '设备概览显示了已移除的接口。'
    }
    if ((ById $main 'ConnectedCore') -or (ById $main 'ConnectionStatus') -or
        (HasText $main ([uri]$status.coreUrl).Authority)) {
        throw '概览仍显示具体 Core 地址或额外的本机在线状态。'
    }
    AssertContained $main (ById $main 'MachineName') '设备名称'
    AssertContained $main (ById $main 'CoreApiStatus') 'Core 状态'
    $openButton = ById $main 'OpenCoreButton'
    AssertContained $main $openButton '打开 Core'
    if (!$openButton.Current.IsEnabled) { throw '打开 Core 按钮不可用。' }
}
function ExpectText([string]$id, [string]$pattern) {
    $null = Wait-Ui "$id / $pattern" { (UiText $id) -match $pattern }
}
function Check-CoreHeader([double]$scale) {
    $title = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Core'))
    AssertContained $main $title 'Core 标题'
    $statusBox = (ById $main 'CoreApiStatus').Current.BoundingRectangle
    $deviceBox = (ById $main 'MachineName').Current.BoundingRectangle
    $refresh = (ById $main 'RefreshButton').Current.BoundingRectangle
    $open = (ById $main 'OpenCoreButton').Current.BoundingRectangle
    if ($deviceBox.Top -lt $statusBox.Bottom -or $statusBox.Right -gt $refresh.Left -or
        [Math]::Abs($open.Top - $refresh.Top) -gt 1 -or [Math]::Abs($open.Left-$refresh.Right-8*$scale) -gt 1) {
        throw 'Core 状态、设备辅助信息与操作的相对位置或按钮间距不正确。'
    }
}
function Check-OverviewActions([string]$capture, [double]$scale) {
    $ids = 'RefreshButton','OpenCoreButton','GlobalAccessButton','AccessSettingsButton','UnbindDirectoryButton','BrowseButton'
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute([intptr]$main.Current.NativeWindowHandle,9,[ref]$frame,16)) {
        throw '无法获取概览截图边界。'
    }
    $image = [Drawing.Bitmap]::new((Join-Path $ScreenshotDirectory "$capture.png"))
    try {
        foreach ($id in $ids) {
            $button = ById $main $id
            AssertContained $main $button $id
            $r = $button.Current.BoundingRectangle
            if ([Math]::Abs($r.Width-104*$scale) -gt 1 -or [Math]::Abs($r.Height-32*$scale) -gt 1) {
                throw "$capture 的 $id 尺寸为 $($r.Width) × $($r.Height) px，预期 104 × 32 DIP。"
            }
            $x = [int]($r.Left - $frame.Left + 5*$scale)
            $y = [int]($r.Top - $frame.Top + $r.Height/2)
            $fill = $image.GetPixel($x,$y).Name
            # The physical pointer may already be over the temporary test window.
            $expected = if ($id -eq 'BrowseButton') { 'ff174e9d','ff123f82' } else { 'fff4f7fa','ffedf3f7' }
            if ($fill -notin $expected) {
                throw "$capture 的 $id 底色 $fill 与预期的常态或悬停色不符。"
            }
        }
        $projectPicker = (ById $main 'ProjectList').Current.BoundingRectangle
        $title = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'授权项目')) |
            Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text } | Select-Object -First 1
        AssertContained $main $title '授权项目标题'
        $titleBox = $title.Current.BoundingRectangle
        if ($titleBox.Right -gt $projectPicker.Left -or [Math]::Abs($titleBox.Top+$titleBox.Height/2-$projectPicker.Top-$projectPicker.Height/2) -gt 2*$scale) {
            throw "$capture 项目标题和选择框未同行对齐。"
        }
        Check-CoreHeader $scale
        $stdioStatus = ById $main 'McpStdioStatus'
        AssertContained $main $stdioStatus 'MCP · STDIO 状态'
        if ($stdioStatus.Current.BoundingRectangle.Right -gt (ById $main 'GlobalAccessButton').Current.BoundingRectangle.Left) {
            throw "$capture 的 STDIO 状态覆盖智能体接入按钮。"
        }
        foreach ($pair in @(@('RefreshButton','OpenCoreButton'),@('AccessSettingsButton','BrowseButton'))) {
            $left = (ById $main $pair[0]).Current.BoundingRectangle
            $right = (ById $main $pair[1]).Current.BoundingRectangle
            $y = [int]($right.Top-$frame.Top+$right.Height/2)
            $start = [int]($left.Right - $frame.Left - 2)
            $end = [int]($right.Left - $frame.Left + 2)
            $background = $image.GetPixel([int](($start+$end)/2),$y).ToArgb()
            $gap = @($start..$end | Where-Object { $image.GetPixel($_,$y).ToArgb() -eq $background }).Count
            if ($gap -ne [Math]::Round(8*$scale)) { throw "$capture 的 $($pair[0]) 与 $($pair[1]) 之间实图留白 $gap px，应为 $(8*$scale) px。" }
        }
        Pass "$capture 项目选择器与标题同行，六个操作均为 104 × 32 DIP；五个次操作浅底，添加目录深蓝，Core 和目录两组按钮实图净距均为 $gap px"
    } finally { $image.Dispose() }
}
try {
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '^在线$'
    ExpectText 'McpStdioStatus' '无端口|无需端口|不占用端口'
    ExpectText 'McpStdioStatus' '按需启动'
    Check-OverviewInterfaces
    Pass '概览显示 Core 在线、设备名称和按需启动的 STDIO；保留打开 Core，隐藏其具体地址和本机在线徽标'
    [IO.File]::WriteAllText((Join-Path $root 'core-offline'),'delay')
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '^连接中$'
    ExpectText 'CoreApiStatus' '^在线$'
    [IO.File]::Delete((Join-Path $root 'core-offline'))
    Pass '刷新时先显示连接中，收到 Core 的成功响应后转为在线'

    $stdio = Start-Interface 'mcp-stdio'
    $stdio.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"interface-qa","version":"1"}}}')
    $reply = $stdio.StandardOutput.ReadLineAsync()
    if (!$reply.Wait(12000) -or ($reply.Result | ConvertFrom-Json).id -ne 1) { throw 'STDIO initialization failed.' }
    $listener = Start-Interface 'mcp-http'
    $null = Wait-Ui '真实 HTTP MCP 监听' {
        if ($listener.HasExited) { throw 'MCP HTTP process exited unexpectedly.' }
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/mcp" -SkipHttpErrorCheck -TimeoutSec 1
            $r.StatusCode -eq 401
        } catch { $false }
    }
    Click $main 'RefreshButton'
    Check-OverviewInterfaces
    if (HasText $main "127.0.0.1:$port/mcp") { throw '桌面概览公布了单独启动的 HTTP 监听。' }
    ExpectText 'McpStdioStatus' '1 个进程'
    AssertContained $main (ById $main 'OpenCoreButton') '打开 Core'
    AssertContained $main (ById $main 'AccessMode') '项目权限'
    $badge = (ById $main 'AccessMode').Current.BoundingRectangle
    $combo = (ById $main 'ProjectList').Current.BoundingRectangle
    if ([Math]::Abs(($badge.Top + $badge.Height/2) - ($combo.Top + $combo.Height/2)) -gt 3) { throw '授权徽标未与选择框垂直居中。' }
    Snapshot 'overview'
    if ($ScreenshotDirectory) {
        $factor = ([EdgeQaCapture]::GetDpiForWindow([intptr]$main.Current.NativeWindowHandle))/96
        $testedDpi = [int]($factor*96)
        Check-DirectoryActions $factor '默认窗口'
        Check-OverviewActions 'overview' $factor
        $size = $main.Current.BoundingRectangle.Size
        $h = [intptr]$main.Current.NativeWindowHandle
        if (![EdgeQaCapture]::Resize($h,460*$factor,565*$factor)) { throw '无法缩放测试窗口' }
        Start-Sleep -Milliseconds 250
        Check-DirectoryActions $factor '最窄窗口'
        AssertContained $main (ById $main 'AccessMode') '窄窗项目权限'
        AssertContained $main (ById $main 'OpenCoreButton') '窄窗打开 Core'
        Check-OverviewInterfaces
        Snapshot 'compact-overview'
        Check-OverviewActions 'compact-overview' $factor
        $null = [EdgeQaCapture]::Resize($h,$size.Width,$size.Height)
    }
    Pass '标准输入输出握手后显示 1 个 STDIO 进程，独立命令行 HTTP 的实际回环地址返回 401，概览不列出该地址'

    Click $main 'AccessSettingsButton'
    $access = Wait-Ui '接入管理' { AccessWindow }
    $null = Wait-Ui '进入当前项目和目录的设置' {
        (ById $access 'AccessProject').Current.Name -eq 'ProjectHub' -and
            (HasText (ById $access 'AccessDirectory') $workspace)
    }
    Click $access 'RefreshAccessButton'
    Click $access 'AccessCheckButton'
    $null = Wait-Ui '只读自检成功' { (UiText 'AccessResult') -like '*实际发现*工具*' }
    AssertContained $main (ById $access 'RefreshAccessButton') '刷新授权'
    AssertContained $main (ById $access 'AccessCheckButton') '只读自检'
    Snapshot 'access'
    Click $access 'CloseAccessButton'
    $null = Wait-Ui '返回选中项目的目录分组' {
        [bool](ById $main 'LanguageButton') -and (HasText (ById $main 'BoundDirectories') $workspace) -and
            (ById $main 'AccessSettingsButton').Current.IsEnabled
    }
    Pass '从目录标题行进入所选项目，设置页显示当前目录，返回保留相同目录列表和入口'
    Click $main 'GlobalAccessButton'
    $access = Wait-Ui '设备接入' { AccessWindow }
    Check-AccessMethods $access
    CheckScroll $access
    Snapshot 'device-access'
    Click $access 'CloseGlobalAccessButton'
    Pass '目录自检正常；设备接入页提供七个智能体与手动 STDIO 入口。'

    $listener.Kill()
    $listener.WaitForExit()
    $stdio.StandardInput.Close()
    if (!$stdio.WaitForExit(8000)) { $stdio.Kill(); $stdio.WaitForExit() }
    ExpectText 'McpStdioStatus' '按需启动'
    Pass 'STDIO 进程结束后，概览恢复按需启动状态'

    [IO.File]::WriteAllText((Join-Path $root 'core-offline'),'403')
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '未授权'
    Check-OverviewInterfaces
    [IO.File]::WriteAllText((Join-Path $root 'core-offline'),'503')
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '服务异常.*503'
    Check-OverviewInterfaces
    if ($ScreenshotDirectory) {
        $null = [EdgeQaCapture]::Resize($h,460*$factor,565*$factor)
        Start-Sleep -Milliseconds 250
        Check-CoreHeader $factor
        Snapshot 'compact-service-error'
        $null = [EdgeQaCapture]::Resize($h,$size.Width,$size.Height)
    }
    Click $main 'GlobalAccessButton'
    $deviceAccess = Wait-Ui 'Core 服务异常时打开本机智能体接入' { AccessWindow }
    $null = Wait-Ui '显示本机 Codex 配置状态' {
        (ById $deviceAccess 'HarnessStatus_codex').Current.Name -like '*尚未*' -and
            (ById $deviceAccess 'HarnessInstall_codex').Current.IsEnabled
    }
    Click $deviceAccess 'CloseGlobalAccessButton'
    Pass 'Core 服务异常期间，已配对设备仍可从概览进入智能体接入并读取本机配置状态'
    [IO.File]::WriteAllText((Join-Path $root 'core-offline'),'drop')
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '无法连接'
    Check-OverviewInterfaces
    [IO.File]::Delete((Join-Path $root 'core-offline'))
    Click $main 'RefreshButton'
    ExpectText 'CoreApiStatus' '^在线$'
    Check-OverviewInterfaces
    Pass 'Core 返回 403、503 和连接中断时，分别显示授权、服务和网络状态；恢复后显示在线'
} finally {
    foreach ($child in $children) {
        if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    [IO.File]::Delete((Join-Path $root 'core-offline'))
}
