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
$out = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$setup = Join-Path $out "Nodicarta-Edge-$Version-win-x64-setup.exe"
$options = @('/V2', "/DPACKAGE=$package", "/DOUTPUT=$setup", "/DVERSION=$Version",
    "/DAPP_ICON=$((Get-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/ProjectHub.Edge.Desktop/Assets/edge.ico')).FullName)")
if ($TestScope) { $options += '/DFIXTURE' }
$compiler = Get-Command 'makensis.exe' -CommandType Application | Select-Object -First 1 -ExpandProperty Source
& $compiler @options (Join-Path $PSScriptRoot 'EdgeSetup.nsi') | Out-Host
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'NSIS setup build failed.' }
Get-Item -LiteralPath $setup
