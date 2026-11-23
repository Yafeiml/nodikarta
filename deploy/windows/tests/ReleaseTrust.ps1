#requires -Version 7.0
param([Parameter(Mandatory)][string]$TestPackage,[Parameter(Mandatory)][string]$PublicPackage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ValidateReleaseTrust.ps1')
$builder = (Resolve-Path (Join-Path $PSScriptRoot '../Build-EdgeSetup.ps1')).Path
$temp = [IO.Directory]::CreateTempSubdirectory('nodicarta-trust-check-')
function Expect-Error([scriptblock]$operation,[string]$expected) {
    try { & $operation } catch {
        if ($_.Exception.Message -notlike $expected) { throw }
        return
    }
    throw "Expected rejection matching '$expected'."
}
try {
    $probe = Join-Path $temp.FullName 'unexpected-installer'
    foreach ($package in $TestPackage,$PublicPackage) {
        $bin = Join-Path $package 'bin'
        $version = (Get-Content -Raw (Join-Path $bin 'nodicarta-release.json') | ConvertFrom-Json).version
        $fixture = $package -eq $TestPackage
        Assert-EdgeSetupTrust $bin $version $fixture
        $reason = if ($fixture) { '*non-production payload*' } else { '*non-test payload*' }
        Expect-Error { & $builder -PackageDirectory $package -Version $version -OutputDirectory $probe -TestScope:(!$fixture) } $reason
        if (Test-Path -LiteralPath $probe) { throw 'Wrong-channel payload reached the installer build.' }
    }
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $path = Join-Path $temp.FullName 'public-input.pem'
        $pub = $key.ExportSubjectPublicKeyInfoPem()
        [IO.File]::WriteAllText($path,$pub)
        if ((Get-EdgePublicSpki $path) -cne [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())) { throw 'Public PEM changed.' }
        foreach ($bad in $key.ExportPkcs8PrivateKeyPem(),($pub + "`n" + $key.ExportPkcs8PrivateKeyPem()),($pub + "`n" + $pub)) {
            [IO.File]::WriteAllText($path,$bad)
            Expect-Error { Get-EdgePublicSpki $path } '*single P-256 PUBLIC KEY*'
        }
    } finally { $key.Dispose() }
    'PASS: public and QA package identities verified independently; crossed packaging, mixed keys and private public-key inputs rejected.'
} finally {
    if ($temp.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        $temp.Name.StartsWith('nodicarta-trust-check-')) { Remove-Item -LiteralPath $temp.FullName -Recurse -Force }
}
