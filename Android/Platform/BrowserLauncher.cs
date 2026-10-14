using System;

namespace TraeTools.Platform;

/// <summary>
/// 用系统默认浏览器打开 URL（Android 实现）。
///
/// <para><b>失败必须可见</b>：早先的实现把异常全部吞掉且返回 void，于是「授权 GitHub」
/// 按钮点了没反应时，用户与日志都拿不到任何线索。现在返回 bool，
/// 并把失败原因写进平台日志（category = browser）。</para>
///
/// <para><b>三级回退</b>：
/// <list type="number">
/// <item>优先用当前 Activity 启动（前台切换最自然，无需 FLAG_ACTIVITY_NEW_TASK）；</item>
/// <item>Activity 不可用时改用 Application 上下文 —— 此时必须带 NEW_TASK，
///       否则抛 AndroidRuntimeException；</item>
/// <item>个别定制 ROM 会拦截直接 startActivity，最后用 <c>Intent.CreateChooser</c> 再试一次。</item>
/// </list></para>
///
/// <para>浏览器属于「隐式 Intent 解析」，Android 11+ 的包可见性限制默认不影响
/// startActivity，但为兼容个别 ROM 仍在清单里声明了 &lt;queries&gt;（见 Properties/AndroidManifest.xml）。</para>
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>打开 URL：成功返回 true；失败返回 false 并已写日志。</summary>
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            PlatformLog.Warn("browser", "打开浏览器失败：URL 为空");
            return false;
        }
        try
        {
            var uri = Android.Net.Uri.Parse(url);
            if (uri == null)
            {
                PlatformLog.Warn("browser", $"打开浏览器失败：URL 无法解析（{url}）");
                return false;
            }

            var activity = MainActivity.Current;
            var context = Android.App.Application.Context;

            // ① 首选：Activity 直接启动
            if (activity != null)
            {
                try
                {
                    activity.StartActivity(new Android.Content.Intent(Android.Content.Intent.ActionView, uri));
                    return true;
                }
                catch (Exception ex)
                {
                    PlatformLog.Warn("browser", "Activity 启动浏览器失败，回退 Application 上下文：" + ex.Message);
                }
            }

            if (context == null)
            {
                PlatformLog.Warn("browser", "打开浏览器失败：Android 上下文不可用（Application.Context 为 null）");
                return false;
            }

            // ② 回退：Application 上下文（必须 NEW_TASK）
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView, uri);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                PlatformLog.Warn("browser", "Application 启动浏览器失败，改用选择器：" + ex.Message);
            }

            // ③ 再回退：让系统弹「用哪个应用打开」
            try
            {
                var view = new Android.Content.Intent(Android.Content.Intent.ActionView, uri);
                view.AddFlags(Android.Content.ActivityFlags.NewTask);
                var chooser = Android.Content.Intent.CreateChooser(view, "选择浏览器打开")!;
                chooser.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(chooser);
                return true;
            }
            catch (Exception ex)
            {
                PlatformLog.Warn("browser",
                    $"打开浏览器失败（三种方式均未成功）：{ex.GetType().Name}: {ex.Message}（URL={url}）");
                return false;
            }
        }
        catch (Exception ex)
        {
            PlatformLog.Warn("browser", $"打开浏览器失败：{ex.GetType().Name}: {ex.Message}（URL={url}）");
            return false;
        }
    }
}
