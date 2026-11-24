#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProjectHub.Edge;

internal static class WindowsNativeEntry
{
    // The installed 1.0.x launcher already calls this C ABI. Keep the CLI in its
    // caller's process: STDIO pipes, working directory and MCP cancellation belong to it.
    [UnmanagedCallersOnly(EntryPoint = "hostfxr_main_startupinfo", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Run(int argc, nint argv, nint host, nint root, nint assembly)
    {
        try
        {
            var args = new string[argc - 1];
            for (var i = 0; i < args.Length; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, (i + 1) * IntPtr.Size))!;
            return Program.Main(args).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
#endif
