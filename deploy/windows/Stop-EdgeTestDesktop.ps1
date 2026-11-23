#requires -Version 7.0
function Stop-EdgeTestDesktop {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    if ($Process.HasExited) { return }
    $null = $Process.Handle # Keep this process identity alive while resolving its tray menu.
    if ([IO.Path]::GetFileName($Process.MainModule.FileName) -ne 'ProjectHub.Edge.Desktop.exe') {
        throw 'Expected the Edge desktop executable.'
    }
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    if (-not ('Nodicarta.TestDelivery.TrayMenu' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Nodicarta.TestDelivery
{
    public static class TrayMenu
    {
        private delegate bool EnumProc(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc visit, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        public static void Select(uint process, IntPtr window, int x, int y)
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process) throw new InvalidOperationException("The Edge tray menu has closed.");
            var previousDpi = SetThreadDpiAwarenessContext((IntPtr)(-4));
            try
            {
                var point = new Point { X = x, Y = y };
                if (!ScreenToClient(window, ref point)) throw new Win32Exception();
                var position = (IntPtr)((point.Y << 16) | (point.X & 0xffff));
                if (!PostMessage(window, 0x200, IntPtr.Zero, position) ||
                    !PostMessage(window, 0x201, (IntPtr)1, position) ||
                    !PostMessage(window, 0x202, IntPtr.Zero, position))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
        }

        public static void Open(uint process)
        {
            IntPtr target = IntPtr.Zero;
            var name = new StringBuilder(128);
            EnumWindows((window, unused) => {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == process && GetClassName(window, name, name.Capacity) > 0 &&
                    name.ToString().StartsWith("AvaloniaMessageWindow ", StringComparison.Ordinal))
                {
                    target = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (target == IntPtr.Zero) throw new InvalidOperationException("The Edge tray is unavailable.");
            // Avalonia.Win32 TrayIconImpl 11.3.13: notify icon 1, WM_TRAYMOUSE, WM_RBUTTONUP.
            if (!PostMessage(target, 0x800, (IntPtr)1, (IntPtr)0x205))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
    }
    $byProcess = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $menuItem = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)
    [Nodicarta.TestDelivery.TrayMenu]::Open($Process.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if ($Process.HasExited) { return }
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)
        foreach ($window in $windows) {
            if ($window.Current.AutomationId -notlike 'AvaloniaTrayPopupRoot_*') { continue }
            foreach ($item in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $menuItem)) {
                if ($item.Current.Name -notin '退出','Quit') { continue }
                $bounds = $item.Current.BoundingRectangle
                if ($bounds.IsEmpty -or $item.Current.IsOffscreen -or !$item.Current.IsEnabled) { continue }
                # Avalonia's native menu item presenter exposes its screen bounds but no InvokePattern.
                [Nodicarta.TestDelivery.TrayMenu]::Select($Process.Id, $window.Current.NativeWindowHandle,
                    [int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
                if (!$Process.WaitForExit(10000)) { throw 'The selected Edge desktop did not exit.' }
                return
            }
        }
        Start-Sleep -Milliseconds 100
    } until ([DateTime]::UtcNow -ge $deadline)
    throw 'Could not identify Quit in the selected Edge tray menu.'
}
