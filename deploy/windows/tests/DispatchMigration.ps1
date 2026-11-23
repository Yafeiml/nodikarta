#requires -Version 7.0
param([Parameter(Mandatory)][string]$OldCli, [Parameter(Mandatory)][string]$NewCli)
$ErrorActionPreference = 'Stop'
$old = (Get-Item -LiteralPath $OldCli).FullName
$new = (Get-Item -LiteralPath $NewCli).FullName
$work = [IO.Directory]::CreateTempSubdirectory('Nodicarta-Dispatch-')
$testLocal = Join-Path $work.FullName 'local'
$testMachine = Join-Path $work.FullName 'machine'
$startup = [IO.Directory]::CreateDirectory((Join-Path $work.FullName 'Startup')).FullName
$shell = Microsoft.PowerShell.Utility\New-Object -ComObject WScript.Shell
$folders = [pscustomobject]@{ Root=$work.FullName }
$folders | Add-Member ScriptMethod Item { param($key) Join-Path $this.Root $key }
$wrapper = [pscustomobject]@{ SpecialFolders=$folders; Real=$shell }
$wrapper | Add-Member ScriptMethod CreateShortcut { param($file) $this.Real.CreateShortcut($file) }
function New-Object($ComObject) { if ($ComObject -ne 'WScript.Shell') { throw 'Unexpected COM object' }; $wrapper }
function Assert($condition,$message) { if (!$condition) { throw $message } }
function Tag($path) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($path.ToUpperInvariant()))).Substring(0,16)
}
function Write-OldLink($state, $oldDir) {
    $path = Join-Path $startup ("ProjectHub Edge " + (Tag $state) + ".lnk")
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = (Get-Command pwsh.exe).Source
    $lnk.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -Action Start -StateDirectory "{1}" -EdgeExe "{2}"' -f (Join-Path $oldDir 'User-Dispatch.ps1'),$state,$old
    $lnk.Save()
    $path
}
$testState = Join-Path $testLocal 'ProjectHub/Edge'
$newState = Join-Path $testLocal 'Nodicarta/Edge'
$oldDir = Join-Path $testLocal ("ProjectHub/Dispatch/" + (Tag $testState))
$newDir = Join-Path $testLocal ("Nodicarta/Dispatch/" + (Tag $newState))
$privateScript = Join-Path $work.FullName 'User-Dispatch.ps1'
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../User-Dispatch.ps1'),(Join-Path $PSScriptRoot '../Edge-StatePaths.ps1') -Destination $work.FullName
    # Substitute only Windows known folders, including those used by the actual
    # migration implementation. All process, shortcut and locking operations are real.
    Add-Content -LiteralPath (Join-Path $work.FullName 'Edge-StatePaths.ps1') -Value (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Isolated-StatePaths.ps1'))
    $unpaired = Join-Path $testMachine 'ProjectHub/Edge'
    $missingDir = Join-Path $testLocal ('ProjectHub/Dispatch/' + (Tag $unpaired))
    $orphan = Write-OldLink $unpaired $missingDir
    $null = & $privateScript -Action Migrate -StateDirectory $unpaired -EdgeExe $new
    Assert ((Test-Path -LiteralPath $orphan) -and !(Test-Path -LiteralPath $missingDir)) 'An orphaned shortcut must not start a worker'
    # The old CLI pairs with a disposable loopback HTTP fixture, then executes a
    # real dispatch --watch process. No projects or analysis tasks are assigned.
    $pair = @'
const http=require('node:http'), {spawn}=require('node:child_process'), {randomUUID}=require('node:crypto');
const [exe,state]=process.argv.slice(1), offer=randomUUID(), code='aa'.repeat(16);
const server=http.createServer((q,s)=>{q.resume();s.writeHead(201,{'Content-Type':'application/json'});s.end(JSON.stringify({deviceId:randomUUID(),edgeId:randomUUID(),accessToken:'a'.repeat(64),refreshToken:'b'.repeat(64),expiresAt:new Date(Date.now()+3600000).toISOString()}));});
server.listen(0,'127.0.0.1',()=>{const p=spawn(exe,['--state',state,'pair','--core','http://127.0.0.1:'+server.address().port],{windowsHide:true,stdio:['pipe','ignore','inherit']});p.stdin.end(offer+'.'+code+'\n');p.on('exit',c=>{server.close();process.exitCode=c;});});
'@
    & node -e $pair $old $testState
    Assert ($LASTEXITCODE -eq 0) 'Could not pair the test device'
    $null = [IO.Directory]::CreateDirectory($oldDir)
    $oldController = Join-Path $oldDir 'User-Dispatch.ps1'
    [IO.File]::WriteAllText($oldController,'# original fixture controller')
    $oldLink = Write-OldLink $testState $oldDir
    $info = [Diagnostics.ProcessStartInfo]::new($old)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    foreach ($arg in 'dispatch','--watch','--state',$testState) { $info.ArgumentList.Add($arg) }
    $info.Environment['PROJECTHUB_DISPATCH_STOP_FILE'] = Join-Path $oldDir 'stop'
    $oldProcess = [Diagnostics.Process]::Start($info)
    Assert (!$oldProcess.WaitForExit(1300)) 'Legacy dispatch did not stay running'
    @{processId=$oldProcess.Id; startUtcTicks=$oldProcess.StartTime.ToUniversalTime().Ticks; exe=$old} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $oldDir 'process.json')
    $first = & $privateScript -Action Migrate -StateDirectory $testState -EdgeExe $new
    Assert ($oldProcess.HasExited -and $first.Running -and $first.StartsAtLogin -and $first.StateDirectory -eq $newState) 'Live dispatch handoff failed'
    Assert ((Test-Path -LiteralPath (Join-Path $newState 'edge.sqlite3')) -and (Get-Item -LiteralPath $testState).LinkTarget -eq $newState) 'State did not physically migrate'
    $link = Join-Path $startup ('Nodicarta Edge ' + (Tag $newState) + '.lnk')
    Assert ((Test-Path -LiteralPath $link) -and !(Test-Path -LiteralPath $oldLink)) 'Startup shortcut did not migrate'
    Assert ($shell.CreateShortcut($link).IconLocation -eq "$new,0") 'Dispatch startup shortcut must display the installed Edge CLI icon'
    $pidFile = Join-Path $newDir 'process.json'
    $running = (Get-Content -Raw -LiteralPath $pidFile | ConvertFrom-Json).processId
    $again = & $oldController -Action Start -StateDirectory $testState -EdgeExe $old
    Assert ($again.Running -and (Get-Content -Raw -LiteralPath $pidFile | ConvertFrom-Json).processId -eq $running) 'Legacy command launched a second worker'
    $stopped = & $privateScript -Action Disable -StateDirectory $newState -EdgeExe $new
    Assert (!$stopped.Running -and !$stopped.StartsAtLogin -and !(Test-Path -LiteralPath $link)) 'Disable must stop and remove startup'
    "PASS active dispatch handoff, physical data and prior controller preserved, old command reuses new PID, broken shortcut remains inactive"
} finally {
    if ($oldProcess -and !$oldProcess.HasExited) { $oldProcess.Kill(); $oldProcess.WaitForExit() }
    $record = Join-Path $newDir 'process.json'
    if ([IO.File]::Exists($record)) {
        $known = Get-Content -Raw -LiteralPath $record | ConvertFrom-Json
        $p = Get-Process -Id $known.processId -ErrorAction SilentlyContinue
        if ($p -and $p.Path -eq $new -and $p.StartTime.ToUniversalTime().Ticks -eq $known.startUtcTicks) { $p.Kill(); $p.WaitForExit() }
    }
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('Nodicarta-Dispatch-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
