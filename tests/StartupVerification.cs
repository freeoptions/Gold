using System;
using System.IO;
using Microsoft.Win32;

namespace GoldTicker
{
    // 验证真实的当前用户启动项写入/删除，结束时恢复原值，不启动界面。
    static class StartupVerification
    {
        [STAThread] static int Main(string[] args)
        {
            object original;
            RegistryValueKind originalKind = RegistryValueKind.String;
            using (var key = Registry.CurrentUser.OpenSubKey(StartupSettings.RunKey))
            {
                original = key == null ? null : key.GetValue(StartupSettings.ValueName);
                if (original != null) originalKind = key.GetValueKind(StartupSettings.ValueName);
            }
            int result = 0;
            string report;
            try
            {
                StartupSettings.SetEnabled(true);
                if (!StartupSettings.Enabled) throw new InvalidOperationException("开启后未读取到正确启动命令");
                StartupSettings.SetEnabled(false);
                using (var key = Registry.CurrentUser.OpenSubKey(StartupSettings.RunKey))
                    if (key != null && key.GetValue(StartupSettings.ValueName) != null)
                        throw new InvalidOperationException("取消后启动项仍然存在");
                report = "开机自启注册表集成验证通过：开启写入带引号的程序路径，关闭删除启动项。";
            }
            catch (Exception ex) { result = 1; report = "验证失败：" + ex.Message; }
            finally
            {
                using (var key = Registry.CurrentUser.CreateSubKey(StartupSettings.RunKey))
                {
                    if (original == null) key.DeleteValue(StartupSettings.ValueName, false);
                    else key.SetValue(StartupSettings.ValueName, original, originalKind);
                }
            }
            using (var key = Registry.CurrentUser.OpenSubKey(StartupSettings.RunKey))
                if (!Object.Equals(original, key == null ? null : key.GetValue(StartupSettings.ValueName)))
                { result = 1; report += "\r\n原启动项恢复校验失败。"; }
                else report += "\r\n原启动项已恢复，未自动开启自启。";
            report += "\r\n未验证：右键菜单交互、重新登录 Windows 后实际启动。";
            File.WriteAllText(args[0], report, new System.Text.UTF8Encoding(false));
            return result;
        }
    }
}
