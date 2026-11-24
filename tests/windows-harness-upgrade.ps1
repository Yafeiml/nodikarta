# Loaded by the disposable Windows desktop fixture.
$installation = [IO.Directory]::CreateDirectory((Join-Path $root 'Edge 安装')).FullName
$fixedCommand = Join-Path $installation 'ProjectHub.Edge.exe'
function Copy-PackagedRelease([string]$desktop) {
    $bin = (Get-Item -LiteralPath $desktop).Directory
    $manifest = Join-Path $bin.Parent.FullName 'SHA256SUMS'
    $id = (Get-FileHash -LiteralPath $manifest).Hash.Substring(0,12).ToLowerInvariant()
    $release = [IO.Directory]::CreateDirectory((Join-Path $installation $id)).FullName
    Copy-Item -LiteralPath $bin.FullName,$manifest -Destination $release -Recurse
    Join-Path $release 'bin/ProjectHub.Edge.Desktop.exe'
}
function Stop-FixtureWindow {
    if ($app) {
        if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
        $app.Dispose()
    }
}
function Check-HeaderSettings($main,[string]$page,[string]$language) {
    $settingsId = 'AppPreferencesButton'
    $idProperty = [Windows.Automation.AutomationElement]::AutomationIdProperty
    $tree = [Windows.Automation.TreeScope]::Descendants
    $buttons = $main.FindAll($tree,[Windows.Automation.PropertyCondition]::new($idProperty,$settingsId))
    $settings = ById $main $settingsId
    $languageButton = ById $main 'LanguageButton'
    if ($buttons.Count -ne 1 -or !$settings -or !$languageButton -or !$settings.Current.IsEnabled) {
        throw "$page/$language 应只有一个可用设置入口，且位于标题栏。"
    }
    $label = if ($language -eq 'en') { 'Settings' } else { '设置' }
    $button = $settings.Current.BoundingRectangle
    $lang = $languageButton.Current.BoundingRectangle
    if ($settings.Current.Name -cne $label -or $button.IsEmpty -or $lang.IsEmpty -or $button.Right -gt $lang.Left+1 -or
        [Math]::Abs(($button.Top+$button.Height/2)-($lang.Top+$lang.Height/2)) -gt 1) {
        throw "$page/$language 的设置按钮应位于语言左侧、文字正确且垂直居中。"
    }
    Click $main $settingsId
    $dialog = Wait-Ui '设备设置弹窗' {
        $byProcess = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
        foreach ($win in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byProcess)) {
            if (ById $win 'CloseAppPreferences') { return $win }
        }
    }
    if (!(ById $dialog 'StartOnLogin') -or !(ById $dialog 'AutomaticAppUpdates')) { throw '设置入口未打开设备偏好。' }
    Click $dialog 'CloseAppPreferences'
    $null = Wait-Ui '关闭设置弹窗后返回原页面' { $settings.Current.IsEnabled -and !(ById $main 'CloseAppPreferences') }
    Pass "$page/$language 的唯一设置入口位于标题栏语言按钮左侧，能打开并关闭设备设置。"
}
function Open-FixtureAccess([string]$language) {
    $main = Wait-Ui '设备概览' { MainWindow }
    if ($language) { Check-HeaderSettings $main 'overview' $language }
    if ($language -and $ScreenshotDirectory) { Save-WindowsTestWindow $main $ScreenshotDirectory "overview-$language" }
    Click $main 'GlobalAccessButton'
    Wait-Ui '智能体接入' { AccessWindow }
}
function Wait-Connected($view, [switch]$LegacyButton) {
    foreach ($harnessId in 'codex','claude','antigravity','cursor','opencode','deepseek','pi') {
        try {
            $null = Wait-Ui "$harnessId 已接入" {
                $button = ById $view "HarnessInstall_$harnessId"
                if ($LegacyButton) { $button -and $button.Current.Name -like '已接入 *' -and !$button.Current.IsEnabled }
                else { $button -and $button.Current.Name -like '断开 * 接入' -and $button.Current.IsEnabled -and
                    (ById $view "HarnessStatus_$harnessId").Current.Name -like '已接入*' }
            }
        } catch {
            $label = (ById $view "HarnessInstall_$harnessId").Current.Name
            $result = (ById $view 'AccessResult').Current.Name
            throw "Release $($start.FileName) reported '$label' after the batch. $result"
        }
    }
}
function Check-ListSurface($view, $frame, [double]$scale, [string]$imagePath) {
    $bmp = [Drawing.Bitmap]::FromFile($imagePath)
    try {
        $ids = @(Get-HarnessFixtureIds $view)
        $silhouettes = @{}
        for ($i=0; $i -lt $ids.Count; $i++) {
            $id = $ids[$i]
            $icon = (ById $view "HarnessIcon_$id").Current.BoundingRectangle
            $copy = (ById $view "HarnessCopy_$id").Current.BoundingRectangle
            $x = $icon.Left + $icon.Width/2 - $frame.Left
            $y = $icon.Top + $icon.Height/2 - $frame.Top
            if ([Math]::Abs($icon.Width-24*$scale) -gt 1 -or [Math]::Abs($icon.Height-24*$scale) -gt 1) {
                throw "$id 品牌图标的显示尺寸错误。"
            }
            $ink = foreach ($iy in 0..([int]$icon.Height-1)) {
                foreach ($ix in 0..([int]$icon.Width-1)) {
                    $color = $bmp.GetPixel([int]($icon.Left-$frame.Left)+$ix, [int]($icon.Top-$frame.Top)+$iy)
                    if ([Math]::Min($color.R,[Math]::Min($color.G,$color.B)) -lt 160) { $iy*[int]$icon.Width+$ix }
                }
            }
            $signature = $ink -join ','
            if (@($ink).Count -lt 12*$scale*$scale -or $silhouettes.ContainsKey($signature)) {
                throw "$id 图标没有绘制，或错误地复用了另一个品牌的图形。"
            }
            $silhouettes[$signature] = $id
            # Sample both sides of the logo, then the empty rounded corner and the adjacent card.
            foreach ($offset in -15,15) {
                if ($bmp.GetPixel([int]($x+$offset*$scale),[int]$y).Name -ne 'fffafafa') { throw "$id 图标浅灰底板未正确显示。" }
            }
            if ($bmp.GetPixel([int]($x-16.5*$scale),[int]($y-16.5*$scale)).Name -ne 'ffffffff' -or
                $bmp.GetPixel([int]($x+20*$scale),[int]$y).Name -ne 'ffffffff') { throw "$id 底板圆角或行底色异常。" }
            $radius = [int][Math]::Ceiling(20*$scale)
            $tileWidth = @((-$radius)..$radius | Where-Object { $bmp.GetPixel([int]$x+$_,[int]$y).Name -ne 'ffffffff' }).Count
            $tileHeight = @((-$radius)..$radius | Where-Object { $bmp.GetPixel([int]$x,[int]$y+$_).Name -ne 'ffffffff' }).Count
            if ([Math]::Abs($tileWidth-36*$scale) -gt 1 -or [Math]::Abs($tileHeight-36*$scale) -gt 1) {
                throw "$id 测得图标底板 $tileWidth × $tileHeight px，与 36 DIP 不符。"
            }
            if ($id -in 'codex','pi') {
                $topOrBottom = [int][Math]::Round($y + $(if ($id -eq 'codex') { -26 } else { 26 })*$scale)
                $center = [int](($copy.Right-$frame.Left+$x)/2)
                $outerLine = @(($topOrBottom-4)..($topOrBottom+4) | Where-Object { $bmp.GetPixel($center,$_).Name -eq 'ffe5e5e5' })
                if ($outerLine.Count -lt 1 -or $outerLine.Count -gt [Math]::Ceiling($scale)) { throw "$id 列表首尾边线异常或重复。" }
                $direction = if ($id -eq 'codex') { 1 } else { -1 }
                $edgeY = if ($id -eq 'codex') { $outerLine[0] } else { $outerLine[-1] }
                $edges = [int]($x-35*$scale),[int]($copy.Right-$frame.Left+17*$scale-1)
                foreach ($side in 0,1) {
                    $inward = 1-2*$side
                    foreach ($distance in 2,3,4) {
                        $scanY = $edgeY + $direction*[int]($distance*$scale)
                        $arc = @(0..([int](8*$scale)) | Where-Object {
                            $bmp.GetPixel($edges[$side]+$inward*$_,$scanY).G -le 243
                        })
                        if (!$arc.Count) { throw "$id 列表圆弧已被行底色遮挡。" }
                    }
                }
            }
            if ($i -gt 0) {
                $previousIcon = (ById $view "HarnessIcon_$($ids[$i-1])").Current.BoundingRectangle
                $previousY = $previousIcon.Top+$previousIcon.Height/2-$frame.Top
                $mid = [int][Math]::Floor(($previousY+$y)/2)
                $left = [int]($x-31*$scale)
                $right = [int]($copy.Right-$frame.Left+13*$scale)
                $line = @((($mid-2)..($mid+2)) | Where-Object {
                    $bmp.GetPixel($left,$_).Name -eq 'ffe5e5e5' -and $bmp.GetPixel($right,$_).Name -eq 'ffe5e5e5' -and
                    $bmp.GetPixel([int](($left+$right)/2),$_).Name -eq 'ffe5e5e5'
                })
                if ($line.Count -lt 1 -or $line.Count -gt [Math]::Ceiling($scale)) { throw "$id 上方应有一条延伸至行两侧的 1 DIP 细分隔线。" }
            }
        }
    } finally { $bmp.Dispose(); Set-HarnessFixtureScroll $view 0 }
}

function Check-RowLayout($view, [string]$language) {
    $expected = @{codex='Codex';claude='Claude';antigravity='Antigravity';cursor='Cursor';opencode='OpenCode';deepseek='DeepSeek';pi='Pi'}
    $ids = @(Get-HarnessFixtureIds $view)
    $list = ById $view 'HarnessRows'
    Set-HarnessFixtureScroll $view 0
    for ($i=0; $i -lt $ids.Count; $i++) {
        $id = $ids[$i]
        if ($i -eq 4) { Set-HarnessFixtureScroll $view 100 }
        $label = $list.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$expected[$id]))
        $button = (ById $view "HarnessInstall_$id").Current.BoundingRectangle
        if (!$label -or $label.Current.ControlType -ne [Windows.Automation.ControlType]::Text -or
            $label.Current.BoundingRectangle.Left -ge $button.Left -or
            [Math]::Abs($label.Current.BoundingRectangle.Bottom - ($button.Top + $button.Height/2)) -gt $button.Height/2) {
            throw "$language 的 $id 行未显示标题 $($expected[$id])。"
        }
    }
    Set-HarnessFixtureScroll $view 0
    Pass "$language 客户端标题按行核对通过：$($expected[$ids] -join '、')。"
    if (!$ScreenshotDirectory) { return }
    $window = MainWindow
    $handle = [intptr]$window.Current.NativeWindowHandle
    $size = $window.Current.BoundingRectangle.Size
    Save-WindowsTestWindow $window $ScreenshotDirectory "clients-$language-default"
    $frame = [EdgeQaCapture+Rect]::new()
    $null = [EdgeQaCapture]::DwmGetWindowAttribute($handle,9,[ref]$frame,16)
    $close = (ById $view 'HideAccessWindow').Current.BoundingRectangle
    $title = (ById $view 'DeviceAccessHeading').Current.Name
    $titlePeers = $view.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$title))
    if (!$title -or @($titlePeers | Where-Object { !$_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Top -lt $close.Bottom }).Count) {
        throw '重复的接入标题仍在标题栏内。'
    }
    $glyphs = foreach ($file in "overview-$language","clients-$language-default") {
        $bmp = [Drawing.Bitmap]::FromFile((Join-Path $ScreenshotDirectory "$file.png"))
        try {
            $ink = foreach ($y in [int]($close.Top-$frame.Top)..([int]($close.Bottom-$frame.Top)-1)) {
                foreach ($x in [int]($close.Left-$frame.Left)..([int]($close.Right-$frame.Left)-1)) {
                    $c = $bmp.GetPixel($x,$y)
                    if ($c.R -lt 150 -and $c.G -lt 160 -and $c.B -lt 175) { "$x,$y" }
                }
            }
            if ($ink.Count -lt 10) { throw "缺少关闭图标像素 $file" }
            $ink -join '|'
        } finally { $bmp.Dispose() }
    }
    if ($glyphs[0] -cne $glyphs[1]) { throw "$language 两页关闭图标轮廓或位置不同。" }
    Pass "$language 概览和接入页关闭图标的 $($ink.Count) 个深色像素位置完全相同；接入标题只在正文。"
    $scale = [EdgeQaCapture]::GetDpiForWindow($handle) / 96
    try {
        foreach ($layout in 'default','narrow') {
            if ($layout -eq 'narrow') {
                if (![EdgeQaCapture]::Resize($handle,[int](474*$scale),[int]$size.Height)) { throw 'Could not resize test window' }
                $null = Wait-Ui '窄窗口尺寸' { $window.Current.BoundingRectangle.Width -le 480*$scale }
                Save-WindowsTestWindow $window $ScreenshotDirectory "clients-$language-narrow"
            }
            Check-HarnessLayout $view (96*$scale)
            $null = [EdgeQaCapture]::DwmGetWindowAttribute($handle,9,[ref]$frame,16)
            Check-ListSurface $view $frame $scale (Join-Path $ScreenshotDirectory "clients-$language-$layout.png")
        }
    } finally { $null = [EdgeQaCapture]::Resize($handle,[int]$size.Width,[int]$size.Height) }
}
$files = @('.codex/config.toml','.claude.json','.gemini/config/mcp_config.json','.cursor/mcp.json',
    '.config/opencode/opencode.json','.dsh/cordis.patch.yml','.pi/agent/mcp.json') | ForEach-Object { Join-Path $testUserHome $_ }
function Test-CommandFiles([string]$command) {
    foreach ($file in $files) {
        $content = [IO.File]::ReadAllText($file)
        $configured = switch ([IO.Path]::GetExtension($file)) {
            '.toml' { [regex]::Match($content, '(?m)^command = (".*")\s*$').Groups[1].Value | ConvertFrom-Json }
            '.yml'  { [regex]::Match($content, "(?m)^\s+command: '([^']*)'\s*$").Groups[1].Value }
            '.json' {
                $json = $content | ConvertFrom-Json
                if ($json.mcp) { $json.mcp.projecthub.command[0] } else { $json.mcpServers.projecthub.command }
            }
        }
        if ($configured -ne [IO.Path]::GetFullPath($command)) { throw "Unexpected MCP command in $file : $configured" }
    }
}
$previousExe = Copy-PackagedRelease $UpgradeFrom
$newExe = Copy-PackagedRelease $exe
foreach ($name in 'agy','dsh','pi') { [IO.File]::WriteAllText((Join-Path $launchers "$name.cmd"), '@exit /b 1') }
$start.ArgumentList.Add('--state')
$start.ArgumentList.Add($state)
[IO.File]::WriteAllText((Join-Path $state 'ui-language.txt'), 'zh-CN')
$app = $null
try {
    $start.FileName = $previousExe
    $app = [Diagnostics.Process]::Start($start)
    $access = Open-FixtureAccess
    Click $access 'InstallDetectedHarnesses'
    Wait-Connected $access -LegacyButton
    Test-CommandFiles (Join-Path (Split-Path $previousExe) 'ProjectHub.Edge.exe')
    Stop-FixtureWindow
    $app = $null

    $newBuild = (Get-Item -LiteralPath $newExe).Directory.Parent.Name
    Copy-Item -LiteralPath (Join-Path (Split-Path $newExe) 'Nodicarta.Edge.Launcher.exe') -Destination $fixedCommand
    [IO.File]::WriteAllText((Join-Path $installation 'active-release'),$newBuild,[Text.Encoding]::ASCII)
    $start.FileName = $newExe
    $start.ArgumentList.Clear()
    foreach ($arg in '--refresh-harness-registration',$installation,'--state',$state) { $start.ArgumentList.Add($arg) }
    $refresh = [Diagnostics.Process]::Start($start)
    try { if (!$refresh.WaitForExit(30000) -or $refresh.ExitCode) { throw 'Upgrade registration failed' } }
    finally { $refresh.Dispose() }
    Test-CommandFiles $fixedCommand
    $hashes = ($files | Get-FileHash).Hash
    $start.ArgumentList.Clear()
    $start.ArgumentList.Add('--state')
    $start.ArgumentList.Add($state)
    $app = [Diagnostics.Process]::Start($start)
    $access = Open-FixtureAccess 'zh-CN'
    Wait-Connected $access
    if ((ById $access 'InstallDetectedHarnesses').Current.IsEnabled) { throw 'Bulk action enabled after upgrade' }
    if (Compare-Object $hashes (($files | Get-FileHash).Hash)) { throw 'Reopening Edge rewrote migrated settings' }
    if ($ScreenshotDirectory) {
        $main = MainWindow
        Check-RowLayout $access 'zh-CN'
        $testedDpi = [EdgeQaCapture]::GetDpiForWindow([intptr]$main.Current.NativeWindowHandle)
    }
    Pass '七项接入由旧版本实际创建。旧程序仍可运行时，升级刷新自动迁移七份配置；打开新版全部显示已接入，不需要点更新或切换。'
    $skillFiles = @(Get-ChildItem -LiteralPath (Join-Path $testUserHome '.agents/skills') -Directory -Filter 'nodicarta-*' |
        Sort-Object Name | ForEach-Object { Join-Path $_.FullName 'SKILL.md' })
    if ($skillFiles.Count -ne 5) { throw 'Expected five bundled Skills' }
    $skillHashes = ($skillFiles | Get-FileHash).Hash
    function Disconnect-Dialog {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))
        foreach ($window in $windows) { if (ById $window 'ConfirmHarnessDisconnect') { return $window } }
    }
    foreach ($button in 'CancelHarnessDisconnect','ConfirmHarnessDisconnect') {
        if ($button -eq 'CancelHarnessDisconnect' -and $ScreenshotDirectory) {
            . (Join-Path $PSScriptRoot 'WindowsPointerInput.ps1')
            $hwnd = [intptr]$main.Current.NativeWindowHandle
            $target = ById $access 'HarnessInstall_codex'
            $r = $target.Current.BoundingRectangle
            $frame = [EdgeQaCapture+Rect]::new()
            $null = [EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)
            $previousPointer = [long]0
            $null = [EdgeQaInput]::GetPhysicalCursorPos([ref]$previousPointer)
            function Check-Fill($name,$expected) {
                Save-WindowsTestWindow $main $ScreenshotDirectory "disconnect-$name"
                $bmp = [Drawing.Bitmap]::FromFile((Join-Path $ScreenshotDirectory "disconnect-$name.png"))
                try {
                    $sample = $bmp.GetPixel([int]($r.Left-$frame.Left+7),[int]($r.Top-$frame.Top+$r.Height/2)).Name
                    if ($sample -ne $expected) { throw "断开按钮 $name 颜色为 $sample，应为 $expected。" }
                } finally { $bmp.Dispose() }
            }
            try {
                $null = [EdgeQaInput]::SetForegroundWindow($hwnd)
                PointAt (ById $access 'DeviceAccessHeading')
                Check-Fill 'normal' 'fffff3f1'
                PointAt $target
                Check-Fill 'hover' 'fffbe6e4'
                PointAt $target -WhilePressed { Check-Fill 'pressed' 'fff6d5d1' }
            } finally { $null = [EdgeQaInput]::SetPhysicalCursorPos(($previousPointer -shl 32 -shr 32), ($previousPointer -shr 32)) }
            Pass '实际指针验证断开按钮默认、悬停、按下三态底色为 #FFF3F1、#FBE6E4、#F6D5D1。'
        } else { Click $access 'HarnessInstall_codex' }
        $dialog = Wait-Ui 'Codex 断开确认' { Disconnect-Dialog }
        if ((ById $dialog 'DisconnectHarnessHeading').Current.Name -ne '断开 Codex 接入？' -or
            !(ById $dialog 'CancelHarnessDisconnect').Current.HasKeyboardFocus) { throw 'Disconnect dialog target or focus is wrong' }
        if ($button -eq 'CancelHarnessDisconnect' -and $ScreenshotDirectory) {
            Save-WindowsTestWindow $dialog $ScreenshotDirectory 'disconnect-codex'
        }
        Click $dialog $button
        $null = Wait-Ui '返回接入列表' { !(Disconnect-Dialog) -and (ById $access 'HarnessInstall_codex').Current.IsEnabled }
        if ($button -eq 'CancelHarnessDisconnect') {
            Wait-Connected $access
            if ((Get-FileHash -LiteralPath $files[0]).Hash -ne $hashes[0]) { throw 'Cancel modified Codex settings' }
        }
    }
    function Assert-Disconnected($view, [string]$connectName) {
        $null = Wait-Ui 'Codex 已断开' { (ById $view 'HarnessInstall_codex').Current.Name -ceq $connectName }
        if ((ById $view 'HarnessStatus_codex').Current.Name -match '^(已接入|Connected)' -or
            [IO.File]::ReadAllText($files[0]) -match '\[mcp_servers\.(projecthub|nodicarta)\]' -or
            (Compare-Object $skillHashes (($skillFiles | Get-FileHash).Hash))) {
            throw 'Disconnect did not preserve the expected configuration and shared Skills'
        }
        if (Compare-Object $hashes[1..6] (($files[1..6] | Get-FileHash).Hash)) { throw 'Disconnect changed another client' }
    }
    Assert-Disconnected $access '接入 Codex'
    Stop-FixtureWindow
    [IO.File]::WriteAllText((Join-Path $state 'ui-language.txt'), 'en')
    $app = [Diagnostics.Process]::Start($start)
    $access = Open-FixtureAccess 'en'
    Assert-Disconnected $access 'Connect Codex'
    if ((ById $access 'HarnessInstall_claude').Current.Name -cne 'Disconnect Claude' -or
        (ById $access 'HarnessStatus_claude').Current.Name -notlike 'Connected*') { throw 'Invalid English row labels' }
    Check-RowLayout $access 'en'
    if ($ScreenshotDirectory) { Pass '中英文在默认和窄窗口共四次实测，各行均为 52 DIP，24 DIP 图标居于浅灰圆角底板内。1 DIP 中性灰分隔线贯穿列表，主操作和复制列对齐，间距 8 DIP。' }
    Pass '断开确认默认焦点为取消。取消保留原配置；确认只断开 Codex，重启后仍然断开，其他六项与五项通用 Skills 保留。'
    Stop-FixtureWindow
    $app = $null
    $unpaired = Join-Path $root 'unpaired-settings'
    $start.ArgumentList.Clear()
    foreach ($arg in '--state',$unpaired) { $start.ArgumentList.Add($arg) }
    $app = [Diagnostics.Process]::Start($start)
    $main = Wait-Ui '隔离的配对页' { $w=MainWindow; if ($w -and (ById $w 'PairButton')) { $w } }
    $pairingLanguage = if ((ById $main 'LanguageButton').Current.Name -eq 'Language') { 'en' } else { 'zh-CN' }
    Check-HeaderSettings $main 'pairing' $pairingLanguage
} finally { Stop-FixtureWindow }
