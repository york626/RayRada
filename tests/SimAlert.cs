// v4.13 自测：检查 AlertForm 的按钮布局（只构造窗体、不显示、不点按钮）。
// 编译：csc /nologo /target:exe /main:SimAlert /codepage:65001 /out:sim-alert.exe ^
//         /reference:C:\Tool\RayRadar\lib\LibreHardwareMonitorLib.dll /reference:System.dll ^
//         /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
//         C:\Tool\RayRadar\RayRadar.cs C:\Tool\RayRadar\tests\SimAlert.cs
// 运行：不需要管理员（不显示窗口、不碰 netsh）。

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using RayRadar;

static class SimAlert
{
    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        List<string> lines = new List<string>();
        lines.Add("陌生设备连接手机入口：192.168.31.153（44-4A-37-33-A0-7C），已自动关闭入口");

        using (AlertForm f = new AlertForm("Ray雷达 · 入口拦截", lines,
            "「重开手机入口」只是把入口开回来：白名单里的设备能进，陌生设备照样会被拦。", "⚠ 陌生设备接入", "保持关闭", "重开手机入口"))
            Dump(f, "入口拦截弹窗（v4.13 新样式）");

        using (AlertForm f = new AlertForm("Ray雷达 · 温度报警", lines, "提示文字"))
            Dump(f, "温度报警弹窗（应只有 1 个「知道了」）");
    }

    static void Dump(AlertForm f, string label)
    {
        Console.WriteLine("=== " + label + " ===");
        Console.WriteLine("  标题：" + f.Text);
        int n = 0;
        foreach (Control c in f.Controls)
        {
            Button b = c as Button;
            if (b != null)
            {
                n++;
                Console.WriteLine("  按钮" + n + "：「" + b.Text + "」  x=" + b.Left + " y=" + b.Top + " 宽=" + b.Width);
            }
            Label l = c as Label;
            if (l != null && l.Font.Bold) Console.WriteLine("  红色大字：「" + l.Text + "」");
        }
        Console.WriteLine("  按钮总数：" + n + "   回车默认键：" + (f.AcceptButton == null ? "(无)" : "「" + ((Button)f.AcceptButton).Text + "」"));
        Console.WriteLine("  客户区：" + f.ClientSize.Width + " × " + f.ClientSize.Height);
        Console.WriteLine();
    }
}
