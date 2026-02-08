using System;
using System.IO;
using System.Text.Json;
using CompanionApp.Models;

namespace CompanionApp.Services;

public static class ConfigService
{
    public static string GetConfigFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "das-KRT_com");
    }

    public static string GetConfigPath()
    {
        return Path.Combine(GetConfigFolder(), "config.json");
    }

    public static CompanionConfig Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            var cfg = new CompanionConfig();
            Save(cfg);
            return cfg;
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return JsonSerializer.Deserialize<CompanionConfig>(json, options) ?? new CompanionConfig();
    }

    public static void Save(CompanionConfig config)
    {
        var folder = GetConfigFolder();
        Directory.CreateDirectory(folder);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(GetConfigPath(), json);
    }
}
