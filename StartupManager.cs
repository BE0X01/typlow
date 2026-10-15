using System;
using Microsoft.Win32;

namespace Typlow;

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string executable) => "\"" + executable + "\" --tray";
    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            string command = Command(Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 찾지 못했습니다."));
            if (!string.Equals(key.GetValue("Typlow") as string, command, StringComparison.Ordinal)) key.SetValue("Typlow", command, RegistryValueKind.String);
        }
        else key.DeleteValue("Typlow", false);
    }
}
