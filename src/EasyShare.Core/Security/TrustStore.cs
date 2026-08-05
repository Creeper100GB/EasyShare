using System.Text.Json;

namespace EasyShare.Core.Security;

public class TrustStore
{
    private HashSet<string> _trusted = new();
    private readonly string _storePath;

    public TrustStore()
    {
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyShare", "trusted.json");
        Load();
    }

    public bool IsTrusted(string fingerprint) => _trusted.Contains(fingerprint);

    public void AddTrusted(string fingerprint, string alias)
    {
        _trusted.Add(fingerprint);
        Save();
    }

    public void RemoveTrusted(string fingerprint)
    {
        _trusted.Remove(fingerprint);
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            _trusted = JsonSerializer.Deserialize<HashSet<string>>(json) ?? new();
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_trusted);
            File.WriteAllText(_storePath, json);
        }
        catch
        {
        }
    }
}
