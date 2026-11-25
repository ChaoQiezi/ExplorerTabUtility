using System;
using System.IO;
using System.Windows;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class SettingsManager
{
    private static readonly object SaveLock = new();
    private static readonly AppSettings Settings;
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppName,
        Constants.SettingsFileName);

    static SettingsManager()
    {
        var directory = Path.GetDirectoryName(SettingsFilePath);
        Directory.CreateDirectory(directory!);

        if (!File.Exists(SettingsFilePath))
        {
            Settings = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception)
        {
            Settings = new AppSettings();
        }
    }

    private static void NotifyStaticPropertyChanged([CallerMemberName] string propertyName = "")
    {
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(propertyName));
    }

    public static bool IsMouseHookActive
    {
        get => Settings.MouseHook;
        set
        {
            if (Settings.MouseHook == value) return;
            Settings.MouseHook = value;
            SaveSettings();
            NotifyStaticPropertyChanged();
        }
    }

    public static bool IsKeyboardHookActive
    {
        get => Settings.KeyboardHook;
        set
        {
            if (Settings.KeyboardHook == value) return;
            Settings.KeyboardHook = value;
            SaveSettings();
            NotifyStaticPropertyChanged();
        }
    }

    public static bool IsWindowHookActive
    {
        get => Settings.WindowHook;
        set
        {
            if (Settings.WindowHook == value) return;
            Settings.WindowHook = value;
            SaveSettings();
            NotifyStaticPropertyChanged();
        }
    }

    public static bool ReuseTabs
    {
        get => Settings.ReuseTabs;
        set
        {
            if (Settings.ReuseTabs == value) return;
            Settings.ReuseTabs = value;
            SaveSettings();
            NotifyStaticPropertyChanged();
        }
    }

    public static string HotKeyProfiles
    {
        get => Settings.HotKeyProfiles;
        set
        {
            if (Settings.HotKeyProfiles == value) return;
            Settings.HotKeyProfiles = value;
            SaveSettings();
        }
    }

    public static Size FormSize
    {
        get => Settings.FormSize;
        set
        {
            if (Settings.FormSize == value) return;
            Settings.FormSize = value;
            SaveSettings();
        }
    }

    public static bool SaveProfilesOnExit
    {
        get => Settings.SaveProfilesOnExit;
        set
        {
            if (Settings.SaveProfilesOnExit == value) return;
            Settings.SaveProfilesOnExit = value;
            SaveSettings();
        }
    }

    public static bool IsFirstRun
    {
        get => Settings.IsFirstRun;
        set
        {
            if (Settings.IsFirstRun == value) return;
            Settings.IsFirstRun = value;
            SaveSettings();
        }
    }

    public static bool IsTrayIconHidden
    {
        get => Settings.IsTrayIconHidden;
        set
        {
            if (Settings.IsTrayIconHidden == value) return;
            Settings.IsTrayIconHidden = value;
            SaveSettings();
        }
    }

    public static bool HaveThemeIssue
    {
        get => Settings.HaveThemeIssue;
        set
        {
            if (Settings.HaveThemeIssue == value) return;
            Settings.HaveThemeIssue = value;
            SaveSettings();
        }
    }

    public static bool AutoUpdate
    {
        get => Settings.AutoUpdate;
        set
        {
            if (Settings.AutoUpdate == value) return;
            Settings.AutoUpdate = value;
            SaveSettings();
        }
    }

    public static bool SaveClosedHistory
    {
        get => Settings.SaveClosedWindows;
        set
        {
            if (Settings.SaveClosedWindows == value) return;
            Settings.SaveClosedWindows = value;
            SaveSettings();
        }
    }

    public static bool RestorePreviousWindows
    {
        get => Settings.RestorePreviousWindows;
        set
        {
            if (Settings.RestorePreviousWindows == value) return;
            Settings.RestorePreviousWindows = value;
            SaveSettings();
        }
    }

    public static WindowRecord[]? ClosedWindows
    {
        get => Settings.ClosedWindows;
        set
        {
            Settings.ClosedWindows = value;
            SaveSettings();
        }
    }

    public static void SaveSettings()
    {
        lock (SaveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings);
                var tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(SettingsFilePath))
                {
                    File.Replace(tempPath, SettingsFilePath, null);
                }
                else
                {
                    File.Move(tempPath, SettingsFilePath);
                }
            }
            catch
            {
                // Fallback direct write if atomic replace is blocked by AV/sync lock
                try
                {
                    var json = JsonSerializer.Serialize(Settings);
                    File.WriteAllText(SettingsFilePath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
                }
            }
        }
    }
}

internal class AppSettings
{
    public bool MouseHook { get; set; }
    public bool KeyboardHook { get; set; } = true;
    public bool WindowHook { get; set; } = true;
    public bool ReuseTabs { get; set; } = true;
    public Size FormSize { get; set; } = new(852, 402);
    public bool SaveProfilesOnExit { get; set; } = true;
    public bool IsFirstRun { get; set; } = true;
    public bool IsTrayIconHidden { get; set; }
    public bool HaveThemeIssue { get; set; }
    public bool AutoUpdate { get; set; }
    public string HotKeyProfiles { get; set; } = Constants.DefaultHotKeyProfiles;
    public bool SaveClosedWindows { get; set; }
    public bool RestorePreviousWindows { get; set; }
    public WindowRecord[]? ClosedWindows { get; set; }
}
