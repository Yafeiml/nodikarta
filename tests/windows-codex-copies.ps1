#requires -Version 7.0
# Run via windows-access-fixture.cjs --codex-copies against a packaged Windows desktop executable.
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$RunRoot, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
. (Join-Path $PSScriptRoot 'windows-codex-button-check.ps1')
$root = (Resolve-Path -LiteralPath $RunRoot).Path
if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
    [IO.Path]::GetFileName($root) -notmatch '^projecthub-accessqa-[a-f0-9]{32}$') { throw 'A disposable fixture root is required.' }
$state = Join-Path $root 'readWrite'
$profileRoot = [IO.Directory]::CreateDirectory((Join-Path $root 'profile')).FullName
$codexHome = [IO.Directory]::CreateDirectory((Join-Path $profileRoot '.codex')).FullName
$launchers = [IO.Directory]::CreateDirectory((Join-Path $profileRoot 'bin')).FullName
foreach ($name in 'codex','claude') { [IO.File]::WriteAllText((Join-Path $launchers "$name.cmd"), '@exit /b 1') }
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path $root 'results' }
$null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
$config = Join-Path $codexHome 'config.toml'
$untouched = "# user setting retained across registration edits`r`nmodel = 'fixture'`r`n"
[IO.File]::WriteAllText($config,$untouched)
$checks = [Collections.Generic.List[string]]::new()
function Pass([string]$description) { $checks.Add($description); Write-Host "PASS $description" }
function Wait-For([string]$description,[scriptblock]$probe,[int]$timeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        try { $result = & $probe; if ($result) { return $result } }
        catch { if ($_.Exception.GetBaseException() -isnot [Windows.Automation.ElementNotAvailableException]) { throw } }
        Start-Sleep -Milliseconds 120
    } until ([DateTime]::UtcNow -gt $deadline)
    throw "Timed out: $description"
}
function ById($parent,[string]$id) {
    $parent.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Click([string]$id) {
    $button = Wait-For $id { $el = ById $main $id; if ($el -and $el.Current.IsEnabled) { $el } }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Open-Access {
    $script:main = Wait-For 'overview opened by this test process' {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
        [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
    }
    Click 'GlobalAccessButton'
}
function Wait-Row([string]$title) {
    $null = Wait-For $title {
        $el = ById $main 'HarnessInstall_codex'
        $el -and $el.Current.Name -eq $title -and $el.Current.IsEnabled
    }
}
function Start-Copy([string]$dir,[string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $dir 'bin/ProjectHub.Edge.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.Environment['PATH'] = "$launchers;$env:SystemRoot\System32"
    $start.Environment['USERPROFILE'] = $start.Environment['HOME'] = $profileRoot
    $start.Environment['CODEX_HOME'] = $codexHome
    $null = $start.Environment.Remove('PROJECTHUB_EDGE_STATE')
    foreach ($key in 'LOCALAPPDATA','APPDATA','XDG_CONFIG_HOME','PI_CODING_AGENT_DIR','CLAUDE_CONFIG_DIR','DSH_HOME') {
        $start.Environment[$key] = Join-Path $profileRoot $key
    }
    foreach ($key in 'OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT') { $null = $start.Environment.Remove($key) }
    foreach ($value in $arguments) { $start.ArgumentList.Add($value) }
    [Diagnostics.Process]::Start($start)
}
function Call-Copy([string]$dir,[string[]]$arguments) {
    $child = Start-Copy $dir $arguments
    try { if (!$child.WaitForExit(30000) -or $child.ExitCode) { throw "Edge command failed: $arguments" } }
    finally { if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
}
function Stop-Copy {
    if ($app) { if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose(); $script:app = $null }
}
function Is-Target([string]$dir) {
    $toml = [IO.File]::ReadAllText($config)
    if (!$toml.StartsWith($untouched)) { throw 'User content in config.toml changed' }
    $match = [regex]::Match($toml,'(?m)^command = ("[^\r\n]+")\s*$')
    $match.Success -and ($match.Groups[1].Value | ConvertFrom-Json) -eq (Join-Path $dir 'bin/ProjectHub.Edge.exe' -Resolve)
}
function Skills-Inventory {
    $files = Get-ChildItem -LiteralPath (Join-Path $profileRoot '.agents/skills') -File -Force -Recurse | Sort-Object FullName
    ($files | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n"
}
function Target-Dir([string]$id) {
    if ($id -notmatch '^[a-f0-9]{12}$') { throw 'Invalid fixture subdirectory' }
    $path = [IO.Path]::GetFullPath((Join-Path $root $id))
    if ([IO.Path]::GetDirectoryName($path) -ne $root) { throw 'Expected a child of the isolated fixture' }
    return $path
}
$a = Target-Dir '111111111111'
$b = Target-Dir '222222222222'
$c = Target-Dir '333333333333'
$d = Target-Dir '444444444444'
$null = [IO.Directory]::CreateDirectory($a)
Copy-Item -LiteralPath (Split-Path (Resolve-Path -LiteralPath $Executable).Path) -Destination (Join-Path $a 'bin') -Recurse
$app = $null
try {
    Call-Copy $a @('--connect-codex','--state',$state)
    if (!(Is-Target $a)) { throw 'Initial registration did not target A' }
    $marker = Join-Path $profileRoot '.agents/skills/nodicarta-inbox/.projecthub-managed'
    $currentMarker = [IO.File]::ReadAllText($marker)
    $oldMarker = '0.8.0 ' + $currentMarker.Split(' ',2)[1]
    [IO.File]::WriteAllText($marker,$oldMarker)
    $preference = Join-Path $profileRoot '.agents/.projecthub-skills-autoupdate'
    [IO.File]::WriteAllText($preference,'false')
    $beforeSkills = Skills-Inventory
    [IO.Directory]::Move($a,$b)
    $app = Start-Copy $b @('--state',$state)
    $null = Wait-For 'new GUI repaired registration before opening access settings' { Is-Target $b }
    Open-Access
    Wait-Row '断开 Codex 接入'
    if ((Skills-Inventory) -cne $beforeSkills) { throw 'Explicit opt-out did not preserve Skills' }
    Pass '移动目录后自动修复 Codex 路径；手动关闭 Skills 更新时保留原文件。'
    $registeredB = [IO.File]::ReadAllText($config)
    Copy-Item -LiteralPath $b -Destination $c -Recurse
    Call-Copy $c @('--background','--state',$state)
    Call-Copy $b @('--state',$state)
    Call-Copy $c @('--refresh-codex-registration',$root)
    if ($app.HasExited -or !(Is-Target $b)) { throw 'Background or maintenance launch displaced the resident' }
    Pass '同一目录重复打开仅唤起窗口，其他目录的后台启动和安装维护均保留当前常驻。'
    $previousApp = $app
    try {
        $app = Start-Copy $c @()
        $null = Wait-For 'previous resident voluntarily quit' { $previousApp.HasExited }
        if ($previousApp.ExitCode -ne 0) { throw 'Old resident did not exit cleanly' }
        $null = Wait-For 'new resident adopted registration and retained explicit state' { Is-Target $c }
        Open-Access
        Wait-Row '断开 Codex 接入'
        if ((Skills-Inventory) -cne $beforeSkills) { throw 'Moving a live instance rewrote opted-out Skills' }
        Pass '从新目录直接打开，旧常驻正常退出，原目录仍在；已配对设备和 Codex 接入随新位置自动生效。'
        Test-CodexButtonAppearance -Window $main -Phase 'connected' -EvidenceDirectory $EvidenceDirectory
    } finally {
        if (!$previousApp.HasExited) { $previousApp.Kill(); $previousApp.WaitForExit() }
        $previousApp.Dispose()
    }
    [IO.File]::WriteAllText($config,$registeredB)
    [IO.File]::WriteAllText($preference,'true')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Wait-For 'background refresh corrected the old path and updated opted-in Skills' {
        !$app.HasExited -and (Is-Target $c) -and [IO.File]::ReadAllText($marker) -ceq $currentMarker
    } 155
    $elapsed = [Math]::Round($watch.Elapsed.TotalSeconds,1)
    Pass ('常驻期间接入配置恢复旧值后，在 {0} 秒内自动修复并更新了允许跟随 Edge 更新的 Skills。' -f $elapsed)
    Stop-Copy
    [IO.Directory]::Move($c,$d)
    $app = Start-Copy $d @('--background')
    $null = Wait-For 'cold start inherited custom state and repaired the missing executable' { Is-Target $d }
    Pass '再次搬动后用后台入口启动，沿用原自定义数据目录并自动修正接入。'
    [ordered]@{machine=[Environment]::MachineName;time=[DateTimeOffset]::Now.ToString('o');desktopSha256=(Get-FileHash -LiteralPath $Executable).Hash;residentRetrySeconds=$elapsed;checks=$checks} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'checks.json')
} finally { Stop-Copy }
