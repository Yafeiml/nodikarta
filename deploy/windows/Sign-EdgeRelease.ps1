#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Public')]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [string]$OutputDirectory,
    [Parameter(ParameterSetName='Public')][string]$KeyName = 'Nodicarta.Edge.Release.P256.v1',
    [Parameter(Mandatory,ParameterSetName='Test')][string]$TestPrivateKeyFile,
    [Parameter(Mandatory,ParameterSetName='Test')][uri]$TestAssetUrl
)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Installer
$expected = "Nodicarta-Edge-$Version-win-x64-setup.exe"
$testing = $PSCmdlet.ParameterSetName -eq 'Test'
$comment = if ($testing) { 'Nodicarta Edge test installer' } else { 'Nodicarta Edge public installer' }
if ($file.Name -cne $expected -or $file.VersionInfo.ProductVersion.TrimEnd() -notin $Version,"$Version.0" -or
    $file.VersionInfo.Comments -cne $comment) { throw 'Installer filename, version or signing scope does not match.' }
$url = "https://github.com/Yafeiml/nodikarta/releases/download/v$Version/$expected"
$pub = [Security.Cryptography.ECDsa]::Create()
$signer = $null
$handle = $null
try {
    $pub.ImportFromPem([IO.File]::ReadAllText((Resolve-Path (Join-Path $PSScriptRoot '../../release/edge-update-public.pem'))))
    $official = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pub.ExportSubjectPublicKeyInfo()))
    if ($testing) {
        if (!$TestAssetUrl.IsLoopback -or $TestAssetUrl.Scheme -ne 'http' -or $TestAssetUrl.UserInfo -or $TestAssetUrl.Fragment -or
            $TestAssetUrl.Segments[-1] -cne $expected) { throw 'Test assets must use a versioned loopback HTTP URL.' }
        $url = $TestAssetUrl.AbsoluteUri
        $signer = [Security.Cryptography.ECDsa]::Create()
        $signer.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $TestPrivateKeyFile)))
    } else {
        $handle = [Security.Cryptography.CngKey]::Open($KeyName, [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider)
        $signer = [Security.Cryptography.ECDsaCng]::new($handle)
    }
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signer.ExportSubjectPublicKeyInfo()))
    if ($signer.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7' -or (($actual -eq $official) -eq $testing)) {
        throw 'The P-256 signing key is not valid for this release channel.'
    }
    $manifest = [ordered]@{schema=1;product='nodicarta-edge';channel='stable';rid='win-x64';version=$Version;url=$url
        sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant();size=$file.Length}
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress) + "`n")
    $format = [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
    $sha = [Security.Cryptography.HashAlgorithmName]::SHA256
    $signature = $signer.SignData($bytes,$sha,$format)
    $verify = if ($testing) { $signer } else { $pub }
    if ($signature.Length -ne 64 -or !$verify.VerifyData($bytes,$signature,$sha,$format)) { throw 'Signature self-check failed.' }
    if (!$OutputDirectory) { $OutputDirectory = $file.DirectoryName }
    $out = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
    $manifestPath = Join-Path $out 'nodicarta-edge-stable-win-x64.json'
    [IO.File]::WriteAllBytes($manifestPath, $bytes)
    [IO.File]::WriteAllText("$manifestPath.sig", [Convert]::ToBase64String($signature) + "`n")
    [pscustomobject]@{Manifest=$manifestPath; Signature="$manifestPath.sig"; Publisher=$actual; Uri=$url}
} finally {
    if ($signer) { $signer.Dispose() }
    if ($handle) { $handle.Dispose() }
    $pub.Dispose()
}
