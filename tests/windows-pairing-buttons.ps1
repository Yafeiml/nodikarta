#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$TextMenus,
    [switch]$LegacyEnglishMenus,
    [switch]$LanguagePreference
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $PSScriptRoot 'Save-WindowsTestWindow.ps1')
. (Join-Path $PSScriptRoot 'WindowsLanguageMenu.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PairingWindowInput {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr flags);
}
'@
$exe = (Get-Item -LiteralPath $Executable).FullName
$scratch = [IO.Directory]::CreateTempSubdirectory('nodicarta-pairing-')
$results = [Collections.Generic.List[object]]::new()
function Await-Ui($label, [scriptblock]$check) {
    $until = [datetime]::UtcNow.AddSeconds(12)
    do {
        $value = & $check
        if ($value) { return $value }
        Start-Sleep -Milliseconds 100
    } until ([datetime]::UtcNow -ge $until)
    throw "Timed out waiting for $label"
}
function ById($id) {
    $where = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$where)
}
function Read-Fill($bmp, $rect, $frame, $scale) {
    # This point is inside the fill and outside the text, icon, corners and focus outline.
    $pixel = $bmp.GetPixel([int]($rect.Left-$frame.Left+5*$scale),[int]($rect.Top-$frame.Top+$rect.Height/2))
    '#{0:X2}{1:X2}{2:X2}' -f $pixel.R,$pixel.G,$pixel.B
}
function Find-FillEdge($bitmap, [int]$near, [int]$y, $fill, [switch]$Last) {
    $hits = @(foreach ($x in ($near-5)..($near+5)) {
        $pixel = $bitmap.GetPixel($x,$y)
        if (('#{0:X2}{1:X2}{2:X2}' -f $pixel.R,$pixel.G,$pixel.B) -eq $fill) { $x }
    })
    if (!$hits.Count) { throw "Missing button edge at $near,$y" }
    if ($Last) { $hits[-1] } else { $hits[0] }
}
function Tab-To($control) {
    if ($window.Current.ProcessId -ne $app.Id) { throw 'Unexpected input window' }
    if (![PairingWindowInput]::PostMessage($hwnd,0x100,[intptr]9,[intptr]1) -or
        ![PairingWindowInput]::PostMessage($hwnd,0x101,[intptr]9,[intptr]0xC0000001L)) { throw 'Could not deliver Tab to the test window' }
    $null = Await-Ui $control.Current.AutomationId { $control.Current.HasKeyboardFocus }
}
try {
    foreach ($lang in 'zh-CN','en') {
        $child = [Diagnostics.ProcessStartInfo]::new($exe)
        $child.UseShellExecute = $false
        $homeDir = [IO.Directory]::CreateDirectory((Join-Path $scratch.FullName "profile-$lang")).FullName
        $child.Environment['USERPROFILE'] = $child.Environment['HOME'] = $homeDir
        $child.Environment['CODEX_HOME'] = Join-Path $homeDir '.codex'
        $child.Environment['APPDATA'] = Join-Path $homeDir 'AppData/Roaming'
        $child.Environment['LOCALAPPDATA'] = Join-Path $homeDir 'AppData/Local'
        $child.Environment['XDG_CONFIG_HOME'] = Join-Path $homeDir '.config'
        foreach ($key in 'OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT','CLAUDE_CONFIG_DIR','DSH_HOME','PI_CODING_AGENT_DIR') { $null=$child.Environment.Remove($key) }
        $child.ArgumentList.Add('--state')
        $child.ArgumentList.Add((Join-Path $scratch.FullName "state-$lang"))
        $app = [Diagnostics.Process]::Start($child)
        try {
            $match = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
            $window = Await-Ui 'pairing window' {
                [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$match) |
                    Where-Object { $_.Current.AutomationId -eq 'EdgeMainWindow' } | Select-Object -First 1
            }
            $hwnd = [intptr]$window.Current.NativeWindowHandle
            if ($LanguagePreference) {
                . (Join-Path $PSScriptRoot 'windows-language-preference.ps1')
                return
            }
            if ($TextMenus) {
                . (Join-Path $PSScriptRoot 'windows-text-menus.ps1')
                return
            }
            $isEnglish = $lang -eq 'en'
            $pairLabel = if ($isEnglish) { 'Pair device' } else { '配对设备' }
            $openLabel = if ($isEnglish) { 'Open Core' } else { '打开 Core' }
            $pair = Await-Ui 'pair button' { ById 'PairButton' }
            if ($pair.Current.Name -cne $pairLabel) {
                Select-EdgeLanguage $window $lang
            }
            $null = Await-Ui $pairLabel { $pair.Current.Name -ceq $pairLabel }
            $open = ById 'PairOpenCore'
            $code = ById 'PairingCode'
            $url = ById 'CoreAddress'
            if ($open.Current.Name -cne $openLabel) { throw 'Wrong Open Core label' }
            foreach ($layout in 'default','narrow') {
                Save-WindowsTestWindow $window $scratch.FullName 'form'
                $scale = [EdgeQaCapture]::GetDpiForWindow($hwnd)/96
                if ($layout -eq 'narrow') {
                    $code.SetFocus()
                    if (![EdgeQaCapture]::Resize($hwnd,460*$scale,552*$scale)) { throw 'Resize failed' }
                    $null = Await-Ui 'minimum width' { $window.Current.BoundingRectangle.Width -lt 480*$scale }
                    Start-Sleep -Milliseconds 250
                    Save-WindowsTestWindow $window $scratch.FullName 'form'
                }
                $op=$open.Current.BoundingRectangle; $pa=$pair.Current.BoundingRectangle
                $gap = $pa.Left - $op.Right
                $inset = $code.Current.BoundingRectangle.Right - $pa.Right
                if ([Math]::Abs($op.Width-112*$scale) -gt 1 -or [Math]::Abs($op.Height-34*$scale) -gt 1 -or
                    [Math]::Abs($op.Width-$pa.Width) -gt 1 -or [Math]::Abs($op.Height-$pa.Height) -gt 1 -or
                    [Math]::Abs($op.Top-$pa.Top) -gt 1 -or [Math]::Abs($gap-8*$scale) -gt 1 -or [Math]::Abs($inset) -gt 1) {
                    throw "$lang/$layout geometry: $($op.Width)x$($op.Height) vs $($pa.Width)x$($pa.Height), gap=$gap, right offset=$inset"
                }
                $frame=[EdgeQaCapture+Rect]::new()
                if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw 'Frame unavailable' }
                $png = Join-Path $scratch.FullName 'form.png'
                $image = [Drawing.Bitmap]::new($png)
                try {
                    $secondary = Read-Fill $image $op $frame $scale
                    $primary = Read-Fill $image $pa $frame $scale
                    $y = [int]($pa.Top-$frame.Top+$pa.Height/2)
                    $openRight = Find-FillEdge $image ($op.Right-$frame.Left) $y $secondary -Last
                    $pairLeft = Find-FillEdge $image ($pa.Left-$frame.Left) $y $primary
                    $visibleGap = $pairLeft - $openRight - 1
                    $widthPx = $image.Width
                } finally { $image.Dispose() }
                if ($visibleGap -ne [int][Math]::Round(8*$scale)) { throw "$lang/$layout painted gap is $visibleGap px" }
                if ($secondary -notin '#4B6476','#3B5366','#304657' -or
                    $primary -notin '#174E9D','#123F82','#103667') { throw "Unexpected fills $secondary and $primary" }
                if (!$open.Current.IsEnabled -or !$pair.Current.IsEnabled) { throw 'Disabled pairing action' }
                $code.SetFocus()
                Tab-To $open
                Tab-To $pair
                if ($lang -eq 'zh-CN' -and $layout -eq 'default') {
                    $null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
                    Copy-Item -LiteralPath $png -Destination (Join-Path $EvidenceDirectory 'pairing-zh.png') -Force
                }
                $results.Add([ordered]@{ language=$lang; layout=$layout; windowWidthPx=$widthPx; dpi=96*$scale;
                    buttonWidthPx=$pa.Width; buttonHeightPx=$pa.Height; gapPx=$gap; paintedGapPx=$visibleGap; rightOffsetPx=$inset;
                    openFill=$secondary; pairFill=$primary; tabOrder=@('PairingCode','PairOpenCore','PairButton') })
            }
            # Force local validation so neither handler can open a browser or redeem a code.
            $url.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('invalid address')
            $open.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $invalid = if ($isEnglish) { 'Enter a complete Core address.' } else { '请输入完整的 Core 地址。' }
            $null = Await-Ui 'invalid URL feedback' { (ById 'Notice').Current.Name -ceq $invalid }
            $pair.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $blank = if ($isEnglish) { 'Paste the one-time pairing code generated by Core.' } else { '请粘贴 Core 生成的一次性配对码。' }
            $null = Await-Ui 'missing code feedback' { (ById 'Notice').Current.Name -ceq $blank }
        } finally {
            if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
            $app.Dispose()
        }
    }
    [ordered]@{ checkedAtUtc=[datetime]::UtcNow.ToString('o'); windowsBuild=[Environment]::OSVersion.Version.Build;
        productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion;
        desktopDllSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -LiteralPath $exe) 'ProjectHub.Edge.Desktop.dll')).Hash;
        localValidation=@('invalid Core URL','empty pairing code'); cases=$results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'checks.json') -Encoding utf8
    $results | ForEach-Object { [pscustomobject]$_ } | Format-Table language,layout,windowWidthPx,buttonWidthPx,buttonHeightPx,gapPx,paintedGapPx,rightOffsetPx,openFill,pairFill
} finally {
    if ($scratch.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $scratch.Name.StartsWith('nodicarta-pairing-')) {
        Remove-Item -LiteralPath $scratch.FullName -Recurse -Force
    }
}
