using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GoldTicker
{
    // 无窗口验证：真实 HTTP、WinForms 消息循环与同一刷新间隔，不模拟报价。
    static class LiveVerification
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void VerifyCadence(decimal price, DateTimeOffset quoteTime)
        {
            // 直接测试频率判断的业务输入，不替换或模拟网络接口。
            var cadence = new PollingCadence();
            Check(cadence.Interval == 15000, "启动频率应为15秒");
            for (int i = 0; i < 4; i++) cadence.Observe(price, quoteTime);
            Check(!cadence.IsSlow, "只有4次相同报价时不应降频");
            cadence.Observe(price, quoteTime);
            Check(cadence.Interval == 300000, "第5次相同报价应降为5分钟");
            cadence.Observe(price, quoteTime);
            Check(cadence.IsSlow, "持续相同应保持5分钟");
            cadence.Failed();
            Check(cadence.IsSlow, "请求失败不应恢复15秒");
            cadence.Observe(price, quoteTime);
            Check(cadence.IsSlow, "失败后同价应保持5分钟");
            cadence.Observe(price + 0.01m, quoteTime);
            Check(cadence.Interval == 15000, "价格变化应立即恢复15秒");
            cadence = new PollingCadence();
            for (int i = 0; i < 4; i++) cadence.Observe(price, quoteTime);
            cadence.Failed();
            for (int i = 0; i < 4; i++) cadence.Observe(price, quoteTime);
            Check(!cadence.IsSlow, "失败应中断连续相同报价计数");
            cadence.Observe(price, quoteTime);
            Check(cadence.IsSlow, "失败后重新累计5次应降频");
            cadence = new PollingCadence();
            for (int i = 0; i < 4; i++) cadence.Observe(price, quoteTime);
            cadence.Observe(price + 0.01m, quoteTime);
            for (int i = 0; i < 3; i++) cadence.Observe(price + 0.01m, quoteTime);
            Check(!cadence.IsSlow, "价格变化应重新计数");
            cadence.Observe(price + 0.01m, quoteTime);
            Check(cadence.IsSlow, "新价格连续5次相同应再次降频");
            cadence.Observe(price + 0.01m, quoteTime.AddSeconds(15));
            Check(!cadence.IsSlow, "同价但行情时间更新应恢复15秒");
            for (int i = 2; i <= 10; i++) cadence.Observe(price + 0.01m, quoteTime.AddSeconds(15 * i));
            Check(!cadence.IsSlow, "同价且行情时间持续更新不能降频");
            // 重放实际误降频日志：5次同价，但每次行情时间都前进15秒。
            cadence = new PollingCadence();
            var replayTime = new DateTimeOffset(2026, 10, 26, 15, 28, 10, TimeSpan.FromHours(8));
            for (int i = 0; i < 5; i++) cadence.Observe(861.04m, replayTime.AddSeconds(15 * i));
            Check(!cadence.IsSlow, "实际日志中的5次同价新行情不应降频");
        }
        [STAThread] static int Main(string[] args)
        {
            var lines = new List<string>();
            string path = args[0];
            int result = 0, count = 0;
            bool busy = false;
            double previousStart = -1;
            decimal? firstPrice = null;
            DateTimeOffset firstQuoteTime = default(DateTimeOffset);
            bool allSame = true;
            var cadence = new PollingCadence();
            var elapsed = Stopwatch.StartNew();
            using (var loop = new ApplicationContext())
            using (var timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = cadence.Interval;
                Func<Task> check = async delegate
                {
                    if (busy) return;
                    busy = true;
                    try
                    {
                        double started = elapsed.Elapsed.TotalSeconds;
                        if (previousStart >= 0 && (started - previousStart < 14 || started - previousStart > 18))
                            throw new InvalidOperationException("定时器请求间隔不在 14~18 秒范围内");
                        previousStart = started;
                        Quote q = await Feed.Fetch();
                        if (!firstPrice.HasValue)
                        {
                            firstPrice = q.Price;
                            firstQuoteTime = q.Time;
                            VerifyCadence(q.Price, q.Time);
                            var colorCadence = new PollingCadence();
                            for (int observation = 1; observation <= 6; observation++)
                            {
                                colorCadence.Observe(q.Price, observation == 6 ? q.Time.AddSeconds(15) : q.Time);
                                using (var bitmap = new Bitmap(92, 32))
                                using (var graphics = Graphics.FromImage(bitmap))
                                {
                                    Theme.DrawTicker(graphics, bitmap.Size, q, colorCadence.IsSlow);
                                    Color expected = colorCadence.IsSlow
                                        ? (Theme.LightTaskbar ? Theme.Up : Color.FromArgb(240, 111, 115))
                                        : (Theme.LightTaskbar ? Theme.DefaultPrice : Theme.DefaultPriceOnDark);
                                    int pixels = 0;
                                    for (int y = 0; y < bitmap.Height; y++)
                                        for (int x = 0; x < bitmap.Width; x++)
                                            if (bitmap.GetPixel(x, y).ToArgb() == expected.ToArgb()) pixels++;
                                    Check(pixels > 0, "查询模式对应的文字颜色未绘制");
                                    Check(colorCadence.IsSlow == (observation == 5), "颜色验证的频率状态不正确");
                                }
                            }
                            lines.Add("同一报价绘制验证通过：正常绿色，第5次价格及行情时间相同红色，同价时间更新恢复绿色。");
                            lines.Add("频率逻辑测试通过：5次价格及时间阈值、任一变化恢复15秒、同价时间更新不降频及实际日志重放、失败中断计数、低频失败保持5分钟。");
                        }
                        else if (q.Price != firstPrice.Value || q.Time != firstQuoteTime) allSame = false;
                        cadence.Observe(q.Price, q.Time);
                        if (timer.Interval != cadence.Interval) { timer.Stop(); timer.Interval = cadence.Interval; timer.Start(); }
                        count++;
                        lines.Add("请求 " + count + "：开始=" + started.ToString("F2") + "s，完成=" + elapsed.Elapsed.TotalSeconds.ToString("F2") +
                            "s，价格=" + q.Price.ToString("F2") + "，行情时间=" + q.Time.ToString("yyyy-MM-dd HH:mm:ss zzz"));
                        if (count == 5)
                        {
                            if (allSame) Check(timer.Interval == 300000, "连续5次真实价格及行情时间相同后定时器应切换到300000毫秒");
                            lines.Add("5次真实请求后，定时器间隔=" + timer.Interval + "毫秒，价格及行情时间均相同=" + allSame);
                            using (var bitmap = new Bitmap(92, 32))
                            {
                                using (var g = Graphics.FromImage(bitmap)) Theme.DrawTicker(g, bitmap.Size, q, false);
                                bitmap.MakeTransparent(Theme.Transparent);
                                if (bitmap.GetPixel(0, 0).A != 0 || bitmap.GetPixel(91, 31).A != 0)
                                    throw new InvalidOperationException("价格条背景没有透明");
                                lines.Add("价格条透明背景像素校验通过");
                            }
                            IntPtr foreground = Native.GetForegroundWindow();
                            if (Native.IsZoomed(foreground) && Native.IsFullscreen(foreground, Screen.PrimaryScreen.Bounds))
                                throw new InvalidOperationException("最大化窗口被误判成全屏");
                            lines.Add("当前前台最大化=" + Native.IsZoomed(foreground) + "；全屏判断=" + Native.IsFullscreen(foreground, Screen.PrimaryScreen.Bounds));
                            lines.Add("15 秒 WinForms 定时器、5 次真实请求、字段校验通过。");
                            timer.Stop(); loop.ExitThread();
                        }
                    }
                    catch (Exception ex)
                    {
                        result = 1; lines.Add("验证失败：" + ex.Message); timer.Stop(); loop.ExitThread();
                    }
                    finally { busy = false; }
                };
                timer.Tick += async delegate { await check(); };
                EventHandler idle = null;
                idle = async delegate { Application.Idle -= idle; timer.Start(); await check(); };
                Application.Idle += idle;
                Application.Run(loop);
            }
            lines.Add("尚未验证：实际等待5分钟后的下一次请求、真实行情恢复时的界面行为，以及任务栏鼠标交互。");
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
            return result;
        }
    }
}
