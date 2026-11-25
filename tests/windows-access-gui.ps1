#requires -Version 7.0
# Supply three disposable CLI-paired states under a fresh projecthub-accessqa-<GUID> temp folder.
# Each has the same project with respectively no grant, a read-only grant, or a read/write grant.
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][guid]$ProjectId,
    [string]$EvidenceFile,
    [string]$ScreenshotDirectory,
    [string]$FixtureDescription = '隔离回环 Core',
    [switch]$InterfaceStatus,
    [switch]$ActionDetails,
    [switch]$Unbind,
    [switch]$Antigravity,
    [string]$PreviousAntigravityCli,
    [switch]$CodexWindows,
    [switch]$SkillsTab,
    [switch]$BackCard,
    [switch]$Language,
    [switch]$BrandIcons,
    [switch]$TitleMarksOnly,
    [switch]$Navigation,
    [switch]$NavigationBaseline,
    [string]$UpgradeFrom,
    [switch]$ChangeCore
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
. (Join-Path $PSScriptRoot 'Save-WindowsTestWindow.ps1')
$root = (Resolve-Path -LiteralPath $RunRoot).Path
if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
    [IO.Path]::GetFileName($root) -notmatch '^projecthub-accessqa-[a-f0-9]{32}$') { throw 'A disposable fixture root is required.' }
$exe = (Resolve-Path -LiteralPath $Executable).Path
$payload = [IO.Path]::GetDirectoryName($exe)
$cli = Join-Path $payload 'ProjectHub.Edge.exe'
$checks = [Collections.Generic.List[string]]::new()
function Wait-Ui([string]$name,[scriptblock]$body) {
    $end = [DateTime]::UtcNow.AddSeconds(25)
    do {
        try { $found = & $body }
        catch [Windows.Automation.ElementNotAvailableException] { $found = $null }
        catch [System.Runtime.InteropServices.COMException] { $found = $null }
        if ($found) { return $found }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $end)
    throw "找不到预期界面状态：$name"
}
function ById($parent,$id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function MainWindow {
    $cond = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    foreach ($win in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$cond)) {
        if ($win.Current.AutomationId -eq 'EdgeMainWindow') { return $win }
    }
}
function AccessWindow {
    ById $main 'AccessWindow'
}
function Click($parent,$id) {
    $el = Wait-Ui $id { $el = ById $parent $id; if ($el -and $el.Current.IsEnabled) { $el } }
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function HasText($parent,[string]$text) {
    $parent.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like "*$text*" } | Select-Object -First 1
}
function SelectOption($parent,[string]$id,[string]$name) {
    $combo = Wait-Ui "$id 可选择" { $el = ById $parent $id; if ($el -and $el.Current.IsEnabled) { $el } }
    $expand = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $label = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
    $item = Wait-Ui $name {
        if ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Collapsed) { $expand.Expand() }
        foreach ($el in $combo.FindAll([Windows.Automation.TreeScope]::Descendants,$label)) {
            $pattern = $null
            if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { return $pattern }
        }
    }
    $item.Select()
    $expand.Collapse()
}
function Snapshot([string]$name) {
    if ($ScreenshotDirectory) {
        Start-Sleep -Milliseconds 250
        Save-WindowsTestWindow $main $ScreenshotDirectory $name
    }
}
function CheckScroll($parent) {
    $active = @()
    foreach ($el in $parent.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
        $scroll = $null
        if (!$el.Current.IsOffscreen -and $el.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scroll) -and $scroll.Current.VerticallyScrollable) {
            $active += $el
        }
    }
    foreach ($el in $active) {
        $p = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($el)
        while ($p -and $p -ne $parent) {
            if ($active -contains $p) { throw "检测到嵌套滚动区域 $($el.Current.AutomationId)" }
            $p = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($p)
        }
    }
}
function AssertContained($window,$element,[string]$label) {
    if (!$element -or $element.Current.IsOffscreen) { throw "$label 在首屏不可见" }
    $bounds = $window.Current.BoundingRectangle
    $rect = $element.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0 -or
        $rect.Left -lt $bounds.Left-1 -or $rect.Right -gt $bounds.Right+1 -or
        $rect.Top -lt $bounds.Top-1 -or $rect.Bottom -gt $bounds.Bottom+1) { throw "$label 超出窗口" }
}
function Check-DirectoryActions([double]$scale,[string]$name) {
    $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text))
    $heading = $texts | Where-Object { $_.Current.Name -eq '本机目录绑定' } | Select-Object -First 1
    $count = $texts | Where-Object { $_.Current.Name -match '^\d+ 个目录$' } | Select-Object -First 1
    $row = @($heading, $count, (ById $main 'AccessSettingsButton'), (ById $main 'BrowseButton'))
    if ($row[3].Current.Name -cne '添加目录') { throw '目录主操作的读屏名称应为「添加目录」。' }
    foreach ($element in $row) { AssertContained $main $element '目录标题、计数及操作' }
    $boxes = @($row | ForEach-Object { $_.Current.BoundingRectangle })
    $centers = @($boxes | ForEach-Object { $_.Top + $_.Height/2 })
    $spread = ($centers | Measure-Object -Maximum).Maximum - ($centers | Measure-Object -Minimum).Minimum
    $gap = $boxes[3].Left - $boxes[2].Right
    if ($spread -gt 2*$scale -or [Math]::Abs($gap-8*$scale) -gt 1 -or
        $boxes[0].Right -gt $boxes[1].Left -or $boxes[1].Right + 7*$scale -gt $boxes[2].Left + 1 -or
        (ById $main 'ProjectList').Current.BoundingRectangle.Bottom -gt $boxes[2].Top) {
        throw "$name 目录行位置或间距异常，按钮净距 $gap px，四个中心跨度 $spread px。"
    }
    Pass "$name 本机目录标题、计数、目录设置、添加目录依次排列，按钮净距 $gap px，四个中心跨度 $spread px"
}
function Pass($text) { Write-Host "PASS $text"; $checks.Add($text) }
. (Join-Path $PSScriptRoot 'windows-device-access.ps1')

foreach ($mode in 'none','readOnly','readWrite') {
    if ($UpgradeFrom -and $mode -ne 'readWrite') { continue }
    if (($InterfaceStatus -or $ActionDetails -or $Unbind -or $Antigravity -or $CodexWindows -or $BackCard -or $ChangeCore -or $Language -or $BrandIcons -or $Navigation) -and $mode -ne 'readWrite') { continue }
    $state = Join-Path $root $mode
    $status = & $cli --state $state status | ConvertFrom-Json
    if ($LASTEXITCODE -or !$status.online -or !([uri]$status.coreUrl).IsLoopback -or ([uri]$status.coreUrl).Port -eq 29480) {
        throw 'An online isolated loopback Core is required.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $testUserHome = [IO.Directory]::CreateDirectory((Join-Path $root "profile-$mode")).FullName
    $start.Environment['USERPROFILE'] = $testUserHome
    $start.Environment['HOME'] = $testUserHome
    $start.Environment['CODEX_HOME'] = Join-Path $testUserHome '.codex'
    $start.Environment['APPDATA'] = Join-Path $testUserHome 'AppData/Roaming'
    $start.Environment['LOCALAPPDATA'] = Join-Path $testUserHome 'AppData/Local'
    $start.Environment['XDG_CONFIG_HOME'] = Join-Path $testUserHome '.config'
    $start.Environment['PI_CODING_AGENT_DIR'] = Join-Path $testUserHome '.pi/agent'
    foreach ($key in 'OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT','CLAUDE_CONFIG_DIR','DSH_HOME') {
        $null = $start.Environment.Remove($key)
    }
    $launchers = [IO.Directory]::CreateDirectory((Join-Path $testUserHome 'bin')).FullName
    foreach ($name in 'codex','claude','cursor','opencode') {
        if ($CodexWindows -and $name -eq 'codex') { continue }
        [IO.File]::WriteAllText((Join-Path $launchers "$name.cmd"), '@exit /b 1')
    }
    if ($Antigravity) { [IO.File]::WriteAllText((Join-Path $launchers 'agy.cmd'), '@exit /b 1') }
    $start.Environment['PATH'] = "$launchers;$env:SystemRoot\System32"
    if ($UpgradeFrom) {
        . (Join-Path $PSScriptRoot 'windows-harness-upgrade.ps1')
        continue
    }
    $opencodeConfig = Join-Path $testUserHome '.config/opencode/opencode.json'
    $null = [IO.Directory]::CreateDirectory((Split-Path $opencodeConfig))
    @{mcp=@{projecthub=@{type='local';command=@((Join-Path $testUserHome 'old/ProjectHub.Edge.exe'),'mcp-stdio-global','--state',$state)}}} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $opencodeConfig -Encoding utf8
    $cursorFile = Join-Path $testUserHome '.cursor/mcp.json'
    $null = [IO.Directory]::CreateDirectory((Split-Path $cursorFile))
    $custom = '{"mcpServers":{"projecthub":{"command":"custom-server"}}}'
    [IO.File]::WriteAllText($cursorFile,$custom)
    $start.ArgumentList.Add('--state')
    $start.ArgumentList.Add($state)
    [IO.File]::WriteAllText((Join-Path $state 'ui-language.txt'), 'zh-CN')
    $app = [Diagnostics.Process]::Start($start)
    try {
        $main = Wait-Ui '主窗口' { MainWindow }
        if ($Navigation) {
            . (Join-Path $PSScriptRoot 'windows-navigation.ps1')
            continue
        }
        if ($BrandIcons) {
            . (Join-Path $PSScriptRoot 'windows-brand-gui.ps1')
            continue
        }
        if ($Language) {
            . (Join-Path $PSScriptRoot 'windows-language.ps1')
            continue
        }
        if ($ChangeCore) {
            . (Join-Path $PSScriptRoot 'windows-change-core.ps1')
            continue
        }
        if ($BackCard) {
            . (Join-Path $PSScriptRoot 'windows-access-back-card.ps1')
            continue
        }
        if ($CodexWindows) {
            Click $main 'GlobalAccessButton'
            $access = Wait-Ui '智能体接入' { AccessWindow }
            $null = Wait-Ui '从 Windows 已安装程序识别 Codex' {
                $row = ById $access 'HarnessStatus_codex'
                $row.Current.Name -like '已安装*' -and (ById $access 'HarnessInstall_codex').Current.IsEnabled
            }
            Snapshot 'windows-before'
            Click $access 'HarnessInstall_codex'
            $null = Wait-Ui '当前用户 Codex 已接入' {
                (ById $access 'HarnessInstall_codex').Current.Name -eq '断开 Codex 接入' -and
                (ById $access 'AccessResult').Current.Name -like '*已接入当前用户的 Codex*'
            }
            $file = Join-Path $start.Environment['CODEX_HOME'] 'config.toml'
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw '没有在隔离账户写入 Codex 配置。' }
            Snapshot 'windows-after'
            if ($ScreenshotDirectory) { $testedDpi = [EdgeQaCapture]::GetDpiForWindow([intptr]$main.Current.NativeWindowHandle) }
            Pass '实际桌面包在 PATH 中没有 Codex 和 PowerShell、临时用户没有启动器时，识别到已安装的 Codex 并完成单项接入。'
            continue
        }
        if ($Antigravity) {
            Click $main 'GlobalAccessButton'
            $access = Wait-Ui '智能体接入' { AccessWindow }
            $testedDpi = Check-AntigravityGuidance $access
            if ($PreviousAntigravityCli) {
                $upgrade = [IO.Directory]::CreateDirectory((Join-Path $root '原固定名称插件')).FullName
                $old = & $PreviousAntigravityCli --state $state bind --project $ProjectId --path $upgrade | ConvertFrom-Json
                $plugin = Join-Path $upgrade '.agents/plugins/projecthub'
                $manifest = Join-Path $plugin 'plugin.json'
                $config = Join-Path $plugin 'mcp_config.json'
                $guide = Join-Path $upgrade '.agents/rules/projecthub-workspace.md'
                $oldEntry = (Get-Content -Raw -LiteralPath $config | ConvertFrom-Json).mcpServers.workspace
                if ($LASTEXITCODE -or !$old.workspaceId -or $oldEntry.command -ne $cli -or
                    (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json).name -cne 'projecthub' -or
                    !(Get-Content -Raw -LiteralPath $guide).Contains('projecthub_workspace')) {
                    throw '旧版测试程序须生成指向同一 Edge 地址的旧命名插件。'
                }
                $app.Kill()
                $app.WaitForExit()
                $app.Dispose()
                $app = [Diagnostics.Process]::Start($start)
                $expectedServerKey = 'kb_' + ([guid]$old.workspaceId).ToString('N')
                $newPlugin = Join-Path $upgrade '.agents/plugins/nodicarta'
                $newGuide = Join-Path $upgrade '.agents/rules/nodicarta-workspace.md'
                $null = Wait-Ui '重启自动迁移旧名称、命令与查询指引' {
                    (Test-Path -LiteralPath $newGuide) -and !(Test-Path -LiteralPath $manifest) -and !(Test-Path -LiteralPath $guide) -and
                    ((Get-Content -Raw -LiteralPath (Join-Path $newPlugin 'plugin.json') | ConvertFrom-Json).name -ceq 'nodicarta') -and
                    ((Get-Content -Raw -LiteralPath (Join-Path $newPlugin 'mcp_config.json') | ConvertFrom-Json).mcpServers.$expectedServerKey.command -ceq $cli) -and
                    (Get-Content -Raw -LiteralPath $newGuide).Contains("nodicarta_${expectedServerKey}")
                }
                Pass '相同的 Edge 程序路径，启动时自动将旧插件的服务名称和查询指引迁移到当前目录专属名称。'
            }
            continue
        }
        if ($Unbind) {
            . (Join-Path $PSScriptRoot 'windows-unbind-gui.ps1')
            continue
        }
        if ($ActionDetails) {
            . (Join-Path $PSScriptRoot 'windows-action-details.ps1')
            continue
        }
        if ($InterfaceStatus) {
            . (Join-Path $PSScriptRoot 'windows-interface-status.ps1')
            continue
        }
        if ($mode -eq 'none') {
            $hint = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'暂无授权项目。请管理员在 Core 中授权后刷新。')
            $null = Wait-Ui '未授权提示' { $main.FindFirst([Windows.Automation.TreeScope]::Descendants,$hint) }
            if (ById $main 'AccessSettingsButton') { throw '无授权时呈现了项目目录设置。' }
            Click $main 'GlobalAccessButton'
            $access = Wait-Ui '未授权项目时的设备级智能体接入' { AccessWindow }
            Check-AccessMethods $access
            Check-DevicePage $access 'Clients'
            if (ById $access 'AccessDirectory') { throw '设备级接入显示了目录选择器。' }
            Click $access 'ConfigFormatSkills'
            Check-DevicePage $access 'Skills'
            Check-SkillsCount $access 0
            $stdio = Show-DeviceConfig $access 'Stdio'
            Check-DevicePage $access 'Stdio'
            if (!$SkillsTab) { Check-StdioCopy $access $stdio }
            CheckScroll $access
            Snapshot 'stdio'
            if ($ScreenshotDirectory) {
                $hwnd = [intptr]$main.Current.NativeWindowHandle
                $dpi = [EdgeQaCapture]::GetDpiForWindow($hwnd)
                $originalSize = $main.Current.BoundingRectangle.Size
                $gaps = foreach ($width in $null, 506) {
                    if ($width) {
                        $null = [EdgeQaCapture]::Resize($hwnd,$width*$dpi/96,540*$dpi/96)
                        Start-Sleep -Milliseconds 250
                        Snapshot 'compact-stdio'
                    }
                    $last = $null
                    foreach ($page in 'Clients','Stdio','Skills') {
                        $tab = ById $access "ConfigFormat$page"
                        AssertContained $main $tab "$page 页签"
                        $rect = $tab.Current.BoundingRectangle
                        if ($last) {
                            $gap = if ([Math]::Abs($last.Top-$rect.Top) -le 1) { $rect.Left-$last.Right } else { $rect.Top-$last.Bottom }
                            if ($gap -le 0 -or [Math]::Abs($gap-2*$dpi/96) -gt 1) { throw "页签边缘间距异常 $gap px。" }
                            $gap
                        }
                        $last = $rect
                    }
                    foreach ($page in 'Clients','Stdio','Skills') {
                        Click $access "ConfigFormat$page"
                        Check-DevicePage $access $page
                    }
                    foreach ($id in 'SkillsCount','SkillsLocalVersion','SkillsBundleVersion','CheckSkillsButton','UpdateSkillsButton','AutoUpdateSkillsBox') {
                        AssertContained $main (ById $access $id) $id
                    }
                    Snapshot $(if ($width) { 'skills-narrow' } else { 'skills-default' })
                    Click $access 'ConfigFormatStdio'
                }
                $null = [EdgeQaCapture]::Resize($hwnd,$originalSize.Width,$originalSize.Height)
                Pass "三个页签在默认和窄窗往返切换，内容互斥，Skills 数量、版本、更新操作均在首屏；相邻控件边缘净距 $($gaps -join ' / ') 像素（${dpi} DPI）"
            }
            Click $access 'ConfigFormatClients'
            Check-DevicePage $access 'Clients'
            $null = Wait-Ui 'OpenCode 已自动修复已删除的旧程序路径' {
                (ById $access 'HarnessInstall_opencode').Current.Name -eq '断开 OpenCode 接入' -and
                (Get-Content -Raw $opencodeConfig | ConvertFrom-Json).mcp.projecthub.command[0] -eq $cli
            }
            if ($ScreenshotDirectory) {
                Check-HarnessLayout $access $dpi
                Snapshot 'harnesses-before'
                $null = [EdgeQaCapture]::Resize($hwnd,506*$dpi/96,540*$dpi/96)
                Start-Sleep -Milliseconds 250
                Check-HarnessLayout $access $dpi
                Snapshot 'harnesses-narrow'
                $null = [EdgeQaCapture]::Resize($hwnd,$originalSize.Width,$originalSize.Height)
            }
            Click $access 'HarnessInstall_codex'
            $config = Join-Path $testUserHome '.codex/config.toml'
            $skillRoot = Join-Path $testUserHome '.agents/skills'
            $null = Wait-Ui '全局 MCP 与五个用户 Skills' {
                (Test-Path -LiteralPath $config) -and
                @(Get-ChildItem -LiteralPath $skillRoot -Filter 'nodicarta-*' -Directory -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') }).Count -eq 5
            }
            $toml = Get-Content -Raw -LiteralPath $config
            if ($toml -notmatch 'mcp-stdio-global' -or $toml -match '\-\-project|\-\-workspace|(?m)^cwd\s*=') {
                throw '全局配置未使用按聊天目录路由的启动方式。'
            }
            if (!$SkillsTab) {
                Check-CopiedConfig $access 'HarnessCopy_codex' ($toml.TrimEnd())
                Check-HarnessCopies $access
            }
            Click $access 'InstallDetectedHarnesses'
            $null = Wait-Ui '批量接入后读回 Claude 和 OpenCode 配置' {
                (ById $access 'HarnessInstall_claude').Current.Name -eq '断开 Claude 接入' -and
                (ById $access 'HarnessInstall_opencode').Current.Name -eq '断开 OpenCode 接入' -and
                (ById $access 'AccessResult').Current.Name -like '*Cursor*'
            }
            $claudeConfig = Join-Path $testUserHome '.claude.json'
            $claude = Get-Content -Raw -LiteralPath $claudeConfig | ConvertFrom-Json
            if ($claude.mcpServers.nodicarta.command -ne $cli -or
                ($claude.mcpServers.nodicarta.args -join '|') -cne "mcp-stdio-global|--state|$state|--workspace-env|CLAUDE_PROJECT_DIR") {
                throw 'Claude Code 注册项未使用本次验收包和 CLI 工作目录参数。'
            }
            if ((Get-Content -Raw $opencodeConfig | ConvertFrom-Json).mcp.projecthub.command[0] -ne $cli) {
                throw 'OpenCode 旧版接入路径未更新。'
            }
            if ([IO.File]::ReadAllText($cursorFile) -cne $custom -or (ById $access 'HarnessInstall_cursor').Current.IsEnabled) {
                throw '批量接入未保护或报告已有的 Cursor 自定义配置。'
            }
            if ((ById $access 'HarnessStatus_pi').Current.Name -match '未检测到' -and
                (Test-Path (Join-Path $testUserHome '.pi/agent/mcp.json'))) { throw '未装 Pi 也写入了配置。' }
            Snapshot 'harnesses'
            Pass '各客户端均有独立状态和复制入口，单项接入及批量接入均通过。'
            if (!$SkillsTab) {
                $codeHash = (Get-FileHash -LiteralPath $claudeConfig).Hash
                $chatProfile = [IO.Directory]::CreateDirectory((Join-Path $start.Environment['APPDATA'] 'Claude')).FullName
                $chatConfig = Join-Path $chatProfile 'claude_desktop_config.json'
                if (Test-Path -LiteralPath $chatConfig) { throw '迟安装回归需要尚未接入 Claude 桌面版的隔离配置。' }
                $chatBefore = '{"mcpServers":{"fixture-existing":{"command":"unrelated-server"}}}'
                [IO.File]::WriteAllText($chatConfig,$chatBefore)
                $classic = [IO.Directory]::CreateDirectory((Join-Path $start.Environment['LOCALAPPDATA'] 'Programs/Claude')).FullName
                [IO.File]::WriteAllBytes((Join-Path $classic 'Claude.exe'),[byte[]]@())
                Click $access 'ConfigFormatStdio'
                Click $access 'ConfigFormatClients'
                $null = Wait-Ui '检测到 Claude 桌面版后，同一行可继续接入' {
                    $action = ById $access 'HarnessInstall_claude'
                    $action.Current.IsEnabled -and $action.Current.Name -ceq '接入 Claude' -and
                        (ById $access 'HarnessStatus_claude').Current.Name -like '部分已接入*'
                }
                $null = Get-HarnessFixtureIds $access
                if ([IO.File]::ReadAllText($chatConfig) -cne $chatBefore -or (Get-FileHash -LiteralPath $claudeConfig).Hash -ne $codeHash) {
                    throw '重新检测客户端时意外写入了 Claude 配置。'
                }
                Click $access 'HarnessInstall_claude'
                $null = Wait-Ui 'Claude 两个客户端均已接入' {
                    (ById $access 'HarnessInstall_claude').Current.Name -ceq '断开 Claude 接入' -and
                        (ById $access 'HarnessStatus_claude').Current.Name -like '已接入*'
                }
                $chatEntries = (Get-Content -Raw -LiteralPath $chatConfig | ConvertFrom-Json).mcpServers
                if ($chatEntries.nodicarta.command -ne $cli -or
                    ($chatEntries.nodicarta.args -join '|') -cne "mcp-stdio-global|--state|$state|--workspace-roots" -or
                    $chatEntries.'fixture-existing'.command -cne 'unrelated-server' -or
                    (Get-FileHash -LiteralPath $claudeConfig).Hash -ne $codeHash) {
                    throw 'Claude 接入应补接桌面版并保留 CLI 原配置及已有服务。'
                }
                Pass 'Claude CLI 已接入后新增桌面版，仍只有一个 Claude 品牌行，点击一次补接桌面版，CLI 配置逐字节保留。'
            }
            Check-DevicePage $access 'Clients'
            Check-DeviceSkills $access $skillRoot
            if ($SkillsTab -and $ScreenshotDirectory) { . (Join-Path $PSScriptRoot 'windows-skills-tab-input.ps1') }
            Click $access 'CloseGlobalAccessButton'
            $null = Wait-Ui '返回设备概览' { [bool](ById $main 'LanguageButton') -and (ById $main 'GlobalAccessButton') }
            Pass '无授权项目和目录时，在设备入口一次安装全局 MCP 和五项 Skills。'
            $app.Kill()
            $app.WaitForExit()
            $app.Dispose()
            $markers = @(Get-ChildItem -LiteralPath $skillRoot -Filter 'nodicarta-*' -Directory | ForEach-Object {
                Join-Path $_.FullName '.projecthub-managed'
            })
            $version = ([IO.File]::ReadAllText($markers[0])).Split(' ')[0]
            foreach ($marker in $markers) {
                $current = [IO.File]::ReadAllText($marker)
                [IO.File]::WriteAllText($marker,('0.8.0' + $current.Substring($version.Length)))
            }
            $app = [Diagnostics.Process]::Start($start)
            $main = Wait-Ui '重新打开临时设备' { MainWindow }
            $null = Wait-Ui '启动时自动同步 Skills' {
                @($markers | Where-Object { ![IO.File]::ReadAllText($_).StartsWith("$version ") }).Count -eq 0
            }
            Click $main 'GlobalAccessButton'
            $access = Wait-Ui '检查保存的自动更新选择' { AccessWindow }
            Click $access 'ConfigFormatSkills'
            Check-DevicePage $access 'Skills'
            Check-SkillsCount $access 5
            $switch = (Wait-Ui '自动更新' { ById $access 'AutoUpdateSkillsBox' }).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            if ($switch.Current.ToggleState -ne 'On') { throw '自动更新偏好未保存。' }
            Pass '重新启动临时设备后，进入设置前已自动更新五个 Skills，开关选择持久保存'
            continue
        }
        $browse = Wait-Ui '添加目录主操作' { ById $main 'BrowseButton' }
        if ($browse.Current.Name -cne '添加目录') { throw "目录主操作名称异常：$($browse.Current.Name)" }
        Click $main 'AccessSettingsButton'
        $null = Wait-Ui '未绑定目录时引导点击添加目录' {
            (ById $main 'Notice').Current.Name -like '*「添加目录」*绑定此项目的本机目录*'
        }
        if (AccessWindow) { throw 'Unexpected config editor without a grant and directory.' }
        $workspace = [IO.Directory]::CreateDirectory((Join-Path $root "workspace-$mode")).FullName
        & $cli --state $state bind --project $ProjectId --path $workspace | Out-Null
        if ($LASTEXITCODE) { throw 'Could not bind disposable directory.' }
        if ($mode -eq 'readWrite' -and $ScreenshotDirectory) {
            Click $main 'RefreshButton'
            $null = Wait-Ui '添加目录和解除绑定同时可见' {
                $remove = ById $main 'UnbindDirectoryButton'
                $remove -and !$remove.Current.IsOffscreen -and (ById $main 'BrowseButton').Current.Name -ceq '添加目录'
            }
            Snapshot 'overview-add-directory'
            Pass '设备概览显示「添加目录」，读屏名称一致，与已绑定目录的「解除绑定」同屏。'
        }
        Click $main 'AccessSettingsButton'
        $access = Wait-Ui '接入窗口' { AccessWindow }
        $null = Wait-Ui '设备窗口切换为目录设置' { $main.Current.Name -like '*目录设置*' }
        $null = Wait-Ui '自动匹配就绪' { (ById $access 'AccessCheckButton').Current.IsEnabled }
        foreach ($id in 'RefreshAccessButton','AccessScopeSummary','AdjustAccessScope','SaveWorkspacePolicyButton','AccessCheckButton') {
            AssertContained $main (ById $access $id) $id
        }
        CheckScroll $access
        foreach ($id in 'AccessConfigTab','CopyStdioButton','CopyConfigButton','ConfigFormatClients','ConfigFormatSkills','GlobalAccessCheckButton') {
            if (ById $access $id) { throw "目录设置仍显示设备接入控件 $id" }
        }
        if ($mode -eq 'readWrite') { Snapshot 'client' }
        Click $access 'AccessCheckButton'
        $auto = if ($mode -eq 'readWrite') { 14 } else { 11 }
        $null = Wait-Ui '自动匹配授权范围' { (ById $access 'AccessResult').Current.Name -like "*实际发现 $auto 个工具*list_projects*" }
        Pass "$mode 自动匹配在原设备窗口显示，并经 MCP 自检确认 $auto 项"
        Click $access 'AdjustAccessScope'
        SelectOption $access 'AccessProfile' '项目知识'
        $count = if ($mode -eq 'readOnly') { 4 } else { 5 }
        $null = Wait-Ui '按权限生成知识配置' { (ById $access 'AccessCheckButton').Current.IsEnabled }
        Click $access 'AccessCheckButton'
        $null = Wait-Ui '只读自检' { (ById $access 'AccessResult').Current.Name -like "*实际发现 $count 个工具*list_projects*" }
        Pass "目录绑定前显示引导；$mode 知识配置通过真实 MCP 自检（$count 项）"
        Click $access 'SaveWorkspacePolicyButton'
        $null = Wait-Ui '目录范围已写入 Edge' {
            (ById $access 'SaveWorkspacePolicyButton').Current.IsEnabled -and
                (ById $access 'ConfigHint').Current.Name -like "*$count 项工具*"
        }
        if ((Test-Path -LiteralPath (Join-Path $workspace '.agents/skills')) -or
            (Test-Path -LiteralPath (Join-Path $workspace '.codex/config.toml'))) { throw '本机目录设置意外写入了项目文件。' }
        Pass "$mode 保存目录策略后在 Edge 中读回 $count 项工具，绑定目录中无项目配置或重复 Skills"
        if ($mode -eq 'readWrite') {
            Click $access 'AccessCatalogTab'
            foreach ($type in '项目与知识','共享素材','反馈','事件','任务','Agent') {
                $null = Wait-Ui "分类 $type" { ById $access "ToolGroup_$type" }
            }
            Click $access 'ToolGroup_共享素材'
            $null = Wait-Ui '素材列表' { HasText $access 'download_asset' }
            CheckScroll $access
            Snapshot 'catalog'
            Click $access 'ToolGroup_事件'
            $null = Wait-Ui '事件尚未提供' { HasText $access '此分类尚未注册工具' }
            Pass '工具目录可切换六类，直接显示素材列表或未实现事件提示'
            if ($ScreenshotDirectory) {
                $factor = ([EdgeQaCapture]::GetDpiForWindow([intptr]$main.Current.NativeWindowHandle))/96
                $testedDpi = [int]($factor*96)
                $size = $main.Current.BoundingRectangle.Size
                $h = [intptr]$main.Current.NativeWindowHandle
                if (![EdgeQaCapture]::Resize($h,575*$factor,545*$factor)) { throw '无法缩放测试窗口' }
                Start-Sleep -Milliseconds 350
                Click $access 'AccessClientTab'
                AssertContained $main (ById $access 'AccessCheckButton') '窄窗常用操作'
                CheckScroll $access
                Snapshot 'compact-client'
                $null = [EdgeQaCapture]::Resize($h,$size.Width,$size.Height)
                Pass '窄窗可访问目录常用按钮与工具目录，未出现重复接入入口和嵌套滚动条'
            }
        }
        Click $access 'CloseAccessButton'
        $null = Wait-Ui '同一窗口返回设备概览' { [bool](ById $main 'LanguageButton') -and (ById $main 'AccessSettingsButton') }
        if ($mode -eq 'readWrite') {
            Click $main 'AccessSettingsButton'
            $access = Wait-Ui '再次进入接入管理' { AccessWindow }
            Click $access 'AccessCheckButton'
            $null = Wait-Ui '自动沿用此前手动选定的知识范围' {
                (ById $access 'AccessResult').Current.Name -like '*实际发现 5 个工具*list_projects*'
            }
            Pass '重新进入后自动沿用已保存的知识范围，后台授权未变，仍为 5 项'
        }
    } finally {
        if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
        $app.Dispose()
    }
}
if ($EvidenceFile) {
    $result = [ordered]@{ checkedAtUtc=[DateTime]::UtcNow.ToString('o'); fixture=$FixtureDescription;
        desktopImageSha256=(Get-EdgeDesktopImageHash $exe); checks=$checks }
    if ($ScreenshotDirectory) {
        $result.windowDpi = $testedDpi
        $result.captures = @(Get-ChildItem -LiteralPath $ScreenshotDirectory -Filter '*.png' | ForEach-Object {
            $img=[Drawing.Image]::FromFile($_.FullName)
            try { [ordered]@{name=$_.Name;widthPx=$img.Width;heightPx=$img.Height} } finally { $img.Dispose() }
        })
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $EvidenceFile -Encoding utf8
}
