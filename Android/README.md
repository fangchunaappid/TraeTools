# TraeTools for Android

给 TraeTools 加上 **Android 端**。本目录是一个独立的 `net10.0-android` 工程，
Windows 版的构建、发布流程与产物**不受影响**。

根目录只有 `TraeTools.csproj` 增加了 3 行 `Remove`（把本目录排除出主程序的默认通配符），
原因见下方「与根工程的关系」。除此之外，仓库其余文件一行未动。

## 包含内容

| 路径 | 说明 |
|---|---|
| `TraeTools.Android.csproj` | 独立的 `net10.0-android` 工程。共享代码（`Models/` `Services/` `ViewModels/` `Views/` 的六个页面、`Styles/` `Assets/`）以链接方式复用，单一真源仍在仓库根目录 |
| `App.axaml(.cs)` | Android 应用入口（单视图生命周期） |
| `Platform/` | `AndroidVault`（Keystore + AES-256-GCM）、`DpapiCompat`（DPAPI 兼容层）、`BrowserLauncher`、`CrashLog`、`CrashReportActivity`、`MainActivity`、`PlatformLog` |
| `Views/` | `MainView`（单视图外壳：顶栏 + 横向标签导航 + 覆盖层对话框宿主）、`OverlayDialog`、5 个对话框（`PromptWindow` / `InputWindow` / `PatInputWindow` / `LoginWindow` / `EulaWindow`） |
| `Services/` | `DataPaths`、`AutoStartManager` 的 Android 实现 |
| `ViewModels/UiHost.cs` | 把宿主解析结果放宽为 `TopLevel`，使上游 ViewModel 无需改动即可正常弹窗与访问剪贴板 |
| `Properties/` `Resources/` | 应用清单（INTERNET + 浏览器查询意图）、自适应图标、主题与配色 |

## 编译

需要 .NET 10 SDK，并安装 Android 工作负载：

```bash
dotnet workload install android
dotnet build Android/TraeTools.Android.csproj -c Release
```

产物：`Android/bin/Release/net10.0-android/*.apk`（最低 Android 8.0 / API 26）。
包名 `com.traetools.android`，`versionName` 取自根目录 `version.json`。
Release 默认使用调试密钥签名，正式分发请自行配置 keystore。

CI 见仓库根的 `.github/workflows/android.yml`（编译并上传 APK）。

## 与根工程的关系

本工程位于 Windows 主程序的工程目录之内，而 MSBuild 与 Avalonia 的默认通配符会向下递归，
且并不认「子目录里有自己的 csproj」：

| 来源 | 默认通配符 |
|---|---|
| .NET SDK | `Compile Include="**/*.cs"`、`None Include="**/*"` |
| Avalonia | `AvaloniaXaml Include="**\*.axaml"`（另有 `**\*.paml`） |

若不排除，主程序会把 Android 源码一并编译，实测报
`AVLN2002: Duplicate x:Class directive, TraeTools.App is already used in /Android/App.axaml`，
并且 `Android/Platform/*.cs` 依赖的 `Android.App` / `Avalonia.Android` 在主程序里无法解析。

因此 `TraeTools.csproj` 中增加了：

```xml
<ItemGroup>
  <Compile Remove="Android\**" />
  <None Remove="Android\**" />
  <AvaloniaXaml Remove="Android\**" />
</ItemGroup>
```

写法与仓库里既有的 `<Compile Remove="TraeTools.LoginHost\**" />` 一致。

## 平台差异

| 能力 | Windows | Android |
|---|---|---|
| 签到 / 自动签到 / 云端签到 / 用量统计 / 仪表盘 | ✅ | ✅ |
| 账号切换（vault 建档 / 冷切换） | ✅ 直接操作本地客户端 | ⚠️ 手机端无本地 Trae 客户端，页面仅作展示 |
| 登录方式 | ✅ 内嵌浏览器（WebView2 宿主）自动取凭证 | ✅ 手动粘贴 Cloud-IDE-Token |
| 开机自启 | ✅ 注册表 `Run` | ❌ 无等价机制，已降级为无操作 |
| 凭证加密 | DPAPI（当前用户） | Android Keystore（RSA-2048 包裹 AES-256-GCM 数据密钥） |
| 系统托盘 / 通知栏 | ✅ 托盘图标 + 原生菜单 | ❌ 无对应概念（自动签到在进程存活期间执行） |

## 已知限制

- **应用内自动更新不可用**：上游 `SettingsViewModel` 的更新流程会下载 zip 并替换 exe，
  手机上无法执行。建议直接到 Releases 下载新 APK 覆盖安装。
- **部分「打开网页」入口点了没反应**：上游 `CloudViewModel` / `SettingsViewModel` 里
  打开浏览器走的是 `Process.Start(...)`，该调用在 Android 上不可用（失败已被 try/catch 捕获，
  日志里会留痕）。设备码授权流程仍可用 —— 授权码会自动复制到剪贴板，
  按日志提示手动访问授权页即可。要修好这两处需要给上游 VM 加一个平台跳转抽象（约 7 处调用点）。
- 仅编译过 `net10.0-android`；未在真机长时间运行验证。
