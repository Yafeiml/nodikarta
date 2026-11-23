#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Public')]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version = '1.0.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/edge-public'),
    [Parameter(Mandatory,ParameterSetName='Test')][uri]$TestFeed,
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPublicKeyFile,
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPrivateKeyFile
)
$ErrorActionPreference = 'Stop'
$testing = $PSCmdlet.ParameterSetName -eq 'Test'
$pem = (Resolve-Path (Join-Path $PSScriptRoot '../../release/edge-update-public.pem')).Path
$pub = [Security.Cryptography.ECDsa]::Create()
try {
    $pub.ImportFromPem([IO.File]::ReadAllText($pem))
    $official = [Convert]::ToBase64String($pub.ExportSubjectPublicKeyInfo())
    $feed = [uri]'https://github.com/Yafeiml/nodikarta/releases/latest/download/nodicarta-edge-stable-win-x64.json'
    if ($testing) {
        if (!$TestFeed.IsLoopback -or $TestFeed.Scheme -ne 'http' -or $TestFeed.UserInfo -or $TestFeed.Query -or $TestFeed.Fragment -or
            $TestFeed.Segments[-1] -cne 'nodicarta-edge-stable-win-x64.json') { throw 'A loopback test feed is required.' }
        $feed = $TestFeed
        $pub.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $TestPublicKeyFile)))
    }
    $key = [Convert]::ToBase64String($pub.ExportSubjectPublicKeyInfo())
    if ($pub.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7' -or ($testing -and $key -eq $official)) {
        throw 'The test and public channels require independent P-256 keys.'
    }
} finally { $pub.Dispose() }
$channel = if ($testing) { 'test' } else { 'stable' }
$out = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$channel/$Version")).FullName
$package = & (Join-Path $PSScriptRoot 'Publish-Edge.ps1') -Desktop -Version $Version -OutputDirectory $out -UpdateFeed $feed -PublisherPublicKey $key -TestChannel:$testing |
    Where-Object { $_.PSObject.Properties['Archive'] }
if (!$package -or ![IO.File]::Exists($package.Archive)) { throw 'Desktop build did not return a verified archive.' }
$setup = & (Join-Path $PSScriptRoot 'Build-EdgeSetup.ps1') -PackageDirectory $package.Directory -Version $Version -OutputDirectory $out -TestScope:$testing |
    Where-Object { $_ -is [IO.FileInfo] }
if (!$setup -or !$setup.Exists) { throw 'The installer build did not return a setup executable.' }
$sign = @{Installer=$setup.FullName;Version=$Version;OutputDirectory=$out}
if ($testing) {
    $sign.TestPrivateKeyFile = $TestPrivateKeyFile
    $sign.TestAssetUrl = [uri]::new($feed, "v$Version/$($setup.Name)")
}
$signed = & (Join-Path $PSScriptRoot 'Sign-EdgeRelease.ps1') @sign
[pscustomobject]@{Version=$Version;Channel=$channel;Installer=$setup.FullName;Archive=$package.Archive
    Package=$package.Directory;Manifest=$signed.Manifest;Signature=$signed.Signature;Publisher=$signed.Publisher}
