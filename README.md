# TraeTools

> Trae 一体化管理工具：多账号每日签到 · 云端自动签到 · 账号快速切换 · 用量统计

TraeTools 是对 **TRAE-Checkin** 的重构版，并集成了 **Trea-Switch** 的账号切换能力，用 Avalonia 重写为现代化桌面应用（.NET 9）。一个工具覆盖 Trae 账号的「登录 → 签到 → 切换 → 用量查看 → 云端托管」全流程。

---

## 🚀 Fork 增强说明（2026-11-04）

本 fork 在上游基础上对 **云端签到体系** 做了一轮大升级，核心变化：**凭证永不手动续命**。

### 1. Token 链式续期（借鉴 [L0NE-6/Trae-AutoCheckin](https://github.com/L0NE-6/Trae-AutoCheckin)，MIT）

上游模式：`X-Cloudide-Session` Cookie 换 JWT，**~14 天必死**，每次都要重新登录 + 重新部署。
本 fork 模式：

```
refreshToken ──ExchangeToken──> 新 accessToken + 轮换出新 refreshToken
                                      │
                                      └─> 立即回写加密缓存（轮换链断不得）
                                           → 凭证长期自循环，永不手动续命
```

- 新版 ExchangeToken 要求 **ECDSA P-256 设备签名**（纯标准库实现，零依赖）
- 链式续期失败时**自动回退** Session Cookie 模式，签到永不中断
- 多账号各自独立成链，互不干扰（与本机 Trae 客户端的登录态也互不影响）

### 2. 加密 state 缓存

refreshToken 是命脉，Actions 下通过 `actions/cache` + `openssl AES-256-CBC`（密钥 `TRAE_CACHE_KEY`）跨运行持久化，公开仓库不裸奔。

### 3. 触发链路：准点 + 兜底

```
北京 00:05:14  外部 cron-job（trae-checkin-dispatch）→ workflow_dispatch   ← 主力，准点
GitHub schedule（cron 16:05，实际常延迟 5~7 小时）                        ← 兜底
        ↓ 串行锁排队；后来者检测「今日已签到」直接跳过，不重复推送
```

### 4. 飞书推送增强

- 显示账号昵称：`✅ 账号 1·trae188（188******18）：签到成功，获得 100 积分，余额 5288`
- **积分余额告警**：低于 `TRAE_CREDIT_ALERT`（默认 500）时追加 `⚠️` 告警行
- 已退役 `TRAE_SESSION_EXPIRE`（链式续期下不再需要手动记到期日）

### 5. Secrets 清单

| Secret | 说明 |
|---|---|
| `TRAE_REFRESH_TOKEN` / `_2` | 各账号 refreshToken（链式续期主凭证） |
| `TRAE_CLIENT_DEVICE_KEY_PEM` / `_PUB_PEM` / `_DEVICE_ID` / `_MACHINE_ID` | 设备证明材料（全部账号共用，导出一次即可） |
| `TRAE_CLIENT_DEVICE_INFO` | 客户端签发 token 时的完整 DeviceInfo JSON（设备指纹，建议同源导出） |
| `TRAE_CACHE_KEY` | 加密缓存的密钥（自选随机长字符串） |
| `TRAE_SESSION` / `_2` | Session Cookie（回退兜底用，可留可删） |
| `TRAE_DEVICE_ID` / `_2` | 签到风控设备号（16 位数字，与上面设备证明是两回事） |
| `FEISHU_WEBHOOK` | 飞书机器人 |
| `TRAE_CREDIT_ALERT` | 积分告警阈值（选填，默认 500） |

初始 refreshToken / 设备密钥的导出步骤见 **[tools/README.md](tools/README.md)**。

### 6. 更新日志（2026-11-04）

- `checkin.py`：移植链式续期（轮换链 + ECDSA 设备证明 + 缓存回写 + Session 自动引导/回退）；修复私钥标量 DER 解析（补跳 AlgorithmIdentifier）；推送带账号昵称；新增积分告警；移除到期提醒
- `checkin.yml`：触发时间 08:00 → 00:05；加密 state 缓存四步（恢复/解密/加密/保存）；新 secrets 透传
- `tools/`：引入 trae_get_token.py、trae_sms_login.py（来源 L0NE-6/Trae-AutoCheckin，MIT）+ 使用文档

### 气死我了，怎么老是给我爆9074

## 功能特性

- **多账号每日签到**：一页管理多个 Trae 账号，一键签到全部启用账号；自动签到可设定时间，到点自动执行
- **云端签到（永不掉线）**：一键把签到任务部署到你自己的 GitHub fork（GitHub Actions 每天 08:00 运行），无需电脑开机
- **账号切换**：为每个账号备份登录态（vault），一键冷切换客户端账号，失败自动回滚
- **用量统计**：按会话展示模型 / 输入输出 Token / 缓存命中率 / 积分消耗，并本地持久化；另含「积分快过期提醒」
- **账号资料**：展示每个账号的昵称 / 脱敏手机号 / 头像 / 学生认证徽章
- **飞书推送**：签到结果可实时推送到飞书机器人
- **自动更新**：内置检查更新与静默替换安装

## 界面

| 导航 | 说明 |
|---|---|
| 仪表盘 | 剩余积分、今日签到状态、积分趋势图、账号概览 |
| 用量统计 | 逐会话用量 + 模型分布 + 汇聚统计，全部本地留档 |
| 每日签到 | 本月日历、连签天数、签到记录、一键/自动签到 |
| 账号切换 | 建档（备份登录态）、一键切换、校验 |
| 云端签到 | GitHub 授权、一键部署、部署状态与日志 |
| 设置 | 自动签到配置、Token 信息、飞书推送、账号管理、关于 |

## 快速开始

> **macOS 用户看这里**：macOS 原生版 TraeBar（菜单栏常驻多账号自动签到，Swift/AppKit 实现）在本仓库的 [`macos`](https://github.com/star620/TraeTools/tree/macos) 分支独立维护与发布，版本 tag 为 `macos-v*`，发布产物见 [Releases](https://github.com/star620/TraeTools/releases)。本分支（main）仅维护 Windows 版。

### 方式一：直接使用
在 [Releases](https://github.com/star620/TraeTools/releases) 下载最新版运行（Windows，需 .NET 9 桌面运行时）。

### 方式二：源码编译

需要 **.NET 9 SDK**：

```bash
git clone https://github.com/star620/TraeTools.git
cd TraeTools
dotnet build -c Release
```

产物位于 `bin/Release/net9.0-windows/TraeTools.exe`。

## 使用指南

### 1. 添加 / 登录账号
`设置 → 账号管理 → + 添加`：自动打开登录窗口（内嵌浏览器），手机号+验证码登录后自动读取凭证并保存；登录态约 14 天，失效可一键静默续期。

### 2. 每日签到
签到页点「一键签到」或开启「每日自动签到」；也可在仪表盘点「立即签到」。签到结果写入本地历史，日历与连签天数随之更新。

### 3. 用量统计
进入「用量统计」页面自动同步近 7 天会话；每次会话展示模型、入/出 Token、缓存命中字节与积分消耗，数据按账号本地保留，可追溯历史趋势。

### 4. 云端签到（部署到 GitHub Actions）
1. `云端签到` 页点击「授权 GitHub」（设备码授权，无需密码，仅需 repo 权限）
2. 点击「一键部署到云端」：程序自动 fork 本仓库 → 写入每个账号的 `TRAE_SESSION` / `TRAE_DEVICE_ID` secret → 启用并触发验证运行
3. 验证成功即生效：**每天北京时间 08:00** 自动签到，即使本机关机也不中断

部署原理：fork 仓库中的 `.github/workflows/checkin.yml` 定时运行 `checkin.py`，脚本用 `X-Cloudide-Session` 换取新 JWT 后调用签到接口（仅依赖 Python 标准库）。

> 从旧版 TRAE-Checkin 迁移：已部署过旧 fork 的用户，在新版「重新部署」时会提示自动删除旧 fork 并重建，无需手动去网页操作。旧 fork 仓库可自行删除。

### 5. 账号切换
`账号切换` 页：为当前登录的客户端「建档」（备份登录态载体）→ 选择目标账号 → 「切换至此」一键冷切换；切换失败会自动回滚到原账号并重启客户端。账号删空后可直接「建档」输入新名称重新建立。

## 常见问题

| 现象 | 说明 / 处理 |
|---|---|
| 签到提示「当前参与用户太多」 | Trae 风控：同设备/网络频繁请求触发。建议错开 08:00 高峰、减少账号数、不同账号用独立设备号 |
| 云端部署 HTTP 401 | GitHub 授权失效或权限不足：点「重新授权」；仍失败则自动删除旧 fork 重建 |
| 云端提示「未找到 workflow」 | 旧 fork 早于本仓库添加 checkin.yml：按提示一键「删除并重建 fork」即可自愈 |
| 签到 0 积分 | 当日已签过（重复签到不加分），或非会员当日基础 150 分已到账 |
| Token 失效 | 程序会用 `X-Cloudide-Session` 静默换新；彻底失效时提示重新登录 |

> 上游模式凭 `TRAE_SESSION`（~14 天有效）需定期重新部署；**本 fork 已升级为 refreshToken 链式续期（见顶部说明），长期免维护**。

## 开发者

- 技术栈：Avalonia 12 / .NET 9 / CommunityToolkit.Mvvm / Sodium.Core / WebView2
- 目录：`ViewModels/` 页面逻辑、`Services/` 网络与领域服务、`Views/` 界面、`Models/` 数据模型
- 行为默认不对外请求官方非公开接口之外的能力；用量/积分接口为逆向网页接口，字段随官方可能变化，代码均做容错解析

## 免责声明

- 本项目为个人开源工具，与 ByteDance / Trae 官方无任何关系
- 使用自动签到、账号切换等功能请遵守 Trae 服务条款，风险自负
- 请勿出售、外借或用于非法用途；账号凭证仅保存在你自己的电脑/GitHub secret 中

## 致谢

- [TRAE-Checkin](https://github.com/star620/TRAE-Checkin)：本项目的前身，云端签到体系（checkin.py + GitHub Actions 流程）源自该仓库
- [Trea-Switch](https://github.com/star620/Trea-Switch)：账号切换（vault 建档 / 冷切换 / 守护回滚）能力源自该仓库
- [L0NE-6/Trae-AutoCheckin](https://github.com/L0NE-6/Trae-AutoCheckin)：Token 链式续期机制、设备证明 ECDSA 实现与引导工具（trae_get_token / trae_sms_login）参考自该仓库（MIT）

## 许可证

见 [LICENSE](LICENSE)。
