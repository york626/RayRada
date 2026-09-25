// SimEtw.cs -- 「按应用流量统计」的采集自测装置（v4.17）
//
// 验什么：ETW 实时会话能不能真的拿到「进程 → 收发字节」，以及和网卡总量对不对得上。
// **要管理员**：起 ETW 会话必须提权（程序本体 manifest 已是 requireAdministrator；手动跑请用管理员窗口）。
// 不动用户数据：数据目录被指到 %TEMP%\rayradar-etw-probe-data（RAYRADAR_APPTRAFFIC_DIR）。
//
// 用法：
//   sim-etw.exe            跑 20 秒
//   sim-etw.exe 40         跑 40 秒
// 说明：跑的时候请**自己造一点流量**（下载/看视频），否则排行是空的。
//       日志逐行落盘到 tests\etw-probe.log（崩溃也留痕），跑完即删。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RayRadar
{
    public static class SimEtw
    {
        static string logPath = @"C:\Tool\RayRadar\tests\etw-probe.log";
        static readonly StringBuilder log = new StringBuilder();
        static readonly object sync = new object();

        static void W(string s)
        {
            lock (sync)
            {
                log.AppendLine(s);
                try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
                Console.WriteLine(s);
            }
        }

        public static int Main(string[] args)
        {
            int seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
            if (args.Length > 1) logPath = args[1];
            try { File.Delete(logPath); } catch { }

            W("=== Ray雷达 · 按应用流量统计自测 (SimEtw) ===");
            W("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "　管理员：" + IsAdmin() + "　64 位：" + (IntPtr.Size == 8));

            string dataDir = Path.Combine(Path.GetTempPath(), "rayradar-etw-probe-data");
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); } catch { }
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("RAYRADAR_APPTRAFFIC_DIR", dataDir);
            W("数据目录：" + dataDir + "（不影响真实数据）");

            long rx0, tx0, rx1, tx1;
            Nic(out rx0, out tx0);

            NetMonitor.DebugCapture = true;
            bool ok = NetMonitor.Start();
            W("NetMonitor.Start() → " + ok + "　LastError=" + (NetMonitor.LastError.Length == 0 ? "(无)" : NetMonitor.LastError));
            W("布局偏移：LoggerName=" + NetMonitor.OffLoggerName + " ProcessTraceMode=" + NetMonitor.OffProcessTraceMode +
              " 缓冲回调=" + NetMonitor.OffBufferCallback + " 记录回调=" + NetMonitor.OffCallback +
              " LOGFILE=" + NetMonitor.SizeLogfile + "（x64 期望 8/28/400/424/448）");
            if (!ok)
            {
                W("采集没起来，分步进度：");
                foreach (string s in NetMonitor.TraceLog) W("  " + s);
                return 2;
            }

            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                if (i % 5 == 4) NetMonitor.Tick();          // 模拟界面定时器：每 5 秒搬一次
                W("  t=" + (i + 1) + "s 回调 " + NetMonitor.CallbackCount + " / 缓冲回调 " + NetMonitor.BufferCallbackCount +
                  " / 网络事件 " + NetMonitor.EventCount + " / 字节 " + Sz(NetMonitor.ByteCount) +
                  " / 运行中=" + NetMonitor.Running);
            }
            NetMonitor.Flush();
            Nic(out rx1, out tx1);

            long appRx = 0, appTx = 0;
            List<KeyValuePair<string, long[]>> rank = AppTraffic.Rank(1);
            foreach (KeyValuePair<string, long[]> kv in rank) { appRx += kv.Value[0]; appTx += kv.Value[1]; }

            W("");
            W("=== 按事件号分项（10/11=TCPv4 发/收，42/43=UDPv4 发/收，26/27=TCPv6，58/59=UDPv6）===");
            foreach (KeyValuePair<int, long[]> kv in NetMonitor.ById)
                W("  id=" + kv.Key.ToString().PadLeft(2) + "  次数 " + kv.Value[0].ToString().PadLeft(7) + "  字节 " + Sz(kv.Value[1]).PadLeft(10));
            W("丢弃：长度不够 " + NetMonitor.DropLen + " / 回环 " + NetMonitor.DropLoop +
              " / PID<=0 " + NetMonitor.DropPid + " / size<=0 " + NetMonitor.DropSize);

            W("");
            W("=== 对账（本窗口 " + seconds + " 秒）===");
            W("网卡计数器增量：收 " + Sz(rx1 - rx0) + " / 发 " + Sz(tx1 - tx0));
            W("按应用合计：    收 " + Sz(appRx) + " / 发 " + Sz(appTx) + "　（" + rank.Count + " 个进程名）");
            W("注意：两者不会相等 —— ETW 是 IP 层、网卡是链路层；且纯 ACK 不产生 ETW 事件（下载时网卡「发送」远大于按应用发送，就是这个原因）");
            W("进程名解析失败 " + NetMonitor.NameErrors.Count + " 条：");
            foreach (string s in NetMonitor.NameErrors) W("  " + s);
            W("=== 每个 PID 首次出现的时间与解析结果 ===");
            foreach (string s in NetMonitor.NameTrace) W("  " + s);

            W("");
            W("=== 按应用排行 ===");
            int n = 0;
            foreach (KeyValuePair<string, long[]> kv in rank)
            {
                if (n++ >= 15) break;
                W("  " + kv.Key.PadRight(22) + " 收 " + Sz(kv.Value[0]).PadLeft(9) + "　发 " + Sz(kv.Value[1]).PadLeft(9));
            }

            W("");
            W("=== 前 12 条原始事件（核对载荷 PID/字节数）===");
            foreach (string s in NetMonitor.DebugLines) W("  " + s);

            NetMonitor.Stop();
            AppTraffic.Save();
            W("");
            W("结束。落盘文件：" + Directory.GetFiles(dataDir).Length + " 个；日志=" + logPath);
            return 0;
        }

        static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
        static string Sz(long b)
        {
            if (b >= 1073741824) return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + "GB";
            if (b >= 1048576) return (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
            if (b >= 1024) return (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + "KB";
            return b + "B";
        }
        /** 与主程序同一套网卡口径（排除回环与虚拟过滤网卡） */
        static void Nic(out long rx, out long tx)
        {
            rx = 0; tx = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string d = ni.Description;
                    if (d.IndexOf("WFP", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (d.IndexOf("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (d.IndexOf("LightWeight Filter", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try { IPv4InterfaceStatistics s4 = ni.GetIPv4Statistics(); rx += s4.BytesReceived; tx += s4.BytesSent; } catch { }
                }
            }
            catch { }
        }
    }
}