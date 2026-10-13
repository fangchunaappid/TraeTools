namespace TraeSwitch.Services;

/// <summary>切换守护结果。</summary>
public enum SwitchOutcomeKind
{
    /// <summary>探测到会话写入，判定已进入目标账号。</summary>
    Active,
    /// <summary>明显没进入（凭据缺失/客户端未起来），已自动回滚到切换前的 live 并重启。</summary>
    RolledBack,
    /// <summary>信号不足（凭据在但未见写入/暂时读不到），需要用户确认是否真的进入。</summary>
    NeedsConfirm
}

/// <summary>
/// 切换结果；RollbackDir 保留"切换前 live"快照，MainForm 处理完需删除；
/// 需要回滚时调用 <see cref="SwitcherService.RollbackSwitchAsync"/>。
/// </summary>
public sealed record SwitchOutcome(
    SwitchOutcomeKind Kind, string? RollbackDir, string[] Rels, string Account,
    /// <summary>守护期间观察到的信号摘要（写日志用，帮助判断"为什么是 NeedsConfirm"）。</summary>
    string? Detail = null);

/// <summary>
/// 冷切换编排：结束进程 → 当前 live 载体做临时回滚快照 → 写回目标账号 vault → 拉起客户端。
/// restore/launch 任一步失败则回滚到切换前内容。Fingerprint 条目支持文件或目录。
/// </summary>
public sealed class SwitcherService(string rootDir, VaultService vault, IClientController client)
{
    /// <summary>守护轮询上限；internal 以便单测缩短。</summary>
    internal static TimeSpan GuardTimeout = TimeSpan.FromSeconds(20);

    public async Task SwitchToAsync(string account, IEnumerable<string> fingerprint)
    {
        var rels = fingerprint.ToArray();
        // 关闭客户端最坏要等 12s 优雅超时 + 8s 强杀超时（内部是 Thread.Sleep 轮询），
        // 同步执行会把 UI 线程整段冻住，必须放后台线程。
        await Task.Run(() => client.KillAll());

        // 1) 当前 live 状态快照到临时目录（回滚用），目录条目整树快照
        var rollback = Path.Combine(Path.GetTempPath(), "traeswitch_rollback_" + Guid.NewGuid().ToString("N"));
        foreach (var rel in rels)
            VaultService.OverwriteEntry(rootDir, rollback, rel);

        try
        {
            // 2) 写回目标账号
            await vault.RestoreAsync(account, rels);
        }
        catch
        {
            Rollback(rollback, rels);
            throw;
        }

        try
        {
            // 3) 拉起
            client.Launch();
        }
        catch
        {
            Rollback(rollback, rels);
            try { client.Launch(); } catch { /* 尽力恢复启动 */ }
            throw;
        }
        finally
        {
            try { Directory.Delete(rollback, true); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>
    /// 带守护的切换：保留"切换前 live"快照直到守护判定结束。
    /// 启动后轮询 storage.json：写入过 = 云会话在工作（Active）；
    /// 凭据缺失/进程没起来 = 明显失败，自动回滚并重启（RolledBack）；
    /// 凭据在但没观察到写入 = 需要用户确认（NeedsConfirm）。
    /// </summary>
    public async Task<SwitchOutcome> SwitchWithGuardAsync(
        string account, IEnumerable<string> fingerprint, Action<TimeSpan>? progress = null)
    {
        var rels = fingerprint.ToArray();
        // 同上：关闭客户端是长时间阻塞操作，放后台线程，别冻 UI。
        await Task.Run(() => client.KillAll());

        var rollback = Path.Combine(Path.GetTempPath(), "traeswitch_rollback_" + Guid.NewGuid().ToString("N"));
        foreach (var rel in rels)
            VaultService.OverwriteEntry(rootDir, rollback, rel);

        try { await vault.RestoreAsync(account, rels); }
        catch
        {
            Rollback(rollback, rels);
            try { Directory.Delete(rollback, true); } catch { /* 忽略清理失败 */ }
            throw;
        }

        var storage = AuthProbe.StoragePath(rootDir);
        // 客户端已关，此处取一次基线：仅用于诊断（"客户端是否写过 storage.json"），不再作成功判据
        var baseline = AuthProbe.GetStamp(storage);

        try { client.Launch(); }
        catch
        {
            // 已恢复 live；本路径异常会向上抛（outcome 为 null，MainForm 不会来收尾），
            // 需在此自行清掉临时回滚目录，避免 %TEMP% 滞留。
            Rollback(rollback, rels);
            try { Directory.Delete(rollback, true); } catch { /* 忽略清理失败 */ }
            try { client.Launch(); } catch { /* 尽力恢复 */ }
            throw;
        }

        // 轮询**登录凭据键**（P1-1），不再看 storage.json 的时间戳：
        // 恢复 vault→live 这一步自身就会改 storage.json 的时间戳/长度，用"首见变化"判成功是假阳性——
        // 客户端随后鉴权失败会 clearUserInfo（删掉 icube.cloudide 键、界面显示未登录），
        // 而时间戳判据在它删除之前就已经报 Active 了。
        // 正确信号只有凭据键本身：被客户端删除 = 明确未进入；全程齐全 = 真进入。
        bool runningSeen = false, credsSeen = false, authCleared = false, stampTouched = false;
        var started = DateTime.UtcNow;
        var deadline = started.Add(GuardTimeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            progress?.Invoke(DateTime.UtcNow - started);
            if (!client.IsRunning()) continue;
            runningSeen = true;
            if (AuthProbe.StampChanged(baseline, AuthProbe.GetStamp(storage))) stampTouched = true;
            switch (AuthProbe.HasAuthCreds(storage))
            {
                case false:   // 客户端 clearUserInfo：登出/未进入（如 refresh token 失效后主动删键）
                    authCleared = true;
                    break;
                case true:    // 凭据键齐全，至少见到过进入态
                    credsSeen = true;
                    break;
                // null = 文件被占用/正在改写，瞬态：忽略，下轮再试
            }
            if (authCleared) break;   // 明确失败，提前结束
        }

        SwitchOutcomeKind kind;
        string? detail;
        if (!runningSeen)
        {
            kind = SwitchOutcomeKind.RolledBack;      // 进程没起来
            detail = "客户端进程在守护期内未起来";
        }
        else if (authCleared)
        {
            kind = SwitchOutcomeKind.RolledBack;      // 客户端主动登出
            detail = "客户端已主动清除登录凭据（通常为 refresh token 失效 / 该账号未登录）";
        }
        else if (credsSeen)
        {
            kind = SwitchOutcomeKind.Active;           // 进程起来且凭据键全程保持
            detail = "登录凭据键全程齐全";
        }
        else
        {
            kind = SwitchOutcomeKind.NeedsConfirm;     // 凭据始终读不到，需人工确认
            detail = stampTouched
                ? "客户端写过 storage.json，但登录凭据键始终读不到（可能被占用或在拖延）"
                : "守护期内未观察到客户端写入 storage.json";
        }

        if (kind == SwitchOutcomeKind.RolledBack)
            await DoRollbackAsync(rollback, rels);

        return new SwitchOutcome(kind, rollback, rels, account, detail);
    }

    /// <summary>守护判定为 NeedsConfirm 且用户选择回滚时调用。</summary>
    public async Task RollbackSwitchAsync(SwitchOutcome outcome)
    {
        if (outcome.RollbackDir is { } dir)
            await DoRollbackAsync(dir, outcome.Rels);
    }

    private async Task DoRollbackAsync(string rollback, string[] rels)
    {
        try { await Task.Run(() => client.KillAll()); } catch { /* 忽略 */ }
        Rollback(rollback, rels);
        try { client.Launch(); } catch { /* 尽力恢复启动 */ }
    }

    private void Rollback(string rollback, string[] rels)
    {
        // 与恢复同一条路径：回滚同样要先清目标侧 code.lock 与 state.vscdb 边车，
        // 否则回滚后启动客户端会回放到失败切换写入的 WAL，旧账号同样起不来。
        VaultService.RestoreEntrySet(rollback, rootDir, rels);
    }
}
