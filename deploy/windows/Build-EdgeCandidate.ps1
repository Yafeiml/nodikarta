#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Public')]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ArchiveDirectory,
    [Parameter(Mandatory,ParameterSetName='Test')][uri]$TestFeed,
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPublicKeyFile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ValidateReleaseTrust.ps1')
$official = Get-EdgePublicSpki (Join-Path $PSScriptRoot '../../release/edge-update-public.pem')
$testing = $PSCmdlet.ParameterSetName -eq 'Test'
$feed = [uri](Get-EdgeStableFeed)
$key = $official
if ($testing) {
    if (!$TestFeed.IsLoopback -or $TestFeed.Scheme -ne 'http' -or $TestFeed.UserInfo -or $TestFeed.Query -or $TestFeed.Fragment -or
        $TestFeed.Segments[-1] -cne 'nodicarta-edge-stable-win-x64.json') { throw 'A loopback test feed is required.' }
    $feed = $TestFeed
    $key = Get-EdgePublicSpki (Get-Item -LiteralPath $TestPublicKeyFile).FullName
    if ($key -ceq $official) { throw 'The test and public channels require independent P-256 keys.' }
}
if (!$ArchiveDirectory) { $ArchiveDirectory = [IO.Directory]::CreateTempSubdirectory('nodicarta-archive-').FullName }
$package = & (Join-Path $PSScriptRoot 'Publish-Edge.ps1') -NativeAot -Desktop -Version $Version -OutputDirectory $ArchiveDirectory -UpdateFeed $feed -PublisherPublicKey $key -TestChannel:$testing |
    Where-Object { $_.PSObject.Properties['Archive'] }
if (!$package -or ![IO.File]::Exists($package.Archive)) { throw 'Desktop build did not return a verified archive.' }
$setup = & (Join-Path $PSScriptRoot 'Build-EdgeSetup.ps1') -PackageDirectory $package.Directory -Version $Version -OutputDirectory $OutputDirectory -TestScope:$testing |
    Where-Object { $_ -is [IO.FileInfo] }
if (!$setup -or !$setup.Exists) { throw 'The installer build did not return a setup executable.' }
[pscustomobject]@{Version=$Version;Installer=$setup.FullName;Archive=$package.Archive;Package=$package.Directory}
