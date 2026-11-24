#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Public')]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version = '1.1.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/edge-public'),
    [Parameter(Mandatory,ParameterSetName='Test')][uri]$TestFeed,
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPublicKeyFile,
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPrivateKeyFile
)
$ErrorActionPreference = 'Stop'
$testing = $PSCmdlet.ParameterSetName -eq 'Test'
$channel = if ($testing) { 'test' } else { 'stable' }
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$out = Join-Path $root "$channel/$Version"
$build = @{Version=$Version;OutputDirectory=$out;ArchiveDirectory=(Join-Path $root "test-archives/$channel/$Version")}
if ($testing) {
    $build.TestFeed = $TestFeed
    $build.TestPublicKeyFile = $TestPublicKeyFile
}
$candidate = & (Join-Path $PSScriptRoot 'Build-EdgeCandidate.ps1') @build
$sign = @{Installer=$candidate.Installer;Version=$Version;OutputDirectory=$out}
if ($testing) {
    $sign.TestPrivateKeyFile = $TestPrivateKeyFile
    $sign.TestAssetUrl = [uri]::new($TestFeed, "v$Version/$([IO.Path]::GetFileName($candidate.Installer))")
}
$signed = & (Join-Path $PSScriptRoot 'Sign-EdgeRelease.ps1') @sign
[pscustomobject]@{Version=$Version;Channel=$channel;Installer=$candidate.Installer;Archive=$candidate.Archive
    Package=$candidate.Package;Manifest=$signed.Manifest;Signature=$signed.Signature;Publisher=$signed.Publisher}
