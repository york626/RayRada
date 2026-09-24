// v4.15 自测装置：直接调用 Ray雷达 里的「竞彩计算器服务器」（CalcServer）起停与状态。
// 不显示浮窗、不碰温度传感器、**不改用户设置文件**（只读 Settings.Load()，改动只在内存里）。
//
// 编译（与 build.ps1 同一套引用；/codepage:65001 保证中文字面量不乱码）：
//   csc /nologo /target:exe /main:SimCalc /codepage:65001 /out:sim-calc.exe ^
//       /reference:C:\Tool\RayRadar\lib\LibreHardwareMonitorLib.dll /reference:System.dll ^
//       /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
//       C:\Tool\RayRadar\RayRadar.cs C:\Tool\RayRadar\tests\SimCalc.cs
//
// 运行（不需要管理员）：
//   sim-calc.exe         → 状态 → 启动 → HTTP 取首页 → 进错目录的报错 → 停止
//   sim-calc.exe keep    → 跑完不停止（留给雷达接管）

using System;
using System.IO;
using System.Net;
using System.Text;
using RayRadar;   // CalcServer / Settings / LanSentinel 都在 namespace RayRadar 里

static class SimCalc
{
    static int fail = 0;

    static void Chk(bool ok, string what, string extra)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what +
                          (extra == null || extra.Length == 0 ? "" : "   [" + extra + "]"));
        if (!ok) fail++;
    }

    static void Main(string[] args)
    {
        Settings st = Settings.Load();
        st.CalcDir = "";            // 强制走默认目录：我的文档\DSH常用\竞彩计算器
        st.CalcServer = true;

        Console.WriteLine("目录      : " + CalcServer.Dir(st));
        Console.WriteLine("serve.mjs : " + (File.Exists(CalcServer.ScriptPath(st)) ? "存在" : "缺失"));
        Console.WriteLine("node.exe  : " + CalcServer.NodeExe());
        Console.WriteLine("端口      : " + CalcServer.Port.ToString());
        Console.WriteLine("状态(启动前): " + CalcServer.StateText(st));
        Console.WriteLine();

        Chk(File.Exists(CalcServer.ScriptPath(st)), "找到 serve.mjs", CalcServer.ScriptPath(st));
        Chk(CalcServer.NodeExe().Length > 0, "找到 node.exe", CalcServer.NodeExe());

        bool wasRunning = CalcServer.Running();
        if (wasRunning) Console.WriteLine("(注意：跑之前端口已在监听，可能是别处启动的)");
        else Chk(!CalcServer.Running(), "启动前没有监听", "");

        string r = CalcServer.Start(st);
        Console.WriteLine("Start() 返回 : " + r.Replace("\r\n", " "));
        Chk(CalcServer.Running(), "启动后端口在监听", "");
        Console.WriteLine("状态(启动后): " + CalcServer.StateText(st));

        // HTTP 真取一次首页
        string html = "";
        try
        {
            WebClient wc = new WebClient();
            wc.Encoding = Encoding.UTF8;
            html = wc.DownloadString(CalcServer.Url());
        }
        catch (Exception ex) { html = "ERR " + ex.Message; }
        Chk(html.Length > 1000 && html.IndexOf("竞彩足球优化计算器") >= 0,
            "HTTP 取到计算器首页", html.Length.ToString() + " 字节");
        Chk(html.IndexOf("webapi.sporttery.cn") >= 0, "首页里含竞彩官网接口地址", "");

        // 目录填错时的报错要能看懂
        Settings bad = Settings.Load();
        bad.CalcDir = @"C:\___no_such_dir_for_simcalc___";
        string r2 = CalcServer.Start(bad);
        Chk(r2.IndexOf("找不到网页文件") >= 0, "目录不存在时给出可读报错",
            r2.Replace("\r\n", " ").Substring(0, Math.Min(30, r2.Length)));
        Chk(CalcServer.StateText(bad).IndexOf("未找到网页文件") >= 0, "目录不存在时状态行也说明白", "");

        // ===== v4.16 看门狗 =====
        Console.WriteLine();
        Console.WriteLine("--- 看门狗 ---");
        int killed = 0;
        foreach (int pid in CalcServer.ListenerPids())          // 模拟「进程被外部杀掉」
        {
            try { System.Diagnostics.Process.GetProcessById(pid).Kill(); killed++; } catch { }
        }
        System.Threading.Thread.Sleep(1200);
        Chk(killed > 0 && !CalcServer.Running(), "已模拟进程意外退出", "杀掉 " + killed + " 个");

        CalcServer.Watchdog(st);                                // 雷达每 30 秒会调它
        System.Threading.Thread.Sleep(1200);
        Chk(CalcServer.Running(), "看门狗把服务器自动拉了起来", CalcServer.Url());

        CalcServer.Stop();                                      // 用户手动停
        CalcServer.Watchdog(st);                                // 看门狗不应该跟用户对着干
        System.Threading.Thread.Sleep(1200);
        Chk(!CalcServer.Running(), "用户手动停止后，看门狗不自动拉起", "");

        CalcServer.Start(st);                                   // 恢复运行（供后面 keep / 收尾）
        System.Threading.Thread.Sleep(800);

        if (args.Length > 0 && args[0] == "keep")
        {
            Console.WriteLine();
            Console.WriteLine("(--keep：保持不变，交给雷达接管)");
        }
        else
        {
            string s = CalcServer.Stop();
            Console.WriteLine("Stop() 返回  : " + s);
            Chk(!CalcServer.Running(), "停止后端口不再监听", "");
            Console.WriteLine("状态(停止后): " + CalcServer.StateText(st));
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "全部通过 ✓" : (fail.ToString() + " 项失败 ✗"));
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
