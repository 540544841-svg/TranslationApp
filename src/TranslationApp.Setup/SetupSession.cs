namespace TranslationApp.Setup;

/// <summary>
/// 一次立契的现场：用户在界面上做的决定 + 安装器随身的载荷。
/// 界面只读写这里，真正的系统改动全部由 <see cref="InstallService"/> 执行。
/// </summary>
internal sealed class SetupSession
{
    public SetupSession()
    {
        // 已经装过一次时，落址默认指回上一次的地方（覆盖安装 = 原位更新）
        InstallDirectory = InstallLayout.TryGetInstalledDirectory(out var installed)
            ? installed
            : InstallLayout.DefaultInstallDirectory;
    }

    public PayloadStore Payload { get; } = PayloadStore.Current;

    public string InstallDirectory { get; set; }

    public EngineOption Engine { get; set; } = EngineOption.Bing;

    public InstallReport? Report { get; set; }
}
