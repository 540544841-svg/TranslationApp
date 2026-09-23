using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Serilog;
using TranslationApp.Core.Capture;

namespace TranslationApp.Services;

/// <summary>
/// 启动并连接 OCR 隔离进程（P0「OCR 原生引擎进程隔离」的进程管理侧）。
///
/// 【为什么用同一 EXE】<c>TranslationApp.exe --ocr-worker &lt;管道名&gt;</c>：
/// 22.5MB 嵌入模型不重复打包、发布产物仍是单文件、WPF 的 WinExe 子系统顺带保证没有控制台黑窗。
///
/// 【生命周期】这里**不主动 kill** 子进程：客户端关闭命名管道后子进程读到 EOF 会自行退出
/// （主进程崩溃/被强杀时同样成立，因为 OS 会关闭句柄）。只在启动失败与进程退出时做收尾。
/// </summary>
public sealed class OcrWorkerLauncher : IDisposable
{
    private readonly object _sync = new();
    private Process? _current;

    /// <summary>
    /// 启动一个新的隔离进程并返回**已连接**的管道流。调用方（<see cref="OcrWorkerClient"/>）
    /// 负责在空闲/失败时 <c>DisposeAsync</c> 该流，从而让子进程自行退出。
    /// </summary>
    public async Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // 由 dotnet 宿主直接跑 dll 时无法自启动（AppPaths 那套「便携模式」判定同样会走到这里）
            throw new InvalidOperationException("OCR 隔离进程需要以 EXE/apphost 方式运行，当前宿主不支持自启动");
        }

        var pipeName = OcrWorkerProtocol.PipeNamePrefix + Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(OcrWorkerProtocol.WorkerArgument);
        startInfo.ArgumentList.Add(pipeName);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("OCR 隔离进程启动失败：Process.Start 返回 null");
        Track(process);

        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // ConnectAsync 内部会对 ERROR_PIPE_BUSY 轮询重试，直到 token 取消（子进程启动 + 建管道）
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            Log.Debug("OCR 隔离进程已连接（PID {Pid}，管道 {Pipe}）", process.Id, pipeName);
            return client;
        }
        catch
        {
            client.Dispose();
            KillQuietly(process);
            throw;
        }
    }

    private void Track(Process process)
    {
        lock (_sync)
        {
            // 理论上不会走到：上一次通道释放后子进程已自行退出。防御性地清理残留，避免进程堆积。
            KillQuietly(_current);
            _current = process;
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_current, process))
                    {
                        _current = null;
                    }
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 收尾异常无关紧要
                }
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OCR 隔离进程未注册退出回调（不影响识别链路）");
        }
    }

    private static void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已退出/无权限：忽略
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            var current = _current;
            _current = null;
            KillQuietly(current);
        }
    }
}
