#if UNITY_EDITOR_WIN

using UnityEditor;
using UnityEngine;
using System.Diagnostics;
using System.IO;
using System;
using Debug = UnityEngine.Debug;

namespace Google.Impl {
    internal static class RelayBuilder {
        [MenuItem("Tools/Google Sign In/Build Deep Link Relay")]
        private static void BuildRelay() {
            var cPath = "";

            try {
                EditorUtility.DisplayProgressBar("Building Relay", "Preparing source files...", 0.2f);

                var exePath = Path.Combine(Application.streamingAssetsPath, $"{Application.productName}.exe");

                cPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".c");

                File.WriteAllText(cPath, @"
$include <windows.h>
$include <shellapi.h>
$include <stdio.h>
$include <stdlib.h>

$pragma comment(linker, ""/SUBSYSTEM:windows /ENTRY:mainCRTStartup"")

typedef struct {
    DWORD pid;
    HWND  hwnd;
} FindWindowData;

BOOL CALLBACK EnumWindowsProc(HWND hwnd, LPARAM lParam) {
    FindWindowData *data = (FindWindowData*)lParam;
    DWORD windowPid;
    GetWindowThreadProcessId(hwnd, &windowPid);

    if (windowPid == data->pid && IsWindowVisible(hwnd)) {
        data->hwnd = hwnd;
        return FALSE; 
    }
    return TRUE;
}

int main() {
    int argc;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);

    // [1]URL [2]SignalPath [3]PID [4]HWND
    if (argv == NULL || argc < 4) {
        if (argv) LocalFree(argv);
        return 1;
    }

    LPWSTR uri        = argv[1];
    LPWSTR outPath    = argv[2];
    DWORD  targetPid  = (DWORD)_wtoi(argv[3]);
    HWND   targetHwnd = NULL;

    if (argc >= 5) {
        targetHwnd = (HWND)(UINT_PTR)_wcstoui64(argv[4], NULL, 10);
    }

    HANDLE hFile = CreateFileW(outPath, GENERIC_WRITE, 0, NULL, 
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        int len = WideCharToMultiByte(CP_UTF8, 0, uri, -1, NULL, 0, NULL, NULL);
        char *buf = (char*)malloc(len);
        WideCharToMultiByte(CP_UTF8, 0, uri, -1, buf, len, NULL, NULL);
        DWORD written;
        WriteFile(hFile, buf, len - 1, &written, NULL);
        CloseHandle(hFile);
        free(buf);
    }

    HWND hwndToWake = NULL;

    if (targetHwnd != NULL && IsWindow(targetHwnd)) {
        hwndToWake = targetHwnd;
    } else if (targetPid != 0) {
        FindWindowData fwd = { targetPid, NULL };
        EnumWindows(EnumWindowsProc, (LPARAM)&fwd);
        hwndToWake = fwd.hwnd;
    }

    if (hwndToWake) {
        if (IsIconic(hwndToWake)) ShowWindow(hwndToWake, SW_RESTORE);
        SetForegroundWindow(hwndToWake);
    }

    LocalFree(argv);
    return 0;
}".Replace('$', '#'));

                EditorUtility.DisplayProgressBar("Building Relay", "Compiling Exe (gcc)...", 0.7f);

                if (!Directory.Exists(Application.streamingAssetsPath)) {
                    Directory.CreateDirectory(Application.streamingAssetsPath);
                }

                RunCommand("gcc", $"\"{cPath}\" -o \"{exePath}\" -O2 -mwindows -static -luser32 -lshell32", Path.GetTempPath());

                EditorUtility.DisplayProgressBar("Building Relay", "Finishing...", 0.99f);

                Debug.Log($"<color=green>Build Relay Successful!</color>\nName: {Application.productName}\nPath: {exePath}");

                AssetDatabase.Refresh();
            } catch (Exception e) {
                Debug.LogError($"Build Relay Failed! Ensure you have gcc in your PATH.\nDetail: {e.Message}");
            } finally {
                if (File.Exists(cPath)) {
                    File.Delete(cPath);
                }

                EditorUtility.ClearProgressBar();
            }
        }

        private static void RunCommand(string fileName, string args, string workingDir) {
            var info = new ProcessStartInfo {
                FileName               = fileName
              , Arguments              = args
              , WorkingDirectory       = workingDir
              , UseShellExecute        = false
              , RedirectStandardError  = true
              , RedirectStandardOutput = true
              , CreateNoWindow         = true
            };

            using var process = Process.Start(info);
            process!.WaitForExit();
            string errors = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0) {
                throw new Exception($"[{fileName} Error]: {errors}");
            }
        }
    }
}

#endif