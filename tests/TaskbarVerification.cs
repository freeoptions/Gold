using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace GoldTicker
{
    static class TaskbarVerification
    {

        [STAThread] static int Main(string[] args)
        {
            try
            {
                Native.MatchTaskbarDpi();
                IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
                if (bar == IntPtr.Zero) throw new Exception("Taskbar not found");
                Quote quote = Feed.Fetch().GetAwaiter().GetResult();
                // No Show(), no tray icon, no mouse or keyboard simulation.
                for (int i = 0; i < 3; i++)
                {
                    var window = new Ticker();
                    bool closed = false;
                    window.FormClosed += delegate { closed = true; };
                    try
                    {
                        Native.EmbedInTaskbar(window.Handle, bar);
                        if (Native.GetParent(window.Handle) != bar) throw new Exception("Wrong parent");
                        if (!Native.DestroyWindow(window.Handle)) throw new Exception("Could not destroy child handle");
                        if (closed || window.IsDisposed) throw new Exception("Ticker disposed after parent-like handle loss");
                        Native.EmbedInTaskbar(window.Handle, bar);
                        int style = Native.GetWindowLong(window.Handle, -16);
                        if ((style & 0x40000000) == 0 || (style & unchecked((int)0x80000000)) != 0)
                            throw new Exception("Wrong child style");
                        if ((Native.GetWindowLong(window.Handle, -20) & 8) != 0)
                            throw new Exception("Still independently topmost");
                        Native.RenderTicker(window.Handle, window.ClientSize, quote, false);
                        Native.RenderTicker(window.Handle, window.ClientSize, quote, false);
                        typeof(Ticker).GetField("embeddedTaskbar", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, bar);
                        MethodInfo move = typeof(Ticker).GetMethod("MoveOnTaskbar", BindingFlags.Instance | BindingFlags.NonPublic);
                        Rectangle taskbar = Native.Bounds(bar);
                        if (taskbar.Width < window.Width || taskbar.Height <= window.Height)
                            throw new Exception("Taskbar has no vertical room for movement verification");
                        foreach (Point target in new[] {
                            new Point(taskbar.Left - 1000, taskbar.Top - 1000),
                            new Point(taskbar.Right + 1000, taskbar.Top - 1000),
                            new Point(taskbar.Left - 1000, taskbar.Bottom + 1000),
                            new Point(taskbar.Right + 1000, taskbar.Bottom + 1000),
                            new Point(taskbar.Left + 20, taskbar.Top),
                            new Point(taskbar.Left + 20, taskbar.Bottom - window.Height) })
                        {
                            move.Invoke(window, new object[] { target.X, target.Y });
                            Rectangle actual = Native.Bounds(window.Handle);
                            Point expected = new Point(
                                Math.Max(taskbar.Left, Math.Min(target.X, taskbar.Right - window.Width)),
                                Math.Max(taskbar.Top, Math.Min(target.Y, taskbar.Bottom - window.Height)));
                            if (!taskbar.Contains(actual) || actual.Location != expected)
                                throw new Exception("Price escaped taskbar or vertical movement failed: " + actual);
                        }
                        string positionPath = Path.Combine(Path.GetDirectoryName(args[0]), "test-position.txt");
                        try
                        {
                            typeof(Ticker).GetField("positionFile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, positionPath);
                            typeof(Ticker).GetField("savedOffset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, (int?)20);
                            typeof(Ticker).GetField("savedVerticalOffset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, (int?)3);
                            MethodInfo save = typeof(Ticker).GetMethod("SavePosition", BindingFlags.Instance | BindingFlags.NonPublic);
                            save.Invoke(window, null);
                            if (File.ReadAllText(positionPath) != "20,3") throw new Exception("Two-axis position not saved");
                            typeof(Ticker).GetField("savedOffset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, null);
                            typeof(Ticker).GetField("savedVerticalOffset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, null);
                            save.Invoke(window, null);
                            if (File.ReadAllText(positionPath) != "") throw new Exception("Default position not cleared");
                        }
                        finally { if (File.Exists(positionPath)) File.Delete(positionPath); }
                        Native.SetWindowPos(window.Handle, IntPtr.Zero, 20, 4, 184, 64, 0x0010 | 0x0004);
                        Rectangle bounds = Native.Bounds(window.Handle), parent = Native.Bounds(bar);
                        if (Math.Abs(bounds.Left - parent.Left - 20) > 1 || Math.Abs(bounds.Top - parent.Top - 4) > 1)
                            throw new Exception("Wrong taskbar coordinates");
                        if ((Native.GetWindowLong(window.Handle, -16) & 0x10000000) != 0)
                            throw new Exception("Test window unexpectedly visible");
                    }
                    finally { window.Dispose(); }
                }
                File.WriteAllText(args[0], "PASS: real taskbar parent; WS_CHILD without WS_POPUP; no independent topmost; per-pixel alpha upload (twice per child); local coordinates; four-edge clamping; vertical-only movement; two-axis position save/reset; 3 hidden window recreations.\r\nNot tested: visible flicker, mouse interactions, actual Explorer restart.");
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(args[0], "FAIL: " + ex); return 1; }
        }
    }
}
