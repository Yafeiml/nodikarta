using System.Globalization;
using System.Runtime.InteropServices;

namespace ProjectHub.Edge.Desktop;

// IShellLinkW and IPersistFile, also used by Explorer. Keep the existing link object
// when retargeting so shell properties outside Edge's ownership survive.
internal sealed unsafe class WindowsShellLink : IDisposable
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFile = new("0000010B-0000-0000-C000-000000000046");
    private const int BufferLength = 32768;
    private readonly string path;
    private bool uninitialize;
    private nint link, persist;

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    private static nint* Table(nint pointer) => *(nint**)pointer;
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"Windows shortcut: {operation} (0x{hr:X8}).", hr);
    }

    internal WindowsShellLink(string path)
    {
        this.path = path;
        var hr = CoInitializeEx(0, 2);
        if (hr != unchecked((int)0x80010106)) Check(hr, "CoInitializeEx"); // An existing MTA is usable too.
        uninitialize = hr >= 0;
        try
        {
            Check(CoCreateInstance(ShellLinkClass, 0, 1, ShellLinkW, out link), "CoCreateInstance");
            Guid iid = PersistFile;
            nint file = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(link)[0])(link, &iid, &file), "QueryInterface(IPersistFile)");
            persist = file;
            if (File.Exists(path))
                fixed (char* name = path)
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Table(persist)[5])(persist, name, 0), "Load");
        }
        catch { Dispose(); throw; }
    }

    internal string TargetPath
    {
        get
        {
            var buffer = new char[BufferLength];
            fixed (char* value = buffer)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)Table(link)[3])(link, value, buffer.Length, 0, 0), "GetPath");
                return new string(value);
            }
        }
        set => SetString(20, value, "SetPath");
    }
    internal string Description { get => GetString(6, "GetDescription"); set => SetString(7, value, "SetDescription"); }
    internal string WorkingDirectory { get => GetString(8, "GetWorkingDirectory"); set => SetString(9, value, "SetWorkingDirectory"); }
    internal string Arguments { get => GetString(10, "GetArguments"); set => SetString(11, value, "SetArguments"); }

    internal string IconLocation
    {
        get
        {
            var buffer = new char[BufferLength];
            int index;
            fixed (char* value = buffer)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int*, int>)Table(link)[16])(link, value, buffer.Length, &index), "GetIconLocation");
                return new string(value) + "," + index.ToString(CultureInfo.InvariantCulture);
            }
        }
        set
        {
            int comma = value.LastIndexOf(',');
            if (comma < 0 || !int.TryParse(value.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                throw new ArgumentException("A shortcut icon must specify its index.", nameof(value));
            fixed (char* name = value[..comma])
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(link)[17])(link, name, index), "SetIconLocation");
        }
    }

    private string GetString(int slot, string operation)
    {
        var buffer = new char[BufferLength];
        fixed (char* value = buffer)
        {
            Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(link)[slot])(link, value, buffer.Length), operation);
            return new string(value);
        }
    }

    private void SetString(int slot, string text, string operation)
    {
        fixed (char* value = text)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Table(link)[slot])(link, value), operation);
    }

    internal void Save()
    {
        fixed (char* name = path)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(persist)[6])(persist, name, 1), "Save");
    }

    public void Dispose()
    {
        if (persist != 0) { ((delegate* unmanaged[Stdcall]<nint, uint>)Table(persist)[2])(persist); persist = 0; }
        if (link != 0) { ((delegate* unmanaged[Stdcall]<nint, uint>)Table(link)[2])(link); link = 0; }
        if (uninitialize) { CoUninitialize(); uninitialize = false; }
    }
}
