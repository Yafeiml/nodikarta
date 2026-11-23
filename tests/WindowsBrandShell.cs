using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowsBrandShell
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct ShellFileInfo {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string Type;
    }
    private delegate bool Visit(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder text, int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string name,string title);
    public static IntPtr ShellWindow(string name) => FindWindow(name,null);
    public static IntPtr NotificationChevron() {
        var area=FindWindowEx(ShellWindow("Shell_TrayWnd"),IntPtr.Zero,"TrayNotifyWnd",null);
        var h=IntPtr.Zero;
        while((h=FindWindowEx(area,h,"SIBTrayButton",null))!=IntPtr.Zero)
            if(GetDlgCtrlID(h)==1502 && IsWindowVisible(h)) return h;
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon, out Rect area);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Point p);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    private static Point origin, target;
    private static IntPtr chevron;
    public static string ClickResult { get; private set; }="not requested";
    public static bool ClickStableChevron(IntPtr h,int x,int y) {
        var name=new StringBuilder(80);
        ClickResult="not a visible notification chevron";
        if(GetClassName(h,name,name.Capacity)==0 || name.ToString()!="SIBTrayButton" || GetDlgCtrlID(h)!=1502) return false;
        var p=new Point { X=x,Y=y };
        ClickResult="pointer hit test or cursor query unavailable";
        if(WindowFromPoint(p)!=h || !GetCursorPos(out origin)) return false;
        System.Threading.Thread.Sleep(500);
        ClickResult="cursor moved or target became covered";
        if(!GetCursorPos(out var still) || origin.X!=still.X || origin.Y!=still.Y ||
            GetAsyncKeyState(1)<0 || GetAsyncKeyState(2)<0 || WindowFromPoint(p)!=h) return false;
        target=p;
        ClickResult="desktop rejected cursor movement";
        if(!SetCursorPos(x,y)) return false;
        chevron=h;
        ClickResult="cursor or visibility changed";
        if(!CursorOnChevron()) return false;
        mouse_event(2,0,0,0,UIntPtr.Zero);
        mouse_event(4,0,0,0,UIntPtr.Zero);
        ClickResult="target clicked";
        return true;
    }
    private static bool CursorOnChevron() => GetCursorPos(out var p) && p.X==target.X && p.Y==target.Y && WindowFromPoint(p)==chevron;
    public static void RestoreChevron(bool close) {
        if(chevron==IntPtr.Zero || !CursorOnChevron() || GetAsyncKeyState(1)<0 || GetAsyncKeyState(2)<0) return;
        if(close) {
            mouse_event(2,0,0,0,UIntPtr.Zero);
            mouse_event(4,0,0,0,UIntPtr.Zero);
        }
        if(CursorOnChevron()) SetCursorPos(origin.X,origin.Y);
        chevron=IntPtr.Zero;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, int size, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    public static IntPtr TrayWindow(uint process) {
        IntPtr result=IntPtr.Zero;
        var name=new StringBuilder(256);
        EnumWindows((h,_) => {
            GetWindowThreadProcessId(h,out uint owner);
            if (owner!=process || GetClassName(h,name,name.Capacity)==0 ||
                !name.ToString().StartsWith("AvaloniaMessageWindow ",StringComparison.Ordinal)) return true;
            result=h;
            return false;
        },IntPtr.Zero);
        if(result==IntPtr.Zero) throw new InvalidOperationException("Test app has no notification window");
        return result;
    }
    public static bool GetTrayRect(IntPtr window,out Rect area) {
        var identifier=new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=window,Id=1 };
        return Shell_NotifyIconGetRect(ref identifier,out area)>=0 && area.Right>area.Left && area.Bottom>area.Top;
    }
    public static void OpenMenu(IntPtr trayWindow) {
        if (!PostMessage(trayWindow,0x800,(IntPtr)1,(IntPtr)0x205)) throw new InvalidOperationException("Unable to open menu");
    }
    public static void ChooseMenuItem(IntPtr window,uint process,int x,int y) {
        GetWindowThreadProcessId(window,out uint owner);
        var point=new Point { X=x,Y=y };
        if(owner!=process || !ScreenToClient(window,ref point)) throw new InvalidOperationException("Wrong menu owner");
        var xy=(IntPtr)((point.Y<<16)|(point.X&65535));
        PostMessage(window,0x200,IntPtr.Zero,xy);
        PostMessage(window,0x201,(IntPtr)1,xy);
        PostMessage(window,0x202,IntPtr.Zero,xy);
    }
    public static IntPtr FileIcon(string path,bool small) {
        if(SHGetFileInfo(path,0,out var info,Marshal.SizeOf<ShellFileInfo>(),0x100u|(small?1u:0u))==IntPtr.Zero)
            throw new InvalidOperationException("Shell could not extract icon");
        return info.Icon;
    }
}
