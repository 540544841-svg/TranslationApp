# AGENTS.md — 译印 INKSEAL 项目规则

> 通用行为准则（中文作答、每次任务结束给下一步建议等）见 `~/.codex/AGENTS.md`；
> 这里只放**本项目特有的硬性规则**。

## 1. 发布前必须先清掉正在运行的实例

发 `publish\TranslationApp.exe` 之前，**先把正在跑的译印实例停掉**，再打包。

原因（两条都是踩过的坑）：

- 正在运行的实例会锁住 `publish\TranslationApp.exe`，新包根本写不进去，只能改名绕开，
  于是 `publish\` 里、`out\` 里堆一堆 `running-old-*.exe`。
- 更要命的是单实例守卫（互斥体 `Local\TranslationApp.SingleInstance`）：用户双击新 exe 时
  只会把**旧窗口**唤到前台——看起来“更新了”，实际看到的还是老界面，然后反复被质疑没重新打包。

做法：

- 发布**一律走 `build\publish.ps1`**，它开头就会 `Stop-Process -Name TranslationApp -Force`
  并等文件句柄释放；不要手工 `dotnet publish -o publish` 绕过这一步。
- 发布完成后把实例起回来（或明确告诉用户去启动），别让用户面对一个凭空消失的托盘图标。
- 若确实需要在实例运行期间出临时包做比对，输出到临时目录（如 `out\publish-staging`），
  不要覆盖 `publish\`。
