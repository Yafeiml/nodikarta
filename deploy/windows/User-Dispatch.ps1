#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Start','Stop','Enable','Disable','Status','Migrate')][string]$Action,
    [string]$StateDirectory,
    [string]$EdgeExe = (Join-Path $PSScriptRoot 'bin/ProjectHub.Edge.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Edge-StatePaths.ps1')
$plan = Get-EdgeStatePlan ($StateDirectory ? $StateDirectory : (Join-Path (Get-EdgeKnownFolder CommonApplicationData) 'Nodicarta/Edge'))
$state = $plan.Path
$exe = [IO.Path]::GetFullPath($EdgeExe)
if ($plan.Independent) {
    # Verify the requested spelling before touching any worker or login link. An
    # independently recreated old store and its dispatch registrations stay separate.
    Invoke-EdgeStateMigration $exe $plan
    $plan.Legacy = $null
}
$local = Get-EdgeKnownFolder LocalApplicationData
$shell = New-Object -ComObject WScript.Shell
$startup = $shell.SpecialFolders.Item('Startup')
function Get-Tag([string]$Path) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Path.ToUpperInvariant()))).Substring(0,16)
}
$profileDir = Join-Path $local ('Nodicarta/Dispatch/' + (Get-Tag $state))
$oldDirs = @(@($state, $plan.Legacy) | Where-Object { $_ } | ForEach-Object { Join-Path $local ('ProjectHub/Dispatch/' + (Get-Tag $_)) } | Select-Object -Unique)
$newLink = Join-Path $startup ("Nodicarta Edge " + (Get-Tag $state) + ".lnk")
$scriptName = 'User-Dispatch.ps1'
function Test-DispatchLink([string]$Path, [string]$Directory) {
    if (![IO.File]::Exists($Path)) { return $false }
    $shortcut = $shell.CreateShortcut($Path)
    if ([IO.Path]::GetFileName($shortcut.TargetPath) -ne 'pwsh.exe' -or
        $shortcut.Arguments -notmatch '^-NoProfile -NonInteractive -WindowStyle Hidden -File "([^"]+)" -Action Start -StateDirectory "([^"]+)" -EdgeExe "([^"]+)"$') { return $false }
    $scriptPath, $linkState, $linkExe = $Matches[1], $Matches[2], $Matches[3]
    $scriptPath -eq (Join-Path $Directory $scriptName) -and $linkState -in @($state,$plan.Legacy) -and
        [IO.Path]::GetFileName($linkExe) -eq 'ProjectHub.Edge.exe'
}
function Read-Worker([string]$Directory) {
    $record = Join-Path $Directory 'process.json'
    if (![IO.File]::Exists($record)) { return }
    $known = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $p = Get-Process -Id $known.processId -ErrorAction SilentlyContinue
    if ($p -and $p.Path -eq $known.exe -and $p.StartTime.ToUniversalTime().Ticks -eq $known.startUtcTicks) {
        [pscustomobject]@{ Process=$p; Directory=$Directory }
    }
}
function Write-ControlFile([string]$Path, [string]$Value) {
    $tmp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try { [IO.File]::WriteAllText($tmp,$Value); [IO.File]::Move($tmp,$Path,$true) }
    finally { [IO.File]::Delete($tmp) }
}
$oldLinks = @(foreach ($dir in $oldDirs) {
    $path = Join-Path $startup ('ProjectHub Edge ' + [IO.Path]::GetFileName($dir) + '.lnk')
    if (Test-DispatchLink $path $dir) { [pscustomobject]@{ Path=$path; Directory=$dir } }
})
$newEnabled = Test-DispatchLink $newLink $profileDir
if ($Action -eq 'Migrate' -and !$newEnabled -and !($oldLinks | Where-Object { [IO.File]::Exists((Join-Path $_.Directory $scriptName)) }) -and
    !(($oldDirs + $profileDir) | Where-Object { Read-Worker $_ })) { return }
$null = [IO.Directory]::CreateDirectory($profileDir)
$gates = [Collections.Generic.List[IO.FileStream]]::new()
try {
    # A legacy controller can still be invoked by an already scheduled login. Share
    # its lock while handing over the worker and replacing the owned shortcuts.
    foreach ($dir in @($profileDir) + $oldDirs) {
        if ([IO.Directory]::Exists($dir)) { $gates.Add([IO.File]::Open((Join-Path $dir 'control.lock'),'OpenOrCreate','ReadWrite','None')) }
    }
    $seen = [Collections.Generic.HashSet[int]]::new()
    $workers = @(foreach ($dir in @($profileDir) + $oldDirs) {
        $worker = Read-Worker $dir
        if ($worker -and $seen.Add($worker.Process.Id)) { $worker }
    })
    $active = $workers | Where-Object Directory -eq $profileDir | Select-Object -First 1
    $enabled = $newEnabled -or $oldLinks.Count -gt 0
    if ($Action -eq 'Status') {
        return [pscustomobject]@{ Running=$workers.Count -gt 0; StartsAtLogin=$enabled; StateDirectory=$state }
    }
    $stop = $Action -in 'Stop','Disable'
    $start = !$stop -and ($Action -ne 'Migrate' -or $workers.Count -gt 0)
    $enable = $Action -eq 'Enable' -or ($enabled -and !$stop)
    if (!$stop) {
        if (![IO.File]::Exists($exe) -or [IO.Path]::GetFileName($exe) -ne 'ProjectHub.Edge.exe') { throw '请提供派工使用的 Edge 可执行文件。' }
        if ($enable -and [IO.File]::Exists($newLink) -and !$newEnabled) { throw '登录启动目录中存在同名自定义快捷方式，请先核对。' }
        if (![IO.File]::Exists((Join-Path $state 'edge.sqlite3')) -and (!$plan.Legacy -or ![IO.File]::Exists((Join-Path $plan.Legacy 'edge.sqlite3')))) {
            if ($Action -eq 'Migrate') { return }
            throw '请先配对设备，再启动派工。'
        }
    }
    foreach ($worker in $workers) {
        if (!$stop -and $worker.Directory -eq $profileDir -and $worker.Process.Path -eq $exe) { continue }
        [IO.File]::WriteAllText((Join-Path $worker.Directory 'stop'),'stop')
        if (!$worker.Process.WaitForExit(30000)) { throw '已请求派工进程停止。请稍后检查状态。' }
        [IO.File]::Delete((Join-Path $worker.Directory 'process.json'))
        if ($worker.Directory -eq $profileDir) { $active = $null }
    }
    if (!$stop) { Invoke-EdgeStateMigration $exe $plan }
    if ($enable) {
        $scriptFile = Join-Path $profileDir $scriptName
        $helperFile = Join-Path $profileDir 'Edge-StatePaths.ps1'
        if ($PSCommandPath -ne $scriptFile) {
            Write-ControlFile $helperFile ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Edge-StatePaths.ps1')))
            Write-ControlFile $scriptFile ([IO.File]::ReadAllText($PSCommandPath))
        }
        $tmp = Join-Path $startup (([guid]::NewGuid().ToString('N')) + '.lnk')
        try {
            $shortcut = $shell.CreateShortcut($tmp)
            $shortcut.TargetPath = (Get-Command pwsh.exe -CommandType Application | Select-Object -First 1).Source
            $shortcut.IconLocation = "$exe,0"
            $shortcut.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -Action Start -StateDirectory "{1}" -EdgeExe "{2}"' -f $scriptFile,$state,$exe
            $shortcut.WorkingDirectory = $profileDir
            $shortcut.WindowStyle = 7
            $shortcut.Save()
            [IO.File]::Move($tmp,$newLink,$true)
        } finally { [IO.File]::Delete($tmp) }
        foreach ($old in $oldLinks) {
            # Shortcuts already queued by Explorer may still reference the former
            # installed script. Make that exact owned entry forward to this one.
            $oldScript = Join-Path $old.Directory $scriptName
            if ([IO.File]::Exists($oldScript)) {
                $backup = $oldScript + '.pre-nodicarta'
                if (![IO.File]::Exists($backup)) { [IO.File]::Copy($oldScript,$backup) }
                $quotedController = "'" + $scriptFile.Replace("'","''") + "'"
                $quotedExe = "'" + $exe.Replace("'","''") + "'"
                $forward = 'param($Action,$StateDirectory,$EdgeExe)' + [Environment]::NewLine +
                    ('& {0} -Action $Action -StateDirectory $StateDirectory -EdgeExe {1}' -f $quotedController,$quotedExe)
                Write-ControlFile $oldScript $forward
            }
            [IO.File]::Delete($old.Path)
        }
    } elseif ($Action -eq 'Disable') {
        if ($newEnabled) { [IO.File]::Delete($newLink) }
        foreach ($old in $oldLinks) { [IO.File]::Delete($old.Path) }
    }
    if ($start -and !$active) {
        $stopFile = Join-Path $profileDir 'stop'
        [IO.File]::Delete($stopFile)
        $prior = $env:PROJECTHUB_DISPATCH_STOP_FILE
        try {
            $env:PROJECTHUB_DISPATCH_STOP_FILE = $stopFile
            $process = Start-Process -FilePath $exe -ArgumentList ('dispatch --watch --state "{0}"' -f $state) -WorkingDirectory $profileDir -WindowStyle Hidden -PassThru
        } finally { $env:PROJECTHUB_DISPATCH_STOP_FILE = $prior }
        if ($process.WaitForExit(1200)) { throw "派工进程已退出（$($process.ExitCode)），请在前台运行 dispatch 核对会话配置。" }
        try {
            $record = @{ processId=$process.Id; startUtcTicks=$process.StartTime.ToUniversalTime().Ticks; exe=$exe } | ConvertTo-Json -Compress
            Write-ControlFile (Join-Path $profileDir 'process.json') $record
            foreach ($dir in $oldDirs) {
                if ([IO.Directory]::Exists($dir)) { Write-ControlFile (Join-Path $dir 'process.json') $record }
            }
        } catch {
            [IO.File]::WriteAllText($stopFile,'stop')
            $null = $process.WaitForExit(30000)
            throw
        }
        $active = $process
    }
    [pscustomobject]@{ Running=$null -ne $active; StartsAtLogin=($Action -ne 'Disable' -and
        ((Test-DispatchLink $newLink $profileDir) -or ($Action -eq 'Stop' -and $oldLinks.Count -gt 0))); StateDirectory=$state }
} finally { foreach ($gate in $gates) { $gate.Dispose() } }
