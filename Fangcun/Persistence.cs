using System.IO;
using System.Text.Json;

namespace Fangcun
{
    /// <summary>
    /// 布局/条目持久化到 %LocalAppData%/Fangcun/config.json
    /// </summary>
    internal static class Persistence
    {
        private static readonly string FilePath =
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Fangcun", "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            return new AppConfig();
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
