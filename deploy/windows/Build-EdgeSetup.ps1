#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][Version]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$TestScope
)
$ErrorActionPreference = 'Stop'
$package = (Get-Item -LiteralPath $PackageDirectory).FullName
$bin = Join-Path $package 'bin'
. (Join-Path $PSScriptRoot 'Test-EdgeRelease.ps1')
Test-EdgeRelease $bin (Join-Path $package 'SHA256SUMS')
. (Join-Path $PSScriptRoot 'ValidateReleaseTrust.ps1')
Assert-EdgeSetupTrust $bin $Version $TestScope.IsPresent
$out = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)).FullName
$setup = Join-Path $out "Nodicarta-Edge-$Version-win-x64-setup.exe"
$staging = Join-Path $out ('.' + [Guid]::NewGuid().ToString('N') + '.tmp.exe')
$options = @('/V2', "/DPACKAGE=$package", "/DOUTPUT=$staging", "/DVERSION=$Version",
    "/DAPP_ICON=$((Get-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/ProjectHub.Edge.Desktop/Assets/edge.ico')).FullName)")
if ($TestScope) { $options += '/DFIXTURE' }
$compiler = Get-Command 'makensis.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (!$compiler) {
    $compiler = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } | ForEach-Object {
        Join-Path $_ 'NSIS\makensis.exe'
    } | Where-Object { [IO.File]::Exists($_) } | Select-Object -First 1
}
if (!$compiler) { throw 'Install NSIS 3 with Unicode support and put makensis.exe on PATH.' }
try {
    & $compiler @options (Join-Path $PSScriptRoot 'EdgeSetup.nsi') | Out-Host
    if ($LASTEXITCODE -or ![IO.File]::Exists($staging)) { throw 'NSIS setup build failed.' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($staging)
    $scope = if ($TestScope) { 'Nodicarta Edge test installer' } else { 'Nodicarta Edge public installer' }
    if ($info.ProductVersion -cne $Version.ToString() -or $info.Comments -cne $scope) { throw 'Incorrect NSIS version or scope.' }
    [IO.File]::Move($staging, $setup, $true)
} finally { [IO.File]::Delete($staging) }
Get-Item -LiteralPath $setup
