using System.Text.Json;

namespace Trenal.Core;

/// <summary>Small secret store (git tokens). iOS uses the Keychain; desktop a 0600 JSON file.</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

public sealed class FileSecretStore(string file) : ISecretStore
{
    public string? Get(string key) => Load().GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        var all = Load();
        all[key] = value;
        Save(all);
    }

    public void Remove(string key)
    {
        var all = Load();
        if (all.Remove(key)) Save(all);
    }

    Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return [];
    }

    void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(all));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
