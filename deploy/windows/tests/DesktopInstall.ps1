#requires -Version 7.0
param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][guid]$GuestUuid)
$ErrorActionPreference = 'Stop'
function Assert($Passed, $Message) { if (!$Passed) { throw $Message } }
Assert ($GuestUuid -ne [guid]::Empty -and [guid](Get-CimInstance Win32_ComputerSystemProduct).UUID -eq $GuestUuid -and
    (Get-CimInstance Win32_ComputerSystem).Model -match 'VMware|Virtual Machine|VirtualBox|KVM|QEMU') 'Use the designated disposable Windows VM.'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nodicarta Edge.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Nodicarta Edge.lnk'
foreach ($path in $menu,$startup,($menu -replace 'Nodicarta Edge','ProjectHub Edge'),($startup -replace 'Nodicarta Edge','ProjectHub Edge')) {
    Assert (!(Test-Path -LiteralPath $path)) 'Restore the clean VM snapshot.'
}
Assert (!(Get-Process -Name ProjectHub.Edge.Desktop,ProjectHub.Edge -ErrorAction SilentlyContinue)) 'The VM already has a running Edge.'
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-desktop-install-')
$package = Join-Path $work.FullName 'package'
$install = Join-Path $work.FullName 'installed desktop'
$state = Join-Path $work.FullName 'isolated state'
$entry = Join-Path $package 'Install-EdgeDesktop.ps1'
try {
    [IO.Compression.ZipFile]::ExtractToDirectory((Get-Item -LiteralPath $Archive).FullName, $package)
    $shell = New-Object -ComObject WScript.Shell
    $first = & $entry -InstallDirectory $install -StateDirectory $state
    $launcher = Join-Path $install 'ProjectHub.Edge.exe'
    $desktop = Join-Path $install 'ProjectHub.Edge.Desktop.exe'
    $manifest = Join-Path $install 'SHA256SUMS'
    $expectedArgs = '--state "' + $state + '"'
    Assert ($first.Executable -eq $desktop -and $first.AgentCommand -eq $launcher -and
        $shell.CreateShortcut($menu).TargetPath -eq $launcher -and $shell.CreateShortcut($menu).Arguments -eq $expectedArgs -and
        $shell.CreateShortcut($startup).TargetPath -eq $launcher -and $shell.CreateShortcut($startup).Arguments -eq "--background $expectedArgs") 'The shortcuts lost the root launcher or selected state.'
    Assert ([IO.File]::Exists($manifest) -and [IO.File]::Exists((Join-Path $install 'NodicartaEdgeCli.dll')) -and
        !(Test-Path -LiteralPath (Join-Path $install 'active-release')) -and
        !@(Get-ChildItem -LiteralPath $install -Directory -Force | Where-Object Name -cmatch '^[0-9a-f]{12}$').Count) 'The installation retained a version directory or pointer.'

    & $entry -InstallDirectory $install -Action DisableStartup
    $null = & $entry -InstallDirectory $install
    Assert (!(Test-Path -LiteralPath $startup) -and $shell.CreateShortcut($menu).Arguments -eq $expectedArgs) 'Reinstall reset state or login preference.'
    & $entry -InstallDirectory $install -Action EnableStartup
    Assert ($shell.CreateShortcut($startup).TargetPath -eq $launcher -and $shell.CreateShortcut($startup).Arguments -eq "--background $expectedArgs") 'Re-enable lost the fixed command.'

    $payload = Join-Path $package 'bin/NodicartaEdgeCli.dll'
    $unaltered = [IO.File]::ReadAllBytes($payload)
    $rejected = $false
    try {
        [IO.File]::AppendAllText($payload, 'tampered')
        $null = & $entry -InstallDirectory $install
    } catch { $rejected = $true }
    finally { [IO.File]::WriteAllBytes($payload, $unaltered) }
    Assert ($rejected -and [IO.File]::Exists($desktop)) 'Tampered package changed the installation.'

    $unrelated = Join-Path $install 'user-file.txt'
    [IO.File]::WriteAllText($unrelated, 'keep')
    & $entry -InstallDirectory $install -Action Uninstall
    Assert (!(Test-Path -LiteralPath $menu) -and !(Test-Path -LiteralPath $startup) -and
        ![IO.File]::Exists($launcher) -and ![IO.File]::Exists($desktop) -and ![IO.File]::Exists($manifest) -and
        [IO.File]::Exists($unrelated) -and (Test-Path -LiteralPath $state)) 'Uninstall left runtime files or removed unrelated data.'
    'PASS: ZIP installer layout, startup preferences, tamper rejection and uninstall.'
} finally {
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        $work.Name.StartsWith('nodicarta-desktop-install-')) { Remove-Item -LiteralPath $work.FullName -Recurse -Force }
}
