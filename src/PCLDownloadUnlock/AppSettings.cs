using System;
using System.IO;
using System.Text.Json;

namespace PCLDownloadUnlock
{
    internal sealed class SettingsData
    {
        public string LastPath { get; set; }
    }

    internal static class AppSettings
    {
        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCLDownloadUnlock");

        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static string LastPath { get; private set; }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                LastPath = data?.LastPath;
            }
            catch
            {
                // 配置损坏不影响使用
            }
        }

        public static void Save(string lastPath)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new SettingsData { LastPath = lastPath },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 忽略
            }
        }
    }
}
