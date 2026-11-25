# Use the Windows known folders, as the desktop and CLI do. Only the two historical
# defaults are aliases; a similarly named directory elsewhere belongs to its caller.
function Get-EdgeKnownFolder([Environment+SpecialFolder]$Folder) {
    [Environment]::GetFolderPath($Folder)
}

function Get-EdgeStatePlan([string]$Path) {
    if (![IO.Path]::IsPathFullyQualified($Path)) { throw 'Edge 数据目录须为绝对路径。' }
    $requested = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    foreach ($folder in 'LocalApplicationData','CommonApplicationData') {
        $base = Get-EdgeKnownFolder $folder
        $legacy = [IO.Path]::Combine($base, 'ProjectHub', 'Edge')
        $current = [IO.Path]::Combine($base, 'Nodicarta', 'Edge')
        if ($requested -in $legacy,$current) {
            $old = Get-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue
            $independent = $old -and !$old.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and [IO.Directory]::Exists($current)
            return [pscustomobject]@{ Path=$current; Legacy=$legacy; Requested=$requested; Independent=$independent }
        }
    }
    [pscustomobject]@{ Path=$requested; Legacy=$null; Requested=$requested; Independent=$false }
}

function Invoke-EdgeStateMigration([string]$Cli, $Plan) {
    if (!$Plan.Legacy) { return }
    # The Windows desktop entry is a GUI-subsystem executable. A pipeline waits for its CLI output.
    $result = & $Cli migrate-default-state --state $Plan.Requested | ForEach-Object { $_ }
    if ($LASTEXITCODE -ne 0 -or @($result).Count -ne 1 -or $result -ne $Plan.Path) {
        throw '未能确认默认数据目录迁移完成，请核对原数据和正在运行的 Edge。'
    }
}
