#requires -Version 7.0
param([Parameter(Mandatory)][string]$OldPackage, [Parameter(Mandatory)][string]$NewPackage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Edge-ActiveRelease.ps1')
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-launcher-')
$install = Join-Path $work.FullName '一号 Edge'
$second = Join-Path $work.FullName '二号 Edge'
$cwd = [IO.Directory]::CreateDirectory((Join-Path $work.FullName '项目 和目录')).FullName
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Assert($condition, $message) { if (!$condition) { throw $message } }
function Stage-Package($source, $destination) {
    $sum = Join-Path $source 'SHA256SUMS'
    $id = (Get-FileHash -LiteralPath $sum).Hash.Substring(0,12).ToLowerInvariant()
    $dir = [IO.Directory]::CreateDirectory((Join-Path $destination $id))
    Copy-Item -LiteralPath (Join-Path $source 'bin'),$sum -Destination $dir.FullName -Recurse
    return $id
}
function Open-Command($command, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($command)
    $info.WorkingDirectory = $cwd
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $info.RedirectStandardOutput = $info.RedirectStandardError = $true
    foreach ($arg in $arguments) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    return $process
}
function Invoke-Request($process, [int]$id, $method, $parameters) {
    $request = @{jsonrpc='2.0'; id=$id; method=$method; params=$parameters} | ConvertTo-Json -Depth 6 -Compress
    $process.StandardInput.WriteLine($request)
    $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    $response = $line | ConvertFrom-Json
    Assert ($response.id -eq $id -and $null -ne $response.result) "Invalid JSON-RPC reply to $method"
    $response.result
}
function Start-Mcp($root, $id) {
    $command = Join-Path $root 'ProjectHub.Edge.exe'
    $process = Open-Command $command @('mcp-stdio-global','--state','相对 状态')
    $reply = Invoke-Request $process 1 'initialize' @{protocolVersion='2025-11-25'; clientInfo=@{name='edge-launch-test';version='1'}; capabilities=@{}}
    Assert ($null -ne $reply.serverInfo.name) 'Failed MCP initialize'
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $module = $process.Modules | Where-Object ModuleName -eq 'hostfxr.dll'
    Assert ($process.Path -eq $command -and $module.FileName -eq (Join-Path $root "$id/bin/hostfxr.dll")) 'MCP loaded the wrong physical runtime'
    $process
}
function Test-Session($process) {
    $reply = Invoke-Request $process 2 'tools/list' @{}
    Assert ($reply.tools.Count -gt 0) 'MCP session was interrupted'
}
function Run-Command($path, [string[]]$arguments) {
    $process = Open-Command $path $arguments
    $process.StandardInput.Close()
    $result = @{
        Out = $process.StandardOutput.ReadToEndAsync()
        Err = $process.StandardError.ReadToEndAsync()
    }
    Assert ($process.WaitForExit(15000)) 'CLI timed out'
    return @{Exit=$process.ExitCode; Out=$result.Out.Result; Err=$result.Err.Result}
}
try {
    $old = Stage-Package $OldPackage $install
    $new = Stage-Package $NewPackage $install
    Assert ($old -ne $new) 'Supply two distinct desktop releases'
    $null = Stage-Package $OldPackage $second
    $launcher = Join-Path $NewPackage 'bin/Nodicarta.Edge.Launcher.exe'
    foreach ($root in $install,$second) {
        Copy-Item -LiteralPath $launcher -Destination (Join-Path $root 'ProjectHub.Edge.exe')
        Set-EdgeActiveRelease $root $old
    }
    $running = Start-Mcp $install $old
    Set-EdgeActiveRelease $install $new
    $upgraded = Start-Mcp $install $new
    $independent = Start-Mcp $second $old
    Assert ([IO.File]::Exists((Join-Path $cwd '相对 状态/edge.sqlite3'))) 'CLI lost the harness working directory or Unicode argument'
    Set-EdgeActiveRelease $install $old
    $rolledBack = Start-Mcp $install $old
    foreach ($session in $running,$upgraded,$independent,$rolledBack) { Test-Session $session }
    $arguments = @('--state','相对 状态','invalid-command')
    $stable = Join-Path $install 'ProjectHub.Edge.exe'
    $direct = Run-Command (Join-Path $install "$old/bin/ProjectHub.Edge.exe") $arguments
    $viaLauncher = Run-Command $stable $arguments
    Assert ($direct.Exit -ne 0 -and $viaLauncher.Exit -eq $direct.Exit -and $viaLauncher.Out -ceq $direct.Out -and $viaLauncher.Err -ceq $direct.Err) 'CLI diagnostics or exit code changed'
    [IO.File]::WriteAllText((Join-Path $install 'active-release'), '..\elsewhere')
    $invalid = Run-Command $stable @('--help')
    Assert ($invalid.Exit -ne 0 -and !$invalid.Out -and $invalid.Err -like '*active-release*') 'Invalid pointer was accepted or polluted stdout'
    "PASS: $old -> $new -> $old; four in-process MCP sessions, two install roots, UTF-8 arguments and working directory, stdio, exit status, invalid pointer."
} finally {
    foreach ($process in $owned) {
        try { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } } finally { $process.Dispose() }
    }
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('nodicarta-launcher-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
