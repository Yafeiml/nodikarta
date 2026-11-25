#define _WIN32_WINNT 0x0602
#include <windows.h>
#include <shellapi.h>
#include <stdint.h>
#include <stdlib.h>
#include <wchar.h>

typedef int32_t (__cdecl *start_cli)(int, const wchar_t **);

static BOOL desktop_arguments(int argc, wchar_t **argv)
{
    BOOL state = FALSE;
    for (int i = 1; i < argc; i++) {
        if (!wcscmp(argv[i], L"--background")) continue;
        if (state || wcscmp(argv[i], L"--state") || ++i == argc || !argv[i][0] || !wcsncmp(argv[i], L"--", 2))
            return FALSE;
        state = TRUE;
    }
    return TRUE;
}

static BOOL valid_handle(HANDLE handle)
{
    return handle && handle != INVALID_HANDLE_VALUE;
}

static void attach_cli_console(void)
{
    const DWORD ids[] = { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE };
    HANDLE handles[ARRAYSIZE(ids)];
    BOOL missing = FALSE;
    for (unsigned i = 0; i < ARRAYSIZE(ids); i++) {
        handles[i] = GetStdHandle(ids[i]);
        missing |= !valid_handle(handles[i]);
    }
    if (missing && AttachConsole(ATTACH_PARENT_PROCESS)) {
        for (unsigned i = 0; i < ARRAYSIZE(ids); i++)
            if (valid_handle(handles[i])) SetStdHandle(ids[i], handles[i]);
    }
}

static int fail(const wchar_t *message, BOOL desktop)
{
    if (desktop) {
        MessageBoxW(NULL, message, L"Nodicarta Edge", MB_OK | MB_ICONERROR);
    } else {
        wchar_t line[256];
        char encoded[768];
        DWORD written, mode;
        HANDLE handle = GetStdHandle(STD_ERROR_HANDLE);
        if (valid_handle(handle)) {
            int length = swprintf(line, ARRAYSIZE(line), L"Nodicarta Edge: %ls\r\n", message);
            if (length > 0 && GetConsoleMode(handle, &mode))
                WriteConsoleW(handle, line, (DWORD)length, &written, NULL);
            else if (length > 0) {
                int bytes = WideCharToMultiByte(CP_UTF8, 0, line, length, encoded, ARRAYSIZE(encoded), NULL, NULL);
                if (bytes > 0) WriteFile(handle, encoded, (DWORD)bytes, &written, NULL);
            }
        }
    }
    return 1;
}

static BOOL ordinary_file(const wchar_t *path)
{
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

static BOOL missing_file(const wchar_t *path)
{
    return GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND;
}

static int launch_desktop(const wchar_t *path)
{
    STARTUPINFOW inherited = {0}, startup = {0};
    PROCESS_INFORMATION child;
    wchar_t *command = _wcsdup(GetCommandLineW());
    if (!command) return fail(L"Cannot allocate the desktop command line.", TRUE);
    GetStartupInfoW(&inherited);
    startup.cb = sizeof(startup);
    startup.dwFlags = inherited.dwFlags & (STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES);
    startup.wShowWindow = inherited.wShowWindow;
    startup.hStdInput = inherited.hStdInput;
    startup.hStdOutput = inherited.hStdOutput;
    startup.hStdError = inherited.hStdError;
    BOOL started = CreateProcessW(path, command, NULL, NULL, !!(startup.dwFlags & STARTF_USESTDHANDLES),
                                  0, NULL, NULL, &startup, &child);
    free(command);
    if (!started) return fail(L"Cannot start the Edge desktop. Run the desktop installer.", TRUE);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    return 0;
}

static int run(int argc, wchar_t **argv)
{
    static wchar_t root[32768], path[32768];
    BOOL desktop = desktop_arguments(argc, argv);
    if (!desktop) attach_cli_console();
    DWORD length = GetModuleFileNameW(NULL, root, ARRAYSIZE(root));
    if (!length || length + 64 >= ARRAYSIZE(root)) return fail(L"Invalid installation path.", desktop);
    wchar_t *file = wcsrchr(root, L'\\');
    if (!file) return fail(L"Invalid installation path.", desktop);
    file[1] = L'\0';

    swprintf(path, ARRAYSIZE(path), L"%ls.edge-lease", root);
    // Keep the shared lease until process exit so setup cannot overwrite a running CLI's files.
    HANDLE lease = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    BOOL installed = valid_handle(lease);
    if (installed) {
        BY_HANDLE_FILE_INFORMATION info;
        if (!GetFileInformationByHandle(lease, &info) ||
            (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)))
            return fail(L"Cannot verify the Edge installation lease. Run the desktop installer.", desktop);
    } else {
        DWORD error = GetLastError();
        // Leave the executable free for setup to replace if a newer launcher is included.
        if (error == ERROR_SHARING_VIOLATION || error == ERROR_LOCK_VIOLATION)
            return desktop ? 1 : fail(L"Edge setup is running. Try again after it completes.", FALSE);
        if (error != ERROR_FILE_NOT_FOUND)
            return fail(L"Cannot open the Edge installation lease. Run the desktop installer.", desktop);
        const wchar_t *const installed_files[] = { L"SHA256SUMS", L"public-install.json" };
        for (unsigned i = 0; i < ARRAYSIZE(installed_files); i++) {
            swprintf(path, ARRAYSIZE(path), L"%ls%ls", root, installed_files[i]);
            if (!missing_file(path))
                return fail(L"The Edge installation lease is missing. Run the desktop installer.", desktop);
        }
    }
    swprintf(path, ARRAYSIZE(path), L"%ls.nodicarta-install-transaction.json", root);
    if (!missing_file(path))
        return fail(L"Edge setup has not finished. Run the desktop installer again.", desktop);

    if (desktop) {
        swprintf(path, ARRAYSIZE(path), L"%lsProjectHub.Edge.Desktop.exe", root);
        if (ordinary_file(path)) return launch_desktop(path);
        if (installed || argc != 1) return fail(L"Cannot locate the Edge desktop. Run the desktop installer.", TRUE);
        // A CLI-only archive has no desktop. Running it without arguments prints help.
        attach_cli_console();
    }

    swprintf(path, ARRAYSIZE(path), L"%lsNodicartaEdgeCli.dll", root);
    if (!ordinary_file(path) || !SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS) ||
        !AddDllDirectory(root)) return fail(L"Cannot locate the Edge CLI. Run the desktop installer.", FALSE);
    HMODULE cli = LoadLibraryExW(path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    start_cli entry = cli ? (start_cli)(void (*)(void))GetProcAddress(cli, "nodicarta_edge_run_cli") : NULL;
    if (!entry) return fail(L"Cannot load the Edge CLI. Run the desktop installer.", FALSE);
    // Keep STDIO, cancellation and working directory in the calling process.
    return entry(argc, (const wchar_t **)argv);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show)
{
    (void)instance; (void)previous; (void)command; (void)show;
    int argc;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return fail(L"Cannot read command line.", FALSE);
    int status = run(argc, argv);
    LocalFree(argv);
    return status;
}
