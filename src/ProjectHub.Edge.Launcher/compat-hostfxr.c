// The stable 1.0.x launcher loads this release-local filename and entry point.
// This module hosts the Native AOT CLI in the same process, with no .NET runtime install.
#define _WIN32_WINNT 0x0602
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>

typedef int32_t (__cdecl *start_cli)(int, const wchar_t **, const wchar_t *, const wchar_t *, const wchar_t *);

__declspec(dllexport) int32_t __cdecl hostfxr_main_startupinfo(int argc, const wchar_t **argv,
    const wchar_t *host, const wchar_t *bin, const wchar_t *assembly)
{
    wchar_t path[32768];
    const wchar_t name[] = L"\\NodicartaEdgeCli.dll";
    if (!bin || wcslen(bin) + ARRAYSIZE(name) >= ARRAYSIZE(path) ||
        !SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS) ||
        !AddDllDirectory(bin)) {
        fwprintf(stderr, L"Nodicarta Edge: Cannot select native libraries for this release.\n");
        return 1;
    }
    wcscpy(path, bin);
    wcscat(path, name);
    HMODULE cli = LoadLibraryExW(path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    start_cli run = cli ? (start_cli)GetProcAddress(cli, "hostfxr_main_startupinfo") : NULL;
    if (!run) {
        fwprintf(stderr, L"Nodicarta Edge: Cannot load the Native AOT CLI.\n");
        return 1;
    }
    return run(argc, argv, host, bin, assembly);
}
