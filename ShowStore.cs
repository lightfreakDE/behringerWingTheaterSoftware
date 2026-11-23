using System.Text.Json;

namespace WingScenes;

public class ShowStore
{
    readonly string _path;
    readonly object _lock = new();
    static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public Show Show { get; private set; } = new();

    public ShowStore(string path)
    {
        _path = path;
        if (File.Exists(path))
            Show = JsonSerializer.Deserialize<Show>(File.ReadAllText(path), Opt) ?? new Show();
    }

    public void Replace(Show show)
    {
        lock (_lock)
        {
            Show = show;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(show, Opt));
            File.Move(tmp, _path, true);
        }
    }
}
