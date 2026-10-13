using System.Text.Json;
using TraeTools.Services.Json;

namespace TraeSwitch.Services;

public sealed class AppSettingsData
{
    public List<string> Accounts { get; set; } = [];
    public string RootDir { get; set; } = "";
    public string ClientExe { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public List<string> Fingerprint { get; set; } = [];
    /// <summary>是否已同意首次启动的用户协议（EULA）。同意后不再弹出。</summary>
    public bool EulaAccepted { get; set; }
}

/// <summary>
/// settings.json 读写；目录不存在自动创建。文件不存在时用默认值。
/// 读取时执行一次"配置自愈"（P0-1 / P0-3）：仍指向旧版 TRAE SOLO CN 的路径与进程名、
/// 已失效的 exe 路径、缺 state.vscdb 的固化旧指纹，都会被自动重探并落盘——
/// 老用户升级后无需手动删除 settings.json，改了白名单也立即生效。
/// </summary>
public sealed class SettingsStore
{
    public AppSettingsData Data { get; private set; }
    public string FilePath { get; }

    /// <summary>本次加载自动修正的配置摘要（无修正为 null）；供 UI 记录日志。</summary>
    public string? AutoFixNote { get; private set; }

    public SettingsStore(string dir)
    {
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "settings.json");

        var load = LoadData();
        Data = load.Data;
        AutoFixNote = load.Note;
        if (load.Changed)
        {
            try { Save(); } catch { /* 落盘失败：内存里已是正确值，下次再写 */ }
        }
    }

    /// <summary>取出并清空自愈提示（只提示一次）。</summary>
    public string? ConsumeAutoFixNote()
    {
        var note = AutoFixNote;
        AutoFixNote = null;
        return note;
    }

    /// <summary>读配置；文件不存在、损坏或解析失败统一回退到默认值，避免启动即崩溃。</summary>
    private (AppSettingsData Data, string? Note, bool Changed) LoadData()
    {
        bool hadFile = File.Exists(FilePath);
        AppSettingsData? parsed = null;
        if (hadFile)
        {
            try
            {
                parsed = AppJsonIndented.Deserialize<AppSettingsData>(File.ReadAllText(FilePath));
            }
            catch
            {
                // 坏文件：回退默认值但不抛；下面 Normalize 后会用正常值重写
            }
        }

        var data = parsed ?? new AppSettingsData();
        bool changed = Normalize(data, out var note);
        // 全新安装（原本没有文件）不算"修正历史问题"，不提示
        return (data, hadFile ? note : null, changed);
    }

    /// <summary>
    /// 配置自愈：把指向旧客户端的路径/进程名、失效 exe、过期指纹改回当前 TRAE（Trae CN）默认值。
    /// 返回是否发生修改；<paramref name="note"/> 记录改了哪些项（用于 UI 日志）。
    /// </summary>
    private static bool Normalize(AppSettingsData d, out string? note)
    {
        note = null;
        var fixedItems = new List<string>();

        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // 数据根：空 / 指向旧客户端 / 目录已不存在（旧目录迁移后只剩空壳）→ 重探
        if (string.IsNullOrWhiteSpace(d.RootDir)
            || CarrierDefaults.IsLegacyClientReference(d.RootDir)
            || (!Directory.Exists(d.RootDir) && Directory.Exists(CarrierDefaults.DefaultUserDataDir)))
        {
            if (!Same(d.RootDir, CarrierDefaults.DefaultUserDataDir))
            {
                d.RootDir = CarrierDefaults.DefaultUserDataDir;
                fixedItems.Add("数据根目录");
            }
        }

        // 进程名：空 / 旧名（"TRAE SOLO CN" 已无进程）→ 重探
        if (string.IsNullOrWhiteSpace(d.ProcessName) || CarrierDefaults.IsLegacyClientReference(d.ProcessName))
        {
            if (!Same(d.ProcessName, CarrierDefaults.DefaultProcessName))
            {
                d.ProcessName = CarrierDefaults.DefaultProcessName;
                fixedItems.Add("进程名");
            }
        }

        // exe 路径：空 / 旧路径 / 文件已不存在 → 重探（与运行时 ResolveClientExe 的回退策略一致）
        if (string.IsNullOrWhiteSpace(d.ClientExe)
            || CarrierDefaults.IsLegacyClientReference(d.ClientExe)
            || !File.Exists(d.ClientExe))
        {
            var detected = CarrierDefaults.DefaultClientExe;
            if (!Same(d.ClientExe, detected))
            {
                d.ClientExe = detected;
                fixedItems.Add("客户端路径");
            }
        }

        // 指纹：固化下来的旧窄清单（缺 state.vscdb / 边车）→ 重置为固定 15 项白名单
        if (CarrierDefaults.IsStaleFingerprint(d.Fingerprint))
        {
            d.Fingerprint = CarrierDefaults.DetectFingerprint();
            fixedItems.Add($"载体指纹（重置为 {d.Fingerprint.Count} 项）");
        }

        if (fixedItems.Count == 0) return false;
        note = "已自动修正过期配置：" + string.Join("、", fixedItems);
        return true;
    }

    public void Save()
        => File.WriteAllText(FilePath, AppJsonIndented.Serialize(Data));
}
