using System.Collections.Concurrent;
using System.Text.Json;

namespace EasyShare.Core.Security;

public class TrustStore
{
    private ConcurrentDictionary<string, string> _trusted = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;

    public TrustStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare", "trusted.json"))
    {
    }

    internal TrustStore(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    public bool IsTrusted(string fingerprint) => _trusted.ContainsKey(fingerprint);

    public string? GetAlias(string fingerprint)
        => _trusted.TryGetValue(fingerprint, out var alias) ? alias : null;

    public void AddTrusted(string fingerprint, string alias)
    {
        _trusted[fingerprint] = alias;
        Save();
    }

    public void RemoveTrusted(string fingerprint)
    {
        _trusted.TryRemove(fingerprint, out _);
        Save();
    }

    public IReadOnlyDictionary<string, string> GetTrusted() => _trusted;

    private void Load()
    {
        if (!File.Exists(_storePath)) return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is not null)
            {
                _trusted = new ConcurrentDictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch
        {
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            var legacy = JsonSerializer.Deserialize<List<string>>(json);
            if (legacy is not null)
            {
                _trusted = new ConcurrentDictionary<string, string>(
                    legacy.ToDictionary(f => f, _ => string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                Save();
            }
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
            var json = JsonSerializer.Serialize(_trusted.ToDictionary(kv => kv.Key, kv => kv.Value));
            File.WriteAllText(_storePath, json);
        }
        catch
        {
        }
    }
}
