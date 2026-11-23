# Target only the isolated test application's window and its Avalonia popup.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EdgeLanguageInput {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern bool GetUserPreferredUILanguages(uint flags, out uint count, [Out] char[] names, ref uint length);
    public static string PreferredUiLanguage() {
        const uint names = 8;
        uint length = 0;
        if (GetUserPreferredUILanguages(names, out _, null, ref length) && length > 1) {
            var buffer = new char[length];
            if (GetUserPreferredUILanguages(names, out uint count, buffer, ref length) && count != 0)
                return new string(buffer).Split('\0')[0];
        }
        return System.Globalization.CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name;
    }
}
'@
function Find-EdgeLanguageItem($RootWindow, $Id, [switch]$Wait) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $until = [DateTime]::UtcNow.AddSeconds(6)
    do {
        try { $item = $RootWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition) }
        catch [System.Runtime.InteropServices.COMException] { $item = $null }
        if ($item -or !$Wait) { return $item }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $until)
    throw "Language control unavailable: $Id"
}
function Get-EdgeLanguageTarget($RootWindow,$Item) {
    $prior = [EdgeLanguageInput]::SetThreadDpiAwarenessContext([intptr](-4))
    try {
        $rect = $Item.Current.BoundingRectangle
        $point = [EdgeLanguageInput+Point]::new()
        $point.X = $rect.Left + $rect.Width/2
        $point.Y = $rect.Top + $rect.Height/2
        $handle = [EdgeLanguageInput]::GetAncestor([EdgeLanguageInput]::WindowFromPoint($point),2)
        if (!$handle -or [Windows.Automation.AutomationElement]::FromHandle($handle).Current.ProcessId -ne $RootWindow.Current.ProcessId) {
            throw 'Language menu test surface is covered'
        }
        if (![EdgeLanguageInput]::ScreenToClient($handle,[ref]$point)) { throw 'Invalid control coordinates' }
        @{Handle=$handle; Position=($point.Y -shl 16) -bor ($point.X -band 65535)}
    } finally { $null = [EdgeLanguageInput]::SetThreadDpiAwarenessContext($prior) }
}
function Send-EdgeLanguageKey($Handle,[int]$Key) {
    $null = [EdgeLanguageInput]::PostMessage($Handle,0x100,$Key,1)
    $null = [EdgeLanguageInput]::PostMessage($Handle,0x101,$Key,0xc0000001L)
}
function Click-EdgeLanguageItem($RootWindow,$Id) {
    $item = Find-EdgeLanguageItem $RootWindow $Id -Wait
    $target = Get-EdgeLanguageTarget $RootWindow $item
    $null = [EdgeLanguageInput]::PostMessage($target.Handle,0x201,1,$target.Position)
    $null = [EdgeLanguageInput]::PostMessage($target.Handle,0x202,0,$target.Position)
}
function Select-EdgeLanguage($RootWindow,[ValidateSet('auto','zh-CN','en')]$Preference) {
    $ids = @{auto='FollowSystemLanguage'; 'zh-CN'='ChineseLanguage'; en='EnglishLanguage'}
    Click-EdgeLanguageItem $RootWindow 'LanguageButton'
    Click-EdgeLanguageItem $RootWindow $ids[$Preference]
}
