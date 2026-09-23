using System.Text.Encodings.Web;
using System.Text.Json;

namespace TranslationApp.Core.Settings;

/// <summary>
/// JSON 文件设置存储，默认路径 %AppData%\TranslationApp\settings.json。
/// 中文不转义便于排查；配置文件中不得出现任何敏感明文（API Key 后续走 DPAPI，见 FR-010）。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;

    public JsonSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.SettingsFile;
    }

    /// <summary>默认存储路径。</summary>
    public string FilePath => _filePath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions)
                   ?? new AppSettings();
        }
        catch (Exception)
        {
            // 文件损坏/被占用等场景回退默认值：设置问题不允许阻断启动
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
