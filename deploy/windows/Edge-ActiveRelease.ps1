# The launcher reads this exact twelve-byte file once at process start.
function Get-EdgeActiveRelease([string]$Root) {
    try { $marker = Get-Item -LiteralPath (Join-Path $Root 'active-release') -Force -ErrorAction Stop }
    catch [Management.Automation.ItemNotFoundException] { return }
    if ($marker.PSIsContainer -or $marker.Length -ne 12 -or $marker.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "安装目录中的 active-release 不是有效的版本记录：$Root"
    }
    $value = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($marker.FullName))
    if ($value -cnotmatch '\A[0-9a-f]{12}\z') { throw "安装目录中的 active-release 格式无效：$Root" }
    $sum = Join-Path $Root "$value/SHA256SUMS"
    if (![IO.File]::Exists($sum) -or !(Get-FileHash -LiteralPath $sum).Hash.StartsWith($value, [StringComparison]::OrdinalIgnoreCase)) {
        throw "无法确认 active-release 指向的版本：$Root"
    }
    $value
}

function Set-EdgeActiveRelease([string]$Root, [string]$Build) {
    $previous = Get-EdgeActiveRelease $Root
    if ($Build -cnotmatch '\A[0-9a-f]{12}\z') { throw 'Edge 版本标识无效。' }
    $release = Join-Path $Root $Build
    if (!(Test-Path -LiteralPath (Join-Path $release 'bin/ProjectHub.Edge.exe') -PathType Leaf) -or
        !(Get-FileHash -LiteralPath (Join-Path $release 'SHA256SUMS')).Hash.StartsWith($Build, [StringComparison]::OrdinalIgnoreCase)) {
        throw "无法确认 Edge 版本目录：$release"
    }
    if ($previous -ceq $Build) { return }
    $marker = Join-Path $Root 'active-release'
    $temp = Join-Path $Root ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temp, $Build, [Text.Encoding]::ASCII)
        [IO.File]::Move($temp, $marker, $true)
    } finally { [IO.File]::Delete($temp) }
}

function Test-OwnedEdgeLauncher([string]$Root, [string]$Hash) {
    foreach ($dir in Get-ChildItem -LiteralPath $Root -Directory) {
        if ($dir.Name -cnotmatch '\A[0-9a-f]{12}\z' -or $dir.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { continue }
        $sum = Join-Path $dir.FullName 'SHA256SUMS'
        if ([IO.File]::Exists($sum) -and (Get-FileHash -LiteralPath $sum).Hash.StartsWith($dir.Name, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.File]::ReadAllLines($sum) -contains "$Hash  Nodicarta.Edge.Launcher.exe") { return $true }
    }
    return $false
}

function Install-EdgeLauncher([string]$Root, [string]$Build) {
    $source = Join-Path $Root "$Build/bin/Nodicarta.Edge.Launcher.exe"
    $sourceHash = (Get-FileHash -LiteralPath $source).Hash
    $command = Join-Path $Root 'ProjectHub.Edge.exe'
    $existing = $null
    try { $existing = Get-Item -LiteralPath $command -Force -ErrorAction Stop }
    catch [Management.Automation.ItemNotFoundException] { }
    if ($existing) {
        if ($existing.PSIsContainer -or $existing.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "启动命令已被其他文件占用：$command" }
        $actual = (Get-FileHash -LiteralPath $command).Hash
        if ($actual -eq $sourceHash) { return }
        if (!(Test-OwnedEdgeLauncher $Root $actual)) { throw "启动命令已被其他文件占用：$command" }
    }
    $temp = Join-Path $Root ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($source, $temp)
        try { [IO.File]::Move($temp, $command, [bool]$existing) }
        catch {
            # A live MCP session may keep the original bootstrap mapped. All
            # released bootstraps read the same active-release protocol.
            if (!$existing -or !(Get-Process -Name ProjectHub.Edge -ErrorAction SilentlyContinue | Where-Object Path -eq $command)) { throw }
        }
    } finally { [IO.File]::Delete($temp) }
}

function Get-EdgeLinkSnapshot([string[]]$Paths) {
    $saved = @{}
    foreach ($path in $Paths) {
        $saved[$path] = $null
        if ([IO.File]::Exists($path)) { $saved[$path] = [IO.File]::ReadAllBytes($path) }
    }
    $saved
}

function Restore-EdgeLinks([hashtable]$Saved) {
    foreach ($path in $Saved.Keys) {
        if ($null -eq $Saved[$path]) { [IO.File]::Delete($path); continue }
        $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::WriteAllBytes($temp, $Saved[$path])
            [IO.File]::Move($temp, $path, $true)
        } finally { [IO.File]::Delete($temp) }
    }
}

function Enter-EdgeDesktopInstall {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = [Threading.Mutex]::new($false, "Local\Nodicarta.Edge.Desktop.Install.$sid")
    try {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { throw '另一个桌面安装操作正在进行，请稍后重试。' }
        $mutex
    } catch { $mutex.Dispose(); throw }
}
