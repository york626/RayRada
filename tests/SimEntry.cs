// v4.11/v4.12 自测装置：直接调用 Ray雷达 里的「DSH 手机入口」相关函数。
// 不显示浮窗、不碰温度传感器、**不改用户设置文件**（只读 Settings.Load()，改动只在内存里）。
//
// 编译（与 build.ps1 同一套引用；/codepage:65001 保证中文字面量不乱码）：
//   csc /nologo /target:exe /main:SimEntry /codepage:65001 /out:sim-entry.exe ^
//       /reference:C:\Tool\RayRadar\lib\LibreHardwareMonitorLib.dll /reference:System.dll ^
//       /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
//       C:\Tool\RayRadar\RayRadar.cs C:\Tool\RayRadar\tests\SimEntry.cs
//
// 运行：
//   sim-entry.exe            → 全部（开/关入口 + 白名单匹配），**需要管理员**（改 netsh portproxy）
//   sim-entry.exe scan       → 只跑白名单匹配（入口已经是开的就行，**不需要管理员**）

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RayRadar;   // LanSentinel / Settings / SentinelHit 都在 namespace RayRadar 里

static class SimEntry
{
    static StreamWriter w;

    static void Say(string s)
    {
        Console.WriteLine(s);
        try { if (w != null) { w.WriteLine(s); w.Flush(); } } catch { }
    }

    // 连一次入口 → 扫描（doBlock=false ⇒ 只观察，绝不阻断）；返回「本机那条连接」是否被判为陌生设备
    static int ScanOwn(Settings st, string ip, string label)
    {
        System.Net.Sockets.TcpClient c = null;
        try
        {
            c = new System.Net.Sockets.TcpClient();
            c.Connect(ip, 3081);
            System.Threading.Thread.Sleep(600);
            List<SentinelHit> hits = LanSentinel.Scan(st, false);
            int mine = 0;
            foreach (SentinelHit h in hits)
            {
                Say("       命中：" + h.Ip + "  原因=" + h.Why);
                if (h.Ip == ip) mine++;
            }
            Say("    " + label + " → 总命中 " + hits.Count + " 个，其中「本机 " + ip + "」" + mine + " 个");
            return mine;
        }
        catch (Exception ex) { Say("    " + label + " → 连接/扫描异常：" + ex.Message); return -1; }
        finally { if (c != null) { try { c.Close(); } catch { } } }
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool scanOnly = (args.Length > 0 && args[0] == "scan");
        string logPath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "sim-entry.log");
        try { w = new StreamWriter(logPath, scanOnly, new UTF8Encoding(false)); } catch { }

        Say("=== Ray雷达「DSH 手机入口」自测" + (scanOnly ? "（只跑白名单匹配）" : "") + " ===");
        Say("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Say("管理员：" + (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否"));
        string ip = LanSentinel.LanIp();
        Say("探测到的内网 IP：" + ip);
        Say("当前状态（读 netsh）：" + LanSentinel.StateText());
        Say("");

        if (!scanOnly)
        {
            Say("--- 1) 关闭手机入口 ---");
            Say("  " + LanSentinel.CloseEntry());
            Say("  关闭后状态：" + LanSentinel.StateText());
            Say("");
            Say("--- 2) 重开手机入口 ---");
            Say("  " + LanSentinel.OpenEntry());
            Say("  重开后状态：" + LanSentinel.StateText());
            Say("");
            Say("--- 3) 再关一次（幂等检查）---");
            Say("  " + LanSentinel.CloseEntry());
            Say("  状态：" + LanSentinel.StateText());
            Say("");
            Say("--- 4) 恢复为开启（最终留给用户的状态）---");
            Say("  " + LanSentinel.OpenEntry());
            Say("最终状态：" + LanSentinel.StateText());
            Say("");
        }

        Say("--- 白名单匹配自测（doBlock=false，只观察不阻断）---");
        Say("  说明：只看「本机自己那条连接」是否被判为陌生设备；同时段手机若也连着，会一并列出来。");
        Settings st = Settings.Load();          // 只读；下面只改内存副本，不 Save
        st.Sentinel = true;
        st.SentinelSelf = false;                // 关掉「放行本机自测」，让本机连接也走白名单判断
        string myMac = LanSentinel.MacOf(ip);
        Say("  本机 MAC = " + (myMac.Length > 0 ? myMac : "(取不到)"));

        st.SentinelWhitelist = myMac;
        int a = ScanOwn(st, ip, "A) 白名单只放「本机 MAC」");
        st.SentinelWhitelist = ip;
        int b = ScanOwn(st, ip, "B) 白名单只放「本机 IP」 ");
        st.SentinelWhitelist = "";
        int c = ScanOwn(st, ip, "C) 白名单清空          ");

        Say("");
        Say("  期望：A=0（MAC 命中）  B=0（IP 命中）  C=1（陌生设备被识别）");
        Say("  实际：A=" + a + "  B=" + b + "  C=" + c);
        Say("  结论：" + ((a == 0 && b == 0 && c >= 1) ? "白名单（IP 与 MAC）匹配逻辑正常 ✓" : "⚠ 与期望不符，需要排查"));
        Say("");
        Say("注：C 会在 sentinel.log 留一行「陌生设备连接入口：本机」—— 那是本次自测，不是真的有人连进来。");
        Say("=== 结束 ===");
        try { if (w != null) { w.Close(); w = null; } } catch { }
    }
}
