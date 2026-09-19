namespace TranslationApp.Core.Settings;

/// <summary>
/// 设置持久化接口。Core 层不关心存储介质，便于单元测试与后续替换
/// （如 FR-023 便携模式：配置存 EXE 同目录）。
/// </summary>
public interface ISettingsStore
{
    /// <summary>加载设置；文件缺失或损坏时返回默认值，绝不抛异常。</summary>
    AppSettings Load();

    /// <summary>原子保存设置（写临时文件后替换，避免写一半被读取/断电损坏）。</summary>
    void Save(AppSettings settings);
}
