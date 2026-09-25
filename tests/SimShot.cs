// SimShot.cs -- 流量统计窗口的截图装置（v4.17）
//
// 用途：不开真机采集，用**合成数据**把 TrafficForm 画出来存成 PNG，用来核对版面。
//   · 总量数据：环境变量 RAYRADAR_TRAFFIC_DATA 指向一份 TSV（日期\t下行\t上行）
//   · 按应用数据：RAYRADAR_APPTRAFFIC_DIR 指向一个目录（每天一个 .dat：应用\t下行\t上行）
//   · 需要管理员？不需要。只构造并显示窗口，不采集、不写用户数据。
//
// 用法： sim-shot.exe <输出png路径>
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace RayRadar
{
    public static class SimShot
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : @"C:\Tool\RayRadar\tests\ui-shot.png";
            int tab = args.Length > 1 ? int.Parse(args[1]) : 0;      // 0=按天明细 1=按应用明细
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            TrafficForm f = new TrafficForm();
            f.TopMost = false;                      // 截图时别挡着用户
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Show();
            if (tab != 0) f.SelectTabForShot(tab);
            for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(outPath, ImageFormat.Png);
            }
            f.Close();
            Console.WriteLine("已保存：" + outPath + "  " + new FileInfo(outPath).Length + " 字节");
            return 0;
        }
    }
}
