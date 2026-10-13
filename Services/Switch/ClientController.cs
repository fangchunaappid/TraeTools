using System.Diagnostics;

namespace TraeSwitch.Services;

public interface IClientController
{
    void KillAll();
    void Launch();
    bool IsRunning();
}

/// <summary>
/// 真实进程控制：按进程名结束全部实例并重新拉起客户端。
/// 关闭采用三级策略（P1-2）：优雅 WM_CLOSE → 等待 → 进程树强杀 → 仍失败则抛错人工介入。
/// 强杀是常态（参考项目实测 TRAE 切换基本都走到强杀），所以"残留 -wal/-shm 的清理"放在恢复侧
/// （见 VaultService.RestoreEntrySet）；但能优雅关就优雅关，让客户端自己 checkpoint 落盘最多数据。
/// </summary>
public sealed class ClientController : IClientController
{
    private readonly string _processName;
    private readonly string _exePath;
    private readonly TimeSpan _gracefulWait;
    private readonly TimeSpan _forceWait;

    public ClientController(string processName, string exePath)
        : this(processName, exePath, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8))
    {
    }

    /// <summary>超时可注入，便于在独立环境验证三级升级逻辑（不依赖真实客户端）。</summary>
    internal ClientController(string processName, string exePath, TimeSpan gracefulWait, TimeSpan forceWait)
    {
        _processName = processName;
        _exePath = exePath;
        _gracefulWait = gracefulWait;
        _forceWait = forceWait;
    }

    public void KillAll()
    {
        if (string.IsNullOrWhiteSpace(_processName))
            throw new InvalidOperationException("未配置客户端进程名（settings.json 的 ProcessName），无法安全关闭客户端");

        List<Process> procs;
        try { procs = [.. Process.GetProcessesByName(_processName)]; }
        catch { return; }
        if (procs.Count == 0) return;

        try { CloseAll(procs, _gracefulWait, _forceWait, IsAlive, ForceKill); }
        finally
        {
            // 拿到的 Process 对象必须 Dispose，否则常驻托盘下句柄持续累积（#33）
            foreach (var p in procs) { try { p.Dispose(); } catch { /* 忽略 */ } }
        }
    }

    /// <summary>
    /// 三级关闭核心。isAlive/forceKill 可注入（独立环境验证用），默认真实进程操作。
    /// 两级后仍有存活进程时抛 <see cref="InvalidOperationException"/>：宁可报错让人工处理，
    /// 也不要带着残留进程/未 checkpoint 的 WAL 继续切换（那正是"切了等于没切"的来源）。
    /// </summary>
    internal static void CloseAll(
        IReadOnlyList<Process> procs, TimeSpan gracefulWait, TimeSpan forceWait,
        Func<Process, bool> isAlive, Func<Process, bool> forceKill)
    {
        // 1) 优雅：向主窗口 PostMessage WM_CLOSE，等客户端自行退出（有机会自己 checkpoint WAL）
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { /* 无窗口/已退出：进入下一级 */ }
        }
        if (WaitAllExited(procs, gracefulWait, isAlive)) return;

        // 2) 树杀：主进程 + 全部子进程强杀（TRAE 为 Electron 多进程）
        foreach (var p in procs)
        {
            if (!isAlive(p)) continue;
            forceKill(p);
        }
        if (WaitAllExited(procs, forceWait, isAlive)) return;

        // 3) 仍存活：失败上报
        var stuck = string.Join(", ", procs.Where(isAlive).Select(SafeDescribe));
        throw new InvalidOperationException(
            $"无法关闭客户端进程（{stuck}）。请手动结束进程后重试切换——继续操作会因残留文件锁/WAL 导致切换无效。");
    }

    private static bool WaitAllExited(IReadOnlyList<Process> procs, TimeSpan timeout, Func<Process, bool> isAlive)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!procs.Any(isAlive)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
    }

    private static bool IsAlive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    private static bool ForceKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); return true; }
        catch { return false; }
    }

    private static string SafeDescribe(Process p)
    {
        try { return $"{p.ProcessName}(PID {p.Id})"; } catch { return "未知进程"; }
    }

    public void Launch()
    {
        if (string.IsNullOrEmpty(_exePath) || !File.Exists(_exePath))
            throw new FileNotFoundException("找不到客户端：" + _exePath);
        Process.Start(new ProcessStartInfo(_exePath) { UseShellExecute = true });
    }

    public bool IsRunning() => GetProcessCount() > 0;

    /// <summary>查询进程数并立即释放全部 Process 句柄（GetProcessesByName 返回的对象必须 Dispose）。</summary>
    private int GetProcessCount()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName(_processName);
            int n = procs.Length;
            foreach (var p in procs) try { p.Dispose(); } catch { /* 忽略 */ }
            return n;
        }
        catch { return 0; }
    }
}
