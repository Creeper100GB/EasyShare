using Microsoft.Win32;

namespace EasyShare.Shell;

public static class ShellIntegration
{
    private const string FilesKey = @"Software\Classes\*\shell\EasyShare";
    private const string DirKey = @"Software\Classes\Directory\shell\EasyShare";
    private const string DirBgKey = @"Software\Classes\Directory\Background\shell\EasyShare";
    private const string MenuLabelDefault = "Mit EasyShare teilen";

    public static void Register(string exePath, string? menuLabel = null)
    {
        var label = string.IsNullOrEmpty(menuLabel) ? MenuLabelDefault : menuLabel;
        using var filesBase = Registry.CurrentUser.CreateSubKey(FilesKey);
        filesBase.SetValue(null, label);
        filesBase.SetValue("Icon", $"{exePath},0");
        using var filesCmd = Registry.CurrentUser.CreateSubKey($@"{FilesKey}\command");
        filesCmd.SetValue(null, $"\"{exePath}\" share \"%1\"");

        using var dirBase = Registry.CurrentUser.CreateSubKey(DirKey);
        dirBase.SetValue(null, label);
        dirBase.SetValue("Icon", $"{exePath},0");
        using var dirCmd = Registry.CurrentUser.CreateSubKey($@"{DirKey}\command");
        dirCmd.SetValue(null, $"\"{exePath}\" share \"%1\"");

        using var dirBgBase = Registry.CurrentUser.CreateSubKey(DirBgKey);
        dirBgBase.SetValue(null, label);
        dirBgBase.SetValue("Icon", $"{exePath},0");
        using var dirBgCmd = Registry.CurrentUser.CreateSubKey($@"{DirBgKey}\command");
        dirBgCmd.SetValue(null, $"\"{exePath}\" share \"%V\"");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FilesKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(DirKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(DirBgKey, false);
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(FilesKey);
        return key != null;
    }
}

