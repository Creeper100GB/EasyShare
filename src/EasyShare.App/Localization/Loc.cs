using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EasyShare.App.Localization;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings;
    private string _language = "de";

    public Loc()
    {
        _strings = Load(_language);
    }

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            _strings = Load(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        }
    }

    public string this[string key] => _strings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    public string T(string key, params object[] args)
    {
        var s = this[key];
        if (args.Length == 0) return s;
        try
        {
            return string.Format(s, args);
        }
        catch (FormatException)
        {
            return s;
        }
    }

    public static string Tr(string key, params object[] args) => Instance.T(key, args);

    private static Dictionary<string, string> Load(string language)
    {
        var name = $"EasyShare.App.Resources.Lang.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return new Dictionary<string, string>();
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? new Dictionary<string, string>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
