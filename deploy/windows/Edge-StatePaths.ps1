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
            return [pscustomobject]@{ Path=$current; Legacy=$legacy; Requested=$requested }
        }
    }
    [pscustomobject]@{ Path=$requested; Legacy=$null; Requested=$requested }
}

function Invoke-EdgeStateMigration([string]$Cli, $Plan) {
    if (!$Plan.Legacy) { return }
    $result = & $Cli migrate-default-state --state $Plan.Path
    if ($LASTEXITCODE -ne 0 -or @($result).Count -ne 1 -or $result -ne $Plan.Path) {
        throw '未能确认默认数据目录迁移完成，请核对原数据和正在运行的 Edge。'
    }
}
