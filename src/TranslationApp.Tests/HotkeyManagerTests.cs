using TranslationApp.Core.Hotkey;
using Xunit;

namespace TranslationApp.Tests;

public sealed class HotkeyManagerTests
{
    [Fact]
    public void TryRegister_SameNameAndDefinition_IsIdempotent()
    {
        using var manager = new HotkeyManager();
        var definition = new HotkeyDefinition(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x44);

        Assert.True(manager.TryRegister("input", definition));
        Assert.True(manager.TryRegister("input", definition));
        Assert.True(manager.IsRegistered("input"));
    }

    [Fact]
    public void TryRegister_NewCombinationUnavailable_KeepsPreviousCombinationRegistered()
    {
        using var owner = new HotkeyManager();
        using var editor = new HotkeyManager();

        var occupied = FindAvailableDefinition(owner, "occupied");
        Assert.True(owner.TryRegister("external-owner", occupied));

        var previous = FindAvailableDefinition(owner, "previous");
        Assert.True(editor.TryRegister("editable", previous));

        // 用户录入一个已被其他实例占用的新组合。
        Assert.False(editor.TryRegister("editable", occupied));

        // 失败不得先注销旧组合；否则用户只看到报错，原热键也同时失效。
        Assert.True(editor.IsRegistered("editable"));
    }

    [Fact]
    public void SuspendAll_KeepsDesiredDefinitionsAndAllowsEditing()
    {
        using var manager = new HotkeyManager();
        var original = FindAvailableDefinition(manager, "original");
        Assert.True(manager.TryRegister("editable", original));

        manager.SuspendAll();
        Assert.True(manager.IsRegistered("editable"));

        var updated = FindAvailableDefinition(manager, "updated");
        Assert.True(manager.TryRegister("editable", updated));
        Assert.Empty(manager.ResumeAll());
        Assert.True(manager.IsRegistered("editable"));
    }

    [Fact]
    public void SuspendAll_StillRejectsCombinationOwnedByAnotherProcess()
    {
        using var owner = new HotkeyManager();
        using var editor = new HotkeyManager();
        var occupied = FindAvailableDefinition(owner, "occupied");
        Assert.True(owner.TryRegister("external-owner", occupied));
        var previous = FindAvailableDefinition(owner, "previous");
        Assert.True(editor.TryRegister("editable", previous));

        editor.SuspendAll();
        Assert.False(editor.TryRegister("editable", occupied));
        Assert.True(editor.IsRegistered("editable"));
        Assert.Empty(editor.ResumeAll());
    }

    [Fact]
    public void SuspendAll_NestedOwners_KeepRecordingUntilEveryOwnerResumes()
    {
        using var manager = new HotkeyManager();
        using var owner = new HotkeyManager();
        var definition = FindAvailableDefinition(manager, "nested");
        Assert.True(manager.TryRegister("editable", definition));

        manager.SuspendAll();
        manager.SuspendAll();
        Assert.Empty(manager.ResumeAll());
        Assert.True(owner.TryRegister("external", definition));
        owner.Unregister("external");
        Assert.Empty(manager.ResumeAll());
        Assert.True(manager.IsRegistered("editable"));
    }

    [Fact]
    public void CanRegister_RejectsCombinationAlreadyOwnedByThisApp()
    {
        using var manager = new HotkeyManager();
        var definition = FindAvailableDefinition(manager, "probe");
        Assert.True(manager.TryRegister("input", definition));

        Assert.True(manager.CanRegister("input", definition));
        Assert.False(manager.CanRegister("select", definition));
    }

    [Fact]
    public void Unregister_ThenRegisterSameName_Works()
    {
        using var manager = new HotkeyManager();
        var definition = FindAvailableDefinition(manager, "reregister");
        Assert.True(manager.TryRegister("input", definition));

        manager.Unregister("input");
        Assert.False(manager.IsRegistered("input"));

        Assert.True(manager.TryRegister("input", definition));
        Assert.True(manager.IsRegistered("input"));
    }

    private static HotkeyDefinition FindAvailableDefinition(HotkeyManager manager, string prefix)
    {
        for (var f = 1; f <= 12; f++)
        {
            var candidate = new HotkeyDefinition(
                HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
                0x70 + f - 1);
            if (manager.TryRegister(prefix + "-" + f, candidate))
            {
                manager.Unregister(prefix + "-" + f);
                return candidate;
            }
        }

        throw new InvalidOperationException("没有找到可注册的测试热键；请关闭占用 Ctrl+Alt+Shift+F1~F12 的程序后重试。");
    }
}
