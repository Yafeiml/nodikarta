function Get-EdgeDesktopImageHash([string]$Executable) {
    $assembly = [IO.Path]::ChangeExtension($Executable, '.dll')
    $image = if ([IO.File]::Exists($assembly)) { $assembly } else { $Executable }
    return (Get-FileHash -LiteralPath $image).Hash
}

function Save-WindowsTestWindow($window, [string]$EvidenceDirectory, [string]$name) {
    if (!$EvidenceDirectory) { return }
    $null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
    Add-Type -AssemblyName System.Drawing
    if (!('EdgeQaCapture' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EdgeQaCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; public Point(int x, int y) { X=x; Y=y; } }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect rect, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    public static bool Resize(IntPtr h, int width, int height) {
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        try { return SetWindowPos(h, IntPtr.Zero, 0, 0, width, height, 0x16); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
'@
    }
    $priorAwareness = [EdgeQaCapture]::SetThreadDpiAwarenessContext([intptr](-4))
    try {
        $hwnd = [intptr]$window.Current.NativeWindowHandle
        $rect = [EdgeQaCapture+Rect]::new()
        if (!$hwnd -or ![EdgeQaCapture]::GetWindowRect($hwnd,[ref]$rect)) { throw 'Cannot capture the test window.' }
        $bmp = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bmp)
        try {
            $dc = $graphics.GetHdc()
            try { $rendered = [EdgeQaCapture]::PrintWindow($hwnd,$dc,2) }
            finally { $graphics.ReleaseHdc($dc) }
            $colors = [Collections.Generic.HashSet[int]]::new()
            for ($y=35; $y -lt $bmp.Height-10; $y+=5) {
                for ($x=10; $x -lt $bmp.Width-10; $x+=5) { $null = $colors.Add($bmp.GetPixel($x,$y).ToArgb()) }
            }
            if (!$rendered -or $colors.Count -lt 10) {
                # Some GPU surfaces are missing from PrintWindow. Only capture a fully exposed test window.
                for ($y=10; $y -lt $bmp.Height; $y+=25) {
                    for ($x=10; $x -lt $bmp.Width; $x+=25) {
                        $point = [EdgeQaCapture+Point]::new($rect.Left+$x,$rect.Top+$y)
                        if ([EdgeQaCapture]::GetAncestor([EdgeQaCapture]::WindowFromPoint($point),2) -ne $hwnd) { throw 'Test window is covered. Cannot safely capture.' }
                    }
                }
                $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bmp.Size)
            }
            $area = [Drawing.Rectangle]::new(0,0,$bmp.Width,$bmp.Height)
            $frame = [EdgeQaCapture+Rect]::new()
            if ([EdgeQaCapture]::DwmGetWindowAttribute($hwnd,9,[ref]$frame,16) -eq 0) {
                $visible = [Drawing.Rectangle]::FromLTRB($frame.Left-$rect.Left,$frame.Top-$rect.Top,$frame.Right-$rect.Left,$frame.Bottom-$rect.Top)
                $area.Intersect($visible)
            }
            $png = $bmp.Clone($area,$bmp.PixelFormat)
            try { $png.Save((Join-Path $EvidenceDirectory ($name + '.png')),[Drawing.Imaging.ImageFormat]::Png) }
            finally { $png.Dispose() }
        } finally { $graphics.Dispose(); $bmp.Dispose() }
    } finally { $null = [EdgeQaCapture]::SetThreadDpiAwarenessContext($priorAwareness) }
}
