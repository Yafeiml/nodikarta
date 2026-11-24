# Run with windows-pairing-buttons.ps1 -LanguagePreference and an isolated, unpaired state.
$windowsLanguage = [Globalization.CultureInfo]::GetCultureInfo([EdgeLanguageInput]::PreferredUiLanguage())
$systemCode = if ($windowsLanguage.TwoLetterISOLanguageName -eq 'zh') { 'zh-CN' } else { 'en' }
$alternateCode = if ($systemCode -eq 'zh-CN') { 'en' } else { 'zh-CN' }
$preferenceFile = Join-Path $child.ArgumentList[1] 'ui-language.txt'
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0]
$languageIds = @('FollowSystemLanguage','ChineseLanguage','EnglishLanguage')
$null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
function Restart-IsolatedWindow {
    $app.Kill()
    $app.WaitForExit()
    $app.Dispose()
    $script:app = [Diagnostics.Process]::Start($child)
    $pidMatch = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    $script:window = Await-Ui 'restarted isolated client' {
        [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidMatch) |
            Where-Object { $_.Current.AutomationId -eq 'EdgeMainWindow' } | Select-Object -First 1
    }
    $script:hwnd = [intptr]$window.Current.NativeWindowHandle
}
function Assert-Language($step,$mode) {
    $english = $mode -eq 'en' -or ($mode -eq 'auto' -and $systemCode -eq 'en')
    $title = if ($english) { 'Nodicarta Edge' } else { '脉络 Edge' }
    $button = if ($english) { 'Language' } else { '语言' }
    $pair = if ($english) { 'Pair device' } else { '配对设备' }
    $hint = if ($english) { 'Connect this device to Nodicarta Core.' } else { '将此设备连接到脉络 Core。' }
    $text = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$hint)
    $null = Await-Ui $step {
        $window.Current.Name -ceq $title -and (ById 'LanguageButton').Current.Name -ceq $button -and
        (ById 'PairButton').Current.Name -ceq $pair -and (ById 'DesktopVersion').Current.Name -ceq "v$productVersion" -and
        $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$text)
    }
    $saved = if ([IO.File]::Exists($preferenceFile)) { [IO.File]::ReadAllText($preferenceFile).Trim() } else { $null }
    if (($mode -eq 'auto' -and $null -ne $saved) -or ($mode -ne 'auto' -and $saved -cne $mode)) { throw "$step preference file: '$saved'" }
    $results.Add([ordered]@{step=$step;title=$title;languageButton=$button;storedOverride=$saved;footer="v$productVersion"})
}
function Inspect-LanguageMenu($mode,[switch]$Keyboard) {
    if ($Keyboard) {
        (ById 'LanguageButton').SetFocus()
        Send-EdgeLanguageKey $hwnd 13
    } else { Click-EdgeLanguageItem $window 'LanguageButton' }
    $items = @($languageIds | ForEach-Object { Find-EdgeLanguageItem $window $_ -Wait })
    $allMenuItems = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem)
    $visibleMenu = $window.FindAll([Windows.Automation.TreeScope]::Descendants,$allMenuItems)
    $names = @($items | ForEach-Object { $_.Current.Name })
    $first = if ($mode -eq 'en' -or ($mode -eq 'auto' -and $systemCode -eq 'en')) {'Auto'} else {'自动'}
    if ($visibleMenu.Count -ne 3 -or (($names -join '|') -cne "$first|中文|English")) { throw "Unexpected menu items: $names" }
    $target = Get-EdgeLanguageTarget $window $items[0]
    $popup = [Windows.Automation.AutomationElement]::FromHandle($target.Handle)
    foreach ($item in $items) {
        if ($item.Current.ProcessId -ne $app.Id) { throw 'Unexpected popup owner' }
    }
    Save-WindowsTestWindow $popup $EvidenceDirectory "menu-$mode"
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute($target.Handle,9,[ref]$frame,16)) { throw 'No popup frame' }
    $scale = [EdgeQaCapture]::GetDpiForWindow($target.Handle)/96
    # Windows UIA exposes these Avalonia menu items without a selection pattern. Read the radio marks in the captured popup.
    $bmp = [Drawing.Bitmap]::new((Join-Path $EvidenceDirectory "menu-$mode.png"))
    try {
        $counts = @(foreach ($item in $items) {
            $r=$item.Current.BoundingRectangle
            $mid = [int]($r.Top+$r.Height/2-$frame.Top)
            $count=0
            for ($y=$mid-[int](7*$scale); $y -le $mid+[int](7*$scale); $y++) {
                for ($x=[int]($r.Left-$frame.Left+6*$scale); $x -lt $r.Left-$frame.Left+29*$scale; $x++) {
                    $pixel=$bmp.GetPixel($x,$y)
                    if ((299*$pixel.R+587*$pixel.G+114*$pixel.B) -lt 155000) { $count++ }
                }
            }
            $count
        })
    } finally { $bmp.Dispose() }
    $expectedIndex = @('auto','zh-CN','en').IndexOf($mode)
    if ($counts[$expectedIndex] -lt 6 -or @(0..2 | Where-Object {$_ -ne $expectedIndex -and $counts[$_] -gt 3}).Count) {
        throw "Incorrect radio marks for $mode : $counts"
    }
    $results.Add([ordered]@{menu=$mode;labels=$names;radioMarkPixels=$counts})
    return $target.Handle
}
function Test-LanguageHover($language,$layout) {
    function Move-ToControl($id) {
        $control = ById $id
        if ($control.Current.ProcessId -ne $app.Id) { throw 'Unexpected control owner' }
        # Move only the test window under the stationary system pointer; Win32 keeps real hover tracking active.
        $p = [EdgeLanguageInput+Point]::new()
        $outer = [EdgeQaCapture+Rect]::new()
        if (![EdgeQaCapture]::GetWindowRect($hwnd,[ref]$outer) -or ![EdgeLanguageInput]::GetPhysicalCursorPos([ref]$p)) { throw 'Cannot locate test window' }
        $bounds = $control.Current.BoundingRectangle
        $left = [int]($outer.Left+$p.X-$bounds.Left-$bounds.Width/2)
        $top = [int]($outer.Top+$p.Y-$bounds.Top-$bounds.Height/2)
        if (![EdgeLanguageInput]::SetWindowPos($hwnd,[intptr]::Zero,$left,$top,0,0,0x15)) { throw 'Cannot position test window' }
        Start-Sleep -Milliseconds 75
        if (![EdgeLanguageInput]::ScreenToClient($hwnd,[ref]$p) -or
            ![EdgeLanguageInput]::PostMessage($hwnd,0x200,0,(($p.Y -shl 16) -bor ($p.X -band 65535)))) {
            throw "Cannot send a mouse-move event to test control $id"
        }
        Start-Sleep -Milliseconds 160
    }
    function Capture-Form($name) {
        Save-WindowsTestWindow $window $scratch.FullName $name
        [Drawing.Bitmap]::new((Join-Path $scratch.FullName "$name.png"))
    }
    $frame = [EdgeQaCapture+Rect]::new()
    if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16)) { throw 'No window frame' }
    $scale = [EdgeQaCapture]::GetDpiForWindow($hwnd)/96
    $languageBounds = (ById 'LanguageButton').Current.BoundingRectangle
    $close = (ById 'HideWindow').Current.BoundingRectangle
    $closeX = [int]($close.Left-$frame.Left+6*$scale)
    $closeY = [int]($close.Top-$frame.Top+$close.Height/2)
    (ById 'CoreAddress').SetFocus()
    $original = [EdgeQaCapture+Rect]::new()
    if (![EdgeQaCapture]::GetWindowRect($hwnd,[ref]$original)) { throw 'Cannot read initial bounds' }
    $normal = $null
    try {
        Move-ToControl 'CoreAddress'
        $normal = Capture-Form 'normal'
        # The adjacent close button still has a hover fill. It verifies real pointer routing on this HWND.
        Move-ToControl 'HideWindow'
        $witness = Capture-Form 'close-hover'
        try {
            $oldClose = $normal.GetPixel($closeX,$closeY).ToArgb()
            $hoverClose = $witness.GetPixel($closeX,$closeY).ToArgb()
            if ($oldClose -eq $hoverClose -or ($hoverClose -band 0xffffff) -ne 0xfff0f0) {
                throw ('Close button did not enter hover ({0:X6} -> {1:X6}, at {2},{3})' -f ($oldClose -band 0xffffff),($hoverClose -band 0xffffff),$closeX,$closeY)
            }
        } finally { $witness.Dispose() }
        Move-ToControl 'LanguageButton'
        $hover = Capture-Form 'language-hover'
        try {
            if ($hover.GetPixel($closeX,$closeY).ToArgb() -ne $oldClose) { throw 'Pointer did not leave the close button' }
            $changed = 0; $samples = 0
            $x0 = [int]($languageBounds.Left-$frame.Left+8*$scale)
            $x1 = [int]($languageBounds.Right-$frame.Left-8*$scale)
            $y0 = [int]($languageBounds.Top-$frame.Top+4*$scale)
            $y1 = [int]($languageBounds.Bottom-$frame.Top-7*$scale)
            foreach ($band in $y0,$y1) {
                for ($y=$band; $y -lt $band+[int](3*$scale); $y++) {
                    for ($x=$x0; $x -lt $x1; $x++) {
                        $samples++
                        if ($normal.GetPixel($x,$y).ToArgb() -ne $hover.GetPixel($x,$y).ToArgb()) { $changed++ }
                    }
                }
            }
            $before = '#{0:X6}' -f ($normal.GetPixel($x0,$y0).ToArgb() -band 0xffffff)
            $after = '#{0:X6}' -f ($hover.GetPixel($x0,$y0).ToArgb() -band 0xffffff)
            if ($changed) { throw "$language/$layout hover fill changed $before to $after ($changed/$samples pixels)" }
            Copy-Item -LiteralPath (Join-Path $scratch.FullName 'language-hover.png') -Destination (Join-Path $EvidenceDirectory "hover-$language-$layout.png") -Force
            $results.Add([ordered]@{hoverLanguage=$language;layout=$layout;windowWidthPx=$window.Current.BoundingRectangle.Width;
                normalFill=$before;hoverFill=$after;samples=$samples;changedPixels=$changed;closeHoverFill='#FFF0F0'})
        } finally { $hover.Dispose() }
    } finally {
        if ($normal) { $normal.Dispose() }
        $null = [EdgeLanguageInput]::SetWindowPos($hwnd,[intptr]::Zero,$original.Left,$original.Top,0,0,0x15)
    }
}
$prior = [EdgeLanguageInput]::SetThreadDpiAwarenessContext([intptr](-4))
try {
    Assert-Language 'first launch uses Auto' 'auto'
    (ById 'DeviceName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('QA Device')
    Save-WindowsTestWindow $window $scratch.FullName 'initial'
    Test-LanguageHover $systemCode 'default'
    $menuHandle = Inspect-LanguageMenu 'auto' -Keyboard
    Send-EdgeLanguageKey $menuHandle 27
    $null = Await-Ui 'Esc dismisses menu and restores button focus' { !(ById 'EnglishLanguage') -and (ById 'LanguageButton').Current.HasKeyboardFocus }
    Assert-Language 'Esc keeps selection' 'auto'
    Select-EdgeLanguage $window $systemCode
    Assert-Language 'select same display language explicitly' $systemCode
    Restart-IsolatedWindow
    Assert-Language 'same-language manual selection survives restart' $systemCode
    $menuHandle = Inspect-LanguageMenu $systemCode -Keyboard
    $alternateItem = ById $(if ($alternateCode -eq 'en') { 'EnglishLanguage' } else { 'ChineseLanguage' })
    for ($i=0; $i -lt 4 -and !$alternateItem.Current.HasKeyboardFocus; $i++) {
        Send-EdgeLanguageKey $menuHandle 40
        Start-Sleep -Milliseconds 100
    }
    if (!$alternateItem.Current.HasKeyboardFocus) { throw 'Down arrow did not focus the alternate language' }
    Send-EdgeLanguageKey $menuHandle 13
    Assert-Language 'arrows and Enter select alternate language' $alternateCode
    Restart-IsolatedWindow
    Start-Sleep -Milliseconds 5500
    Assert-Language 'manual selection survives restart and Windows settings poll' $alternateCode
    (ById 'DeviceName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('QA Device')
    Save-WindowsTestWindow $window $EvidenceDirectory "pairing-$alternateCode"
    Test-LanguageHover $alternateCode 'default'
    $menuHandle = Inspect-LanguageMenu $alternateCode
    Click-EdgeLanguageItem $window $(if ($alternateCode -eq 'en') { 'EnglishLanguage' } else { 'ChineseLanguage' })
    Assert-Language 'reselecting current language preserves preference' $alternateCode
    Select-EdgeLanguage $window 'auto'
    Assert-Language 'Auto removes override and follows Windows display language' 'auto'
    Restart-IsolatedWindow
    Assert-Language 'Auto survives restart without preference file' 'auto'
    (ById 'DeviceName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('QA Device')
    $scale = [EdgeQaCapture]::GetDpiForWindow($hwnd)/96
    if (![EdgeQaCapture]::Resize($hwnd,[int](460*$scale),[int](552*$scale))) { throw 'Cannot resize test window' }
    $null = Await-Ui 'narrow window' { $window.Current.BoundingRectangle.Width -lt 480*$scale }
    $vf=(ById 'DesktopVersion').Current.BoundingRectangle
    if ($vf.Width -le 0 -or $vf.Right -gt $window.Current.BoundingRectangle.Right) { throw 'Footer version clipped' }
    Save-WindowsTestWindow $window $EvidenceDirectory "pairing-$systemCode-narrow"
    Test-LanguageHover $systemCode 'narrow'
    Select-EdgeLanguage $window $alternateCode
    Assert-Language 'switch to alternate language in narrow window' $alternateCode
    Test-LanguageHover $alternateCode 'narrow'
    Select-EdgeLanguage $window 'auto'
    Assert-Language 'restore Auto in narrow window' 'auto'
    $null = Inspect-LanguageMenu 'auto'
    [ordered]@{version=$productVersion;checkedAtUtc=[datetime]::UtcNow.ToString('o');windowsBuild=[Environment]::OSVersion.Version.Build;
        windowsDisplayLanguage=$windowsLanguage.Name;desktopImageSha256=(Get-EdgeDesktopImageHash $exe);
        cases=$results} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'checks.json') -Encoding utf8
} finally { $null = [EdgeLanguageInput]::SetThreadDpiAwarenessContext($prior) }
