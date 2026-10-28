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
import datetime
import json
import os
import random
import sys
import time
import urllib.request

BASE = "https://api.trae.cn"


def _post(path, headers, body=""):
    """POST 请求。非 2xx 不抛 HTTPError，而是以 (status, body) 正常返回，便于上层判断原因。"""
    import urllib.error
    req = urllib.request.Request(BASE + path, data=body.encode("utf-8"), headers=headers, method="POST")
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


def get_token(session: str) -> str:
    """用 X-Cloudide-Session Cookie 换取全新 JWT。"""
    headers = {
        "Cookie": "X-Cloudide-Session=" + session,
        "Referer": "https://www.trae.cn/",
        "Origin": "https://www.trae.cn",
        "User-Agent": "TraeCheckin/1.0",
        "Accept": "application/json, text/plain, */*",
    }
    status, text = _post("/cloudide/api/v3/common/GetUserToken", headers)
    data = json.loads(text)
    token = (data.get("Result") or {}).get("Token")
    if status == 401:
        raise RuntimeError(
            "账号会话已失效(HTTP 401)：X-Cloudide-Session 可能已过期(~14天有效期)。"
            "请在浏览器重新登录 trae.cn 并复制新的 Cookie 值，更新到 TRAE_SESSION 后重试。"
            "原始返回: " + text[:200])
    if status != 200 or not token:
        raise RuntimeError("GetUserToken 失败: HTTP %s %s" % (status, text[:200]))
    return token


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


def iter_sessions():
    """按顺序产出 (账号序号, session, device_id)。账号 1 读 TRAE_SESSION；
    之后依次读 TRAE_SESSION_2, TRAE_SESSION_3… 直到缺空为止。"""
    s = os.environ.get("TRAE_SESSION", "").strip()
    if s:
        yield 1, s, os.environ.get("TRAE_DEVICE_ID", "").strip()
    n = 2
    while True:
        s = os.environ.get("TRAE_SESSION_%d" % n, "").strip()
        if not s:
            break
        yield n, s, os.environ.get("TRAE_DEVICE_ID_%d" % n, "").strip()
        n += 1


def random_device_id():
    """随机生成 16 位数字风控设备号（仅在缺省时兜底）。"""
    return str(random.randint(10**15, 10**16 - 1))


def main():
    accounts = list(iter_sessions())
    if not accounts:
        print("错误：缺少环境变量 TRAE_SESSION")
        sys.exit(1)
    webhook = os.environ.get("FEISHU_WEBHOOK", "").strip()
    ok_names, fail_names = [], []
    rerun_flags = []    # 标记各成功账号是否为「今日已签到」的重跑（重跑不推送，防重复）
    lines = []          # 每账号一行的明细（签到结果 + 所得 + 余额），用于推送
    all_ok = True
    for index, session, device_id in accounts:
        # 多账号之间错开 3~6 秒随机间隔，降低请求密度，规避 9074「参与用户太多」风控
        if index > 1:
            time.sleep(random.uniform(3, 6))
        name = "账号 %d" % index
        device_id = device_id or random_device_id()
        print("[%s] device_id=%s" % (name, device_id))
        try:
            token = get_token(session)
            print("[%s] 已换取新 JWT，长度=%d" % (name, len(token)))

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
