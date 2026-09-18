using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Avatar;

namespace TraeTools.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    public record TrendPoint(string DateLabel, double Credits, double X, double Y, string Tooltip);

    [ObservableProperty]
    private int _remainingCredits = 0;

    [ObservableProperty]
    private string _todayReward = "--";

    [ObservableProperty]
    private string _checkinStatus = "--";

    [ObservableProperty]
    private int _streakDays = 0;

    [ObservableProperty]
    private bool _isMember = false;

    [ObservableProperty]
    private string _currentAccount = "未添加账号";

    [ObservableProperty]
    private string _dateText = DateTime.Today.ToString("yyyy-MM-dd ddd");

    public ObservableCollection<AccountInfo> Accounts { get; } = new();

    public IList<Point> LinePoints { get; private set; } = new List<Point>();
    public Geometry FillGeometry { get; private set; } = new StreamGeometry();
    public double TodayX { get; private set; }
    public double TodayY { get; private set; }

    public ObservableCollection<TrendPoint> TrendPoints { get; } = new();
    public ObservableCollection<string> XAxisLabels { get; } = new();

    public const double ChartWidth = 520;
    public const double ChartHeight = 200;

    /// <summary>Y 轴刻度（积分）。</summary>
    [ObservableProperty]
    private int _chartYMax = 0;

    [ObservableProperty]
    private int _chartYMid = 0;

    [ObservableProperty]
    private int _chartYMin = 0;

    /// <summary>总积分历史文件（%APPDATA%\TraeTools\data\credits_total_&lt;accId&gt;.txt，逐账号独立）。</summary>
    private static string TotalHistoryPathFor(string accountId)
        => System.IO.Path.Combine(TraeTools.Services.DataPaths.DataDir, $"credits_total_{accountId}.txt");

    /// <summary>读取某账号总积分历史（按日期升序；文件格式 yyyy-MM-dd,total）。</summary>
    private static List<(DateTime Date, double Total)> ReadTotalHistory(string accountId)
    {
        var list = new List<(DateTime Date, double Total)>();
        try
        {
            var path = TotalHistoryPathFor(accountId);
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && DateTime.TryParse(parts[0], out var d) && double.TryParse(parts[1], out var v))
                    list.Add((d, v));
            }
        }
        catch { /* 读取失败返回空 */ }
        return list.OrderBy(x => x.Date).ToList();
    }

    /// <summary>当天无记录时追加当前总积分（同时写入 SQLite 与旧版文本文件）。</summary>
    private static void AppendTotalToday(TraeCheckin.TraeAccount acc, double total)
    {
        if (total < 0) return;
        // 写入 SQLite（ON CONFLICT 自动去重）
        try
        {
            MainViewModel.CheckinDb?.InsertSnapshot(acc.Id, total);
        }
        catch { /* 数据库写入失败不影响 */ }
        // 同时写入旧版文本文件（保持兼容）
        try
        {
            var history = ReadTotalHistory(acc.Id);
            if (history.Any(h => h.Date.Date == DateTime.Today)) return;
            var dir = System.IO.Path.GetDirectoryName(TotalHistoryPathFor(acc.Id))!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(TotalHistoryPathFor(acc.Id), $"{DateTime.Today:yyyy-MM-dd},{total:0.##}{Environment.NewLine}");
        }
        catch { /* 记录失败不影响 */ }
    }

    /// <summary>从该账号真实总积分历史重建曲线（近 N 天），并更新 Y 轴刻度。</summary>
    private void BuildChartFromHistory(string accountId)
    {
        // 优先从 SQLite 读取趋势数据
        List<(DateTime Date, double Remaining)> dbTrend = new();
        try
        {
            dbTrend = MainViewModel.CheckinDb?.GetSnapshotTrend(accountId, 14) ?? new();
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        List<(DateTime Date, double Total)> history;
        if (dbTrend.Count > 0)
        {
            history = dbTrend.Select(t => (t.Date, t.Remaining)).ToList();
        }
        else
        {
            history = ReadTotalHistory(accountId);
        }

        if (history.Count == 0)
        {
            // 无历史：退化为单日当前积分 mock（避免空图）
            double[] single = { RemainingCredits, RemainingCredits };
            LinePoints = ComputeLinePoints(single);
            FillGeometry = ComputeFillGeometry(single);
            TrendPoints.Clear();
            XAxisLabels.Clear();
            XAxisLabels.Add("今日");
            ChartYMax = ChartYMid = ChartYMin = (int)RemainingCredits;
            // 无历史时今日标记同样对齐最后一个数据点，避免落到 (0,0)
            if (LinePoints.Count > 0)
            {
                TodayX = LinePoints[^1].X - 6;
                TodayY = LinePoints[^1].Y - 6;
            }
            return;
        }

        var recent = history.TakeLast(14).ToList();
        double[] values = recent.Select(h => h.Total).ToArray();
        LinePoints = ComputeLinePoints(values);
        FillGeometry = ComputeFillGeometry(values);

        TrendPoints.Clear();
        const double pad = 12;
        double stepX = values.Length > 1 ? (ChartWidth - 2 * pad) / (values.Length - 1) : 0;
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            TrendPoints.Add(new TrendPoint(recent[i].Date.ToString("M/d"), values[i], x, y,
                $"{recent[i].Date:yyyy-MM-dd}\n积分：{(int)values[i]}"));
        }
        XAxisLabels.Clear();
        int labelCount = Math.Max(1, Math.Min(8, recent.Count));
        int span = recent.Count - 1;
        for (int i = 0; i < labelCount; i++)
        {
            int idx = labelCount > 1 ? (int)Math.Round(i * (double)span / (labelCount - 1)) : 0;
            XAxisLabels.Add(idx == recent.Count - 1 ? "今日" : recent[idx].Date.ToString("M/d"));
        }

        ChartYMax = (int)Math.Ceiling(max);
        ChartYMid = (int)Math.Ceiling((max + min) / 2);
        ChartYMin = (int)Math.Floor(min);

        // 今日标记点（历史路径此前未赋值会画到 (0,0)，导致左上角出现白点）
        if (TrendPoints.Count > 0)
        {
            TodayX = TrendPoints[^1].X - 6;
            TodayY = TrendPoints[^1].Y - 6;
        }
    }

    public DashboardViewModel()
    {
        // 填充账号列表（优先真实账号，否则 mock）
        PopulateAccounts();

        // 从 config 读取剩余积分与签到状态
        var cfg = MainViewModel.AppConfig;
        if (cfg != null)
        {
            try
            {
                if (cfg.LastRemaining >= 0)
                    RemainingCredits = (int)cfg.LastRemaining;

                if (cfg.LastCheckinDate.HasValue)
                {
                    CheckinStatus = cfg.LastCheckinDate.Value.Date == DateTime.Today
                        ? "已完成 ✓"
                        : "未签到";
                }

                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null)
                {
                    CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
                    IsMember = acc.IsMember;
                }
            }
            catch { /* 保留 mock 默认值 */ }
        }

        // 趋势数据：优先读取激活账号的真实总积分历史，无账号或无历史时不生成模拟曲线
        var app = MainViewModel.AppConfig;
        var active = app?.Accounts.FirstOrDefault(a => a.Id == app.ActiveAccountId) ?? app?.Accounts.FirstOrDefault();
        if (active != null)
        {
            BuildChartFromHistory(active.Id);
            if (TrendPoints.Count == 0)
            {
                var values = GenerateTrendValues();
                LinePoints = ComputeLinePoints(values);
                FillGeometry = ComputeFillGeometry(values);
                TodayX = LinePoints[LinePoints.Count - 1].X - 6;
                TodayY = LinePoints[LinePoints.Count - 1].Y - 6;
                BuildTrendPointsAndLabels(values);
            }
        }
    }

    private void PopulateAccounts()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg != null && cfg.Accounts.Count > 0)
            {
                var colors = new[] { "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899" };
                int idx = 0;
                foreach (var acc in cfg.Accounts)
                {
                    var name = string.IsNullOrEmpty(acc.Name)
                        ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                        : acc.Name;
                    Accounts.Add(new AccountInfo
                    {
                        Name = name,
                        Initial = name.Length > 0 ? name[0].ToString() : "?",
                        Color = colors[idx % colors.Length],
                        Status = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "已签到" : "待签到",
                        StatusType = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "ok" : "info",
                        IsCurrent = acc.Id == cfg.ActiveAccountId
                    });
                    idx++;
                }
                LoadAvatars();
                return;
            }
        }
        catch { /* 加载失败保持空列表 */ }

        // 无真实账号时不展示示例账号（示例账号无法操作，容易造成混乱）
    }

    /// <summary>为账号概览卡片异步加载头像（有 AvatarUrl 且未加载过才拉，内存缓存）。</summary>
    private void LoadAvatars()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return;
        foreach (var item in Accounts)
        {
            if (item.HasAvatar) continue;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == item.Id);
            if (string.IsNullOrEmpty(acc?.AvatarUrl)) continue;
            _ = AvatarLoader.LoadIntoAsync(acc.AvatarUrl, img =>
            {
                if (img != null) item.AvatarImage = img;
            });
        }
    }

    private double[] GenerateTrendValues()
    {
        // 基于当前剩余积分，向前推 13 天的模拟积分（每天 +50）
        double today = RemainingCredits;
        double[] values = new double[14];
        for (int i = 0; i < 14; i++)
        {
            values[i] = today - (13 - i) * 50;
            if (values[i] < 0) values[i] = 0;
        }
        values[13] = today;
        return values;
    }

    private void BuildTrendPointsAndLabels(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();

        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / (max - min));
            var date = DateTime.Today.AddDays(i - (values.Length - 1));
            string dateLabel = date.ToString("M/d");
            string tooltip = $"{date:yyyy-MM-dd}\n积分：{(int)values[i]}";
            TrendPoints.Add(new TrendPoint(dateLabel, values[i], x, y, tooltip));
        }

        // X 轴标签：约 8 个均匀分布
        int labelCount = 8;
        for (int i = 0; i < labelCount; i++)
        {
            int idx = (int)Math.Round(i * (values.Length - 1) / (double)(labelCount - 1));
            if (idx == values.Length - 1)
                XAxisLabels.Add("今日");
            else
                XAxisLabels.Add(TrendPoints[idx].DateLabel);
        }
    }

    private static IList<Point> ComputeLinePoints(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var pts = new List<Point>();
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    private static Geometry ComputeFillGeometry(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pad, ChartHeight - pad), true);
            for (int i = 0; i < values.Length; i++)
            {
                double x = pad + i * stepX;
                double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
                ctx.LineTo(new Point(x, y));
            }
            ctx.LineTo(new Point(pad + (values.Length - 1) * stepX, ChartHeight - pad));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    public async Task LoadAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null) return;

            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) return;

            var status = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            if (status != null && status.code == 0)
            {
                CheckinStatus = status.checked_in ? "已完成 ✓" : "未签到";
                TodayReward = "+" + (status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                // 状态接口失败（token 失效/风控）：明确提示重登，不显示误导性的"已完成"
                CheckinStatus = "需重登";
            }

            // 归零守卫：接口失败/未授权时 GetRemainingCreditsAsync 可能返回 0，此时不覆盖现有显示
            var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (credits > 0 || (credits >= 0 && string.IsNullOrEmpty(api.LastError)))
            {
                RemainingCredits = (int)credits;
                cfg.LastRemaining = credits;
                try { cfg.Save(); } catch { /* 忽略保存失败 */ }
            }
        }
        catch
        {
            // 网络/解析失败时保留 mock 数据，不崩溃
        }
    }

    /// <summary>对齐 TraeCheckin.RefreshAllAsync：按激活账号真实拉取状态/积分/单日奖励。</summary>
    public async Task RefreshAllAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc == null)
            {
                CurrentAccount = "未添加账号";
                CheckinStatus = "未登录";
                return;
            }
            CurrentAccount = string.IsNullOrEmpty(acc.Name)
                ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                : acc.Name!;
            IsMember = acc.IsMember;
            AccountHelpers.EnsureDeviceId(acc);

            double remaining = RemainingCredits;   // 失败保留旧值，不清零
            TraeCheckin.CheckinStatus? status = null;
            if (api != null && !string.IsNullOrEmpty(acc.Token))
            {
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                    if (st != null && st.code == 0) status = st;
                    var r = await api.GetRemainingCreditsAsync(acc.Token ?? "", acc.DeviceId);
                    if (r >= 0 && string.IsNullOrEmpty(api.LastError))
                    {
                        remaining = r;
                        if (cfg != null) { cfg.LastRemaining = r; try { cfg.Save(); } catch { /* 忽略 */ } }
                    }
                }
            }

            RemainingCredits = (int)remaining;
            if (status != null)
            {
                CheckinStatus = status.checked_in ? "今日已签到 ✓" : "今日可签到";
                TodayReward = "+" + (int)(status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                CheckinStatus = string.IsNullOrEmpty(acc.Token) ? "未登录" : "需重登";
            }

            // 记录今日总积分并重建该账号趋势曲线（账号切换后曲线随之同步）
            if (remaining >= 0)
            {
                AppendTotalToday(acc, remaining);
                BuildChartFromHistory(acc.Id);
            }
        }
        catch (Exception ex)
        {
            AccountHelpers.CheckinLog("?", $"[仪表盘刷新] 异常：{ex.Message}");
        }
    }

    /// <summary>重读当前激活账号并刷新展示（账号切换联动用）。</summary>
    public void Reload()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) return;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null) return;
            CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
            IsMember = acc.IsMember;
            Accounts.Clear();
            PopulateAccounts();
        }
        catch { /* 刷新失败保留原值 */ }
    }

    [RelayCommand]
    private void GoToCheckin()
    {
        RaiseNavigateRequested("checkin");
    }

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await LoadAsync();
    }

    [RelayCommand]
    private async Task QuickCheckin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null)
            {
                RaiseNavigateRequested("checkin");
                return;
            }

            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token))
            {
                RaiseNavigateRequested("checkin");
                return;
            }

            var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            AccountHelpers.CheckinLog(name, $"[仪表盘快签] 开始签到，DeviceId={acc.DeviceId}");
            var result = await api.ClaimAsync(acc.Token, acc.DeviceId);
            if (result != null && result.code == 0)
            {
                CheckinStatus = "已完成 ✓";
                // 非会员 150，会员 150 + 50 连签
                TodayReward = "+" + (int)(result.credits + (acc.IsMember ? result.extra_credits : 0));
                acc.LastCheckinDate = DateTime.Now;
                cfg.LastCheckinDate = DateTime.Now;
                // 解析本次所得并写入签到历史（与签到页/自动签到同口径）
                try
                {
                    var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
                    double gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
                    AccountHelpers.AppendHistory(acc, gained);
                    AccountHelpers.CheckinLog(name, $"[仪表盘快签] 签到成功，获得 {gained} 积分");
                }
                catch { /* 历史写入失败不影响 */ }
                // 刷新积分
                var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
                if (credits > 0 || (credits >= 0 && string.IsNullOrEmpty(api.LastError)))
                {
                    RemainingCredits = (int)credits;
                    cfg.LastRemaining = credits;
                }
                try { cfg.Save(); } catch { /* 忽略 */ }
            }
            else
            {
                AccountHelpers.CheckinLog(name, $"[仪表盘快签] Claim 失败：code={result?.code ?? -1}, message={result?.message ?? "null"}");
            }
        }
        catch
        {
            // 签到失败时跳转到签到页
            RaiseNavigateRequested("checkin");
        }
    }
}










