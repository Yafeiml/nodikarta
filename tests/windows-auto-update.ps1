#requires -Version 7.0
# Called by windows-auto-update.cjs with an isolated signed channel, account and Core.
param([Parameter(Mandatory)][string]$OldSetup, [string]$OldVersion, [string]$NewVersion,
      [Parameter(Mandatory)][string]$Root, [string]$Core, [string]$ProjectId, [switch]$Recovery,
      [string]$ReplaySetup, [string]$ReplaySha256)
$ErrorActionPreference = 'Stop'
$fixture = (Resolve-Path -LiteralPath $Root).Path
if ([IO.Path]::GetDirectoryName($fixture) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
    [IO.Path]::GetFileName($fixture) -notmatch '^nodicarta-auto-update-[a-zA-Z0-9]{6}$') { throw 'Disposable fixture required' }
if (Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue) { throw 'Release the real desktop before this test.' }
. (Join-Path $PSScriptRoot '../deploy/windows/Stop-EdgeTestDesktop.ps1')
$node = (Get-Command node -CommandType Application | Select-Object -First 1).Source
$install = Join-Path $fixture 'Edge 安装'
$state = Join-Path $fixture 'paired state'
$project = [IO.Directory]::CreateDirectory((Join-Path $fixture '工作目录')).FullName
$scope = Join-Path $fixture 'user shell'
$user = [IO.Directory]::CreateDirectory((Join-Path $fixture 'user')).FullName
$env:USERPROFILE = $env:HOME = $user
$env:NODICARTA_EDGE_SETUP_TEST_HOME = $scope
$env:NODICARTA_EDGE_SETUP_SCOPE = 'fixture'
$env:NODICARTA_EDGE_STATE = $env:PROJECTHUB_EDGE_STATE = $state
$env:CODEX_HOME = [IO.Directory]::CreateDirectory((Join-Path $user '.codex')).FullName
$env:PATH = Join-Path $env:SystemRoot 'System32'
foreach ($key in 'LOCALAPPDATA','APPDATA','XDG_CONFIG_HOME','PI_CODING_AGENT_DIR','CLAUDE_CONFIG_DIR','DSH_HOME') {
    [Environment]::SetEnvironmentVariable($key, (Join-Path $user $key))
}
foreach ($key in 'OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT') { [Environment]::SetEnvironmentVariable($key,$null) }
function Wait-Check([string]$what,[scriptblock]$check,[int]$seconds=90) {
    $until=[DateTime]::UtcNow.AddSeconds($seconds)
    do { $value=& $check; if ($value) { return $value }; Start-Sleep -Milliseconds 200 } until ([DateTime]::UtcNow -ge $until)
    throw "Timeout: $what"
}
function In-Test {
    @(Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -and
        $_.Path.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase) })
}
function Check($ok,[string]$message) { if (!$ok) { throw $message } }
function Fixture-Reply([string]$command) {
    if ($command) { $rpc.StandardInput.WriteLine($command) }
    $line = $rpc.StandardOutput.ReadLineAsync().WaitAsync([timespan]::FromSeconds(55)).GetAwaiter().GetResult()
    Check ($null -ne $line) 'The MCP fixture exited'
    $line | ConvertFrom-Json
}
function Start-Local([string]$file,[string[]]$arguments,[switch]$Wait) {
    $info=[Diagnostics.ProcessStartInfo]::new($file)
    $info.UseShellExecute=$false
    $info.WorkingDirectory=[IO.Path]::GetDirectoryName($file)
    foreach ($a in $arguments) { $info.ArgumentList.Add($a) }
    $p=[Diagnostics.Process]::Start($info)
    if (!$Wait) { return $p }
    try { Check ($p.WaitForExit(60000) -and $p.ExitCode -eq 0) 'Desktop registration did not finish' } finally { $p.Dispose() }
}
function Database-Fingerprint {
    # Hash the actual identity, workspace binding and folder policy; no credential leaves this process.
    $script = @'
const {DatabaseSync}=require('node:sqlite'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=process.argv[1],folders=fs.readdirSync(root,{recursive:true,encoding:'utf8'});
const hash=crypto.createHash('sha256'); let identities=0,bindings=0;
for(const file of folders.filter(f=>path.basename(f)==='edge.sqlite3').sort()){
  const db=new DatabaseSync(path.join(root,file),{readOnly:true});
  const users=db.prepare('SELECT core,status,hex(credentials) AS credential FROM identity ORDER BY singleton').all();
  const workspaces=db.prepare('SELECT * FROM workspaces ORDER BY local_path').all();
  const policies=db.prepare('SELECT * FROM workspace_access ORDER BY workspace_id').all();
  identities+=users.length;bindings+=workspaces.length;hash.update(JSON.stringify([file,users,workspaces,policies]));db.close();
}
if(!identities || !bindings) throw Error('Fixture is missing its real pairing or folder');
console.log(hash.digest('hex'));
'@
    $digest=& $node --no-warnings -e $script $state
    Check ($LASTEXITCODE -eq 0 -and $digest -match '^[a-f0-9]{64}$') 'Could not check fixture database'
    return $digest
}
function Skills-Fingerprint {
    $files=Get-ChildItem (Join-Path $user '.agents/skills') -File -Force -Recurse | Sort-Object FullName
    Check (@($files | Where-Object Name -eq 'SKILL.md').Count -eq 5) 'The installed Skills fixture is incomplete'
    return (($files | Get-FileHash).Hash -join ',')
}
$live=Join-Path $fixture 'resident-live'
[IO.File]::WriteAllText($live,'isolated test')
$rpc = $null
try {
    # NSIS reads everything after the unquoted last /D= as a single destination.
    $info=[Diagnostics.ProcessStartInfo]::new($OldSetup); $info.UseShellExecute=$false
    $info.Arguments="/S /D=$install"
    $setup=[Diagnostics.Process]::Start($info)
    try { Check ($setup.WaitForExit(90000) -and $setup.ExitCode -eq 0) 'Initial setup failed' } finally { $setup.Dispose() }
    $old=Wait-Check 'old version resident' { @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $OldVersion } }
    Check ($old.MainWindowHandle -eq 0) 'Quiet install opened a main window'
    $manifest=Join-Path $install 'SHA256SUMS'
    $oldBuild=(Get-FileHash -LiteralPath $manifest).Hash
    $cli=Join-Path $install 'ProjectHub.Edge.exe'
    (Get-Content -Raw -LiteralPath (Join-Path $fixture 'pairing.txt')) | & $cli --state $state pair --core $Core | Out-Null
    Check ($LASTEXITCODE -eq 0) 'Fixture could not pair'
    & $cli --state $state bind --project $ProjectId --path $project | Out-Null
    Check ($LASTEXITCODE -eq 0) 'Fixture could not bind its workspace'
    Start-Local $old.Path @('--connect-codex','--state',$state) -Wait
    $skillsPreference=Join-Path $user '.agents/.projecthub-skills-autoupdate'
    [IO.File]::WriteAllText($skillsPreference,'false')
    $config=Join-Path $env:CODEX_HOME 'config.toml'
    $configHash=(Get-FileHash -LiteralPath $config).Hash
    $before=Database-Fingerprint
    $skills=Skills-Fingerprint
    if ($Recovery) { . (Join-Path $PSScriptRoot 'windows-setup-recovery.ps1'); return }
    $null=Wait-Check 'a real automatic probe rejected the damaged signature' { Test-Path -LiteralPath (Join-Path $fixture 'signature-served') }
    Start-Sleep -Seconds 2
    Check (!$old.HasExited -and (Get-FileHash -LiteralPath $manifest).Hash -ceq $oldBuild -and
        !(Test-Path -LiteralPath (Join-Path $fixture 'unsafe-download'))) 'An invalid update was downloaded or activated'
    Stop-EdgeTestDesktop $old
    $mcp = [Diagnostics.ProcessStartInfo]::new($node)
    $mcp.UseShellExecute = $false
    $mcp.CreateNoWindow = $true
    $mcp.RedirectStandardInput = $mcp.RedirectStandardOutput = $true
    foreach ($arg in (Join-Path $PSScriptRoot 'windows-draft-session.cjs'),$cli,$state) { $mcp.ArgumentList.Add($arg) }
    $rpc = [Diagnostics.Process]::Start($mcp)
    $null = Fixture-Reply
    Check ((Fixture-Reply connect).ok) 'The test MCP session could not connect'
    [IO.File]::WriteAllText((Join-Path $fixture 'serve-valid'),'ready')
    $previousPid=$old.Id
    $request=Start-Local $cli @('--background','--state',$state)
    try { Check ($request.WaitForExit(10000) -and $request.ExitCode -eq 0) 'The login launcher did not dispatch' } finally { $request.Dispose() }
    $old=Wait-Check 'baseline resident started through the fixed launcher' {
        @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $OldVersion }
    }
    $null=Wait-Check 'valid signature served while MCP is running' {
        $sig = Join-Path $fixture 'signature-served'
        (Test-Path -LiteralPath $sig) -and [IO.File]::ReadAllText($sig) -ceq 'valid'
    }
    Start-Sleep -Seconds 2
    Check (!$old.HasExited -and !(Test-Path -LiteralPath (Join-Path $fixture 'download-count')) -and
        (Get-FileHash -LiteralPath $manifest).Hash -ceq $oldBuild -and (Fixture-Reply probe).ok) 'The valid update did not wait for the live MCP session'
    Check ((Fixture-Reply disconnect).connections -eq 0) 'The test MCP session did not close'
    $next=Wait-Check 'the desktop automatically installed and started the newer signed release' {
        @(In-Test) | Where-Object { $_.MainModule.FileVersionInfo.ProductVersion -eq $NewVersion -and
            !(Test-Path -LiteralPath (Join-Path $install '.nodicarta-install-transaction.json')) -and
            !(Test-Path -LiteralPath (Join-Path $install '.nodicarta-install-backup')) }
    } 150
    Check ($old.HasExited -and @(In-Test).Count -eq 1 -and $next.MainWindowHandle -eq 0) 'Desktop handoff was not quiet and unique'
    $newBuild=(Get-FileHash -LiteralPath $manifest).Hash
    Check ($oldBuild -ne $newBuild -and $next.Path -eq (Join-Path $install 'ProjectHub.Edge.Desktop.exe') -and
        !(Test-Path -LiteralPath (Join-Path $install 'active-release')) -and
        @((Get-ChildItem -LiteralPath $install -Directory -Force) | Where-Object Name -cmatch '^[0-9a-f]{12}$').Count -eq 0) 'The new desktop does not use a flat install'
    $shell=New-Object -ComObject WScript.Shell
    $menu=$shell.CreateShortcut((Join-Path $scope 'Programs/Nodicarta Edge.lnk'))
    $startup=$shell.CreateShortcut((Join-Path $scope 'Startup/Nodicarta Edge.lnk'))
    Check ($menu.TargetPath -eq $cli -and $startup.TargetPath -eq $cli -and $startup.Arguments -eq "--background --state `"$state`"") 'Login settings lost their target or state'
    Check ((Database-Fingerprint) -ceq $before -and [IO.File]::ReadAllText($skillsPreference) -ceq 'false' -and
        $skills -ceq (Skills-Fingerprint) -and $configHash -ceq (Get-FileHash -LiteralPath $config).Hash) 'User pairing, bindings or Skills changed'
    [ordered]@{WindowsBuild=[Environment]::OSVersion.Version.ToString();previous=$OldVersion;current=$NewVersion;
        previousManifest=$oldBuild;activeManifest=$newBuild;invalidSignatureKeptPid=$previousPid;newPid=$next.Id;
        processCount=@(In-Test).Count;silent=$true;startupTargetAndStatePreserved=$true;pairedDatabasePreserved=$true;
        agentCommandAndSkillsPreserved=$true;deferredUntilMcpClosed=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'verified.json')
    Write-Host "PASS: real desktop rejected a damaged signature, waited for MCP, and automatically updated $OldVersion to $NewVersion."
} finally {
    if ($rpc) {
        $rpc.StandardInput.Close()
        if (!$rpc.WaitForExit(10000)) { $rpc.Kill($true); $rpc.WaitForExit() }
        $rpc.Dispose()
    }
    foreach ($p in @(In-Test)) { Stop-EdgeTestDesktop $p }
    if (@(In-Test).Count -eq 0) { [IO.File]::Delete($live) }
}
