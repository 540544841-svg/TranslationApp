using System.Windows;
using TranslationApp.Core.Capture;

namespace TranslationApp.Services;

/// <summary>
/// WPF 剪贴板完整快照适配器。剪贴板/OLE 操作统一切回 UI 线程，避免后台取词线程无 STA 导致访问失败。
/// </summary>
public sealed class WpfClipboardSnapshotProvider : IClipboardSnapshotProvider
{
    public IClipboardSnapshot? Capture() => OnUiThread(CaptureCore);

    public bool TryRestore(IClipboardSnapshot snapshot)
    {
        if (snapshot is not Snapshot data)
        {
            return false;
        }

        return OnUiThread(() =>
        {
            try
            {
                Clipboard.SetDataObject(data.Data, copy: true);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static Snapshot? CaptureCore()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source is null)
            {
                return null;
            }

            var clone = new DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var value = source.GetData(format, autoConvert: false);
                    if (value is not null)
                    {
                        clone.SetData(format, value);
                    }
                }
                catch
                {
                    // 某些第三方私有格式不可序列化；其余标准格式仍照常保留。
                }
            }

            return clone.GetFormats(autoConvert: false).Length == 0 ? null : new Snapshot(clone);
        }
        catch
        {
            return null;
        }
    }

    private static T OnUiThread<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.Invoke(action);
    }

    private sealed record Snapshot(DataObject Data) : IClipboardSnapshot;
}
