#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$OwnerAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string]$StateDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Edge-StatePaths.ps1')
$serviceName = 'ProjectHubEdge'
$account = "NT SERVICE\$serviceName"
$sc = Join-Path $env:SystemRoot 'System32/sc.exe'
$ownerSid = ([Security.Principal.NTAccount]::new($OwnerAccount)).Translate([Security.Principal.SecurityIdentifier])
$serviceSidText = (& $sc showsid $serviceName | Out-String)
$serviceSidValue = [regex]::Match($serviceSidText, 'S-1-5-80(?:-\d+){5}').Value
if (!$serviceSidValue) { throw '无法确定 Windows 服务账户。' }
$serviceSid = [Security.Principal.SecurityIdentifier]::new($serviceSidValue)
$bin = Join-Path $PSScriptRoot 'bin'
$manifest = Join-Path $PSScriptRoot 'SHA256SUMS'
if (!(Test-Path -LiteralPath (Join-Path $bin 'ProjectHub.Edge.exe')) -or !(Test-Path -LiteralPath $manifest)) {
    throw '请先解压完整的 Windows 发布包，再运行安装脚本。'
}
$release = (Get-FileHash -LiteralPath $manifest).Hash.Substring(0,12).ToLowerInvariant()
$installRoot = Join-Path $env:ProgramFiles 'ProjectHub/Edge'
$releaseDirectory = Join-Path $installRoot $release
$requestedState = $StateDirectory
$executable = Join-Path $releaseDirectory 'ProjectHub.Edge.exe'

. (Join-Path $PSScriptRoot 'Test-EdgeRelease.ps1')
Test-EdgeRelease $bin $manifest
$existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($existing -and ($existing.StartName -ne $account -or !$existing.PathName.StartsWith('"' + $installRoot + '\', [StringComparison]::OrdinalIgnoreCase))) {
    throw '此名称已由其他服务占用。'
}
if ($existing -and !$PSBoundParameters.ContainsKey('StateDirectory')) {
    if ($existing.PathName -notmatch ' --state "([^"]+)"$') { throw '请显式指定已有服务的数据目录。' }
    $requestedState = $Matches[1]
}
$statePlan = Get-EdgeStatePlan ($requestedState ? $requestedState : (Join-Path (Get-EdgeKnownFolder CommonApplicationData) 'Nodicarta/Edge'))
if ($existing -and $statePlan.Independent -and $existing.PathName -match ' --state "([^"]+)"$' -and
    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Matches[1])) -ne $statePlan.Requested) {
    throw '已有服务使用独立的数据目录，请先核对服务注册。'
}
$state = $statePlan.Path
$serviceCommand = '"{0}" service --state "{1}"' -f $executable, $state
if (!(Test-Path -LiteralPath $releaseDirectory)) {
    $null = [IO.Directory]::CreateDirectory($installRoot)
    $staging = Join-Path $installRoot ([Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $bin -Destination $staging -Recurse
    Test-EdgeRelease $staging $manifest
    if ((Get-Item -LiteralPath $staging).Parent.FullName -ne [IO.Path]::GetFullPath($installRoot) -or
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($releaseDirectory)) -ne [IO.Path]::GetFullPath($installRoot)) {
        throw '发行目录不在受保护的安装目录内。'
    }
    Move-Item -LiteralPath $staging -Destination $releaseDirectory
} else { Test-EdgeRelease $releaseDirectory $manifest }

$wasRunning = $existing -and $existing.State -eq 'Running'
$restoreCommand = if ($existing) { $existing.PathName }
if ($existing) { Stop-Service $serviceName -ErrorAction Stop }
try {
Invoke-EdgeStateMigration $executable $statePlan
if ($existing -and $statePlan.Legacy -and $existing.PathName -match '^(.* --state )"([^"]+)"$' -and
    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Matches[2])) -eq $statePlan.Legacy) {
    $restoreCommand = $Matches[1] + '"' + $state + '"'
}

$folder = [IO.DirectoryInfo]::new($state)
$sids = @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),$ownerSid,$serviceSid)
if ($folder.Exists) {
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl($folder)
    if ($folder.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or !$acl.AreAccessRulesProtected -or
        (!(Test-Path -LiteralPath (Join-Path $state 'edge.sqlite3')) -and @($folder.EnumerateFileSystemInfos()).Count -gt 0)) {
        throw '现有数据目录不属于已受保护的 Edge 实例，请指定一个新的专用目录。'
    }
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference -notin $sids) {
            throw '此数据目录授权给另一 Windows 身份，请核对原来的 OwnerAccount 和目录。'
        }
    }
} else {
    $null = [IO.Directory]::CreateDirectory($folder.Parent.FullName)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
}
foreach ($sid in $sids) {
    $rights = if ($sid -in @($ownerSid,$serviceSid)) { 'Modify' } else { 'FullControl' }
    $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,'ContainerInherit,ObjectInherit','None','Allow'))
}
if ($folder.Exists) { [IO.FileSystemAclExtensions]::SetAccessControl($folder,$acl) }
else { [IO.FileSystemAclExtensions]::Create($folder,$acl) }
if ($existing) {
    & $sc config $serviceName 'binPath=' $serviceCommand 'start=' 'auto' 'DisplayName=' 'Nodicarta Edge'
} else {
    & $sc create $serviceName 'binPath=' $serviceCommand 'start=' 'auto' 'obj=' $account 'DisplayName=' 'Nodicarta Edge'
}
if ($LASTEXITCODE) { throw 'Windows 无法注册 Edge 服务。' }
$registered = $true
Start-Service $serviceName -ErrorAction Stop
} catch {
    if ($existing) {
        & $sc config $serviceName 'binPath=' $restoreCommand
        if ($wasRunning -and !$LASTEXITCODE) { Start-Service $serviceName }
    } elseif ($registered) { & $sc delete $serviceName }
    throw
}
[PSCustomObject]@{ Service = $serviceName; StateDirectory = $state; Executable = $executable; Owner = $OwnerAccount }
