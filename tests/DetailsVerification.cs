using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
namespace GoldTicker
{
    static class DetailsVerification
    {
        [STAThread] static int Main(string[] args)
        {
            try
            {
                Native.MatchTaskbarDpi();
                using (var details = new Details())
                {
                    if (details.StartPosition != FormStartPosition.Manual) throw new Exception("First-show placement is not manual");
                    Point target = new Point(300, 300);
                    details.Location = target;
                    if (Native.Bounds(details.Handle).Location != target) throw new Exception("First handle has wrong location");
                }
                using (var clicks = new OutsideClickMonitor(delegate(Point point) { }))
                {
                    for (int i=0; i<3; i++)
                    {
                        clicks.Start();
                        if (!clicks.IsRunning) throw new Exception("Hook not installed");
                        clicks.Stop();
                        if (clicks.IsRunning) throw new Exception("Hook not removed");
                    }
                }
                File.WriteAllText(args[0], "PASS: first hidden details handle at requested manual position; real mouse-hook install/remove lifecycle (3 times).\r\nNot tested: visible first double-click, actual outside clicks; no input simulated.");
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(args[0], "FAIL: " + ex); return 1; }
        }
    }
}
