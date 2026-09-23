namespace TranslationApp.Core.Capture;

public interface IClipboardSnapshot;

public interface IClipboardSnapshotProvider
{
    IClipboardSnapshot? Capture();

    bool TryRestore(IClipboardSnapshot snapshot);
}
