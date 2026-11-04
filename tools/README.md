# tools/ — 凭证引导工具

来源：[L0NE-6/Trae-AutoCheckin](https://github.com/L0NE-6/Trae-AutoCheckin)（MIT License），版权归原作所有。

用途：为 `checkin.py` 的 Token 链式续期模式提供初始 `refreshToken` 与设备证明材料。
拿到后填入仓库 Secrets，签到凭证即可长期自动续期，不再受 Session ~14 天过期限制。

## 使用步骤（本机执行，需装有 Trae 桌面客户端并登录过）

### 1. 导出 refreshToken（每个账号各跑一次）

```bash
python trae_get_token.py
# 多账号：
python trae_get_token.py --accounts
```

把输出的值填入 Secrets：
- 账号 1 → `TRAE_REFRESH_TOKEN`
- 账号 2 → `TRAE_REFRESH_TOKEN_2`

### 2. 导出设备证明材料（新版 ExchangeToken 必需，全部账号共用）

```bash
python trae_get_token.py --export-keys
```

填入 Secrets（注意：与签到风控设备号 `TRAE_DEVICE_ID` 是两回事）：
- `TRAE_CLIENT_DEVICE_KEY_PEM`
- `TRAE_CLIENT_DEVICE_PUB_PEM`
- `TRAE_CLIENT_DEVICE_ID`
- `TRAE_CLIENT_MACHINE_ID`

### 3. 配置缓存加密密钥（防 refreshToken 泄露到 Actions 缓存）

自选一个随机长字符串填入 Secrets：`TRAE_CACHE_KEY`

### 4. 验证

手动触发 `Trae Daily Checkin`，日志出现
`[账号 N] 链式续期成功（refreshToken 已轮换回写缓存）` 即成功。

## 也可选：网页 OAuth 登录取凭据

```bash
python trae_sms_login.py
```

浏览器完成登录后自动获得 refreshToken（要求本机 Trae 客户端登录过，
因为它需要客户端设备身份里的公钥；回调端口固定 17388）。
