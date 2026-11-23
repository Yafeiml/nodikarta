#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$serviceName = 'ProjectHubEdge'
$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if (!$service) { Write-Output '未安装脉络 Edge 服务。'; return }
$root = Join-Path $env:ProgramFiles 'ProjectHub/Edge'
if ($service.StartName -ne "NT SERVICE\$serviceName" -or !$service.PathName.StartsWith('"' + $root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '该服务不属于此 Windows 发布包。'
}
Stop-Service $serviceName
& (Join-Path $env:SystemRoot 'System32/sc.exe') delete $serviceName
if ($LASTEXITCODE) { throw '服务卸载失败。' }
Write-Output '已移除 Windows 服务。设备配对、工作区绑定和版本目录保留。若停用设备，请在 Core 网页撤销。'
