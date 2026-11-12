# Read-only check of an already running price window. Does not launch apps or simulate input.
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GoldTaskbarHitVerification
{
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    struct Point { public int X,Y; public Point(int x,int y) { X=x;Y=y; } }
    struct Rect { public int Left,Top,Right,Bottom; }
    public static string Check()
    {
        IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            IntPtr price = FindWindowEx(bar, IntPtr.Zero, null, "浙商金价");
            if (price == IntPtr.Zero || !IsWindowVisible(price)) throw new Exception("Embedded price is not visible/running");
            if ((GetWindowLong(price,-20) & 8) != 0) throw new Exception("Price is independently topmost");
            Rect r; GetWindowRect(price, out r);
            var points = new [] {
                new Point(r.Left+8,r.Top+8), new Point(r.Right-8,r.Top+8),
                new Point(r.Left+8,r.Bottom-8), new Point(r.Right-8,r.Bottom-8),
                new Point((r.Left+r.Right)/2,(r.Top+r.Bottom)/2)
            };
            int hit = 0;
            foreach (Point point in points) if (WindowFromPoint(point) == price) hit++;
            if (hit != points.Length) throw new Exception("Mouse hit area incomplete: " + hit + "/" + points.Length);
            return "PASS: visible taskbar child; no independent topmost; all 5 mouse hit points belong to gold (including blank corners).";
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }
}
'@
[GoldTaskbarHitVerification]::Check()
