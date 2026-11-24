#requires -Version 7.0
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Installer
$version = $file.VersionInfo.ProductVersion
$tag = "edge-rehearsal-v$version-12345"
$temp = [IO.Directory]::CreateTempSubdirectory('nodicarta-rehearsal-sign-')
$keyName = "Nodicarta.Edge.QA.$([Guid]::NewGuid().ToString('N'))"
$handle = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $keyName)
try {
    # The signer's public key must match. Exercise its production branch against a disposable key pair.
    $windows = [IO.Directory]::CreateDirectory((Join-Path $temp 'deploy/windows')).FullName
    $release = [IO.Directory]::CreateDirectory((Join-Path $temp 'release')).FullName
    $sign = Join-Path $windows 'Sign-EdgeRelease.ps1'
    foreach ($name in 'Sign-EdgeRelease.ps1','ValidateReleaseTrust.ps1') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$name") -Destination $windows
    }
    $key = [Security.Cryptography.ECDsaCng]::new($handle)
    [IO.File]::WriteAllText((Join-Path $release 'edge-update-public.pem'), $key.ExportSubjectPublicKeyInfoPem())
    $argsForSign = @{Installer=$file.FullName; Version=$version; KeyName=$keyName; OutputDirectory=$temp.FullName}
    $stable = & $sign @argsForSign
    $before = (Get-FileHash -LiteralPath $stable.Signature).Hash
    $rehearsal = & $sign @argsForSign -RehearsalTag $tag
    foreach ($case in @(@($stable,'stable',"v$version"), @($rehearsal,'rehearsal',$tag))) {
        $result, $channel, $expectedTag = $case
        $bytes = [IO.File]::ReadAllBytes($result.Manifest)
        $descriptor = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $signature = [Convert]::FromBase64String([IO.File]::ReadAllText($result.Signature))
        if ([IO.Path]::GetFileName($result.Manifest) -cne "nodicarta-edge-$channel-win-x64.json" -or
            $result.Signature -cne "$($result.Manifest).sig" -or $descriptor.channel -cne $channel -or
            $descriptor.version -cne $version -or $descriptor.size -ne $file.Length -or
            $descriptor.sha256 -cne (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() -or
            $descriptor.url -cne "https://github.com/Yafeiml/nodikarta/releases/download/$expectedTag/$($file.Name)" -or
            $signature.Length -ne 64 -or !$key.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw "Invalid $channel manifest." }
    }
    if ((Get-FileHash -LiteralPath $stable.Signature).Hash -cne $before) { throw 'Rehearsal replaced the stable signature.' }
    foreach ($bad in "edge-rehearsal-v${version}.0-12","EDGE-rehearsal-v$version-12","$tag/asset","${tag}?x",
                      "edge-rehearsal-v$version-１２","$tag`n","v$version",'https://example.com/edge') {
        try { & $sign @argsForSign -RehearsalTag $bad } catch {
            if ($_.Exception.Message -notlike 'RehearsalTag must match*') { throw }
            continue
        }
        throw "Unexpectedly accepted tag: $bad"
    }
    'PASS: independent stable and rehearsal manifests, fixed repository and version, P1363 verification and invalid tag rejection.'
} finally {
    if ($key) { $key.Dispose() }
    $handle.Delete()
    $handle.Dispose()
    $tempParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())
    if ($temp.Parent.FullName -eq $tempParent -and $temp.Name.StartsWith('nodicarta-rehearsal-sign-')) {
        Remove-Item -LiteralPath $temp.FullName -Force -Recurse
    }
}
