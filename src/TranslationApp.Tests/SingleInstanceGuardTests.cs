using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>单实例守卫冒烟测试（阶段 0 验收：重复启动被拦截）。</summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public void SecondAcquire_IsNotFirstInstance()
    {
        var name = "Local\\TranslationApp.Tests." + Guid.NewGuid().ToString("N");

        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.IsFirstInstance, "第一个实例应获得互斥体");
        Assert.False(second.IsFirstInstance, "第二个实例应被判定为重复启动");
    }
}
