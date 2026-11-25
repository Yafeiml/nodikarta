#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProjectHub.Edge;

internal static class WindowsNativeEntry
{
    // Keep the CLI and its STDIO pipes in the process started by the client.
    [UnmanagedCallersOnly(EntryPoint = "nodicarta_edge_run_cli", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Run(int argc, nint argv)
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
