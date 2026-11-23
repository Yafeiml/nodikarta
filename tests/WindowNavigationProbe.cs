using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

// Listen to Windows' own location events for just the fixture HWND, including changes
// that a before/after UI Automation snapshot would miss.
public sealed class WindowNavigationProbe : IDisposable
{
    private readonly IntPtr window;
    private readonly uint process;
    private readonly Thread listener;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly List<string> events = new();
    private IntPtr hook;
    private uint threadId;
    private delegate void Callback(IntPtr hook, uint type, IntPtr h, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] private struct Message {
        public IntPtr Hwnd; public uint Id; public UIntPtr W; public IntPtr L;
        public uint Time; public int X,Y; public uint Private;
    }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,Callback cb,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message msg,IntPtr hwnd,uint first,uint last);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message msg,IntPtr hwnd,uint first,uint last,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid,uint msg,UIntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr mode);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd,ref Point p);

    public bool CanMinimize => (GetWindowLongPtr(window,-16).ToInt64() & 0x20000)!=0;
    public bool CanMaximize => (GetWindowLongPtr(window,-16).ToInt64() & 0x10000)!=0;
    public bool CanResize => (GetWindowLongPtr(window,-16).ToInt64() & 0x40000)!=0;
    public void SystemCommand(int command) {
        if(!PostMessage(window,0x112,(IntPtr)command,IntPtr.Zero)) throw new Win32Exception();
    }
    public bool RefusesInteractiveResize() => SendMessageTimeout(window,0x112,(IntPtr)0xf002,IntPtr.Zero,2,600,out _)!=IntPtr.Zero;
    public void DoubleClickTitle(int screenX,int screenY) {
        var p=new Point { X=screenX,Y=screenY };
        if(!ScreenToClient(window,ref p)) throw new Win32Exception();
        var xy=(IntPtr)((p.Y<<16)|(p.X&65535));
        for(int i=0;i<2;i++) {
            PostMessage(window,0x201,(IntPtr)1,xy);
            PostMessage(window,0x202,IntPtr.Zero,xy);
            Thread.Sleep(28);
        }
    }
    public void CaptionDoubleClick(int screenX,int screenY) {
        var screenPosition=(IntPtr)((screenY<<16)|(screenX&65535));
        if(!PostMessage(window,0xA3,(IntPtr)2,screenPosition)) throw new Win32Exception();
    }

    public WindowNavigationProbe(IntPtr window,uint process) {
        this.window=window; this.process=process;
        listener=new Thread(Listen){IsBackground=true};
        listener.Start();
        if(!ready.Wait(TimeSpan.FromSeconds(5)) || hook==IntPtr.Zero) throw new Win32Exception("Window location hook unavailable");
    }
    public string Bounds() {
        if(!GetWindowRect(window,out var r)) throw new Win32Exception("Test window disappeared");
        return $"{r.L},{r.T},{r.R},{r.B}";
    }
    private void Listen() {
        SetThreadDpiAwarenessContext((IntPtr)(-4));
        threadId=GetCurrentThreadId();
        PeekMessage(out _,IntPtr.Zero,0,0,0);
        Callback onLocation=(_,type,h,obj,child,tid,time) => {
            if(h==window && obj==0 && child==0 && GetWindowRect(h,out var r)) {
                lock(events) events.Add($"{r.L},{r.T},{r.R},{r.B}");
            }
        };
        hook=SetWinEventHook(0x800B,0x800B,IntPtr.Zero,onLocation,process,0,0);
        ready.Set();
        while(hook!=IntPtr.Zero && GetMessage(out var msg,IntPtr.Zero,0,0)>0) DispatchMessage(ref msg);
        if(hook!=IntPtr.Zero) UnhookWinEvent(hook);
        GC.KeepAlive(onLocation);
    }
    public string[] Drain() {
        lock(events) { var result=events.ToArray(); events.Clear(); return result; }
    }
    public void Dispose() {
        PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
        listener.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }
}
