using TranslationApp.Core.Layout;

namespace TranslationApp.Core.Settings;

/// <summary>
/// 配置结构迁移：把旧版本的 settings.json 升到 <see cref="CurrentSchemaVersion"/>。
/// 纯函数、不做 IO，由 <see cref="JsonSettingsStore.Load"/> 在反序列化之后调用，便于单测穷举。
/// </summary>
public static class SettingsMigrations
{
    /// <summary>当前配置结构版本。</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>版本 1 的出厂默认小窗宽高（窗口 DIP，含阴影留白）。</summary>
    private const double LegacyQuickWindowWidth = 420;

    private const double LegacyQuickWindowHeight = 320;

    /// <summary>
    /// 按 <see cref="AppSettings.SchemaVersion"/> 逐级升级。已是最新版时原样返回。
    /// </summary>
    public static AppSettings Upgrade(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return settings;
        }

        if (settings.SchemaVersion < 2)
        {
            // 小窗按设计稿改版（卡片宽 420 → 472）。只迁移「从没改过尺寸」的配置：
            // 宽高任一被用户调过就整体不动，避免把用户的选择覆盖掉。
            if (Math.Abs(settings.QuickWindowWidth - LegacyQuickWindowWidth) < 0.001
                && Math.Abs(settings.QuickWindowHeight - LegacyQuickWindowHeight) < 0.001)
            {
                settings.QuickWindowWidth = WindowSizePolicy.DefaultWidthDip;
                settings.QuickWindowHeight = WindowSizePolicy.DefaultHeightDip;
            }
        }

        if (settings.SchemaVersion < 3)
        {
            MigrateToLlmProviders(settings);
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return settings;
    }

    /// <summary>
    /// 2 → 3：AI 从「单档三字段」变成「供应商列表 + 当前档」。
    /// 老配置里填过的值搬进第一档；没填过的（空串）保留出厂默认值，不把默认值当成用户配置。
    /// 遗留言段随即置空，此后 settings.json 里只有供应商列表这一个真相源。
    /// </summary>
    private static void MigrateToLlmProviders(AppSettings settings)
    {
        var provider = settings.LlmProviders.Count > 0 ? settings.LlmProviders[0] : LlmProvider.CreateDefault();

        if (settings.LlmBaseUrl?.Trim() is { Length: > 0 } baseUrl)
        {
            provider.BaseUrl = baseUrl;
        }

        if (settings.LlmModel?.Trim() is { Length: > 0 } model)
        {
            provider.Model = model;
        }

        if (settings.LlmApiKeyEncrypted is { Length: > 0 } key)
        {
            provider.ApiKeyEncrypted = key;
        }

        provider.Id = LlmProvider.DefaultId;
        provider.Name = LlmProviderNameFromUrl(provider.BaseUrl);

        settings.LlmProviders = [provider];
        settings.LlmActiveProviderId = LlmProvider.DefaultId;
        settings.LlmBaseUrl = "";
        settings.LlmModel = "";
        settings.LlmApiKeyEncrypted = "";
    }

    /// <summary>
     /// 从接口地址推一个可读的档位名：出厂默认地址保留 DeepSeek 这个名字，本机地址叫「本地模型」，
     /// 其余取域名并去掉 api. 前缀（api.moonshot.cn → moonshot.cn）。
     /// </summary>
    internal static string LlmProviderNameFromUrl(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
        {
            return "默认供应商";
        }

        return uri.Host switch
        {
            // 出厂默认那一档保持和 LlmProvider.CreateDefault 同名，迁移后不会突然变成域名
            "api.deepseek.com" => "DeepSeek",
            "localhost" or "127.0.0.1" or "::1" => "本地模型",
            _ => uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host,
        };
    }
}
