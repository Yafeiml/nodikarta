function Test-EdgeRelease([string]$Directory, [string]$Manifest) {
    $files = @{}
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory)) + '\'
    foreach ($line in (Get-Content -LiteralPath $Manifest)) {
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw '发行包文件清单格式无效。' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $filename = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (!$filename.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            $files.ContainsKey($filename) -or (Get-FileHash -LiteralPath $filename).Hash -ne $expected) { throw '发行包完整性检查失败。' }
        $files[$filename] = $true
    }
    if ($files.Count -eq 0 -or @(Get-ChildItem -LiteralPath $Directory -File -Force -Recurse).Count -ne $files.Count) {
        throw '发行包清单和目录内容不一致。'
    }
}
