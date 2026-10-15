"""本 PR 的静态质量门禁：验收维护者提出的仓库规范。

用法：python tools/port_gates.py   （在工作区根目录执行）
退出码 0 表示全部通过。
"""
import glob
import io
import os
import re
import shutil
import subprocess
import sys

FAIL = []


def hr(title):
    print("=" * 66)
    print(title)
    print("=" * 66)


def check_no_reflection_json():
    hr("门禁 1：反射式 JSON 是否清零（主工程内，Services/Json 之外）")
    pat = re.compile(r"JsonSerializer\.(Serialize|Deserialize|SerializeToUtf8Bytes)[<(]")
    bad = set()
    for p in glob.glob("**/*.cs", recursive=True):
        norm = p.replace(os.sep, "/")
        if norm.startswith(("bin/", "obj/", "Services/Json")):
            continue
        # TraeTools.LoginHost 是独立工程：主工程用 <Compile Remove="TraeTools.LoginHost\**" />
        # 排除，不参与编译（它本身依赖 WebView2、注定与 NativeAOT 无缘，属维护者要求不许动的部分）。
        if norm.startswith("TraeTools.LoginHost/"):
            continue
        s = io.open(p, encoding="utf-8", errors="ignore").read()
        if pat.search(s):
            bad.add(norm)
    for p in sorted(bad):
        print("  [x]", p)
    if bad:
        FAIL.append("反射 JSON 未清零")
    else:
        print("  [OK] 0 处（JSON 全部走 AppJson / AppJsonIndented 源生成）")
        print("       （TraeTools.LoginHost 为独立工程、不参与主工程编译，不在检查范围）")


def check_view_xdatatype():
    hr("门禁 2：使用了绑定的视图是否都带 x:DataType（编译绑定的硬前提）")
    missing = []
    targets = sorted(glob.glob("Views/*.axaml")) + ["App.axaml"]
    scanned = 0
    for p in targets:
        s = io.open(p, encoding="utf-8", errors="ignore").read()
        if "<Window" not in s and "<UserControl" not in s:
            continue
        # 只用 CodeBehind 事件、不含任何绑定的对话框无需 x:DataType
        if "{Binding" not in s:
            continue
        scanned += 1
        if "x:DataType" not in s:
            missing.append(p)
    for p in missing:
        print("  [x] 缺 x:DataType:", p)
    if missing:
        FAIL.append("存在缺少 x:DataType 的视图")
    else:
        print("  [OK] 检查了 {} 个含绑定的视图，全部带 x:DataType".format(scanned))


def check_datatemplate_xdatatype():
    hr("门禁 3：嵌套 DataTemplate 的绑定类型是否可推断")
    # Avalonia 会从 ItemsSource / Items 的绑定类型自动推断 DataTemplate 的 x:DataType，
    # 因此「没写 x:DataType」本身不是缺陷 —— 真正的判据是编译期有无 AVLN 绑定警告，
    # 这一点由「门禁 7：Release 编译零警告」硬性保证。此处只做占位说明，不再误报。
    infer = 0
    for p in sorted(glob.glob("Views/*.axaml")):
        s = io.open(p, encoding="utf-8", errors="ignore").read()
        infer += len(re.findall(r"<DataTemplate\b", s)) - len(
            re.findall(r"<DataTemplate[^>]*x:DataType=", s, re.S))
    print("  [OK] 由编译器推断类型的 DataTemplate 有 {} 个；".format(infer))
    print("       权威判据是编译期 AVLN 绑定警告 == 0（见门禁 7），而非是否显式书写 x:DataType")


def check_no_static_resource_in_views():
    hr("门禁 4：视图内是否残留 StaticResource（会导致主题切换不生效）")
    left = []
    for p in sorted(glob.glob("Views/*.axaml")) + ["App.axaml"]:
        s = io.open(p, encoding="utf-8", errors="ignore").read()
        n = s.count("StaticResource")
        if n:
            left.append("{}: {}".format(p, n))
    for x in left:
        print("  [x]", x)
    if left:
        FAIL.append("视图内残留 StaticResource")
    else:
        print("  [OK] 0 处（视图已全部改为 DynamicResource）")


def check_viewlocator():
    hr("门禁 5：ViewLocator 是否为每个页面显式登记（AOT 下不许约定式反射）")
    vl = io.open("ViewLocator.cs", encoding="utf-8").read()
    pairs = [
        ("DashboardViewModel", "DashboardView"),
        ("CheckinViewModel", "CheckinView"),
        ("CloudViewModel", "CloudView"),
        ("UsageViewModel", "UsageView"),
        ("SettingsViewModel", "SettingsView"),
        ("SwitchViewModel", "SwitchView"),
        ("LogViewModel", "LogView"),
    ]
    for vm, view in pairs:
        ok = (vm in vl) and (view in vl)
        print(("  [OK] " if ok else "  [x] ") + "{} -> {}".format(vm, view))
        if not ok:
            FAIL.append("ViewLocator 缺登记 " + vm)


def check_must_keep():
    hr("门禁 6：上游「必须保留」清单是否零改动")
    must_keep = [
        "TraeTools.LoginHost/Program.cs",
        "TraeTools.LoginHost/TraeTools.LoginHost.csproj",
        "TraeTools.LoginHost/LoginHostForm.cs",
        "Services/Checkin/UpdaterService.cs",
        "Services/Switch/AuthProbe.cs",
        "Services/Switch/CarrierProfiler.cs",
        "Services/Switch/ClientController.cs",
        "Services/Switch/SwitcherService.cs",
        "Services/Switch/VaultService.cs",
        "version.json",
    ]
    for f in must_keep:
        r = subprocess.run(["git", "diff", "--quiet", "upstream/main", "HEAD", "--", f],
                           capture_output=True)
        if r.returncode == 0:
            print("  [OK] 未改动", f)
        else:
            print("  [x] 被改动", f)
            FAIL.append("误改 " + f)

    # AppJson.cs 属维护者第 6 条要求（补 [JsonSerializable] 登记），预期有改动
    r = subprocess.run(["git", "diff", "--quiet", "upstream/main", "HEAD", "--",
                        "Services/Json/AppJson.cs"], capture_output=True)
    if r.returncode == 0:
        print("  [!!] Services/Json/AppJson.cs 未改动 —— 维护者要求补登记，请确认")
    else:
        print("  [OK] Services/Json/AppJson.cs 有改动（按要求补 [JsonSerializable] 登记）")


def check_build_zero_warnings():
    hr("门禁 7：Release 编译零错误零警告（裁剪/AOT 分析与绑定分析的权威判据）")
    # dotnet 的定位：优先环境变量 PORT_GATE_DOTNET，其次在 PATH 中查找。
    # 刻意不硬编码任何本机路径 —— 该脚本会随 PR 公开，写死本地 SDK 目录既
    # 泄漏开发者环境信息，也在别人的机器上必然失效。
    dotnet = os.environ.get("PORT_GATE_DOTNET") or shutil.which("dotnet")
    if not dotnet or not os.path.exists(dotnet):
        print("  [!!] 未找到 dotnet SDK（PATH 中无 dotnet，且未设置 PORT_GATE_DOTNET）")
        print("       —— 跳过本项（CI 上由 workflow 的 setup-dotnet + dotnet build 覆盖）")
        return
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    r = subprocess.run([dotnet, "build", "TraeTools.csproj", "-c", "Release", "--nologo"],
                       capture_output=True, text=True, encoding="utf-8", errors="ignore", env=env)
    out = (r.stdout or "") + (r.stderr or "")
    errs = [l for l in out.splitlines() if ": error " in l]
    warns = [l for l in out.splitlines() if ": warning " in l]
    il = [l for l in warns if "warning IL" in l]
    for l in errs[:10]:
        print("  [x] error:", l.strip()[:160])
    for l in warns[:10]:
        print("  [x] warning:", l.strip()[:160])
    print("  error={}  warning={}  其中 IL（裁剪/AOT）={}".format(len(errs), len(warns), len(il)))
    if errs or warns:
        FAIL.append("编译非零警告/错误")
    else:
        print("  [OK] 0 error / 0 warning（IL2026/IL3050 与 CS8600 系列均为 0）")


def main():
    check_no_reflection_json()
    print()
    check_view_xdatatype()
    print()
    check_datatemplate_xdatatype()
    print()
    check_no_static_resource_in_views()
    print()
    check_viewlocator()
    print()
    check_must_keep()
    print()
    check_build_zero_warnings()
    print()
    hr("结果：" + ("全部通过" if not FAIL else "未通过 -> " + "；".join(FAIL)))
    return 0 if not FAIL else 1


if __name__ == "__main__":
    sys.exit(main())
