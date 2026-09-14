#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <shobjidl_core.h>
#include <string>
#include <vector>

namespace
{
    int Fail(const wchar_t* explanation, DWORD code, bool quiet)
    {
        std::wstring message(explanation);
        message += L"\n\nWindows error: " + std::to_wstring(code);
        if (quiet)
        {
            const std::string diagnostic = "VoiceInput launcher failed: " + std::to_string(code) + "\r\n";
            DWORD written = 0;
            WriteFile(GetStdHandle(STD_ERROR_HANDLE), diagnostic.data(), static_cast<DWORD>(diagnostic.size()), &written, nullptr);
        }
        else
        {
            MessageBoxW(nullptr, message.c_str(), L"VoiceInput", MB_OK | MB_ICONERROR);
        }
        return static_cast<int>(code == ERROR_SUCCESS ? ERROR_GEN_FAILURE : code);
    }

    HANDLE InheritableStandardHandle(DWORD which)
    {
        HANDLE result = INVALID_HANDLE_VALUE;
        const HANDLE original = GetStdHandle(which);
        if (original != nullptr && original != INVALID_HANDLE_VALUE &&
            DuplicateHandle(GetCurrentProcess(), original, GetCurrentProcess(), &result, 0, TRUE, DUPLICATE_SAME_ACCESS))
            return result;

        SECURITY_ATTRIBUTES security{ sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE };
        return CreateFileW(L"NUL", which == STD_INPUT_HANDLE ? GENERIC_READ : GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    }
}

// Keep the portable/installed entry point independent of .NET. The runtime,
// generated XAML resources and native libraries live together under app/.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int)
{
    SetCurrentProcessExplicitAppUserModelID(L"VoiceInput.Desktop");
    int count = 0;
    wchar_t** parsed = CommandLineToArgvW(GetCommandLineW(), &count);
    bool waitForExit = parsed != nullptr && count > 1 &&
        (wcscmp(parsed[1], L"--smoke-test") == 0 || wcscmp(parsed[1], L"--voiceinput-uia-worker") == 0);
    bool silentStartup = false;
    if (parsed != nullptr)
        for (int index = 1; index < count; ++index)
        {
            if (wcscmp(parsed[index], L"--startup-smoke") == 0) waitForExit = true;
            if (wcscmp(parsed[index], L"--startup") == 0) silentStartup = true;
        }
    if (parsed != nullptr) LocalFree(parsed);
    const bool quiet = waitForExit || silentStartup;

    std::vector<wchar_t> module(32768);
    const DWORD length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (length == 0 || length >= module.size())
        return Fail(L"无法确定程序路径。", length == 0 ? GetLastError() : ERROR_FILENAME_EXCED_RANGE, quiet);
    const std::wstring path(module.data(), length);
    const std::wstring directory = path.substr(0, path.find_last_of(L"\\/")) + L"\\app";
    const std::wstring executable = directory + L"\\RealtimeTranscription.exe";
    const DWORD attributes = GetFileAttributesW(executable.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return Fail(L"程序文件不完整。请完整解压便携包，或重新安装 VoiceInput；保留 VoiceInput.exe 与 app 文件夹的相对位置。",
            ERROR_FILE_NOT_FOUND, quiet);

    // lpCmdLine already contains correctly quoted arguments. Preserve it byte
    // for byte (including Unicode and embedded quotes); never round-trip through
    // a shell or reconstruct arguments with different escaping rules.
    std::wstring command = L"\"" + executable + L"\"";
    if (arguments != nullptr && *arguments != L'\0') command += L" " + std::wstring(arguments);
    if (command.size() >= 32767)
        return Fail(L"启动参数过长。", ERROR_BAD_LENGTH, quiet);

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = waitForExit ? sizeof(STARTUPINFOEXW) : sizeof(STARTUPINFOW);
    PROCESS_INFORMATION process{};
    std::vector<BYTE> attributeStorage;
    HANDLE handles[3] = { INVALID_HANDLE_VALUE, INVALID_HANDLE_VALUE, INVALID_HANDLE_VALUE };
    if (waitForExit)
    {
        handles[0] = InheritableStandardHandle(STD_INPUT_HANDLE);
        handles[1] = InheritableStandardHandle(STD_OUTPUT_HANDLE);
        handles[2] = InheritableStandardHandle(STD_ERROR_HANDLE);
        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
        attributeStorage.resize(bytes);
        startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeStorage.data());
        const bool validHandles = handles[0] != INVALID_HANDLE_VALUE && handles[1] != INVALID_HANDLE_VALUE && handles[2] != INVALID_HANDLE_VALUE;
        if (!validHandles || !InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &bytes))
        {
            const DWORD error = GetLastError();
            for (HANDLE handle : handles) if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
            return Fail(L"无法准备输入输出通道。", error, true);
        }
        if (!UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            handles, sizeof(handles), nullptr, nullptr))
        {
            const DWORD error = GetLastError();
            DeleteProcThreadAttributeList(startup.lpAttributeList);
            for (HANDLE handle : handles) CloseHandle(handle);
            return Fail(L"无法传递输入输出通道。", error, true);
        }
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = handles[0];
        startup.StartupInfo.hStdOutput = handles[1];
        startup.StartupInfo.hStdError = handles[2];
    }

    const BOOL started = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr,
        waitForExit ? TRUE : FALSE, waitForExit ? EXTENDED_STARTUPINFO_PRESENT : 0,
        nullptr, directory.c_str(), &startup.StartupInfo, &process);
    const DWORD startError = started ? ERROR_SUCCESS : GetLastError();
    if (waitForExit)
    {
        DeleteProcThreadAttributeList(startup.lpAttributeList);
        for (HANDLE handle : handles) CloseHandle(handle);
    }
    if (!started)
        return Fail(L"无法启动 VoiceInput。请完整解压便携包，或重新安装程序。", startError, quiet);

    CloseHandle(process.hThread);
    DWORD exitCode = ERROR_SUCCESS;
    if (waitForExit)
    {
        if (WaitForSingleObject(process.hProcess, INFINITE) != WAIT_OBJECT_0 || !GetExitCodeProcess(process.hProcess, &exitCode))
            exitCode = GetLastError();
    }
    CloseHandle(process.hProcess);
    return static_cast<int>(exitCode);
}
