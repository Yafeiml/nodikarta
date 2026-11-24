# Inspect candidate PE files without loading or executing code from the package.
function Get-EdgePublicSpki([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt 4096) { throw 'Expected a single P-256 PUBLIC KEY PEM.' }
    $pem = [regex]::Match([IO.File]::ReadAllText($file.FullName),
        '\A\s*-----BEGIN PUBLIC KEY-----\s+(?<body>[A-Za-z0-9+/=\s]+?)\s+-----END PUBLIC KEY-----\s*\z')
    if (!$pem.Success) { throw 'Expected a single P-256 PUBLIC KEY PEM.' }
    $der = [Convert]::FromBase64String($pem.Groups['body'].Value)
    $ecdsa = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $ecdsa.ImportSubjectPublicKeyInfo($der, [ref]$read)
        if ($read -ne $der.Length -or $ecdsa.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') {
            throw 'Expected a single P-256 PUBLIC KEY PEM.'
        }
        return [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    } finally { $ecdsa.Dispose() }
}

function Get-EdgeStableFeed {
    'https://github.com/Yafeiml/nodikarta/releases/latest/download/nodicarta-edge-stable-win-x64.json'
}

function Get-EdgeEmbeddedRelease([string]$File) {
    $stream = [IO.File]::OpenRead($File)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $assembly = $reader.GetAssemblyDefinition()
            if ($reader.GetString($assembly.Name) -cne 'ProjectHub.Edge.Desktop') { throw 'Unexpected Edge assembly name.' }
            $values = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
            foreach ($handle in $assembly.GetCustomAttributes()) {
                $attr = $reader.GetCustomAttribute($handle)
                if ($attr.Constructor.Kind -ne 'MemberReference') { continue }
                $ctor = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attr.Constructor)
                if ($ctor.Parent.Kind -ne 'TypeReference') { continue }
                $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$ctor.Parent)
                if ($type.ResolutionScope.Kind -ne 'AssemblyReference' -or $reader.GetString($type.Namespace) -cne 'System.Reflection') { continue }
                $reference = $reader.GetAssemblyReference([Reflection.Metadata.AssemblyReferenceHandle]$type.ResolutionScope)
                if ($reader.GetString($reference.Name) -cne 'System.Runtime') { continue }
                $name = $reader.GetString($type.Name)
                if ($name -cnotin 'AssemblyMetadataAttribute','AssemblyInformationalVersionAttribute') { continue }
                $blob = $reader.GetBlobReader($attr.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'Invalid Edge assembly attribute.' }
                $key = if ($name -ceq 'AssemblyInformationalVersionAttribute') { 'Version' } else { $blob.ReadSerializedString() }
                $value = $blob.ReadSerializedString()
                if ($null -eq $key -or $blob.ReadUInt16() -ne 0 -or $blob.RemainingBytes -ne 0 -or !$values.TryAdd($key, $value)) {
                    throw 'Ambiguous Edge assembly attribute.'
                }
            }
            return $values
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-EdgeNativeExports([string]$File) {
    $stream = [IO.File]::OpenRead($File)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if ($pe.HasMetadata -or $pe.PEHeaders.CoffHeader.Machine -ne 'Amd64') { throw "Expected an x64 native PE: $File" }
            $export = $pe.PEHeaders.PEHeader.ExportTableDirectory
            if (!$export.Size) { return }
            $table = $pe.GetSectionData($export.RelativeVirtualAddress).GetReader()
            $table.Offset = 24
            $count = $table.ReadUInt32()
            $table.Offset = 32
            $names = $pe.GetSectionData($table.ReadInt32()).GetReader()
            if ($count -gt $names.RemainingBytes / 4) { throw "Invalid export table in $File" }
            for ($i = 0; $i -lt $count; $i++) {
                $name = $pe.GetSectionData($names.ReadInt32()).GetReader()
                $start = $name.Offset
                while ($name.ReadByte() -ne 0) { }
                $length = $name.Offset - $start - 1
                $name.Offset = $start
                $name.ReadUTF8($length)
            }
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-EdgeSetupTrust([string]$Bin, [Version]$Version, [bool]$TestScope) {
    $desktop = Join-Path $Bin 'ProjectHub.Edge.Desktop.exe'
    $managed = Join-Path $Bin 'ProjectHub.Edge.Desktop.dll'
    $native = @(Get-EdgeNativeExports $desktop) -ccontains 'DotNetRuntimeDebugHeader'
    if ($native) {
        foreach ($name in 'ProjectHub.Edge.Desktop.dll','ProjectHub.Edge.dll','coreclr.dll','clrjit.dll') {
            if (Test-Path -LiteralPath (Join-Path $Bin $name)) { throw "Unexpected managed runtime payload: $name" }
        }
        $null = Get-EdgeNativeExports (Join-Path $Bin 'ProjectHub.Edge.exe')
        $bridge = @(Get-EdgeNativeExports (Join-Path $Bin 'hostfxr.dll'))
        if ($bridge.Count -ne 1 -or $bridge[0] -cne 'hostfxr_main_startupinfo') {
            throw 'Expected the Edge launcher compatibility module.'
        }
        $cliExports = @(Get-EdgeNativeExports (Join-Path $Bin 'NodicartaEdgeCli.dll'))
        if ($cliExports -cnotcontains 'DotNetRuntimeDebugHeader' -or $cliExports -cnotcontains 'hostfxr_main_startupinfo') {
            throw 'The native CLI or its launcher compatibility entry is missing.'
        }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($desktop)
        $fields = $info.Comments -csplit '\|'
        if ($fields.Count -ne 4 -or $fields[0] -cne 'Nodicarta/public-v1' -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Bin 'ProjectHub.Edge.exe')).ProductVersion -cne $Version.ToString() -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Bin 'NodicartaEdgeCli.dll')).ProductVersion -cne $Version.ToString()) {
            throw 'Invalid Native AOT release metadata.'
        }
        $embedded = @{Version=$info.ProductVersion; NodicartaReleaseLine='public-v1'; NodicartaUpdateFeed=$fields[1];
            NodicartaUpdatePublicKey=$fields[2]; NodicartaUpdateTestChannel=$fields[3]}
    } else {
        $embedded = Get-EdgeEmbeddedRelease $managed
    }
    $stamp = Get-Content -LiteralPath (Join-Path $Bin 'nodicarta-release.json') -Raw | ConvertFrom-Json -ErrorAction Stop
    $official = Get-EdgePublicSpki (Join-Path $PSScriptRoot '../../release/edge-update-public.pem')
    $spki = [Convert]::FromBase64String($embedded['NodicartaUpdatePublicKey'])
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $key.ImportSubjectPublicKeyInfo($spki, [ref]$read)
        if ($read -ne $spki.Length -or $key.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7' -or
            [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) -cne $embedded['NodicartaUpdatePublicKey']) { throw 'Invalid embedded P-256 SPKI.' }
    } finally { $key.Dispose() }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($spki))
    $v = $Version.ToString()
    if ($v -notmatch '^\d+\.\d+\.\d+$' -or $embedded['Version'] -cne $v -or $stamp.version -cne $v -or
        $stamp.releaseLine -cne 'public-v1' -or $embedded['NodicartaReleaseLine'] -cne 'public-v1' -or
        $stamp.channel -cne 'stable' -or $stamp.rid -cne 'win-x64' -or $stamp.publisherKeyId -cne $hash -or
        (Get-Item -LiteralPath (Join-Path $Bin 'ProjectHub.Edge.Desktop.exe')).VersionInfo.ProductVersion -cne $v) {
        throw 'Edge release receipt and executable must agree on publisher, platform and version.'
    }
    if ($TestScope) {
        $feed = [uri]$embedded['NodicartaUpdateFeed']
        if ($embedded['NodicartaUpdateTestChannel'] -cne 'true' -or $embedded['NodicartaUpdatePublicKey'] -ceq $official -or
            !$feed.IsAbsoluteUri -or !$feed.IsLoopback -or $feed.Scheme -ne 'http' -or $feed.UserInfo -or $feed.Query -or
            $feed.Fragment -or $feed.Segments[-1] -cne 'nodicarta-edge-stable-win-x64.json') { throw 'Refusing a non-test payload for the test installer.' }
    } elseif ($embedded['NodicartaUpdateTestChannel'] -cne 'false' -or $embedded['NodicartaUpdatePublicKey'] -cne $official -or
        $embedded['NodicartaUpdateFeed'] -cne (Get-EdgeStableFeed)) { throw 'Refusing a non-production payload for the public installer.' }
}
