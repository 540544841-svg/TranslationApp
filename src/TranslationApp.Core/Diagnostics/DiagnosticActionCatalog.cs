namespace TranslationApp.Core.Diagnostics;

/// <summary>Doctor 结果对应的一键操作；Key 由表示层解释，Core 只负责稳定映射。</summary>
public sealed record DiagnosticAction(string Key, string Label);

/// <summary>
/// 把诊断结果映射为用户下一步可执行的动作。
/// 映射保持纯函数，便于单元测试，也避免 XAML/ViewModel 各自维护一份易漂移的规则。
/// </summary>
public static class DiagnosticActionCatalog
{
    public static DiagnosticAction? Resolve(DiagnosticResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // 已通过的项目不需要抢夺注意力；跳过项只有在明确给出建议时才提供入口。
        if (result.Status == DiagnosticStatus.Passed)
        {
            return null;
        }

        if (result.Id.StartsWith("hotkey-", StringComparison.Ordinal)
            || result.Id.StartsWith("hotkey:", StringComparison.Ordinal))
        {
            return new DiagnosticAction("navigate:hotkeys", "打开热键设置");
        }

        return result.Id switch
        {
            "ocr-windows" or "ocr-paddle" =>
                new DiagnosticAction("navigate:advanced", "检查 OCR 设置"),
            "proxy" =>
                new DiagnosticAction("navigate:advanced", "检查代理设置"),
            "database" or "data-directory" =>
                new DiagnosticAction("open-data-folder", "打开数据目录"),
            "update-channel" or "update-cache" or "update-replace" =>
                new DiagnosticAction("navigate:updates", "打开更新与数据"),
            _ when result.Status == DiagnosticStatus.Failed || result.Status == DiagnosticStatus.Warning =>
                new DiagnosticAction("rerun", "重新诊断"),
            _ => null,
        };
    }
}
