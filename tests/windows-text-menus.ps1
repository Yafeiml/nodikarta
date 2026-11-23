# Invoked with -TextMenus by windows-pairing-buttons.ps1 for an isolated, unpaired client.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TextMenuInput {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint caller, uint target, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void SelectAll(IntPtr window) {
        uint caller = GetCurrentThreadId(), target = GetWindowThreadProcessId(window, out _);
        if (!AttachThreadInput(caller, target, true)) throw new Exception("Cannot attach to test window");
        var saved = new byte[256];
        bool captured = false;
        try {
            if (!(captured = GetKeyboardState(saved))) throw new Exception("Cannot read local keyboard state");
            var modified = (byte[])saved.Clone();
            modified[0x11] = modified[0xa2] = 0x80;
            if (!SetKeyboardState(modified)) throw new Exception("Cannot set local Ctrl modifier");
            SendMessage(window, 0x100, (IntPtr)65, (IntPtr)1);
            SendMessage(window, 0x101, (IntPtr)65, (IntPtr)unchecked((int)0xc0000001));
        } finally {
            if (captured) SetKeyboardState(saved);
            AttachThreadInput(caller, target, false);
        }
    }
}
'@
function Send-FormKey([int]$key) {
    $null = [PairingWindowInput]::PostMessage($hwnd,0x100,$key,1)
    $null = [PairingWindowInput]::PostMessage($hwnd,0x101,$key,0xc0000001L)
}
function MenuItems {
    $where = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem)
    $window.FindAll([Windows.Automation.TreeScope]::Descendants,$where)
}
Save-WindowsTestWindow $window $scratch.FullName 'form'
$prior = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
try {
    foreach ($language in 'zh-CN','en','zh-CN') {
        $title = if ($language -eq 'en') { 'Nodicarta Edge' } else { '脉络 Edge' }
        if ($window.Current.Name -cne $title) {
            if ($LegacyEnglishMenus) {
                (ById 'LanguageButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            } else { Select-EdgeLanguage $window $language }
            $null = Await-Ui $title { $window.Current.Name -ceq $title }
        }
        $labels = if ($language -eq 'en' -or $LegacyEnglishMenus) { 'Cut','Copy','Paste' } else { '剪切','复制','粘贴' }
        foreach ($fieldId in 'DeviceName','PairingCode') {
            $field = ById $fieldId
            $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('SampleTest')
            $field.SetFocus()
            [TextMenuInput]::SelectAll($hwnd)
            $rect = $field.Current.BoundingRectangle
            $point = [TextMenuInput+Point]::new()
            $point.X = $rect.Left + 20
            $point.Y = $rect.Top + $rect.Height/2
            if (![TextMenuInput]::ScreenToClient($hwnd,[ref]$point)) { throw 'Cannot locate field' }
            $location = ($point.Y -shl 16) -bor ($point.X -band 65535)
            $null = [PairingWindowInput]::PostMessage($hwnd,0x204,2,$location)
            $null = [PairingWindowInput]::PostMessage($hwnd,0x205,0,$location)
            $null = Await-Ui "$language/$fieldId three menu items" { @(MenuItems).Count -eq 3 }
            $items = @(MenuItems)
            $names = @($items | ForEach-Object { $_.Current.Name })
            $enabled = @($items | ForEach-Object { $_.Current.IsEnabled })
            $allowCopy = $fieldId -eq 'DeviceName'
            if (($names -join '|') -cne ($labels -join '|') -or $enabled[0] -ne $allowCopy -or $enabled[1] -ne $allowCopy) {
                throw "$language/$fieldId unexpected names or state: $names / $enabled"
            }
            if (!$results.Count) {
                $row = $items[0].Current.BoundingRectangle
                $popupHandle = [EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint(
                    [EdgeQaCapture+Point]::new($row.Left+$row.Width/2,$row.Top+$row.Height/2)),2)
                $popup = [Windows.Automation.AutomationElement]::FromHandle($popupHandle)
                if ($popup.Current.ProcessId -eq $app.Id -and $popupHandle -ne $hwnd) {
                    Save-WindowsTestWindow $popup $EvidenceDirectory 'menu-zh'
                }
            }
            Send-FormKey 27
            $null = Await-Ui 'closed text menu' { @(MenuItems).Count -eq 0 }
            $results.Add([ordered]@{language=$language; field=$fieldId; items=$names; enabled=$enabled})
        }
        if ($LegacyEnglishMenus) { break }
    }
    foreach ($fieldId in 'DeviceName','PairingCode') {
        $field=ById $fieldId
        $field.SetFocus()
        $null = Await-Ui 'focused edit field' { $field.Current.HasKeyboardFocus }
        [TextMenuInput]::SelectAll($hwnd)
        Send-FormKey 8
        if ($fieldId -eq 'DeviceName') {
            $pattern = $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
            $null = Await-Ui 'Ctrl+A and Backspace cleared the test string' { $pattern.Current.Value -ceq '' }
        } else {
            (ById 'DeviceName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Test device')
            (ById 'CoreAddress').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('invalid')
            (ById 'PairButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $null = Await-Ui 'Ctrl+A and Backspace cleared the masked code' { (ById 'Notice').Current.Name -ceq '请粘贴 Core 生成的一次性配对码。' }
        }
    }
    $null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
    [ordered]@{version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion;
        windowsBuild=[Environment]::OSVersion.Version.Build; checkedAtUtc=[datetime]::UtcNow.ToString('o');
        shortcut='Ctrl+A, Backspace'; cases=$results} | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'checks.json') -Encoding utf8
} finally {
    $null = [EdgeQaCapture]::SetThreadDpiAwarenessContext($prior)
}
