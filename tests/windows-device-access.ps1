# Device-level checks run against the temporary profile created by windows-access-gui.ps1.
function Get-HarnessFixtureIds($view) {
    # The fixture puts a Claude Code launcher on its private PATH in every mode.
    $null = Wait-Ui '隔离用户的 Claude Code 列表行' { ById $view 'HarnessInstall_claude' }
    'codex'
    if (ById $view 'HarnessInstall_claude-desktop') { 'claude-desktop' }
    'claude','antigravity','cursor','opencode','deepseek','pi'
}

function Set-HarnessFixtureScroll($view,[double]$percent) {
    $element = ById $view 'HarnessRows'
    while ($element) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable) {
            $pattern.SetScrollPercent(-1,$percent)
            $null = Wait-Ui '客户端列表滚动就位' { [Math]::Abs($pattern.Current.VerticalScrollPercent-$percent) -lt 1 }
            return
        }
        if ($element.Current.AutomationId -eq 'AccessWindow') { break }
        $element = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($element)
    }
}

function Check-AccessMethods($access) {
    $buttons = $access.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button))
    $formats = @($buttons | ForEach-Object { $_.Current.AutomationId } | Where-Object { $_ -like 'ConfigFormat*' })
    if (($formats -join ',') -ne 'ConfigFormatClients,ConfigFormatStdio,ConfigFormatSkills' -or (ById $access 'HttpWorkspace') -or (HasText $access '本机 HTTP')) {
        throw "智能体接入方式异常：$($formats -join ', ')"
    }
    foreach ($id in (Get-HarnessFixtureIds $access)) {
        $null = Wait-Ui "$id 列表行" { ById $access "HarnessCopy_$id" }
        if (!(ById $access "HarnessStatus_$id") -or !(ById $access "HarnessInstall_$id")) { throw "缺少 $id 状态或接入按钮。" }
    }
}

function Check-DevicePage($access,[string]$page) {
    $sections = @{ Clients='HarnessInstall_codex'; Stdio='CopyConfigButton'; Skills='CheckSkillsButton' }
    $null = Wait-Ui "$page 页内容" { ById $access $sections[$page] }
    foreach ($other in $sections.Keys) {
        if ($other -ne $page -and (ById $access $sections[$other])) { throw "$page 同时呈现了 $other 页内容。" }
    }
    if (ById $access 'GlobalAccessCheckButton') { throw "$page 仍显示设备级 Codex 自检按钮。" }
    if ($page -eq 'Skills' -and !(HasText $access 'Edge 内置版本')) { throw 'Skills 页缺少「Edge 内置版本」标题。' }
    if ($page -ne 'Skills' -and (ById $access 'SkillsHost')) { throw "$page 重复显示通用 Skills 卡片。" }
    if ($page -eq 'Clients' -and (ById $access 'HarnessStatus_codex').Current.Name -match 'Skills|\d+\s*/\s*\d+') {
        throw 'Codex 客户端行重复显示 Skills 数量。'
    }
}

function Check-SkillsCount($access,[int]$installed) {
    $null = Wait-Ui "Skills 已安装 $installed/5" {
        (ById $access 'SkillsCount').Current.Name -match "(?<!\d)$installed\s*/\s*5(?!\d)"
    }
}

function Check-HarnessLayout($access, [double]$dpi) {
    $scale = $dpi/96
    $ids = @(Get-HarnessFixtureIds $access)
    Set-HarnessFixtureScroll $access 0
    for ($i=0; $i -lt $ids.Count; $i++) {
        $id = $ids[$i]
        if ($i -eq 4) { Set-HarnessFixtureScroll $access 100 }
        $previous = if ($i -gt 0) { (ById $access "HarnessInstall_$($ids[$i-1])").Current.BoundingRectangle }
        $icon = (ById $access "HarnessIcon_$id").Current.BoundingRectangle
        $button = (ById $access "HarnessInstall_$id").Current.BoundingRectangle
        $copy = (ById $access "HarnessCopy_$id").Current.BoundingRectangle
        if ([Math]::Abs($icon.Width - 24*$scale) -gt 1 -or [Math]::Abs($icon.Height - 24*$scale) -gt 1 -or
            $icon.Right -ge $button.Left -or
            [Math]::Abs($copy.Left - $button.Right - 8*$scale) -gt 1 -or [Math]::Abs($button.Top - $copy.Top) -gt 1 -or
            ($previous -and ([Math]::Abs($button.Left - $previous.Left) -gt 1 -or [Math]::Abs($button.Top - $previous.Top - 53*$scale) -gt 2)) -or
            (ById $access "HarnessDisconnect_$id")) {
            throw "$id 图标、行高或右侧按钮的实际位置不符。"
        }
    }
    Set-HarnessFixtureScroll $access 0
}

function Check-AntigravityGuidance($access) {
    $expectedResult = 'Antigravity 已接入。绑定目录后，从该项目新建聊天即可查询资料。解绑后会移除连接和查询指引。'
    $existing = [IO.Directory]::CreateDirectory((Join-Path $root '已有目录')).FullName
    $first = & $cli --state $state bind --project $ProjectId --path $existing | ConvertFrom-Json
    if ($LASTEXITCODE -or !$first.workspaceId -or @(Get-ChildItem -LiteralPath $existing -Force).Count) {
        throw '首次接入前，普通目录绑定应保留空目录。'
    }
    $folder = if ($ScreenshotDirectory) { $ScreenshotDirectory } else { Join-Path $root 'antigravity-captures' }
    $null = Wait-Ui 'Antigravity 已检测且可接入' { (ById $access 'HarnessInstall_antigravity').Current.IsEnabled }
    Save-WindowsTestWindow $main $folder 'antigravity-before'
    $hwnd = [intptr]$main.Current.NativeWindowHandle
    $dpi = [EdgeQaCapture]::GetDpiForWindow($hwnd)
    $scale = $dpi/96
    $size = $main.Current.BoundingRectangle.Size
    Add-Type -AssemblyName System.Windows.Forms
    $font = [Drawing.Font]::new('Microsoft YaHei UI', [single](12*$scale), [Drawing.GraphicsUnit]::Pixel)
    try {
        foreach ($phase in 'default-before','narrow-before','narrow-after','default-after') {
            if ($phase -eq 'narrow-before' -or $phase -eq 'default-after') {
                $w,$h = if ($phase -eq 'narrow-before') { 506*$scale; 540*$scale } else { $size.Width; $size.Height }
                if (![EdgeQaCapture]::Resize($hwnd,$w,$h)) { throw '无法调整测试窗口大小。' }
                $null = Wait-Ui '窗口完成缩放' {
                    $actual = $main.Current.BoundingRectangle.Width
                    if ($phase -eq 'narrow-before') { $actual -le (506+16)*$scale }
                    else { $actual -ge $size.Width-16*$scale }
                }
                Start-Sleep -Milliseconds 250
            }
            if ($phase -eq 'narrow-after') {
                Click $access 'HarnessInstall_antigravity'
                try {
                    $null = Wait-Ui 'Antigravity 单项接入完成' {
                        try { (ById $access 'HarnessInstall_antigravity').Current.Name -eq '断开 Google Antigravity 接入' }
                        catch {
                            if ($_.Exception.GetBaseException() -is [Windows.Automation.ElementNotAvailableException]) { return $false }
                            throw
                        }
                    }
                } catch {
                    throw "Antigravity 接入未完成：$($_.Exception.Message)。界面反馈：$((ById $access 'AccessResult').Current.Name)"
                }
                $file = Join-Path $testUserHome '.gemini/config/mcp_config.json'
                $entry = (Get-Content -Raw -LiteralPath $file | ConvertFrom-Json).mcpServers.projecthub
                if ($entry.command -ne $cli -or ($entry.args -join '|') -cne "mcp-stdio-global|--state|$state|--workspace-roots") {
                    throw '单项接入未写入本次设备的 Antigravity 配置。'
                }
                $added = [IO.Directory]::CreateDirectory((Join-Path $root '接入后加入')).FullName
                $second = & $cli --state $state bind --project $ProjectId --path $added | ConvertFrom-Json
                if ($LASTEXITCODE -or !$second.workspaceId -or $second.workspaceId -eq $first.workspaceId) {
                    throw '每个绑定目录都需要单独的工作区标识。'
                }
                foreach ($binding in @($first,$second)) {
                    $pluginDir = Join-Path $binding.localPath '.agents/plugins/nodicarta'
                    $manifest = Get-Content -Raw -LiteralPath (Join-Path $pluginDir 'plugin.json') | ConvertFrom-Json
                    $serverKey = 'kb_' + ([guid]$binding.workspaceId).ToString('N')
                    $workspaceEntry = (Get-Content -Raw -LiteralPath (Join-Path $pluginDir 'mcp_config.json') | ConvertFrom-Json).mcpServers.$serverKey
                    $guide = Get-Content -Raw -LiteralPath (Join-Path $binding.localPath '.agents/rules/nodicarta-workspace.md')
                    if ($manifest.name -cne 'nodicarta' -or $workspaceEntry.command -ne $cli -or
                        $workspaceEntry.cwd -cne '${PLUGIN_ROOT}/../../..' -or
                        ($workspaceEntry.args -join '|') -cne "mcp-stdio-global|--state|$state|--bound-workspace|$($binding.workspaceId)" -or
                        !$guide.Contains("nodicarta_${serverKey}") -or $guide -notmatch 'search_project_knowledge') {
                        throw '已有或新绑定的目录缺少有效的项目连接或查询指引。'
                    }
                }
                $null = & $cli --state $state unbind --project $ProjectId --workspace $second.workspaceId --path $added
                if ($LASTEXITCODE -or (Test-Path -LiteralPath (Join-Path $added '.agents/plugins/nodicarta')) -or
                    (Test-Path -LiteralPath (Join-Path $added '.agents/rules/nodicarta-workspace.md')) -or
                    !(Test-Path -LiteralPath (Join-Path $existing '.agents/plugins/nodicarta/mcp_config.json'))) {
                    throw '解绑未准确清理指定目录的 Antigravity 接入。'
                }
                Pass '桌面一次接入补齐已有绑定，两个目录得到各自命名的 MCP 服务和对应指引，解绑只清理所选目录。'
            }
            $row = ById $access 'HarnessStatus_antigravity'
            AssertContained $main $row 'Antigravity 列表说明'
            $expectedRow = if ($phase.EndsWith('after')) { '已接入 · 已绑定目录自动接入' } else { '已安装 · 已绑定目录自动接入' }
            $width = [Windows.Forms.TextRenderer]::MeasureText($expectedRow,$font,[Drawing.Size]::Empty,[Windows.Forms.TextFormatFlags]::NoPadding).Width
            if ($row.Current.Name -cne $expectedRow -or $row.Current.BoundingRectangle.Width -lt $width -or
                $row.Current.BoundingRectangle.Height -gt 21*$scale) { throw "$phase 的列表说明有误或无法完整显示为一行。" }
            if ($phase.EndsWith('after')) {
                $result = Wait-Ui '完整接入操作说明' { $el = ById $access 'AccessResult'; if ($el.Current.Name -ceq $expectedResult) { $el } }
                AssertContained $main $result 'Antigravity 底部操作说明'
                if ($phase -eq 'narrow-after' -and $result.Current.BoundingRectangle.Height -le 21*$scale) { throw "$phase 的底部操作说明没有换行。" }
            }
            if ($phase -ne 'default-before') { Save-WindowsTestWindow $main $folder "antigravity-$phase" }
            Pass "$phase 的 Antigravity 列表说明完整显示为一行。$(if ($phase.EndsWith('after')) { '单项接入后的底部说明已完整换行。' })"
        }
    } finally { $font.Dispose() }
    return $dpi
}

function Show-DeviceConfig($access,[string]$format) {
    Click $access "ConfigFormat$format"
    if (!(ById $access 'AccessConfigPreview')) { Click $access 'ToggleAccessConfig' }
    (Wait-Ui "$format 配置内容" {
        $preview = ById $access 'AccessConfigPreview'
        if ($preview -and $preview.Current.Name) { $preview }
    }).Current.Name
}

function Check-StdioCopy($access,[string]$preview) {
    $entry = ($preview | ConvertFrom-Json).mcpServers.projecthub
    if ([IO.Path]::GetFullPath($entry.command) -ne $cli -or
        ($entry.args -join '|') -cne "mcp-stdio-global|--state|$state" -or $entry.PSObject.Properties['cwd']) {
        throw 'STDIO 配置未使用当前客户端工作目录。'
    }
    Check-CopiedConfig $access 'CopyConfigButton' $preview
}

function Check-CopiedConfig($access,[string]$button,[string]$preview,[scriptblock]$validate) {
    # Restore the pre-test clipboard only if it still contains our own write.
    Add-Type -AssemblyName System.Windows.Forms
    $original = [Windows.Forms.Clipboard]::GetDataObject()
    $backup = [Windows.Forms.DataObject]::new()
    foreach ($format in $(if ($original) { $original.GetFormats($false) })) {
        $value = $original.GetData($format,$false)
        if ($null -eq $value -and $format -in 'Text','UnicodeText','OEMText','System.String' -and
            $original.GetDataPresent('UnicodeText')) { $value = $original.GetData('UnicodeText') }
        if ($null -eq $value -and $format -in 'Text','OEMText' -and $original.GetDataPresent('UnicodeText')) { continue }
        if ($value -is [IO.MemoryStream]) { $value = [IO.MemoryStream]::new($value.ToArray()) }
        elseif ($value -is [Drawing.Bitmap]) { $value = $value.Clone() }
        elseif ($value -is [byte[]]) { $value = $value.Clone() }
        elseif ($value -isnot [string] -and $value -isnot [string[]]) {
            throw "请在独立验收桌面运行复制测试，当前剪贴板含无法还原的格式 $format。"
        }
        $backup.SetData($format,$false,$value)
    }
    $copied = $null
    try {
        Click $access $button
        $copied = Wait-Ui "$button 已复制相应格式的本设备配置" {
            $text = [Windows.Forms.Clipboard]::GetText()
            if ($validate) {
                if ($text -like '*mcp-stdio-global*' -and (& $validate $text)) { return $text }
            } elseif ($text.Replace("`r`n","`n") -ceq $preview.Replace("`r`n","`n")) { return $text }
        }
    } finally {
        if ($copied -and [Windows.Forms.Clipboard]::GetText() -ceq $copied) {
            if ($original) { [Windows.Forms.Clipboard]::SetDataObject($backup,$true) }
            else { [Windows.Forms.Clipboard]::Clear() }
        }
    }
}

function Check-HarnessCopies($access) {
    foreach ($id in (Get-HarnessFixtureIds $access)) {
        if ($id -eq 'codex') { continue } # The caller checks its TOML separately.
        Check-CopiedConfig $access "HarnessCopy_$id" '' {
            param($text)
            if ($id -eq 'deepseek') { return $text.Contains('@deepseek-ai/dsh-mcp-client') -and $text.Contains($cli) -and $text.Contains($state) }
            try { $doc = $text | ConvertFrom-Json -ErrorAction Stop } catch { return $false }
            if ($id -eq 'opencode') {
                $entry = $doc.mcp.projecthub
                return $entry.type -eq 'local' -and $entry.command[0] -eq $cli -and $entry.command -contains $state
            }
            $entry = $doc.mcpServers.projecthub
            if ($entry.command -ne $cli -or $entry.args -notcontains $state) { return $false }
            switch ($id) {
                'claude-desktop' { return $entry.args -contains '--workspace-roots' -and $entry.args -notcontains 'CLAUDE_PROJECT_DIR' }
                'claude' { return $entry.args -contains 'CLAUDE_PROJECT_DIR' }
                'antigravity' { return $entry.args -contains '--workspace-roots' }
                'cursor' { return $entry.env.PROJECTHUB_WORKSPACE -ceq '${workspaceFolder}' }
                'pi' { return $entry.args[0] -eq 'mcp-stdio-global' }
            }
        }
    }
}

function Check-DeviceSkills($access,[string]$skillRoot) {
    Click $access 'ConfigFormatSkills'
    Check-DevicePage $access 'Skills'
    Check-SkillsCount $access 5
    $version = [regex]::Match((ById $access 'SkillsBundleVersion').Current.Name,'\d+\.\d+\.\d+').Value
    if (!$version) { throw '未显示内置 Skills 版本。' }
    $markers = @(Get-ChildItem -LiteralPath $skillRoot -Directory -Filter 'nodicarta-*' | ForEach-Object {
        Join-Path $_.FullName '.projecthub-managed'
    })
    if ($markers.Count -ne 5 -or !(Test-Path -LiteralPath (Join-Path $skillRoot 'nodicarta-init/SKILL.md'))) { throw '未发现包含 nodicarta-init 的五个受管 Skills。' }
    $old = '0.8.0'
    function Set-OldSkills {
        foreach ($marker in $markers) {
            $content = [IO.File]::ReadAllText($marker)
            if (!$content.StartsWith("$version ")) { throw '测试预期存在带版本的原版 Skills 标记。' }
            [IO.File]::WriteAllText($marker,$old + $content.Substring($version.Length))
        }
    }
    function SkillsAreCurrent {
        @($markers | Where-Object { ![IO.File]::ReadAllText($_).StartsWith("$version ") }).Count -eq 0
    }
    $switch = (ById $access 'AutoUpdateSkillsBox').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($switch.Current.ToggleState -ne 'On') { throw '随 Edge 自动更新应默认开启。' }
    $switch.Toggle()
    $preference = Join-Path (Split-Path $skillRoot) '.projecthub-skills-autoupdate'
    $null = Wait-Ui '保存关闭自动更新的选择' {
        $switch.Current.ToggleState -eq 'Off' -and (Test-Path -LiteralPath $preference) -and [IO.File]::ReadAllText($preference) -ceq 'false'
    }
    Set-OldSkills
    Click $access 'CheckSkillsButton'
    $null = Wait-Ui '显示已安装的旧版本和可更新状态' {
        (ById $access 'SkillsLocalVersion').Current.Name -like "*$old*" -and (ById $access 'UpdateSkillsButton').Current.IsEnabled
    }
    Snapshot 'skills-update'
    Click $access 'UpdateSkillsButton'
    $null = Wait-Ui '手动更新后的 Skills 与内置版本一致' {
        (SkillsAreCurrent) -and (ById $access 'SkillsLocalVersion').Current.Name -like "*$version*"
    }
    Check-SkillsCount $access 5
    Snapshot 'skills-current'
    $switch.Toggle()
    $null = Wait-Ui '保存自动更新偏好' {
        $switch.Current.ToggleState -eq 'On' -and (Test-Path -LiteralPath $preference) -and [IO.File]::ReadAllText($preference) -ceq 'true'
    }
    Pass "自动更新默认开启，手动关闭后可检查并更新全部五项 Skills，重新开启后已保存选择。"
}
