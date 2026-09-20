// v4.14 自测：验证 KillConnections 能切断一条已建立的连接（需管理员，SetTcpEntry 要管理员）。
// 编译：csc /nologo /target:exe /main:SimKill /codepage:65001 /out:sim-kill.exe ^
//         /reference:C:\Tool\RayRadar\lib\LibreHardwareMonitorLib.dll /reference:System.dll ^
//         /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
//         C:\Tool\RayRadar\RayRadar.cs C:\Tool\RayRadar\tests\SimKill.cs

using System;
using System.Net.Sockets;
using System.Text;
using RayRadar;

static class SimKill
{
    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== v4.14 KillConnections 自测 ===");
        Console.WriteLine("管理员：" + (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否（会失败）"));
        string ip = LanSentinel.LanIp();
        Console.WriteLine("入口状态：" + LanSentinel.StateText());
        TcpClient c = null;
        try
        {
            c = new TcpClient();
            c.Connect(ip, 3081);
            Console.WriteLine("已建立连接：" + c.Client.LocalEndPoint + "  ->  " + c.Client.RemoteEndPoint);
            int n = LanSentinel.KillConnections(ip, 3081);
            Console.WriteLine("KillConnections 切断了 " + n + " 条连接");
            System.Threading.Thread.Sleep(800);
            bool dead;
            try
            {
                NetworkStream s = c.GetStream();
                byte[] b = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
                s.Write(b, 0, b.Length); s.Flush();
                System.Threading.Thread.Sleep(600);
                byte[] buf = new byte[64];
                int r = s.Read(buf, 0, 64);
                dead = (r == 0);
                Console.WriteLine("切断后再通信：" + (r == 0 ? "读到 0 字节（连接已断）" : "竟然还能读到 " + r + " 字节"));
            }
            catch (Exception ex) { dead = true; Console.WriteLine("切断后再通信：异常 = " + ex.Message); }
            Console.WriteLine(dead ? "结论：连接被成功切断 ✓" : "结论：⚠ 连接仍然可用");
        }
        catch (Exception ex) { Console.WriteLine("出错：" + ex.Message); }
        finally { if (c != null) { try { c.Close(); } catch { } } }
    }
}
