// SimApp.cs -- 「按应用流量统计」的自测装置（v4.17）
//
// 验什么：AppTraffic 的聚合 / 排行 / 逐日序列 / 落盘与重载 / 裁剪，全部用**合成数据**，不碰真实采集。
//   · 数据目录被指到 %TEMP%\rayradar-simapp（RAYRADAR_APPTRAFFIC_DIR），真实数据不受影响
//   · 不需要管理员、不起 ETW、不显示窗口
// 用法： sim-app.exe
using System;
using System.Collections.Generic;
using System.IO;

namespace RayRadar
{
    public static class SimApp
    {
        static int pass = 0, fail = 0;
        static void Chk(string name, bool ok) { Chk(name, ok, null); }
        static void Chk(string name, bool ok, string extra)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (extra != null ? "   [" + extra + "]" : ""));
            if (ok) pass++; else fail++;
        }

        public static int Main(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "rayradar-simapp");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("RAYRADAR_APPTRAFFIC_DIR", dir);
            Console.WriteLine("=== AppTraffic 自测（数据目录 " + dir + "）===\n");

            string today = DateTime.Today.ToString("yyyy-MM-dd");
            string y1 = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            string y40 = DateTime.Today.AddDays(-40).ToString("yyyy-MM-dd");

            Console.WriteLine("① 累加与合并");
            Dictionary<string, long[]> b1 = new Dictionary<string, long[]>();
            b1["chrome"] = new long[] { 1000, 100 };
            b1["Steam"] = new long[] { 500, 50 };
            AppTraffic.Add(today, b1);
            Dictionary<string, long[]> b2 = new Dictionary<string, long[]>();
            b2["chrome"] = new long[] { 2000, 200 };      // 同一应用第二次 ⇒ 应相加
            b2["node"] = new long[] { 30, 3 };
            AppTraffic.Add(today, b2);
            long[] got = AppTraffic.DayApp(today, "chrome");
            Chk("同一天同一应用会累加（1000+2000）", got[0] == 3000 && got[1] == 300, got[0] + "/" + got[1]);
            Chk("另一个应用单独一行", AppTraffic.DayApp(today, "Steam")[0] == 500);
            Chk("DayTotal 是当天所有应用之和", Sum(today) == 3000 + 300 + 500 + 50 + 30 + 3, Sum(today).ToString());

            Console.WriteLine("\n② 落盘 + 重载（模拟重启）");
            AppTraffic.Save();
            string file = Path.Combine(dir, today + ".dat");
            Chk("按天一个文件，文件名是日期", File.Exists(file), Path.GetFileName(file));
            int lines = File.ReadAllLines(file).Length;
            Chk("文件里有 3 行（chrome/Steam/node）", lines == 3, lines + " 行");
            AppTraffic.Load();                            // 重新读一遍
            Chk("重载后数据不丢", AppTraffic.DayApp(today, "chrome")[0] == 3000);

            Console.WriteLine("\n③ 排行（降序）与 Top-N 合并");
            Dictionary<string, long[]> b3 = new Dictionary<string, long[]>();
            b3["msedge"] = new long[] { 9000, 900 };
            AppTraffic.Add(today, b3);
            AppTraffic.Save();
            List<KeyValuePair<string, long[]>> rank = AppTraffic.Rank(1);
            Chk("排行第一是 msedge（9900）", rank.Count > 0 && rank[0].Key == "msedge", rank.Count > 0 ? rank[0].Key + " " + (rank[0].Value[0] + rank[0].Value[1]) : "空");
            Chk("按合计降序", rank.Count >= 2 && (rank[0].Value[0] + rank[0].Value[1]) >= (rank[1].Value[0] + rank[1].Value[1]));
            List<KeyValuePair<string, long[]>> top2 = AppTraffic.RankTop(1, 2);
            Chk("RankTop(2) ⇒ 2 条应用 + 1 条「其他」", top2.Count == 3 && top2[2].Key.StartsWith("其他"), top2.Count + " 条：" + top2[top2.Count - 1].Key);

            Console.WriteLine("\n④ 时间窗过滤（昨天/40 天前）");
            Dictionary<string, long[]> b4 = new Dictionary<string, long[]>();
            b4["chrome"] = new long[] { 777, 77 };
            AppTraffic.Add(y1, b4);
            Dictionary<string, long[]> b5 = new Dictionary<string, long[]>();
            b5["chrome"] = new long[] { 555, 55 };
            AppTraffic.Add(y40, b5);
            AppTraffic.Save();
            long rx1 = 0, tx1 = 0;
            foreach (KeyValuePair<string, long[]> kv in AppTraffic.Rank(1)) if (kv.Key == "chrome") { rx1 = kv.Value[0]; tx1 = kv.Value[1]; }
            Chk("「今天」不含昨天（3000 而不是 3777）", rx1 == 3000 && tx1 == 300, rx1 + "/" + tx1);
            long rx30 = 0;
            foreach (KeyValuePair<string, long[]> kv in AppTraffic.Rank(30)) if (kv.Key == "chrome") rx30 = kv.Value[0];
            Chk("「最近 30 天」含昨天不含 40 天前（3777）", rx30 == 3777, rx30.ToString());
            long rx365 = 0;
            foreach (KeyValuePair<string, long[]> kv in AppTraffic.Rank(365)) if (kv.Key == "chrome") rx365 = kv.Value[0];
            Chk("「最近 1 年」含 40 天前（4332）", rx365 == 4332, rx365.ToString());

            Console.WriteLine("\n⑤ 逐日序列（缺的日子补 0，升序）");
            List<KeyValuePair<string, long[]>> ser = AppTraffic.AppSeries("chrome", 5);
            Chk("返回 5 天", ser.Count == 5, ser.Count.ToString());
            Chk("最后一天是今天且 3000", ser[4].Key == today && ser[4].Value[0] == 3000, ser[4].Key + " " + ser[4].Value[0]);
            Chk("昨天有数据（777）", ser[3].Value[0] == 777, ser[3].Key + " " + ser[3].Value[0]);
            Chk("中间没记录的日子补 0", ser[1].Value[0] == 0, ser[1].Key + " " + ser[1].Value[0]);

            Console.WriteLine("\n⑥ 原子写（不留 .tmp）与保留策略");
            Chk("没有残留 .tmp 文件", Directory.GetFiles(dir, "*.tmp").Length == 0);
            File.WriteAllLines(Path.Combine(dir, "2000-01-01.dat"), new string[] { "old\t1\t1" });
            AppTraffic.Load();
            AppTraffic.Prune();
            Chk("超过保留期的整天文件被删掉", !File.Exists(Path.Combine(dir, "2000-01-01.dat")));
            Chk("今天的数据还在", File.Exists(file));

            Console.WriteLine("\n结果：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        static long Sum(string day)
        {
            long rx, tx;
            AppTraffic.DayTotal(day, out rx, out tx);
            return rx + tx;
        }
    }
}
