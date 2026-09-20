using System.Windows;
using Serilog;
using TranslationApp.Core.Api;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.Services;

/// <summary>
/// 本地 HTTP API 生命周期网关（FR-046 / spec §4）：设置项（开关/端口/token）与监听实例之间的胶水——
/// 任一变更都重建监听（token 在构造时固化，改 token 必须重启监听）。
/// 启停门控在 App.ApplyPrivacySideEffects：隐私模式开启时**绝不监听**（与钩子同纪律）。
/// </summary>
public sealed class LocalApiGateway(AppSettings settings, ISettingsStore store, TranslatorCatalog catalog)
{
    private LocalApiServer? _server;

    /// <summary>当前生效 token（DPAPI 解密后只在内存；空 = 尚未生成）。</summary>
    public string? Token { get; private set; }

    public bool IsRunning => _server?.IsRunning == true;

    /// <summary>最近一次启动失败原因（设置页状态行用；成功为 null）。</summary>
    public string? LastStartFailure { get; private set; }

    /// <summary>按当前设置启停监听。<paramref name="allowed"/> = 开启 &amp;&amp; 非隐私模式。</summary>
    public void Apply(bool allowed)
    {
        Stop();
        if (!allowed)
        {
            return;
        }

        var token = EnsureToken();
        var port = settings.LocalApiPort is >= 1024 and <= 65535
            ? settings.LocalApiPort
            : LocalApiServer.DefaultPort;

        var server = new LocalApiServer(port, token, TranslateAsync, ConfiguredEngineIds);
        if (!server.TryStart())
        {
            server.Dispose();
            LastStartFailure = $"端口 {port} 启动失败（可能被占用，换一个）";
            Log.Warning("本地 API 监听启动失败（端口 {Port}）", port);
            return;
        }

        _server = server;
        LastStartFailure = null;
        Log.Information("本地 API 已监听 http://127.0.0.1:{Port}/（token 鉴权，仅回环）", port);
    }

    public void Stop()
    {
        _server?.Dispose();
        _server = null;
    }

    /// <summary>重新生成 token（DPAPI 落盘）并由调用方触发 Apply 重启监听。</summary>
    public string RegenerateToken()
    {
        Token = LocalApiServer.GenerateToken();
        SaveToken();
        return Token;
    }

    private string EnsureToken()
    {
        if (!string.IsNullOrEmpty(Token))
        {
            return Token;
        }

        Token = SecretStore.Unprotect(settings.LocalApiTokenEncrypted);
        if (string.IsNullOrEmpty(Token))
        {
            Token = LocalApiServer.GenerateToken();
            SaveToken();
        }

        return Token;
    }

    private void SaveToken()
    {
        settings.LocalApiTokenEncrypted = SecretStore.Protect(Token ?? "");
        store.Save(settings);
    }

    private static IReadOnlyList<string> ConfiguredIdsOf(TranslatorCatalog catalog) =>
        catalog.All.Where(t => t.IsConfigured).Select(t => t.Id).ToArray();

    private IReadOnlyList<string> ConfiguredEngineIds() => ConfiguredIdsOf(catalog);

    private async Task<LocalApiTranslation> TranslateAsync(
        string text, string source, string target, System.Threading.CancellationToken cancellationToken)
    {
        var translator = catalog.Resolve(settings.Engine);

        // 批 5 / FR-051：脚本调用应与手动翻译同口吻，故沿用同一个「风格」设置。
        // 语境刻意不带——API 是无状态单句请求，塞上文会让调用方无法预期结果。
        var style = TranslationStyles.Parse(settings.TranslationStyle);
        var result = translator is IPromptDirectiveTranslator directable && style != TranslationStyle.None
            ? await directable.TranslateAsync(text, source, target, new TranslationDirective(null, style), cancellationToken)
            : await translator.TranslateAsync(text, source, target, cancellationToken);

        return new LocalApiTranslation(result.TranslatedText, translator.Name, result.GlossaryHits);
    }
}
