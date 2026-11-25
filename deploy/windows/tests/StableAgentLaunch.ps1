#requires -Version 7.0
param([Parameter(Mandatory)][string]$OldPackage, [Parameter(Mandatory)][string]$NewPackage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Test-EdgeRelease.ps1')
if (Get-Process ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue) { throw 'Use an idle, disposable Windows guest.' }
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-launcher-')
$install = Join-Path $work.FullName '一号 Edge'
$second = Join-Path $work.FullName '二号 Edge'
$cwd = [IO.Directory]::CreateDirectory((Join-Path $work.FullName '项目 和目录')).FullName
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Assert($condition, $message) { if (!$condition) { throw $message } }
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
function Start-Mcp($root, $expectedVersion) {
    $command = Join-Path $root 'ProjectHub.Edge.exe'
    $process = Open-Command $command @('mcp-stdio-global','--state','相对 状态')
    $reply = Invoke-Request $process 1 'initialize' @{protocolVersion='2025-11-25'; clientInfo=@{name='edge-launch-test';version='1'}; capabilities=@{}}
    Assert ($reply.serverInfo.name -ceq 'nodicarta' -and $reply.serverInfo.version -ceq $expectedVersion) 'Failed MCP initialize or unexpected Edge version'
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $module = $process.Modules | Where-Object ModuleName -eq 'NodicartaEdgeCli.dll'
    Assert ($process.Path -eq $command -and $module.FileName -eq (Join-Path $root 'NodicartaEdgeCli.dll')) 'MCP loaded the wrong physical runtime'
    Assert (!($process.Modules | Where-Object ModuleName -eq 'hostfxr.dll')) 'Native MCP loaded hostfxr'
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
function Test-DesktopLaunch([string]$Root, [switch]$Background) {
    $image = Join-Path $Root 'ProjectHub.Edge.Desktop.exe'
    $command = Join-Path $Root 'ProjectHub.Edge.exe'
    $info = [Diagnostics.ProcessStartInfo]::new($command)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $Root
    $info.Environment['NODICARTA_EDGE_STATE'] = Join-Path $work.FullName 'desktop-state'
    $info.Environment['USERPROFILE'] = Join-Path $work.FullName 'profile'
    $info.Environment['LOCALAPPDATA'] = Join-Path $work.FullName 'local'
    $info.Environment['APPDATA'] = Join-Path $work.FullName 'roaming'
    $info.Environment['CODEX_HOME'] = Join-Path $work.FullName 'profile/.codex'
    if ($Background) { $info.ArgumentList.Add('--background') }
    $owned.Add([Diagnostics.Process]::Start($info))
    $until = [datetime]::UtcNow.AddSeconds(20)
    do {
        $window = Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction Ignore | Where-Object Path -eq $image
        if ($window -and $window.Id -notin $owned.Id) { $owned.Add($window) }
        if ($window -and $window.WaitForInputIdle(250) -and ($Background.IsPresent -eq ($window.MainWindowHandle -eq 0))) { break }
        Start-Sleep -Milliseconds 150
    } until ([datetime]::UtcNow -ge $until)
    Assert ($window -and $window.WaitForInputIdle(250) -and ($Background.IsPresent -eq ($window.MainWindowHandle -eq 0))) 'Fixed desktop command did not open in the requested mode'
    $window.Kill()
    $window.WaitForExit()
}
function Install-FixtureFiles([string]$package, [string]$root) {
    $bin = Join-Path $package 'bin'
    $sum = Join-Path $package 'SHA256SUMS'
    Test-EdgeRelease $bin $sum
    $null = [IO.Directory]::CreateDirectory($root)
    $lease = [IO.File]::Open((Join-Path $root '.edge-lease'),'OpenOrCreate','ReadWrite','None')
    try {
        Get-ChildItem -LiteralPath $bin -Force | Copy-Item -Destination $root -Recurse -Force
        Copy-Item -LiteralPath $sum -Destination $root -Force
        $blocked = Run-Command (Join-Path $root 'ProjectHub.Edge.exe') @('--version')
        Assert ($blocked.Exit -eq 1 -and !$blocked.Out -and $blocked.Err -like '*setup is running*') 'New CLI launched while setup held its exclusive lease'
    } finally { $lease.Dispose() }
}
try {
    $launcher = Join-Path $OldPackage 'bin/ProjectHub.Edge.exe'
    $old = (Run-Command $launcher @('--version')).Out.Trim()
    $new = (Run-Command (Join-Path $NewPackage 'bin/ProjectHub.Edge.exe') @('--version')).Out.Trim()
    Assert ($old -and $new -and $old -ne $new) 'Supply two distinct desktop releases'
    Install-FixtureFiles $OldPackage $install
    Install-FixtureFiles $OldPackage $second
    $running = Start-Mcp $install $old
    $independent = Start-Mcp $second $old
    $busy = $false
    try { Install-FixtureFiles $NewPackage $install }
    catch [IO.IOException] { if (($_.Exception.HResult -band 0xffff) -ne 32) { throw }; $busy = $true }
    Assert $busy 'Setup replaced runtime files while MCP was running'
    Test-Session $running
    $running.StandardInput.Close()
    Assert ($running.WaitForExit(10000)) 'Old CLI did not exit'
    Install-FixtureFiles $NewPackage $install
    Test-DesktopLaunch $install
    Test-DesktopLaunch $install -Background
    $upgraded = Start-Mcp $install $new
    Assert ([IO.File]::Exists((Join-Path $cwd '相对 状态/edge.sqlite3'))) 'CLI lost the harness working directory or Unicode argument'
    Test-Session $upgraded
    $upgraded.StandardInput.Close()
    Assert ($upgraded.WaitForExit(10000)) 'New CLI did not exit'
    Install-FixtureFiles $OldPackage $install
    $rolledBack = Start-Mcp $install $old
    Test-Session $independent
    Test-Session $rolledBack
    $arguments = @('--state','相对 状态','invalid-command')
    $stable = Join-Path $install 'ProjectHub.Edge.exe'
    $direct = Run-Command $launcher $arguments
    $viaLauncher = Run-Command $stable $arguments
    Assert ($direct.Exit -ne 0 -and $viaLauncher.Exit -eq $direct.Exit -and $viaLauncher.Out -ceq $direct.Out -and $viaLauncher.Err -ceq $direct.Err) 'CLI diagnostics or exit code changed'
    [IO.File]::WriteAllText((Join-Path $install '.nodicarta-install-transaction.json'), '{}')
    $invalid = Run-Command $stable @('--help')
    Assert ($invalid.Exit -ne 0 -and !$invalid.Out -and $invalid.Err -like '*setup has not finished*') 'Interrupted setup was accepted or polluted stdout'
    "PASS: $old -> $new -> $old; fixed GUI and quiet startup, native flat modules, busy MCP deferral, Unicode arguments, exit status and interrupted setup."
} finally {
    foreach ($process in $owned) {
        try { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } } finally { $process.Dispose() }
    }
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('nodicarta-launcher-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
