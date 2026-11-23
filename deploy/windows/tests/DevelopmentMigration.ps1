#requires -Version 7.0
param([Parameter(Mandatory)][string]$DevelopmentArchive, [Parameter(Mandatory)][string]$Setup, [string]$Evidence)
$ErrorActionPreference = 'Stop'
$archive = (Get-Item -LiteralPath $DevelopmentArchive).FullName
$installer = Get-Item -LiteralPath $Setup
if ($installer.VersionInfo.Comments -ne 'Nodicarta Edge test installer') { throw 'Expected an isolated test installer' }
$scratch = [IO.Directory]::CreateTempSubdirectory('nodicarta-dev-migration-')
try {
    $install = [IO.Directory]::CreateDirectory((Join-Path $scratch.FullName 'installed')).FullName
    $incoming = Join-Path $scratch.FullName 'old-package'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$incoming)
    $sum = Join-Path $incoming 'SHA256SUMS'
    . (Join-Path $PSScriptRoot '../Test-EdgeRelease.ps1')
    Test-EdgeRelease (Join-Path $incoming 'bin') $sum
    $oldBuild = (Get-FileHash -LiteralPath $sum).Hash.Substring(0,12).ToLowerInvariant()
    $release = [IO.Directory]::CreateDirectory((Join-Path $install $oldBuild)).FullName
    Copy-Item -LiteralPath $sum,(Join-Path $incoming 'bin') -Destination $release -Recurse
    $exe = Join-Path $release 'bin/ProjectHub.Edge.Desktop.exe'
    $oldVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if ([Version]$oldVersion -le [Version]$installer.VersionInfo.ProductVersion) { throw 'Expected a higher historical development release' }
    Copy-Item -LiteralPath (Join-Path $release 'bin/Nodicarta.Edge.Launcher.exe') -Destination (Join-Path $install 'ProjectHub.Edge.exe')
    $active = Join-Path $install 'active-release'
    [IO.File]::WriteAllText($active,$oldBuild)
    $scope = Join-Path $scratch.FullName 'shell'
    $startup = [IO.Directory]::CreateDirectory((Join-Path $scope 'Startup')).FullName
    $state = [IO.Directory]::CreateDirectory((Join-Path $scratch.FullName 'existing data')).FullName
    $shell = New-Object -ComObject WScript.Shell
    $linkPath = Join-Path $startup 'Nodicarta Edge.lnk'
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $exe
    $link.Arguments = '--background --state "' + $state + '"'
    $link.Description = 'Nodicarta Edge'
    $link.Save()
    $sha = (Get-FileHash -LiteralPath $linkPath).Hash
    $temp = [IO.Directory]::CreateDirectory((Join-Path $scratch.FullName 'temp')).FullName
    $info = [Diagnostics.ProcessStartInfo]::new($installer.FullName)
    $info.UseShellExecute = $false
    $info.Arguments = "/S /D=$install"
    $info.Environment['NODICARTA_EDGE_SETUP_TEST_HOME'] = $scope
    $info.Environment['TEMP'] = $info.Environment['TMP'] = $temp
    $info.Environment['LOCALAPPDATA'] = Join-Path $scratch.FullName 'local'
    $p = [Diagnostics.Process]::Start($info)
    try {
        if (!$p.WaitForExit(60000) -or $p.ExitCode -eq 0) { throw 'A development downgrade proceeded without consent' }
        $code = $p.ExitCode
    } finally { $p.Dispose() }
    if ((Get-Content -LiteralPath (Join-Path $temp 'Nodicarta-Edge-Setup.log') -Raw) -notmatch 'migration requires explicit consent' -or
        [IO.File]::ReadAllText($active) -cne $oldBuild -or (Get-FileHash -LiteralPath $linkPath).Hash -ne $sha -or
        (Test-Path -LiteralPath (Join-Path $install 'public-install.json'))) { throw 'The old release or shortcut changed.' }
    $result = [ordered]@{development=$oldVersion; publicVersion=$installer.VersionInfo.ProductVersion;
        silentImplicitMigrationRefused=$true; exitCode=$code; retainedOldMarkerAndOnlyStartupLink=$true}
    if ($Evidence) { $result | ConvertTo-Json | Set-Content -LiteralPath $Evidence }
    [pscustomobject]$result
} finally {
    if ($scratch.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $scratch.Name -like 'nodicarta-dev-migration-*') {
        Remove-Item -LiteralPath $scratch.FullName -Recurse -Force
    }
}
