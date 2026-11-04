#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Trae 每日签到脚本（GitHub Actions 版 · 增强推送 v2）
v2 修复：
  1. 「获得积分」此前取自 claim 响应（该响应不含积分，恒为 0）；
     现改为对齐 TraeTools 桌面端逻辑：签到后查询 status 接口解析本次所得
     （credits = 基础签到所得；extra_credits 为连签加成、仅会员到账）。
  2. 「余额」此前在签到后立即查询，资格包接口存在延迟可能返回旧值；
     现改为签到前记录 pre 余额，签到后重试查询直到余额 >= pre + 本次所得。
原理：
  Trae 网页端的 JWT 只有 8 小时有效期，但真正的会话凭证是 HttpOnly Cookie
  `X-Cloudide-Session`（约 14 天有效）。本脚本通过该 Cookie 调用
  `GetUserToken` 接口换取全新 JWT，再用新 JWT 执行每日签到。
依赖：仅标准库，无第三方依赖。
环境变量：
  TRAE_SESSION        账号 1 的 X-Cloudide-Session Cookie（必填；后端兼容单账号部署）
  TRAE_DEVICE_ID      账号 1 的 x-device-id，16 位数字（选填，缺省随机）
  TRAE_SESSION_N      第 N(N≥2) 个账号的会话 Cookie；缺失即停止读取更多账号
  TRAE_DEVICE_ID_N    第 N 个账号的 x-device-id（选填，缺省随机）
  全部账号共享：       FEISHU_WEBHOOK（选填，签到后推送一条汇总）
用法：
  python checkin.py
"""
import base64
import datetime
import hashlib
import json
import os
import platform
import random
import sys
import time
import urllib.request

BASE = "https://api.trae.cn"


def _post(path, headers, body=""):
    """POST 请求。非 2xx 不抛 HTTPError，而是以 (status, body) 正常返回，便于上层判断原因。
    path 支持绝对 URL（OAuth 兑换端点在 api.trae.com.cn 域）。"""
    import urllib.error
    url = path if path.startswith("http") else BASE + path
    req = urllib.request.Request(url, data=body.encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")


def _auth_headers(token, device_id):
    """签到/查询类接口的统一请求头。"""
    return {
        "Authorization": "Cloud-IDE-JWT " + token,
        "X-User-Region": "cn",
        "x-device-id": device_id,
        "Content-Type": "application/json",
        "User-Agent": "TraeCheckin/1.0",
    }


def get_token_full(session: str):
    """用 X-Cloudide-Session Cookie 换取全新 JWT，返回 (token, Result 字典)。
    Result 里若带 RefreshToken，调用方可借此引导进入链式续期模式。"""
    headers = {
        "Cookie": "X-Cloudide-Session=" + session,
        "Referer": "https://www.trae.cn/",
        "Origin": "https://www.trae.cn",
        "User-Agent": "TraeCheckin/1.0",
        "Accept": "application/json, text/plain, */*",
    }
    status, text = _post("/cloudide/api/v3/common/GetUserToken", headers)
    data = json.loads(text)
    result = data.get("Result") or {}
    token = result.get("Token")
    if status == 401:
        raise RuntimeError(
            "账号会话已失效(HTTP 401)：X-Cloudide-Session 可能已过期(~14天有效期)。"
            "请在浏览器重新登录 trae.cn 并复制新的 Cookie 值，更新到 TRAE_SESSION 后重试。"
            "原始返回: " + text[:200])
    if status != 200 or not token:
        raise RuntimeError("GetUserToken 失败: HTTP %s %s" % (status, text[:200]))
    return token, result


def get_token(session: str) -> str:
    """用 X-Cloudide-Session Cookie 换取全新 JWT（只返回 token 的简化封装）。"""
    return get_token_full(session)[0]


def get_user_info(token: str, session: str):
    """拉取账号资料（/cloudide/api/v3/trae/GetUserInfo）：UID/昵称/脱敏手机号。
    与桌面端 TraeApiClient.GetUserProfileAsync 同款，逆向自 www.trae.cn/dashboard。
    成功返回「昵称（手机号）」标签；失败返回 None（调用方降级为「账号 N」）。
    session 可为空（纯 refreshToken 模式下没有 Cookie，此时省略 Cookie 头）。"""
    headers = {
        "Authorization": "Cloud-IDE-JWT " + token,
        "Referer": "https://www.trae.cn/",
        "Origin": "https://www.trae.cn",
        "User-Agent": "TraeCheckin/1.0",
        "Accept": "application/json, text/plain, */*",
    }
    if session:
        headers["Cookie"] = "X-Cloudide-Session=" + session
    status, text = _post("/cloudide/api/v3/trae/GetUserInfo", headers)
    if status != 200:
        print("[资料] GetUserInfo HTTP %s，降级为编号显示" % status)
        return None
    try:
        result = (json.loads(text).get("Result") or {})
    except json.JSONDecodeError:
        print("[资料] GetUserInfo 响应解析失败，降级为编号显示")
        return None
    screen_name = (result.get("ScreenName") or "").strip()
    user_id = (result.get("UserID") or "").strip()
    mobile = (result.get("NonPlainTextMobile") or "").strip()
    label = screen_name or user_id
    if not label:
        return None
    if mobile:
        label += "（%s）" % mobile
    return label

def checkin(token: str, device_id: str) -> dict:
    """执行每日签到（claim）。注意：claim 响应不含本次所得积分。"""
    status, text = _post("/trae/api/v2/ug/checkin_credits/claim", _auth_headers(token, device_id), "{}")
    try:
        return {"http": status, "body": json.loads(text)}
    except json.JSONDecodeError:
        return {"http": status, "body": {"raw": text}}


def checkin_status(token: str, device_id: str) -> dict:
    """查询今日签到状态：checked_in / credits（基础所得）/ extra_credits（连签加成）。"""
    status, text = _post("/trae/api/v2/ug/checkin_credits/status", _auth_headers(token, device_id), "{}")
    try:
        return {"http": status, "body": json.loads(text)}
    except json.JSONDecodeError:
        return {"http": status, "body": {}}


def resolve_gained(status_body: dict, is_member: bool = False) -> float:
    """对齐 TraeTools 桌面端 CheckinEvaluator.ResolveGainedCredits：
    仅当 code=0 且 checked_in=True 时数值可信；非会员只计 credits，会员额外计入 extra_credits。"""
    try:
        code = status_body.get("code", -1)
        checked = bool(status_body.get("checked_in", False))
        if code != 0 or not checked:
            return 0.0
        credits = _as_num(status_body.get("credits", 0))
        extra = _as_num(status_body.get("extra_credits", 0))
        return credits + (extra if is_member else 0.0)
    except Exception:
        return 0.0


def _as_num(v):
    """接口字段可能为数字或字符串，统一转 float；解析失败按 0。"""
    if isinstance(v, bool):
        return 0.0
    if isinstance(v, (int, float)):
        return float(v)
    if isinstance(v, str):
        try:
            return float(v.strip())
        except ValueError:
            return 0.0
    return 0.0


def get_remaining_credits(token: str, device_id: str) -> float:
    """查询剩余积分（汇总所有资格包的剩余额度）。解析失败返回 -1。"""
    status, text = _post("/trae/api/v2/pay/user_current_entitlement_list",
                         _auth_headers(token, device_id), "{}")
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return -1.0
    packs = data.get("user_entitlement_pack_list")
    if not isinstance(packs, list) or not packs:
        return -1.0
    total = 0.0
    for p in packs:
        info = p.get("entitlement_base_info") or {}
        quota = info.get("quota") or {}
        usage = p.get("usage") or {}
        limit = _as_num(quota.get("credits_limit"))
        used = _as_num(usage.get("credits_amount"))
        rem = limit - used
        if rem < 0:
            rem = 0.0
        total += rem
    return total


def _fmt_num(x) -> str:
    """积分展示：整数不带小数点，小数最多保留 2 位；-1 视为查询失败。"""
    if x is None or x < 0:
        return "未知"
    if abs(x - round(x)) < 1e-9:
        return str(int(round(x)))
    return ("%.2f" % x).rstrip("0").rstrip(".")


def notify_feishu(webhook, text):
    """向飞书机器人推送一条文本消息；webhook 为空则跳过。返回 HTTP 状态码，失败返回 None。"""
    if not webhook:
        return None
    try:
        payload = json.dumps({"msg_type": "text", "content": {"text": text}}).encode("utf-8")
        req = urllib.request.Request(webhook, data=payload, headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status
    except Exception:
        return None


def beijing_now_str():
    """返回北京时间字符串（GitHub Actions 运行在 UTC，需 +8 小时）。"""
    return (datetime.datetime.utcnow() + datetime.timedelta(hours=8)).strftime("%Y-%m-%d %H:%M:%S")


def beijing_today():
    """返回北京时间的今天（date 对象）。"""
    return (datetime.datetime.utcnow() + datetime.timedelta(hours=8)).date()


def credit_alert_threshold():
    """积分告警阈值：环境变量 TRAE_CREDIT_ALERT（默认 500）。解析失败回退默认值。"""
    raw = os.environ.get("TRAE_CREDIT_ALERT", "").strip()
    try:
        return float(raw) if raw else 500.0
    except ValueError:
        print("[告警] TRAE_CREDIT_ALERT 格式应为数字，当前值无效: %r，回退 500" % raw)
        return 500.0

def credit_low_line(name, balance):
    """余额低于阈值时返回一条告警行，否则返回 None。balance=-1（查询失败）不告警。"""
    if balance is None or balance < 0:
        return None
    th = credit_alert_threshold()
    if balance < th:
        return "⚠️ %s：积分余额仅剩 %s（低于告警阈值 %s），请注意补充" % (name, _fmt_num(balance), _fmt_num(th))
    return None


# ══════════════════ Token 链式续期 ══════════════════
# 机制借鉴 L0NE-6/Trae-AutoCheckin（MIT License）：
#   用 refreshToken 调 OAuth ExchangeToken 换新 accessToken 时，服务端会
#   同时轮换出新 refreshToken；脚本立即把新值回写本地缓存（Actions 下由
#   加密 state 缓存跨运行持久化）。只要链不断，凭证长期不过期，
#   彻底摆脱 X-Cloudide-Session 的 ~14 天手动续命死线。
# 优先级：TRAE_REFRESH_TOKEN(_N) / 缓存中的 refreshToken → 链式续期；
#         否则回退 Session Cookie 模式（并尝试从 GetUserToken 响应引导）。

OAUTH_HOST = "https://api.trae.com.cn"
EP_EXCHANGE = OAUTH_HOST + "/cloudide/api/v3/trae/oauth/ExchangeToken"        # 旧版
EP_EXCHANGE_PROOF = "https://api.trae.cn/trae/api/v3/oauth/ExchangeToken"     # 新版：需设备证明
CLIENT_ID_LEGACY = "en1oxy7wnw8j9n"          # 旧客户端 / 旧 token 用
CLIENT_ID_IDE = os.getenv("TRAE_CLIENT_ID", "").strip() or "ono9krqynydwx5"   # 桌面端实测值
IDE_VERSION = "3.4.9"
TOKEN_CACHE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)) or ".", ".trae_token_cache.json")

# ── 纯标准库 ECDSA P-256（新版 ExchangeToken 缺设备签名会报 20405）──
P256_P = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffff
P256_A = P256_P - 3
P256_N = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551
P256_GX = 0x6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296
P256_GY = 0x4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5

def _der_tlv(buf, i):
    tag = buf[i]; i += 1
    ln = buf[i]; i += 1
    if ln & 0x80:
        n = ln & 0x7f
        ln = int.from_bytes(buf[i:i + n], "big"); i += n
    return tag, buf[i:i + ln], i + ln

def _pem_der(pem):
    body = "".join(l.strip() for l in pem.splitlines() if "---" not in l)
    return base64.b64decode(body)

def ec_private_scalar(pem):
    """从 PKCS#8 / SEC1 PEM 里取出 P-256 私钥标量。"""
    der = _pem_der(pem)
    try:  # PKCS#8
        _, body, _ = _der_tlv(der, 0)
        i = 0
        _, _, i = _der_tlv(body, i)   # INTEGER version
        _, _, i = _der_tlv(body, i)   # SEQUENCE AlgorithmIdentifier（跳过！）
        _, octs, i = _der_tlv(body, i)
        _, inner, _ = _der_tlv(octs, 0)
        j = 0
        _, _, j = _der_tlv(inner, j)
        _, d, j = _der_tlv(inner, j)
        return int.from_bytes(d, "big")
    except Exception:  # SEC1
        _, body, _ = _der_tlv(der, 0)
        i = 0
        _, _, i = _der_tlv(body, i)
        _, d, i = _der_tlv(body, i)
        return int.from_bytes(d, "big")

def _ec_add(p1, p2):
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    x1, y1 = p1
    x2, y2 = p2
    if x1 == x2 and (y1 + y2) % P256_P == 0:
        return None
    if p1 == p2:
        lam = (3 * x1 * x1 + P256_A) * pow(2 * y1, -1, P256_P) % P256_P
    else:
        lam = (y2 - y1) * pow(x2 - x1, -1, P256_P) % P256_P
    x3 = (lam * lam - x1 - x2) % P256_P
    return x3, (lam * (x1 - x3) - y1) % P256_P

def _ec_mul(k, pt):
    r = None
    while k:
        if k & 1:
            r = _ec_add(r, pt)
        pt = _ec_add(pt, pt)
        k >>= 1
    return r

def _der_sig(r, s):
    def enc(x):
        b = x.to_bytes(32, "big").lstrip(b"\x00") or b"\x00"
        if b[0] & 0x80:
            b = b"\x00" + b
        return b"\x02" + bytes([len(b)]) + b
    body = enc(r) + enc(s)
    return b"\x30" + bytes([len(body)]) + body

def ec_sign(pem, data):
    """ECDSA P-256 / SHA-256 签名，返回 base64(DER)。低 s 归一化，与客户端一致。"""
    d = ec_private_scalar(pem)
    z = int.from_bytes(hashlib.sha256(data).digest(), "big")
    while True:
        k = int.from_bytes(os.urandom(32), "big") % (P256_N - 1) + 1
        x1, _ = _ec_mul(k, (P256_GX, P256_GY))
        r = x1 % P256_N
        if r == 0:
            continue
        s = (pow(k, -1, P256_N) * (z + r * d)) % P256_N
        if s == 0:
            continue
        if s > P256_N // 2:
            s = P256_N - s
        return base64.b64encode(_der_sig(r, s)).decode("ascii")

def norm_pem(v):
    """把环境变量里字面量的 \n 还原成真换行（复制粘贴友好）。"""
    v = (v or "").strip()
    if "\\n" in v and "\n" not in v:
        v = v.replace("\\n", "\n")
    return v

def device_keys():
    """从环境变量取设备证明材料（新版 ExchangeToken 必需，需从 Trae 桌面客户端导出）。
    为避免与你现有 TRAE_DEVICE_ID（签到风控设备号，16 位数字）混淆，
    这组变量统一用 TRAE_CLIENT_ 前缀。取不到返回 None（走旧版接口兜底）。
    另支持 TRAE_CLIENT_DEVICE_INFO：客户端签发 token 时上送的完整 DeviceInfo
    JSON（服务端可能将 token 绑定到完整设备指纹，建议与设备密钥同源导出）。"""
    priv = norm_pem(os.environ.get("TRAE_CLIENT_DEVICE_KEY_PEM", ""))
    pub = norm_pem(os.environ.get("TRAE_CLIENT_DEVICE_PUB_PEM", ""))
    did = os.environ.get("TRAE_CLIENT_DEVICE_ID", "").strip()
    if priv and pub and did:
        info = None
        raw = os.environ.get("TRAE_CLIENT_DEVICE_INFO", "").strip()
        if raw:
            try:
                info = json.loads(raw)
            except json.JSONDecodeError:
                print("[续期] TRAE_CLIENT_DEVICE_INFO 不是合法 JSON，忽略")
        return {"deviceId": did, "privateKeyPem": priv, "publicKeyPem": pub,
                "machineId": os.environ.get("TRAE_CLIENT_MACHINE_ID", "").strip(),
                "deviceInfo": info}
    return None

def device_proof(path, refresh_token, keys, client_id=None, platform_code="IDE_PC"):
    """构造 (DeviceProof, DeviceInfo)，供新版 ExchangeToken 使用。
    有 TRAE_CLIENT_DEVICE_INFO 时逐字段复刻客户端原版上送内容。"""
    ts = int(time.time())
    nonce = os.urandom(16).hex()
    canonical = "\n".join(["POST", path, client_id or CLIENT_ID_IDE, refresh_token, str(ts), nonce])
    proof = {"Timestamp": ts, "Nonce": nonce,
             "Signature": ec_sign(keys["privateKeyPem"], canonical.encode("utf-8"))}
    if keys.get("deviceInfo"):
        info = dict(keys["deviceInfo"])
        info["DevicePublicKey"] = keys["publicKeyPem"]   # 确保与签名私钥同源
        return proof, info
    info = {"DeviceID": keys.get("deviceId") or "", "MachineID": keys.get("machineId") or "",
            "PlatformCode": platform_code, "DeviceType": "PC",
            "DeviceName": platform.node() or "PC", "DeviceModel": "",
            "ClientVersion": IDE_VERSION, "DevicePublicKey": keys["publicKeyPem"],
            "DeviceBrand": "", "DeviceCPU": "",
            "OSInfo": platform.system() or "Windows", "OSVersion": platform.release() or ""}
    return proof, info

def refresh_tokens(refresh_token, keys=None):
    """用 refreshToken 换新 accessToken；Trae 会同时轮换 refreshToken。
    返回 (new_access, new_refresh, msg)。优先走带设备证明的新端点，
    没有设备材料时退回旧端点（102/204 错误码会给出明确提示）。"""
    if keys and keys.get("privateKeyPem") and keys.get("publicKeyPem"):
        path = "/trae/api/v3/oauth/ExchangeToken"
        last = None
        # (ClientID, PlatformCode) 组合链：桌面端实测钥匙 → 旧 ClientID → SOLO 线
        combos = [(CLIENT_ID_IDE, "IDE_PC"), (CLIENT_ID_LEGACY, "IDE_PC"), (CLIENT_ID_LEGACY, "SOLO_PC")]
        for cid, pcode in combos:
            try:
                proof, info = device_proof(path, refresh_token, keys, cid, pcode)
            except Exception as e:
                return None, None, "设备签名失败（检查设备私钥）: %s" % e
            body = json.dumps({"ClientID": cid, "ClientSecret": "", "RefreshToken": refresh_token,
                               "DeviceInfo": info, "DeviceProof": proof, "IDEVersion": IDE_VERSION})
            status, text = _post(EP_EXCHANGE_PROOF, {"Content-Type": "application/json",
                                                     "x-cloudide-token": ""}, body)
            if status < 400:
                break
            last = (status, text)
            # 10101 不匹配 / 20403 设备不匹配 → 换下一组「钥匙×产品线」再试
            if "10101" not in text and "20403" not in text:
                break
        else:
            status, text = last or (status, text)
    else:
        body = json.dumps({"ClientID": CLIENT_ID_LEGACY, "RefreshToken": refresh_token,
                           "ClientSecret": "-", "UserID": ""})
        status, text = _post(EP_EXCHANGE, {"Content-Type": "application/json",
                                           "Accept": "application/json"}, body)
    if status >= 400 or status < 0:
        return None, None, "refresh http=%s %s" % (status, text[:200])
    try:
        res = json.loads(text)["Result"]
        return res.get("Token"), res.get("RefreshToken"), "ok"
    except Exception as e:
        return None, None, "解析续期响应失败: %s %s" % (e, text[:200])

def load_token_cache():
    try:
        with open(TOKEN_CACHE_FILE, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}

def save_token_cache(cache):
    try:
        with open(TOKEN_CACHE_FILE, "w", encoding="utf-8") as fh:
            json.dump(cache, fh, ensure_ascii=False, indent=2)
    except Exception as e:
        print("[缓存] 写入失败: %s" % e)

def renew_access_token(index, refresh_token, name):
    """链式续期：refreshToken → 新 accessToken（+轮换新 refreshToken），并立即回写缓存。
    缓存即命脉：轮换链断不得。Actions 下由 workflow 的加密 state 缓存跨运行持久化。"""
    keys = device_keys()
    if keys:
        print("[%s] 链式续期：携带设备证明（新版接口）" % name)
    else:
        print("[%s] 链式续期：未配置设备证明材料，走旧版接口兜底" % name)
    new_at, new_rt, msg = refresh_tokens(refresh_token, keys)
    if not new_at:
        raise RuntimeError("refreshToken 续期失败: %s" % msg)
    cache = load_token_cache()
    rec = cache.get(str(index)) or {}
    if new_rt:
        rec["refreshToken"] = new_rt
    rec["updatedAt"] = beijing_now_str()
    cache[str(index)] = rec
    save_token_cache(cache)
    return new_at

def bootstrap_refresh_token(index, result, name):
    """Session 模式引导：GetUserToken 响应里若带 RefreshToken，写入缓存，
    下次运行即可切到链式续期（此后不再依赖 14 天会话 Cookie）。"""
    boot_rt = (result.get("RefreshToken") or result.get("refreshToken") or "").strip()
    if not boot_rt:
        return False
    cache = load_token_cache()
    rec = cache.get(str(index)) or {}
    if rec.get("refreshToken") == boot_rt:
        return False
    rec["refreshToken"] = boot_rt
    rec["updatedAt"] = beijing_now_str()
    rec["source"] = "GetUserToken bootstrap"
    cache[str(index)] = rec
    save_token_cache(cache)
    print("[%s] ✨ 检测到 RefreshToken，已写入缓存——下次运行起进入链式续期模式" % name)
    return True

def iter_accounts():
    """产出 (序号, session, device_id, refresh_token)。
    Session: TRAE_SESSION / TRAE_SESSION_N；refreshToken: TRAE_REFRESH_TOKEN / TRAE_REFRESH_TOKEN_N。
    两类可只配其一；同序号两者都配时优先走链式续期、失败回退 Session。"""
    def _env(name):
        return os.environ.get(name, "").strip()
    s1, rt1 = _env("TRAE_SESSION"), _env("TRAE_REFRESH_TOKEN")
    if s1 or rt1:
        yield 1, s1, _env("TRAE_DEVICE_ID"), rt1
    n = 2
    while True:
        s = _env("TRAE_SESSION_%d" % n)
        rt = _env("TRAE_REFRESH_TOKEN_%d" % n)
        if not s and not rt:
            break
        yield n, s, _env("TRAE_DEVICE_ID_%d" % n), rt
        n += 1


def random_device_id():
    """随机生成 16 位数字风控设备号（仅在缺省时兜底）。"""
    return str(random.randint(10**15, 10**16 - 1))


def main():
    accounts = list(iter_accounts())
    if not accounts:
        print("错误：缺少环境变量 TRAE_SESSION 或 TRAE_REFRESH_TOKEN")
        sys.exit(1)
    webhook = os.environ.get("FEISHU_WEBHOOK", "").strip()
    ok_names, fail_names = [], []
    rerun_flags = []    # 标记各成功账号是否为「今日已签到」的重跑（重跑不推送，防重复）
    lines = []          # 每账号一行的明细（签到结果 + 所得 + 余额），用于推送
    all_ok = True
    for index, session, device_id, refresh_token in accounts:
        # 多账号之间错开 3~6 秒随机间隔，降低请求密度，规避 9074「参与用户太多」风控
        if index > 1:
            time.sleep(random.uniform(3, 6))
        name = "账号 %d" % index
        device_id = device_id or random_device_id()
        try:
            # 取 token：优先链式续期（refreshToken 轮换链），失败回退 Session Cookie
            token = None
            if refresh_token or load_token_cache().get(str(index), {}).get("refreshToken"):
                rt = refresh_token or load_token_cache().get(str(index), {}).get("refreshToken")
                try:
                    token = renew_access_token(index, rt, name)
                    print("[%s] 链式续期成功，JWT 长度=%d（refreshToken 已轮换回写缓存）" % (name, len(token)))
                except Exception as e:
                    if session:
                        print("[%s] 链式续期失败（%s），回退 Session Cookie 模式" % (name, e))
                    else:
                        raise
            if token is None:
                token, token_result = get_token_full(session)
                print("[%s] 已换取新 JWT，长度=%d" % (name, len(token)))
                bootstrap_refresh_token(index, token_result, name)

            # 拉取账号资料（昵称/脱敏手机号），失败降级为「账号 N」
            profile = get_user_info(token, session)
            if profile:
                name = "账号 %d·%s" % (index, profile)
                print("[%s] 识别成功" % name)
            else:
                print("[%s] 未获取到昵称，沿用编号显示" % name)

            # 签到前状态：若今日已签过，则本次运行不产生新积分（重跑场景如实记 0）
            pre = checkin_status(token, device_id)
            pre_code = pre["body"].get("code", -1)
            pre_checked = bool(pre["body"].get("checked_in", False))
            print("[%s] 签到前 status：code=%s, checked_in=%s" % (name, pre_code, pre_checked))
            already = (pre["http"] == 200) and (pre_code == 0) and pre_checked

            # 签到前余额（用于签到后刷新校验）
            pre_balance = get_remaining_credits(token, device_id)
            print("[%s] 签到前余额：%s" % (name, _fmt_num(pre_balance)))

            if already:
                balance = pre_balance if pre_balance >= 0 else get_remaining_credits(token, device_id)
                print("[%s] 今日已签到过，本次获得：0 积分" % name)
                ok_names.append(name)
                rerun_flags.append(True)
                lines.append("✅ %s：今日已签到，获得 0 积分，余额 %s" % (name, _fmt_num(balance)))
                low = credit_low_line(name, balance)
                if low:
                    lines.append(low)
                continue

            result = checkin(token, device_id)
            body = result["body"]
            code = body.get("code", -1)
            # 9074「参与用户太多」= 设备号被风控标记；换全新设备号自动重试（最多 5 次）
            attempt = 1
            while code == 9074 and attempt < 5:
                device_id = random_device_id()
                attempt += 1
                print("[%s] 命中风控 9074，换新设备号重试（第 %d 次）" % (name, attempt))
                time.sleep(random.uniform(0.8, 1.5))
                result = checkin(token, device_id)
                body = result["body"]
                code = body.get("code", -1)

            checked = body.get("checked_in", False)
            ok = (result["http"] == 200) and (code == 0 or checked)
            if not ok:
                reason = body.get("message") or ("HTTP %s" % result["http"])
                print("[%s] 签到失败：%s" % (name, reason))
                fail_names.append(name)
                all_ok = False
                lines.append("❌ %s：签到失败：%s" % (name, reason))
                continue

            # claim 响应不含所得积分：对齐桌面端，签到后再查 status 解析本次所得
            after = checkin_status(token, device_id)
            gained = resolve_gained(after["body"])
            print("[%s] 签到后 status：code=%s, checked_in=%s, credits=%s, extra_credits=%s → gained=%s"
                  % (name, after["body"].get("code", -1), after["body"].get("checked_in", False),
                     after["body"].get("credits", 0), after["body"].get("extra_credits", 0), gained))

            # 余额刷新：资格包接口可能有延迟，签到后余额应 >= pre + gained，否则等待重试
            balance = get_remaining_credits(token, device_id)
            tries = 0
            while (pre_balance >= 0 and balance >= 0
                   and balance < pre_balance + gained and tries < 4):
                tries += 1
                print("[%s] 余额未刷新（%s < %s），第 %d 次等待重试…" % (name, balance, pre_balance + gained, tries))
                time.sleep(4)
                balance = get_remaining_credits(token, device_id)

            print("[%s] 签到成功，本次获得：%s 积分，剩余：%s" % (name, _fmt_num(gained), _fmt_num(balance)))
            ok_names.append(name)
            lines.append("✅ %s：签到成功，获得 %s 积分，余额 %s" % (name, _fmt_num(gained), _fmt_num(balance)))
            low = credit_low_line(name, balance)
            if low:
                lines.append(low)
        except Exception as e:
            print("[%s] 签到异常: %s" % (name, e))
            fail_names.append(name)
            all_ok = False
            lines.append("❌ %s：签到异常：%s" % (name, e))
    # 汇总一条飞书推送（webhook 为空则跳过）。
    # 防重复：若本次运行所有成功账号都是「今日已签到」的重跑且无失败，则不推送
    # （首个触发源已推送过；本运行仅作为 schedule 兜底/串行队列中的重复触发）。
    rerun_only = all_ok and ok_names and len(rerun_flags) == len(ok_names)
    if rerun_only:
        print("所有账号今日均已签到，跳过推送（避免重复）")
    elif webhook and (ok_names or fail_names):
        summary = ["Trae 多账号签到结果", "时间：%s" % beijing_now_str()]
        summary.extend(lines)
        notify_feishu(webhook, "\n".join(summary))
    if not all_ok:
        sys.exit(1)
    print("全部账号签到完成")


if __name__ == "__main__":
    main()
