// Host the selected self-contained CLI in this process. MCP clients own the
// process and its pipes directly, including when they cancel a running session.
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>

typedef int32_t (__cdecl *start_cli)(int, const wchar_t **, const wchar_t *, const wchar_t *, const wchar_t *);

static int fail(const wchar_t *message)
{
    fwprintf(stderr, L"Nodicarta Edge: %ls\n", message);
    return 1;
}

int wmain(int argc, wchar_t **argv)
{
    static wchar_t host[32768], root[32768], path[32768], bin[32768];
    DWORD length = GetModuleFileNameW(NULL, host, ARRAYSIZE(host));
    if (!length || length + 64 >= ARRAYSIZE(host))
        return fail(L"Invalid installation path.");
    wcscpy(root, host);
    wchar_t *file = wcsrchr(root, L'\\');
    if (!file)
        return fail(L"Invalid installation path.");
    file[1] = L'\0';

#ifdef NODICARTA_PORTABLE_CLI
    // The versioned CLI directly hosts the same AOT library as the installed launcher.
    wcscpy(bin, root);
    bin[wcslen(bin) - 1] = L'\0';
#else
    swprintf(path, ARRAYSIZE(path), L"%lsactive-release", root);
    HANDLE marker = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (marker == INVALID_HANDLE_VALUE)
        return fail(L"Cannot read active-release. Run the desktop installer.");
    char bytes[13];
    DWORD count;
    BOOL read = ReadFile(marker, bytes, sizeof(bytes), &count, NULL);
    CloseHandle(marker);
    if (!read || count != 12)
        return fail(L"Invalid active-release.");
    wchar_t build[13] = {0};
    for (DWORD i = 0; i < count; i++) {
        char c = bytes[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            return fail(L"Invalid active-release.");
        build[i] = (wchar_t)c;
    }
    swprintf(bin, ARRAYSIZE(bin), L"%ls%ls\\bin", root, build);
#endif
    swprintf(path, ARRAYSIZE(path), L"%ls\\hostfxr.dll", bin);
    HMODULE fxr = LoadLibraryExW(path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!fxr)
        return fail(L"Cannot load the selected runtime. Run the desktop installer.");
    start_cli run = (start_cli)GetProcAddress(fxr, "hostfxr_main_startupinfo");
    if (!run)
        return fail(L"The selected runtime cannot host the CLI.");
    swprintf(path, ARRAYSIZE(path), L"%ls\\ProjectHub.Edge.dll", bin);
    return run(argc, (const wchar_t **)argv, host, bin, path);
}
