using System.IO;
using System.Text.Json;
using ScreenSwap.Models;

namespace ScreenSwap.Services;

internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenSwap",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                       ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时回退到默认值
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 无写权限时不影响程序运行
        }
    }
}
