function Test-EdgeRelease([string]$Directory, [string]$Manifest, [switch]$Installed) {
    $files = @{}
    $Directory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory))
    $prefix = $Directory + '\'
    $manifestFile = Get-Item -LiteralPath $Manifest -Force
    $directoryItem = Get-Item -LiteralPath $Directory -Force
    if ($manifestFile.PSIsContainer -or !$directoryItem.PSIsContainer -or
        $manifestFile.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        $directoryItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        (!$Installed -and @(Get-ChildItem -LiteralPath $Directory -Force -Recurse -Attributes ReparsePoint).Count)) {
        throw '发行包包含无法校验的文件或目录。'
    }
    foreach ($line in (Get-Content -LiteralPath $Manifest)) {
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw '发行包文件清单格式无效。' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $filename = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (!$filename.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $files.ContainsKey($filename)) {
            throw '发行包完整性检查失败。'
        }
        if ($Installed) {
            for ($path = $filename; $path -ne $Directory; $path = [IO.Path]::GetDirectoryName($path)) {
                if ((Get-Item -LiteralPath $path -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                    throw '安装目录包含无法校验的链接。'
                }
            }
        }
        if ((Get-FileHash -LiteralPath $filename).Hash -ne $expected) {
            throw '发行包完整性检查失败。'
        }
        $files[$filename] = $true
    }
    if ($Installed) {
        if (!$files.ContainsKey((Join-Path $Directory 'ProjectHub.Edge.exe')) -or $manifestFile.FullName -ne (Join-Path $Directory 'SHA256SUMS')) {
            throw '安装目录的文件清单无效。'
        }
        return
    }
    $verified = @(Get-ChildItem -LiteralPath $Directory -File -Force -Recurse)
    if ($files.Count -eq 0 -or $verified.Count -ne $files.Count -or @($verified | Where-Object { !$files.ContainsKey($_.FullName) }).Count) {
        throw '发行包清单和目录内容不一致。'
    }
}
