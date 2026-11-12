using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GoldTicker
{
    static class Theme
    {
        public static readonly Color Transparent = Color.Magenta;
        public static readonly bool LightTaskbar = ReadLightTaskbar();
        static bool ReadLightTaskbar()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key == null || Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 1)) != 0;
        }
        public static readonly float Scale = GetScale();
        static float GetScale()
        {
            // 屏幕 DC 在每显示器 DPI 模式下可能固定返回96；使用任务栏窗口的实际 DPI。
            uint dpi = Native.GetDpiForWindow(Native.FindWindow("Shell_TrayWnd", null));
            return Math.Max(1f, dpi / 96f);
        }
        public static Size Scaled(int width, int height) { return new Size((int)Math.Round(width * Scale), (int)Math.Round(height * Scale)); }
        public static readonly Color Surface = Color.FromArgb(250, 250, 248);
        public static readonly Color Ink = Color.FromArgb(35, 39, 44);
        public static readonly Color Muted = Color.FromArgb(100, 106, 114);
        public static readonly Color DefaultPrice = Color.FromArgb(22, 135, 70);
        public static readonly Color DefaultPriceOnDark = Color.FromArgb(100, 210, 140);
        public static readonly Color Up = Color.FromArgb(183, 55, 61);
        public static readonly Color Down = Color.FromArgb(28, 121, 81);
        public static readonly Color Line = Color.FromArgb(224, 226, 228);
        public static Color PriceColor(Quote q, bool failed)
        {
            return q == null || failed || q.IsOld ? Muted : q.Change > 0 ? Up : q.Change < 0 ? Down : Ink;
        }
        public static void DrawTicker(Graphics g, Size size, Quote q, bool slowQuery)
        {
            g.Clear(Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            var state = g.Save();
            g.ScaleTransform(size.Width / 92f, size.Height / 32f);
            using (var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(slowQuery ? (LightTaskbar ? Up : Color.FromArgb(240, 111, 115)) : (LightTaskbar ? DefaultPrice : DefaultPriceOnDark)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(q == null ? "--.--" : q.Price.ToString("F2"), font, brush, new RectangleF(0, 0, 92, 32), format);
            g.Restore(state);
        }
    }

    sealed class Quote
    {
        public decimal Price, Previous, Change;
        public string Percent;
        public DateTimeOffset Time;
        public bool IsOld { get { return DateTimeOffset.UtcNow - Time > TimeSpan.FromMinutes(3); } }
    }

    sealed class PollingCadence
    {
        public const int NormalMilliseconds = 15000;
        public const int SlowMilliseconds = 300000;
        public const int SamePriceThreshold = 5;
        decimal? lastPrice;
        DateTimeOffset lastQuoteTime;
        int samePriceCount;
        public int Interval { get; private set; }
        public bool IsSlow { get { return Interval == SlowMilliseconds; } }
        public PollingCadence() { Interval = NormalMilliseconds; }
        public void Observe(decimal price, DateTimeOffset quoteTime)
        {
            if (!lastPrice.HasValue || lastPrice.Value != price || lastQuoteTime != quoteTime)
            {
                samePriceCount = 1;
                Interval = NormalMilliseconds;
            }
            else
            {
                samePriceCount = Math.Min(SamePriceThreshold, samePriceCount + 1);
                if (samePriceCount >= SamePriceThreshold) Interval = SlowMilliseconds;
            }
            lastPrice = price;
            lastQuoteTime = quoteTime;
        }
        // 失败不能作为价格没变的证据，也不能作为恢复正常频率的证据。
        public void Failed() { samePriceCount = 0; }
    }

    static class Feed
    {
        const string Url = "https://api.jdjygold.com/gw2/generic/jrm/h5/m/stdLatestPrice?productSku=1961543816";
        static readonly HttpClient Client = CreateClient();
        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 GoldTicker/1.0");
            client.DefaultRequestHeaders.Add("Referer", "https://m.jd.com/");
            return client;
        }
        public static async Task<Quote> Fetch()
        {
            string raw = await Client.GetStringAsync(Url).ConfigureAwait(false);
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw);
            if (!root.ContainsKey("success") || !Convert.ToBoolean(root["success"]))
                throw new InvalidDataException("报价接口返回失败");
            var result = (Dictionary<string, object>)root["resultData"];
            var data = (Dictionary<string, object>)result["datas"];
            if (Convert.ToString(data["productSku"]) != "1961543816")
                throw new InvalidDataException("报价品种不匹配");
            var q = new Quote();
            q.Price = Decimal.Parse(Convert.ToString(data["price"]), CultureInfo.InvariantCulture);
            q.Previous = Decimal.Parse(Convert.ToString(data["yesterdayPrice"]), CultureInfo.InvariantCulture);
            q.Change = Decimal.Parse(Convert.ToString(data["upAndDownAmt"]), CultureInfo.InvariantCulture);
            q.Percent = Convert.ToString(data["upAndDownRate"]);
            decimal percent = Decimal.Parse(q.Percent.TrimEnd('%'), CultureInfo.InvariantCulture);
            long ms = Int64.Parse(Convert.ToString(data["time"]), CultureInfo.InvariantCulture);
            q.Time = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms).ToOffset(TimeSpan.FromHours(8));
            if (q.Price <= 0 || q.Previous <= 0 || Math.Abs(q.Price - q.Previous - q.Change) > 0.02m ||
                Math.Abs(q.Change / q.Previous * 100 - percent) > 0.02m || q.Time > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new InvalidDataException("报价字段校验失败");
            return q;
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        public static void MatchTaskbarDpi()
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            if (bar != IntPtr.Zero) SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(bar));
        }
        public static void EmbedInTaskbar(IntPtr child, IntPtr bar)
        {
            const int childStyle = 0x40000000;
            int style = GetWindowLong(child, -16);
            SetWindowLong(child, -16, (style & unchecked((int)~0x80000000u)) | childStyle);
            SetWindowPos(child, new IntPtr(-2), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
            SetParent(child, bar);
            if (GetParent(child) != bar)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法嵌入任务栏");
            SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0020);
        }
        [StructLayout(LayoutKind.Sequential)] struct BlendFunction
        {
            public byte Operation, Flags, Alpha, Format;
        }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr screenDc, IntPtr destination, ref Size size, IntPtr sourceDc,
            ref Point source, uint key, ref BlendFunction blend, uint flags);
        public static void RenderTicker(IntPtr hwnd, Size size, Quote quote, bool slowQuery)
        {
            using (var image = new Bitmap(size.Width, size.Height))
            {
                using (var graphics = Graphics.FromImage(image)) Theme.DrawTicker(graphics, size, quote, slowQuery);
                image.MakeTransparent(Theme.Transparent);
                using (var pixels = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var graphics = Graphics.FromImage(pixels))
                    {
                        // 最低非零透明度保留整个价格区域的鼠标命中，避免数字间隙穿透任务栏。
                        graphics.Clear(Color.FromArgb(1, 0, 0, 0));
                        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                        graphics.DrawImageUnscaled(image, 0, 0);
                    }
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    IntPtr memoryDc = CreateCompatibleDC(screenDc);
                    IntPtr bitmap = pixels.GetHbitmap(Color.FromArgb(0));
                    IntPtr previous = SelectObject(memoryDc, bitmap);
                    try
                    {
                        var source = new Point(0, 0);
                        var blend = new BlendFunction { Alpha = 255, Format = 1 };
                        if (!UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memoryDc, ref source, 0, ref blend, 2))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法绘制透明价格");
                    }
                    finally
                    {
                        SelectObject(memoryDc, previous); DeleteObject(bitmap); DeleteDC(memoryDc); ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
            }
        }
        public static Rectangle Bounds(IntPtr hwnd)
        {
            Rect r;
            return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
        }
        public static bool IsFullscreen(IntPtr hwnd, Rectangle screen)
        {
            // 最大化窗口的外边框可能越过屏幕边缘，不能按 GetWindowRect 直接视为全屏。
            if (hwnd == IntPtr.Zero || IsZoomed(hwnd) || hwnd == FindWindow("Shell_TrayWnd", null) || hwnd == FindWindow("Progman", null)) return false;
            var name = new System.Text.StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "WorkerW") return false;
            Rect r;
            Rectangle bounds = DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(Rect))) == 0
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Bounds(hwnd);
            return !bounds.IsEmpty && bounds.Left == screen.Left && bounds.Top == screen.Top &&
                bounds.Right == screen.Right && bounds.Bottom == screen.Bottom;
        }
    }

    static class Diagnostics
    {
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZheshangGoldTicker");
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "status.log");
                if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
                File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class OutsideClickMonitor : IDisposable
    {
        delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct MouseData
        {
            public Point Position;
            public uint Buttons, Flags, Time;
            public UIntPtr Extra;
        }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        readonly HookCallback callback;
        readonly Action<Point> mouseDown;
        IntPtr hook;
        public bool IsRunning { get { return hook != IntPtr.Zero; } }
        public OutsideClickMonitor(Action<Point> mouseDown)
        {
            this.mouseDown = mouseDown;
            callback = OnMouse;
        }
        public void Start()
        {
            if (IsRunning) return;
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Stop()
        {
            if (!IsRunning) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            int kind = message.ToInt32();
            if (code >= 0 && (kind == 0x0201 || kind == 0x0204 || kind == 0x0207 || kind == 0x020B))
            {
                var mouse = (MouseData)Marshal.PtrToStructure(data, typeof(MouseData));
                mouseDown(mouse.Position);
            }
            return CallNextHookEx(hook, code, message, data);
        }
        public void Dispose() { Stop(); }
    }

    sealed class Details : Form
    {
        public Quote Quote;
        public bool Failed, Pinned;
        public bool SlowQuery;
        public DateTimeOffset? LastAttempt, LastSuccess;
        public Details()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = Theme.Scaled(300, 278);
            AccessibleName = "浙商银行积存金详情";
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Pinned = false; Hide(); } };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
        }
        public static void Draw(Graphics g, Size size, Quote q, bool failed, DateTimeOffset? lastAttempt, DateTimeOffset? lastSuccess, bool slowQuery)
        {
            g.Clear(Theme.Surface);
            var state = g.Save();
            g.ScaleTransform(size.Width / 300f, size.Height / 278f);
            size = new Size(300, 278);
            using (var normal = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var small = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var large = new Font("Segoe UI", 36f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var ink = new SolidBrush(Theme.Ink))
            using (var muted = new SolidBrush(Theme.Muted))
            using (var priceBrush = new SolidBrush(slowQuery ? Theme.Up : Theme.DefaultPrice))
            using (var line = new Pen(Theme.Line))
            {
                g.DrawRectangle(line, 0, 0, size.Width - 1, size.Height - 1);
                g.DrawString("浙商银行积存金", normal, ink, 18, 16);
                g.DrawString(q == null ? "--" : q.Price.ToString("F2"), large, priceBrush, 15, 43);
                g.DrawString("元/克", small, muted, 225, 72);
                g.DrawLine(line, 18, 106, size.Width - 18, 106);
                string[] labels = { "昨收价", "涨跌额", "涨跌幅" };
                string[] values = q == null ? new[] { "--", "--", "--" } : new[] {
                    q.Previous.ToString("F2"), q.Change.ToString("+0.00;-0.00;0.00"), q.Percent };
                for (int i = 0; i < 3; i++)
                {
                    g.DrawString(labels[i], normal, muted, 18, 116 + i * 27);
                    var rect = new Rectangle(130, 116 + i * 27, size.Width - 148, 24);
                    using (var valueBrush = new SolidBrush(i == 0 ? Theme.Ink : Theme.PriceColor(q, failed)))
                    using (var format = new StringFormat { Alignment = StringAlignment.Far })
                        g.DrawString(values[i], normal, valueBrush, rect, format);
                }
                string status = failed ? (slowQuery ? "查询失败 · 每 5 分钟重试" : "查询失败 · 每 15 秒重试") : q == null ? "正在获取报价…" :
                    slowQuery ? "报价及行情时间未更新 · 每 5 分钟查询" : q.IsOld ? "每 15 秒查询 · 上游暂无新行情" : "每 15 秒查询";
                g.DrawString(status, small, muted, 18, 205);
                g.DrawString(q == null ? "行情时间：--" : "行情时间：" + q.Time.ToString("MM-dd HH:mm:ss"), small, muted, 18, 226);
                string queryTime = failed ? (lastAttempt.HasValue ? lastAttempt.Value.ToString("HH:mm:ss") : "--") :
                    lastSuccess.HasValue ? lastSuccess.Value.ToString("HH:mm:ss") : "--";
                g.DrawString((failed ? "最近尝试：" : "查询成功：") + queryTime, small, muted, 18, 247);
            }
            g.Restore(state);
        }
        protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics, ClientSize, Quote, Failed, LastAttempt, LastSuccess, SlowQuery); }
    }

    static class StartupSettings
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "ZheshangGoldTicker";
        public static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }
        public static bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && String.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
        }
        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    sealed class Ticker : Form
    {
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        readonly PollingCadence cadence = new PollingCadence();
        readonly System.Windows.Forms.Timer interaction = new System.Windows.Forms.Timer();
        readonly Details details = new Details();
        readonly OutsideClickMonitor outsideClicks;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly string positionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZheshangGoldTicker", "position.txt");
        Quote quote;
        bool failed, busy, dragging, moved;
        Point dragStart, windowStart;
        int? savedOffset, savedVerticalOffset;
        string drawnPrice;
        bool drawnSlowQuery;
        DateTime lastPlacement = DateTime.MinValue;
        DateTimeOffset? lastAttempt, lastSuccess;
        string visibilityReason;
        IntPtr embeddedTaskbar;
        public Ticker()
        {
            outsideClicks = new OutsideClickMonitor(OnGlobalMouseDown);
            details.VisibleChanged += delegate
            {
                if (!details.Visible) { outsideClicks.Stop(); return; }
                try { outsideClicks.Start(); }
                catch (System.ComponentModel.Win32Exception ex) { Diagnostics.Write("详情外部点击监听失败，错误码=" + ex.NativeErrorCode); }
            };
            Text = "浙商金价";
            AccessibleName = "浙商银行积存金价格";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;
            DoubleBuffered = true;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Transparent;
            // 使用每像素透明图像；不混用 TransparencyKey / SetLayeredWindowAttributes。
            ClientSize = Theme.Scaled(92, 32);
            try
            {
                string[] parts = File.ReadAllText(positionFile).Trim().Split(',');
                int offset, verticalOffset;
                if ((parts.Length == 1 || parts.Length == 2) && Int32.TryParse(parts[0], out offset))
                {
                    savedOffset = offset;
                    if (parts.Length == 2 && Int32.TryParse(parts[1], out verticalOffset)) savedVerticalOffset = verticalOffset;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            menu.Items.Add("立即刷新", null, async delegate { await UpdateQuote(); });
            menu.Items.Add("恢复默认位置", null, delegate { savedOffset = null; savedVerticalOffset = null; SavePosition(); PositionOnTaskbar(); });
            var configuration = new ToolStripMenuItem("配置");
            var autoStart = new ToolStripMenuItem("开机自启");
            autoStart.Checked = StartupSettings.Enabled;
            autoStart.Click += delegate
            {
                try
                {
                    StartupSettings.SetEnabled(!autoStart.Checked);
                    autoStart.Checked = StartupSettings.Enabled;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法保存开机自启设置：" + ex.Message, "浙商金价", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            configuration.DropDownOpening += delegate { autoStart.Checked = StartupSettings.Enabled; };
            configuration.DropDownItems.Add(autoStart);
            menu.Items.Add(configuration);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.Icon = SystemIcons.Information;
            tray.Text = "浙商金价 · 正在获取";
            tray.ContextMenuStrip = menu;
            // 仅显示主窗口时显示托盘；隐藏集成验证不产生界面。
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleDetails(); };
            MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !moved) ToggleDetails();
            };
            MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = true; moved = false; dragStart = MousePosition; windowStart = Native.Bounds(Handle).Location;
                Capture = true;
            };
            MouseMove += delegate
            {
                if (!dragging) return;
                Point mouse = MousePosition;
                int dx = mouse.X - dragStart.X, dy = mouse.Y - dragStart.Y;
                if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) moved = true;
                if (moved) { MoveOnTaskbar(windowStart.X + dx, windowStart.Y + dy); details.Hide(); }
            };
            MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || !dragging) return;
                dragging = false; Capture = false;
                if (moved)
                {
                    Rectangle taskbar = Native.Bounds(Native.FindWindow("Shell_TrayWnd", null));
                    if (taskbar.IsEmpty) return;
                    Rectangle bounds = Native.Bounds(Handle);
                    savedOffset = bounds.Left - taskbar.Left;
                    savedVerticalOffset = bounds.Top - taskbar.Top;
                    SavePosition(); PositionOnTaskbar();
                }
            };
            refresh.Interval = cadence.Interval;
            refresh.Tick += async delegate { await UpdateQuote(); };
            interaction.Interval = 100;
            interaction.Tick += delegate { TickInteraction(); };
            Shown += async delegate { tray.Visible = true; PositionOnTaskbar(); interaction.Start(); refresh.Start(); await UpdateQuote(); };
            Diagnostics.Write("启动，正常15秒查询，连续5次价格及行情时间相同后改为5分钟查询");
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x00080000; return p; }
        }
        void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(positionFile));
                File.WriteAllText(positionFile, savedOffset.HasValue ? savedOffset.Value.ToString() +
                    (savedVerticalOffset.HasValue ? "," + savedVerticalOffset.Value.ToString() : "") : "");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void PositionOnTaskbar()
        {
            if (dragging) return;
            IntPtr hwnd = Native.FindWindow("Shell_TrayWnd", null);
            Rectangle bar = Native.Bounds(hwnd);
            if (bar.IsEmpty) { HideFor("未找到任务栏"); return; }
            if (bar.Width < Width || bar.Height < Height) { HideFor("任务栏空间不足以容纳价格条"); return; }
            Rectangle screen = Screen.FromRectangle(bar).Bounds;
            // 自动隐藏时任务栏仅剩几像素，不在屏幕上留下孤立数字。
            if (bar.Bottom <= screen.Top + 3 || bar.Top >= screen.Bottom - 3 ||
                bar.Right <= screen.Left + 3 || bar.Left >= screen.Right - 3)
            { HideFor("任务栏自动隐藏"); return; }
            if (embeddedTaskbar != hwnd || !IsHandleCreated || Native.GetParent(Handle) != hwnd)
            {
                try
                {
                    Native.MatchTaskbarDpi();
                    Native.EmbedInTaskbar(Handle, hwnd);
                    embeddedTaskbar = hwnd;
                    drawnPrice = null;
                    Diagnostics.Write("价格窗口已嵌入任务栏，独立置顶已关闭");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    HideFor("嵌入失败，错误码=" + ex.NativeErrorCode);
                    return;
                }
            }
            Rectangle notify = Native.Bounds(Native.FindWindowEx(hwnd, IntPtr.Zero, "TrayNotifyWnd", null));
            int x = savedOffset.HasValue ? bar.Left + savedOffset.Value : notify.IsEmpty ? bar.Right - Width - 240 : notify.Left - Width - 10;
            int y = bar.Top + (savedVerticalOffset.HasValue ? savedVerticalOffset.Value : (bar.Height - Height) / 2);
            MoveOnTaskbar(x, y);
            if (!Visible) Show();
            SetVisibilityReason("任务栏价格条可见");
        }
        void MoveOnTaskbar(int screenX, int screenY)
        {
            Rectangle bar = Native.Bounds(embeddedTaskbar);
            if (bar.IsEmpty) return;
            if (bar.Width < Width || bar.Height < Height) { HideFor("任务栏空间不足以容纳价格条"); return; }
            screenX = Math.Max(bar.Left, Math.Min(screenX, bar.Right - Width));
            screenY = Math.Max(bar.Top, Math.Min(screenY, bar.Bottom - Height));
            Rectangle current = Native.Bounds(Handle);
            if (current.Location == new Point(screenX, screenY) && current.Size == Size) return;
            Native.SetWindowPos(Handle, IntPtr.Zero, screenX - bar.Left, screenY - bar.Top, Width, Height, 0x0010 | 0x0004);
        }
        void SetVisibilityReason(string reason)
        {
            if (reason == visibilityReason) return;
            visibilityReason = reason;
            Diagnostics.Write(reason);
        }
        void HideFor(string reason) { Hide(); details.Hide(); SetVisibilityReason(reason); }
        async Task UpdateQuote()
        {
            if (busy) return;
            busy = true;
            lastAttempt = DateTimeOffset.Now;
            int previousInterval = cadence.Interval;
            try
            {
                quote = await Feed.Fetch(); failed = false; lastSuccess = DateTimeOffset.Now;
                cadence.Observe(quote.Price, quote.Time);
                Diagnostics.Write("查询成功，价格=" + quote.Price.ToString("F2") + "，行情时间=" + quote.Time.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            }
            catch (Exception ex) { failed = true; cadence.Failed(); Diagnostics.Write("查询失败，类型=" + ex.GetType().Name); }
            finally { busy = false; }
            if (IsDisposed) return;
            if (previousInterval != cadence.Interval)
            {
                refresh.Stop(); refresh.Interval = cadence.Interval; refresh.Start();
                Diagnostics.Write(cadence.IsSlow ? "连续5次价格及行情时间相同，切换为5分钟查询" : "价格或行情时间变化，恢复15秒查询");
            }
            AccessibleName = quote == null ? "浙商金价，暂无报价" : "浙商金价 " + quote.Price.ToString("F2") + " 元每克";
            tray.Text = quote == null ? "浙商金价 · 更新失败" : "浙商 " + quote.Price.ToString("F2") + " | " +
                (failed ? "更新失败" : cadence.IsSlow ? "每5分钟查询" : quote.IsOld ? "行情未更新" : quote.Percent);
            details.Quote = quote; details.Failed = failed;
            details.SlowQuery = cadence.IsSlow;
            details.LastAttempt = lastAttempt; details.LastSuccess = lastSuccess;
            InvalidatePriceIfChanged(); details.Invalidate();
        }
        void ShowDetails()
        {
            details.Quote = quote; details.Failed = failed;
            details.SlowQuery = cadence.IsSlow;
            details.LastAttempt = lastAttempt; details.LastSuccess = lastSuccess;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Rectangle tickerBounds = Native.Bounds(Handle);
            int y = tickerBounds.Top - details.Height - 8;
            if (y < area.Top) y = tickerBounds.Bottom + 8;
            details.Location = new Point(Math.Max(area.Left, Math.Min(tickerBounds.Right - details.Width, area.Right - details.Width)),
                Math.Max(area.Top, Math.Min(y, area.Bottom - details.Height)));
            details.Show(); details.Invalidate();
        }
        void ToggleDetails()
        {
            if (details.Visible && details.Pinned) { details.Pinned = false; details.Hide(); }
            else { details.Pinned = true; ShowDetails(); }
        }
        void OnGlobalMouseDown(Point position)
        {
            if (IsDisposed || !IsHandleCreated || !details.Visible || menu.Visible ||
                Native.Bounds(Handle).Contains(position) || Native.Bounds(details.Handle).Contains(position)) return;
            // 先返回鼠标钩子，再由界面线程收起详情；不拦截或记录其他应用的点击。
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed || details.IsDisposed) return;
                details.Pinned = false;
                details.Hide();
            }));
        }
        void TickInteraction()
        {
            if (DateTime.UtcNow - lastPlacement >= TimeSpan.FromMilliseconds(500))
            { PositionOnTaskbar(); lastPlacement = DateTime.UtcNow; }
            if (details.Visible && Native.GetAsyncKeyState(27) < 0) { details.Pinned = false; details.Hide(); }
            InvalidatePriceIfChanged();
        }
        void InvalidatePriceIfChanged()
        {
            string price = quote == null ? "--.--" : quote.Price.ToString("F2");
            if (!IsHandleCreated || embeddedTaskbar == IntPtr.Zero ||
                (drawnPrice == price && drawnSlowQuery == cadence.IsSlow)) return;
            try
            {
                Native.RenderTicker(Handle, ClientSize, quote, cadence.IsSlow);
                drawnPrice = price;
                drawnSlowQuery = cadence.IsSlow;
                Diagnostics.Write("价格图像提交成功，价格=" + price);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                SetVisibilityReason("价格图像提交失败，错误码=" + ex.NativeErrorCode);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // 像素已通过 UpdateLayeredWindow 提交，不再向颜色键窗口绘制。
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Diagnostics.Write("程序退出");
                outsideClicks.Dispose();
                refresh.Dispose(); interaction.Dispose(); details.Dispose();
                tray.Visible = false; tray.Dispose(); menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        [STAThread] static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                var error = e.ExceptionObject as Exception;
                Diagnostics.Write("未处理异常，类型=" + (error == null ? "未知" : error.GetType().Name) +
                    "，即将退出=" + e.IsTerminating + (error == null ? "" : "，调用位置=" + error.StackTrace));
            };
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Native.MatchTaskbarDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--verify")
            {
                Directory.CreateDirectory(args[1]);
                string reportPath = Path.Combine(args[1], "verification.txt");
                try
                {
                    Quote q = Feed.Fetch().GetAwaiter().GetResult();
                    File.WriteAllText(reportPath, "真实接口读取及字段校验通过\r\n当前价格：" + q.Price.ToString("F2") +
                        "\r\n昨收价：" + q.Previous.ToString("F2") + "\r\n涨跌额：" + q.Change + "\r\n涨跌幅：" + q.Percent +
                        "\r\n行情时间：" + q.Time.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\r\n行情超过三分钟：" + q.IsOld +
                        "\r\n界面缩放：" + Theme.Scale + "\r\n价格条尺寸：" + Theme.Scaled(92, 32) +
                        "\r\n详情尺寸：" + Theme.Scaled(300, 278) +
                        "\r\n验证时间：" + DateTimeOffset.Now.ToString("o"), new System.Text.UTF8Encoding(false));
                    using (var bitmap = new Bitmap(Theme.Scaled(300, 278).Width, Theme.Scaled(300, 278).Height))
                    using (var g = Graphics.FromImage(bitmap))
                    { Details.Draw(g, bitmap.Size, q, false, DateTimeOffset.Now, DateTimeOffset.Now, false); bitmap.Save(Path.Combine(args[1], "details.png"));
                      Details.Draw(g, bitmap.Size, q, false, DateTimeOffset.Now, DateTimeOffset.Now, true); bitmap.Save(Path.Combine(args[1], "details-slow.png")); }
                    using (var bitmap = new Bitmap(Theme.Scaled(92, 32).Width, Theme.Scaled(92, 32).Height))
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        Theme.DrawTicker(g, bitmap.Size, q, false);
                        bitmap.MakeTransparent(Theme.Transparent);
                        bitmap.Save(Path.Combine(args[1], "ticker.png"));
                    }
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(reportPath, "验证失败：" + ex.Message); return 1; }
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\ZheshangGoldTicker", out created))
            {
                if (!created) return 0;
                // 嵌入窗口随 Explorer 销毁句柄时保持消息循环，由定时器重新创建并嵌入。
                using (var ticker = new Ticker())
                {
                    ticker.FormClosed += delegate { Application.ExitThread(); };
                    ticker.Show();
                    Application.Run();
                }
            }
            return 0;
        }
    }
}
