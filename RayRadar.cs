using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ray雷达")]
[assembly: AssemblyProduct("RayRadar")]
[assembly: AssemblyDescription("桌面硬件监控浮窗：CPU / 内存 / 网速 + CPU·显卡·主板·硬盘温度 + 温度报警")]
[assembly: AssemblyCompany("Ray")]
[assembly: AssemblyVersion("4.2.0.0")]
[assembly: AssemblyFileVersion("4.2.0.0")]

namespace RayRadar
{
    public static class Program
    {
        static Mutex mutex;
        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;   // 必须在用到 LHM 之前挂上
            try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { }   // TLS 1.2（GitHub 必须）
            foreach (string a in args)
            {
                // 诊断用：只下载温度驱动、不安装，把结果写进日志后退出（/getdriver）
                if (string.Equals(a, "/getdriver", StringComparison.OrdinalIgnoreCase))
                {
                    string got = Driver.Download();
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " download -> " + (got == null ? "FAILED" : got + " (" + new FileInfo(got).Length + " bytes)");
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "rayradar-getdriver.log"), line); } catch { }
                    return;
                }
            }
            bool created;
            mutex = new Mutex(true, "RayRadar_SingleInstance_v3", out created);
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RadarForm f = new RadarForm(Settings.Load());
            foreach (string a in args)
            {
                if (string.Equals(a, "/settings", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-settings", StringComparison.OrdinalIgnoreCase))
                    f.Shown += delegate { f.OpenSettings(); };
            }
            Application.Run(f);
        }
        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            foreach (string ext in new string[] { ".dll", ".exe" })
            {
                Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(simple + ext);
                if (st == null) continue;
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[65536]; int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return Assembly.Load(ms.ToArray());
                }
            }
            return null;
        }
    }

    // PawnIO（已签名的开源 ring0 驱动）负责 CPU / 主板 / 硬盘 / 内存温度。
    // 单文件版把这套官方安装包内置进 exe，换电脑后点一次即可就地安装，不需要另带文件。
    public static class Driver
    {
        public static string ExeDir { get { try { return Path.GetDirectoryName(Application.ExecutablePath); } catch { return ""; } } }
        public static string LibPath
        {
            get
            {
                try
                {
                    string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    return Path.Combine(Path.Combine(pf, "PawnIO"), "PawnIOLib.dll");
                }
                catch { return ""; }
            }
        }
        public static bool Installed()
        {
            try { string p = LibPath; return p != "" && File.Exists(p); }
            catch { return false; }
        }

        // 返回内置安装包释放出来的路径；没有内置则返回 null
        public static string Extract(string name, string fileName)
        {
            try
            {
                Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (st == null) return null;
                string p = Path.Combine(Path.GetTempPath(), fileName);
                using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[65536]; int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }
                return p;
            }
            catch { return null; }
        }

        // 优先用 exe 同目录下的官方安装包（可离线）；其次用编译时内置的那份；最后从官方地址下载。
        public static string SetupPath()
        {
            try
            {
                string beside = Path.Combine(ExeDir, "PawnIO_setup.exe");
                if (File.Exists(beside)) return beside;
            }
            catch { }
            return Extract("PawnIO_setup.exe", "PawnIO_setup.exe");
        }

        // PawnIO 采用 GPL-2.0：公开发布的 exe 里不打包它（避免承担 GPL 源码提供义务），
        // 改为首次使用时从官方发布页下载；官方域名不通时退回 gh-proxy 镜像。
        public const string SetupUrlDirect = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
        public const string SetupUrlMirror = "https://gh-proxy.com/" + SetupUrlDirect;

        static bool TryGet(string url, string target)
        {
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { }   // 强制 TLS 1.2
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.UserAgent = "RayRadar";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = true;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[65536];
                    int n; long total = 0;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) { fs.Write(buf, 0, n); total += n; }
                    return total > 500000;
                }
            }
            catch { return false; }
        }

        public static string Download()
        {
            try
            {
                string target = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                if (TryGet(SetupUrlDirect, target)) return target;
                if (TryGet(SetupUrlMirror, target)) return target;
                return null;
            }
            catch { return null; }
        }

        public static bool Install()
        {
            try
            {
                string exe = SetupPath();
                if (exe == null) exe = Download();
                if (exe == null) return false;
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, "-install");
                psi.UseShellExecute = false;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(180000);
                System.Threading.Thread.Sleep(1500);
                return Installed();
            }
            catch { return false; }
        }

        public static bool Uninstall()
        {
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string un = Path.Combine(Path.Combine(pf, "PawnIO"), "uninstall.exe");
                if (!File.Exists(un)) return false;
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(un, "/S");
                psi.UseShellExecute = false;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(120000);
                System.Threading.Thread.Sleep(1500);
                return !Installed();
            }
            catch { return false; }
        }
    }

    public class Settings
    {
        public bool TopMost = true, LockPos = false, DockCollapse = false, TextBlack = false, AutoStart = true;
        public int Skin = 0, Opacity = 100, X = -1, Y = -1;
        public bool ShowCpu = true, ShowMem = true, ShowNet = true, ShowDisk = false;
        public bool ShowCpuTemp = true, ShowGpuTemp = true, ShowGpuHot = true, ShowBoardTemp = true, ShowDiskTemp = true, ShowDimmtemp = false;
        public bool DriverAsk = true;
        public bool CollapseTemps = true;          // 温度折叠：浮窗上只显示「主温度」，点它在上方展开其它
        public string MainTemp = "CPU";            // 主温度键：CPU/GPU/Hot/Board/Disk/Dimm
        public bool Alarm = true, AlarmSound = true, AlarmRise = true;
        public int LimCpu = 90, LimGpu = 85, LimHot = 95, LimBoard = 65, LimDisk = 75, LimDimm = 60, RiseLimit = 15;
        // v4.10「DSH 手机入口哨兵」：只有本机存在 3081 端口转发（手机入口）时才实际工作
        public bool Sentinel = true;             // 总开关（默认开；没有 3081 入口时等于不做事）
        public bool SentinelSelf = true;         // 放行本机自测连接（自测拦截效果时可临时关掉）
        public string SentinelWhitelist = "";    // 逗号分隔的 IP/MAC（MAC 写法 82-8B-68-6C-2F-FF）

        public static string DirPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RayRadar"); } }
        public static string FilePath { get { return Path.Combine(DirPath, "settings.ini"); } }

        static int I(string v, int d) { int r; return int.TryParse(v, out r) ? r : d; }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('='); if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    bool b = (v == "1");
                    switch (k)
                    {
                        case "TopMost": s.TopMost = b; break;
                        case "LockPos": s.LockPos = b; break;
                        case "DockCollapse": s.DockCollapse = b; break;
                        case "Skin": s.Skin = I(v, 0); break;
                        case "TextBlack": s.TextBlack = b; break;
                        case "Opacity": s.Opacity = I(v, 100); break;
                        case "X": s.X = I(v, -1); break;
                        case "Y": s.Y = I(v, -1); break;
                        case "ShowCpu": s.ShowCpu = b; break;
                        case "ShowMem": s.ShowMem = b; break;
                        case "ShowNet": s.ShowNet = b; break;
                        case "ShowDisk": s.ShowDisk = b; break;
                        case "ShowCpuTemp": s.ShowCpuTemp = b; break;
                        case "ShowGpuTemp": s.ShowGpuTemp = b; break;
                        case "ShowGpuHot": s.ShowGpuHot = b; break;
                        case "ShowBoardTemp": s.ShowBoardTemp = b; break;
                        case "ShowDiskTemp": s.ShowDiskTemp = b; break;
                        case "ShowDimmTemp": s.ShowDimmtemp = b; break;
                        case "Alarm": s.Alarm = b; break;
                        case "AlarmSound": s.AlarmSound = b; break;
                        case "AlarmRise": s.AlarmRise = b; break;
                        case "LimCpu": s.LimCpu = I(v, 90); break;
                        case "LimGpu": s.LimGpu = I(v, 85); break;
                        case "LimHot": s.LimHot = I(v, 95); break;
                        case "LimBoard": s.LimBoard = I(v, 65); break;
                        case "LimDisk": s.LimDisk = I(v, 75); break;
                        case "LimDimm": s.LimDimm = I(v, 60); break;
                        case "RiseLimit": s.RiseLimit = I(v, 15); break;
                        case "AutoStart": s.AutoStart = b; break;
                        case "DriverAsk": s.DriverAsk = b; break;
                        case "CollapseTemps": s.CollapseTemps = b; break;
                        case "MainTemp": s.MainTemp = v; break;
                        case "Sentinel": s.Sentinel = b; break;
                        case "SentinelSelf": s.SentinelSelf = b; break;
                        case "SentinelWhitelist": s.SentinelWhitelist = v; break;
                    }
                }
            }
            catch { }
            return s;
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                List<string> L = new List<string>();
                Action<string, bool> ab = delegate(string k, bool v) { L.Add(k + "=" + (v ? "1" : "0")); };
                Action<string, int> ai = delegate(string k, int v) { L.Add(k + "=" + v); };
                ab("TopMost", TopMost); ab("LockPos", LockPos); ab("DockCollapse", DockCollapse);
                ai("Skin", Skin); ab("TextBlack", TextBlack); ai("Opacity", Opacity); ai("X", X); ai("Y", Y);
                ab("ShowCpu", ShowCpu); ab("ShowMem", ShowMem); ab("ShowNet", ShowNet); ab("ShowDisk", ShowDisk);
                ab("ShowCpuTemp", ShowCpuTemp); ab("ShowGpuTemp", ShowGpuTemp); ab("ShowGpuHot", ShowGpuHot);
                ab("ShowBoardTemp", ShowBoardTemp); ab("ShowDiskTemp", ShowDiskTemp); ab("ShowDimmTemp", ShowDimmtemp);
                ab("Alarm", Alarm); ab("AlarmSound", AlarmSound); ab("AlarmRise", AlarmRise);
                ai("LimCpu", LimCpu); ai("LimGpu", LimGpu); ai("LimHot", LimHot); ai("LimBoard", LimBoard); ai("LimDisk", LimDisk); ai("LimDimm", LimDimm); ai("RiseLimit", RiseLimit);
                ab("AutoStart", AutoStart); ab("DriverAsk", DriverAsk);
                ab("CollapseTemps", CollapseTemps); L.Add("MainTemp=" + MainTemp);
                ab("Sentinel", Sentinel); ab("SentinelSelf", SentinelSelf); L.Add("SentinelWhitelist=" + SentinelWhitelist);
                File.WriteAllLines(FilePath, L.ToArray());
            }
            catch { }
        }
        public static bool IsAdmin()
        {
            try { return (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // 提权运行时用「登录时以最高权限运行」的计划任务（这样温度才全读得到，且不弹 UAC）；
        // 普通权限时退回 HKCU Run。
        public static void ApplyAutoStart(bool on)
        {
            try
            {
                if (IsAdmin())
                {
                    string args = on
                        ? "/Create /F /TN RayRadar /TR " + Application.ExecutablePath + " /SC ONLOGON /RL HIGHEST"
                        : "/Delete /F /TN RayRadar";
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                    psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                    psi.UseShellExecute = false;
                    System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                    p.WaitForExit(8000);
                }
                RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (rk != null)
                {
                    if (on && !IsAdmin()) rk.SetValue("RayRadar", "\"" + Application.ExecutablePath + "\"");
                    else rk.DeleteValue("RayRadar", false);   // 提权时不要留注册表自启，避免启动两份
                    rk.Close();
                }
            }
            catch { }
        }
    }

    public class Skins
    {
        public static readonly Color[][] Sets = new Color[][]
        {
            new Color[] { Color.FromArgb(41,167,224),  Color.FromArgb(141,198,63), Color.FromArgb(240,138,36) },
            new Color[] { Color.FromArgb(45,45,48),    Color.FromArgb(66,66,70),   Color.FromArgb(24,24,26) },
            new Color[] { Color.FromArgb(224,64,154),  Color.FromArgb(176,58,140), Color.FromArgb(240,98,146) },
            new Color[] { Color.FromArgb(0,169,165),   Color.FromArgb(77,182,172), Color.FromArgb(0,121,107) },
            new Color[] { Color.FromArgb(245,166,35),  Color.FromArgb(247,202,24), Color.FromArgb(230,126,34) }
        };
        public static Color[] Get(int i) { if (i < 0 || i >= Sets.Length) i = 0; return Sets[i]; }
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct FILETIME { public uint lo; public uint hi; }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [StructLayout(LayoutKind.Sequential)] public class MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
            public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX b);
        public static ulong U(FILETIME f) { return ((ulong)f.hi << 32) | f.lo; }

        // 置顶：WinForms 在窗口句柄创建之前设置 TopMost 只记状态、不写 WS_EX_TOPMOST，所以要自己补一次
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        public const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x00000008;
        [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint ret, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static int ReadDiskTemp()
        {
            IntPtr h = CreateFileW(@"\\.\PhysicalDrive0", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) return -1000;
            try
            {
                byte[] inb = new byte[12]; BitConverter.GetBytes((uint)16).CopyTo(inb, 0);
                byte[] outb = new byte[512];
                IntPtr pin = Marshal.AllocHGlobal(inb.Length), pout = Marshal.AllocHGlobal(outb.Length);
                Marshal.Copy(inb, 0, pin, inb.Length);
                uint ret = 0;
                bool ok = DeviceIoControl(h, 0x2D1400, pin, (uint)inb.Length, pout, (uint)outb.Length, out ret, IntPtr.Zero);
                Marshal.Copy(pout, outb, 0, outb.Length);
                Marshal.FreeHGlobal(pin); Marshal.FreeHGlobal(pout);
                if (!ok) return -1000;
                return BitConverter.ToInt16(outb, 12);
            }
            finally { CloseHandle(h); }
        }
    }
}
namespace RayRadar
{
    using LibreHardwareMonitor.Hardware;

    public class TempProvider
    {
        Computer c;
        public bool Ready = false;
        public string Error = "";
        public float Cpu = -1000, Gpu = -1000, Hot = -1000, Board = -1000, Disk = -1000, Dimm = -1000;

        public TempProvider()
        {
            try
            {
                c = new Computer();
                c.IsCpuEnabled = true; c.IsGpuEnabled = true; c.IsMotherboardEnabled = true;
                c.IsStorageEnabled = true; c.IsMemoryEnabled = true;
                c.Open();
                Ready = true;
            }
            catch (Exception ex) { Error = ex.Message; Ready = false; }
        }

        public void Update()
        {
            if (!Ready) return;
            float cpu = -1000, cpuMax = -1000, gpu = -1000, hot = -1000, board = -1000, disk = -1000, dimm = -1000;
            try
            {
                foreach (IHardware h in c.Hardware)
                {
                    try { h.Update(); } catch { }
                    foreach (ISensor s in h.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                        float v = s.Value.Value;
                        if (v <= 0 || v > 150) continue;
                        string hn = h.HardwareType.ToString();
                        string sn = s.Name;
                        if (hn == "Cpu")
                        {
                            if (sn.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0) cpu = v;
                            else if (sn.IndexOf("CCD", StringComparison.OrdinalIgnoreCase) >= 0 && v > cpuMax) cpuMax = v;
                        }
                        else if (hn == "GpuNvidia")
                        {
                            if (sn.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0) { if (hot < 0) hot = v; }
                            else if (sn.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0) { if (gpu < 0) gpu = v; }
                        }
                        else if (hn == "Motherboard" && board < 0) board = v;
                        else if (hn == "Storage" && sn.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) >= 0 && disk < 0) disk = v;
                        else if (hn == "Memory" && sn.IndexOf("DIMM", StringComparison.OrdinalIgnoreCase) >= 0 && v > dimm) dimm = v;
                    }
                    foreach (IHardware sh in h.SubHardware)
                    {
                        try { sh.Update(); } catch { }
                        foreach (ISensor s in sh.Sensors)
                        {
                            if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                            float v = s.Value.Value;
                            if (v <= 0 || v > 150) continue;
                            if (h.HardwareType.ToString() == "Motherboard" && board < 0) board = v;
                        }
                    }
                }
            }
            catch { }
            if (cpu < 0) cpu = cpuMax;
            Cpu = cpu; Gpu = gpu; Hot = hot; Board = board; Disk = disk; Dimm = dimm;
        }
    }

    public class AlertForm : Form
    {
        // 按内容自动换行排版：长句不再被窗口右侧裁掉（用户 2026-09-18 反馈）
        public AlertForm(string title, List<string> lines, string hint)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; ShowInTaskbar = true; MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 10f);

            const int pad = 20, gap = 8;
            int width = 490, textW = width - pad * 2;
            Font fHead = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            Font fBody = new Font("Microsoft YaHei UI", 10f);
            Font fHint = new Font("Microsoft YaHei UI", 9f);

            Label head = new Label();
            head.Text = "⚠ 温度报警"; head.Font = fHead;
            head.ForeColor = Color.FromArgb(200, 30, 30);
            head.Location = new Point(pad - 2, 14); head.AutoSize = true;
            Controls.Add(head);

            int y = 52;
            foreach (string l in lines)
            {
                if (l == null || l.Length == 0) { y += 6; continue; }
                Size sz = TextRenderer.MeasureText(l, fBody, new Size(textW, 2000), TextFormatFlags.WordBreak);
                Label lb = new Label();
                lb.AutoSize = false; lb.Font = fBody; lb.Text = l;
                lb.Location = new Point(pad, y);
                lb.Size = new Size(textW, Math.Max(20, sz.Height + 4));
                lb.ForeColor = Color.FromArgb(30, 30, 30);
                Controls.Add(lb);
                y += lb.Height + gap;
            }
            if (!string.IsNullOrEmpty(hint))
            {
                y += 2;
                Size sz = TextRenderer.MeasureText(hint, fHint, new Size(textW, 2000), TextFormatFlags.WordBreak);
                Label lb = new Label();
                lb.AutoSize = false; lb.Font = fHint; lb.Text = hint;
                lb.Location = new Point(pad, y);
                lb.Size = new Size(textW, Math.Max(18, sz.Height + 4));
                lb.ForeColor = Color.FromArgb(110, 112, 118);
                Controls.Add(lb);
                y += lb.Height + gap;
            }

            ClientSize = new Size(width, y + 50);
            Button ok = new Button();
            ok.Text = "知道了"; ok.Size = new Size(110, 30);
            ok.Location = new Point((ClientSize.Width - 110) / 2, ClientSize.Height - 42);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
            AcceptButton = ok;
        }
    }
}
namespace RayRadar
{
    using LibreHardwareMonitor.Hardware;

    // ===== v4.10：DSH 手机入口哨兵 =====
    // 只有本机存在 3081 端口转发（= 开了手机入口）时才实际工作：
    // 监控该入口上的连接，白名单之外的设备一律「提示音 + 弹窗 + 删掉转发（阻断）」。
    // 对别人无害：没有 3081 转发的电脑，这个模块什么都不做。
    public class SentinelHit
    {
        public string Ip = "";
        public string Mac = "";
        public string Why = "";
        public bool Blocked = false;
    }

    public static class LanSentinel
    {
        public static string ListenIp = "";
        public static int ListenPort = 0;
        public static string LastOffender = "";      // "IP,MAC" —— 设置窗口「加入上次拦截」用
        static DateTime entryCheckedAt = DateTime.MinValue;
        static bool wasUp = false;
        static readonly Dictionary<string, DateTime> lastAlarm = new Dictionary<string, DateTime>();

        public static string LogPath { get { return Path.Combine(Settings.DirPath, "sentinel.log"); } }

        public static void Log(string m)
        {
            try
            {
                Directory.CreateDirectory(Settings.DirPath);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // 每 10 秒查一次 portproxy（少起进程）
        public static bool RefreshEntry()
        {
            if ((DateTime.Now - entryCheckedAt).TotalSeconds < 10) return ListenPort > 0;
            entryCheckedAt = DateTime.Now;
            ListenIp = ""; ListenPort = 0;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", "interface portproxy show v4tov4");
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (string line in outp.Split('\n'))
                {
                    Match mm = Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3})\s+(\d+)\s+127\.0\.0\.1\s+(\d+)");
                    if (mm.Success)
                    {
                        ListenIp = mm.Groups[1].Value;
                        ListenPort = int.Parse(mm.Groups[2].Value);
                        break;
                    }
                }
            }
            catch { }
            if (ListenPort > 0 && !wasUp) { wasUp = true; Log("哨兵已启用：监控 " + ListenIp + ":" + ListenPort.ToString() + "（白名单命中即放行，其余报警并阻断）"); }
            if (ListenPort <= 0 && wasUp) { wasUp = false; Log("入口未开启（portproxy 里没有 3081 转发），哨兵待机"); }
            return ListenPort > 0;
        }

        // 关掉入口：删掉这条转发（需要管理员 —— Ray雷达本来就是管理员运行）
        public static bool Block()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("netsh.exe",
                    "interface portproxy delete v4tov4 listenaddress=" + ListenIp + " listenport=" + ListenPort.ToString());
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(5000);
                bool ok = (p.ExitCode == 0);
                entryCheckedAt = DateTime.MinValue;      // 下一拍立刻重新探测
                return ok;
            }
            catch { return false; }
        }

        [DllImport("iphlpapi.dll")]
        static extern int SendARP(int dest, int src, byte[] mac, ref int len);

        // 取对端 MAC（同网段直连才有值；自己连自己时为空，正常）
        public static string MacOf(string ip)
        {
            try
            {
                System.Net.IPAddress a;
                if (!System.Net.IPAddress.TryParse(ip, out a)) return "";
                byte[] mac = new byte[6]; int len = mac.Length;
                if (SendARP(BitConverter.ToInt32(a.GetAddressBytes(), 0), 0, mac, ref len) != 0) return "";
                string[] h = new string[6];
                for (int i = 0; i < 6; i++) h[i] = mac[i].ToString("X2");
                return string.Join("-", h);
            }
            catch { return ""; }
        }

        static Dictionary<string, bool> ParseIps(string s)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>();
            string src = (s == null) ? "" : s;
            foreach (string part in src.Split(new char[] { ',', ';', ' ', '\r', '\n', '\t' }))
            {
                string t = part.Trim();
                if (t.Length > 0) d[t.ToUpperInvariant()] = true;
            }
            return d;
        }

        // 每 1 秒调一次（OnTick）；返回本次命中的陌生设备
        public static List<SentinelHit> Scan(Settings st, bool doBlock)
        {
            List<SentinelHit> hits = new List<SentinelHit>();
            if (st == null || !st.Sentinel) return hits;
            if (!RefreshEntry() || ListenPort <= 0) return hits;

            Dictionary<string, bool> wl = ParseIps(st.SentinelWhitelist);
            List<string> ips = new List<string>();
            List<string> why = new List<string>();
            try
            {
                foreach (TcpConnectionInformation c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (c.LocalEndPoint.Port != ListenPort) continue;
                    if (c.State != TcpState.Established && c.State != TcpState.TimeWait && c.State != TcpState.CloseWait) continue;
                    string ip = c.RemoteEndPoint.Address.ToString();
                    if (ip.IndexOf(':') >= 0) continue;                                        // 只看 IPv4
                    if (ip == "127.0.0.1" || ip == "0.0.0.0") continue;
                    if (st.SentinelSelf && ip == ListenIp) continue;                           // 本机自测放行
                    string mac = MacOf(ip);
                    if (wl.ContainsKey(ip.ToUpperInvariant())) continue;                        // IP 命中白名单
                    if (mac.Length > 0 && wl.ContainsKey(mac)) continue;                        // MAC 命中白名单
                    if (!ips.Contains(ip)) { ips.Add(ip); why.Add(mac.Length > 0 ? "MAC " + mac : "无 ARP 记录"); }
                }
            }
            catch { }

            for (int i = 0; i < ips.Count; i++)
            {
                string ip = ips[i];
                DateTime last;
                if (lastAlarm.TryGetValue(ip, out last) && (DateTime.Now - last).TotalSeconds < 60) continue;
                lastAlarm[ip] = DateTime.Now;
                SentinelHit h = new SentinelHit();
                h.Ip = ip;
                h.Mac = MacOf(ip);
                h.Why = why[i];
                LastOffender = h.Ip + (h.Mac.Length > 0 ? "," + h.Mac : "");
                Log("⚠ 陌生设备连接入口：" + h.Ip + (h.Mac.Length > 0 ? "（MAC " + h.Mac + "）" : "") + " —— " + h.Why);
                if (doBlock)
                {
                    h.Blocked = Block();
                    Log(h.Blocked ? "已自动关闭手机入口（阻断 " + h.Ip + "）" : "⚠ 自动阻断失败（需要管理员权限）");
                }
                hits.Add(h);
            }
            return hits;
        }
    }

    public class RadarForm : Form
    {
        Settings st;
        System.Windows.Forms.Timer timer, tempTimer;
        Font fCap, fVal, fNet, fSm;
        double cpuPct = 0, memPct = 0, upK = 0, dnK = 0, rdK = 0, wrK = 0;
        ulong pIdle, pTot;
        long pRx, pTx; DateTime pTime = DateTime.Now;
        System.Diagnostics.PerformanceCounter diskR, diskW;
        bool diskOk = true;
        TempProvider temp;
        bool admin = false;
        bool collapsed = false; Rectangle expandBounds;
        bool dragging = false; Point dragOff;
        const int BW = 40, BH = 40, BWNET = 75;
        DateTime lastPopup = DateTime.MinValue;
        Dictionary<string, DateTime> lastAlarm = new Dictionary<string, DateTime>();
        List<KeyValuePair<DateTime, float>> cpuHist = new List<KeyValuePair<DateTime, float>>();
        bool tempBlocked = false;
        bool driverChecked = false;
        int topTick = 0;
        TempFlyout flyout;
        Point downPt; bool downLeft = false;
        bool suppressClick = false;          // 双击后抑制紧随的 MouseUp
        readonly DateTime startedAt = DateTime.Now;   // 程序启动时刻（温升报警预热用）
        const double RiseWarmupSeconds = 180;         // 启动后 3 分钟内不判温升（开机温度本身在爬升）
        const float RiseFloorC = 45f;                 // 温升报警还要求当前温度 ≥ 45°C，避免低温区的无意义波动
        const double RiseConfirmSeconds = 20;         // 陡升后复测延时：仍持续升温才报警（区分负载尖峰与液冷故障）
        DateTime risePending = DateTime.MinValue;     // 已测到陡升、等待复测的时刻
        int flushTick = 0;

        public RadarForm(Settings s)
        {
            st = s;
            admin = (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Settings.ApplyAutoStart(st.AutoStart);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "Ray雷达"; DoubleBuffered = true;
            fCap = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);
            fVal = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            fNet = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            fSm = new Font("Microsoft YaHei UI", 6.5f, FontStyle.Regular);
            ApplySettings();
            if (st.X < 0 || st.Y < 0) { Rectangle w0 = Screen.PrimaryScreen.WorkingArea; st.X = w0.Right - Width; st.Y = w0.Bottom - Height; }
            Location = new Point(st.X, st.Y); ClampToWorkArea();
            try
            {
                diskR = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                diskW = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                diskR.NextValue(); diskW.NextValue();
            }
            catch { diskOk = false; }
            try { temp = new TempProvider(); } catch { temp = null; }
            Native.FILETIME i, k, u; Native.GetSystemTimes(out i, out k, out u);
            pIdle = Native.U(i); pTot = Native.U(k) + Native.U(u);
            timer = new System.Windows.Forms.Timer(); timer.Interval = 1000; timer.Tick += new EventHandler(OnTick); timer.Start();
            tempTimer = new System.Windows.Forms.Timer(); tempTimer.Interval = 3000; tempTimer.Tick += new EventHandler(OnTempTick); tempTimer.Start();
            Shown += delegate { ApplyTopMost(); OnTempTick(null, null); };
            try { Traffic.Load(); } catch { }
            FormClosing += delegate { try { Traffic.Save(); } catch { } HideFlyout(); };
        }

        void OnTempTick(object sender, EventArgs e)
        {
            if (temp != null && temp.Ready) { try { temp.Update(); } catch { } }
            if (!driverChecked) { driverChecked = true; TryDriverPrompt(); }
            CheckAlarm();
            Invalidate();
            if (flyout != null && flyout.Visible) flyout.Invalidate();
        }

        // 换到新电脑时：没有 PawnIO 驱动就读不到 CPU 温度，这里一次性提示并就地安装（安装包内置在 exe 里）
        void TryDriverPrompt()
        {
            try
            {
                if (!st.DriverAsk) return;
                if (Driver.Installed()) return;
                if (temp != null && temp.Cpu > -900) return;   // 温度读得到就不用管驱动
                DialogResult r = MessageBox.Show(
                    "检测到本机没有 PawnIO 温度驱动：CPU / 主板 / 硬盘 / 内存 温度需要它。\r\n\r\n"
                    + "它是已签名的开源驱动（GPL-2.0，不是杀毒软件会查杀的 WinRing0），安装包将从官方发布页下载（约 3MB，只需一次）。\r\n\r\n"
                    + "现在安装？（装完重启本程序即可显示全部温度）",
                    "Ray雷达 - 缺少温度驱动", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { st.DriverAsk = false; st.Save(); return; }
                Cursor = Cursors.WaitCursor;
                bool ok = Driver.Install();
                Cursor = Cursors.Default;
                if (ok) MessageBox.Show("温度驱动安装完成。请关闭并重新打开 Ray雷达，CPU / 主板 / 硬盘温度就会显示出来。", "Ray雷达", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show("安装未完成（可能被取消或需要联网）。也可以在『设置』里点『安装温度驱动』重试。", "Ray雷达", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        string T(float v)
        {
            if (v <= -900) { if (!admin && !tempBlocked) return "—"; return "—"; }
            return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "°";
        }

        void Raise(List<string> msgs, string key, float val, int lim, string name)
        {
            if (val <= -900) return;
            if (val < lim) return;
            DateTime last;
            if (lastAlarm.TryGetValue(key, out last) && (DateTime.Now - last).TotalMinutes < 5) return;
            lastAlarm[key] = DateTime.Now;
            msgs.Add(name + " " + ((int)Math.Round(val)) + "°C，超过阈值 " + lim + "°C");
        }

        void CheckAlarm()
        {
            if (!st.Alarm || temp == null || !temp.Ready) return;
            List<string> msgs = new List<string>();
            Raise(msgs, "cpu", temp.Cpu, st.LimCpu, "CPU 温度");
            Raise(msgs, "gpu", temp.Gpu, st.LimGpu, "显卡温度");
            Raise(msgs, "hot", temp.Hot, st.LimHot, "显卡热点");
            Raise(msgs, "board", temp.Board, st.LimBoard, "主板温度");
            Raise(msgs, "disk", temp.Disk, st.LimDisk, "硬盘温度");
            Raise(msgs, "dimm", temp.Dimm, st.LimDimm, "内存温度");

            if (st.AlarmRise && temp.Cpu > -900)
            {
                DateTime now = DateTime.Now;
                // 采样中断（睡眠唤醒 / 程序被挂起）就重新开始积累，避免跨空档算出假温升
                if (cpuHist.Count > 0 && (now - cpuHist[cpuHist.Count - 1].Key).TotalSeconds > 8) cpuHist.Clear();
                cpuHist.Add(new KeyValuePair<DateTime, float>(now, temp.Cpu));
                while (cpuHist.Count > 0 && (now - cpuHist[0].Key).TotalSeconds > 20) cpuHist.RemoveAt(0);
                // 刚开机/刚启动时 CPU 温度本身就在爬升，预热期内不判温升（2026-09-18 用户反馈开机即误报）
                bool warm = (now - startedAt).TotalSeconds >= RiseWarmupSeconds;
                if (warm && cpuHist.Count > 2)
                {
                    float rise = temp.Cpu - cpuHist[0].Value;
                    bool over = (rise >= st.RiseLimit && temp.Cpu >= RiseFloorC);
                    if (risePending == DateTime.MinValue)
                    {
                        // 第一次测到陡升：先只记下，不报警。
                        // 游戏/编译等负载刚起来时会出现「升一下就不升了」的尖峰，
                        // 而液冷故障是持续爬升——用第二次复测区分两者（2026-09-18 用户反馈每次启动游戏都误报）。
                        if (over) risePending = now;
                    }
                    else if ((now - risePending).TotalSeconds >= RiseConfirmSeconds)
                    {
                        risePending = DateTime.MinValue;
                        if (over)
                        {
                            DateTime last;
                            if (!(lastAlarm.TryGetValue("rise", out last) && (DateTime.Now - last).TotalMinutes < 5))
                            {
                                lastAlarm["rise"] = now;
                                msgs.Add("CPU 温度持续上升，" + ((int)Math.Round(RiseConfirmSeconds + 20)) + " 秒内累计上升 "
                                    + ((int)Math.Round(rise)) + "°C（当前 " + ((int)Math.Round(temp.Cpu)) + "°C）");
                                cpuHist.Clear();
                            }
                        }
                    }
                }
            }

            if (msgs.Count == 0) return;
            if ((DateTime.Now - lastPopup).TotalSeconds < 60) return;
            lastPopup = DateTime.Now;
            if (st.AlarmSound) { try { SystemSounds.Hand.Play(); } catch { } }
            try { using (AlertForm f = new AlertForm("Ray雷达 · 温度报警", msgs, "若为水冷/液冷：请检查水泵转速、水管接头是否渗漏、冷排风扇是否停转。")) f.ShowDialog(); } catch { }
        }

        public void ApplySettings()
        {
            TopMost = st.TopMost;
            Opacity = Math.Max(0.2, Math.Min(1.0, st.Opacity / 100.0));
            int w = 0;
            if (st.ShowCpu) w += BW;
            if (st.ShowMem) w += BW;
            if (st.ShowNet) w += BWNET;
            if (st.ShowDisk) w += BWNET;
            foreach (string k in TempKeys) if (ShowTemp(k)) w += BW;
            if (w == 0) w = BW;
            ClientSize = new Size(w, BH);
            ApplyTopMost();
            if (flyout != null && flyout.Visible) { flyout.Rebuild(); PositionFlyout(); }
            Invalidate();
        }

        // 强制把「始终置顶」真正写进窗口样式。
        // 实测（2026-09-18 自检）：在本程序里直接 SetWindowPos(hwnd, HWND_TOPMOST, …) 会返回 True，
        // 但 WS_EX_TOPMOST 样式位不动；必须走 WinForms 的 TopMost 属性——先置 false 再置 true 才真正生效。
        void ApplyTopMost()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (st.TopMost)
                {
                    if (TopMost) TopMost = false;
                    TopMost = true;
                }
                else
                {
                    TopMost = false;
                }
            }
            catch { }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTopMost();
        }

        // WinForms 显示/定位窗口时还会再调 SetWindowPos，可能把置顶带冲掉（例如全屏游戏结束后），
        // 所以每 5 秒核对一次样式位，掉了就补回来。
        void EnsureTopMost()
        {
            try
            {
                if (!IsHandleCreated) return;
                bool isTop = (Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
                if (isTop != st.TopMost) ApplyTopMost();
            }
            catch { }
        }
        void Save() { st.X = Location.X; st.Y = Location.Y; st.Save(); }
        public void OpenSettings()
        {
            using (SettingsForm sf = new SettingsForm(st, this)) { sf.ShowDialog(this); ApplySettings(); Save(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            downPt = e.Location; downLeft = (e.Button == MouseButtons.Left);
            if (e.Button == MouseButtons.Right) { HideFlyout(); OpenSettings(); return; }
            if (e.Button == MouseButtons.Left && !st.LockPos && !collapsed)
            { dragging = true; dragOff = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int nx = Cursor.Position.X - dragOff.X, ny = Cursor.Position.Y - dragOff.Y;
            if (nx < wa.Left) nx = wa.Left; if (ny < wa.Top) ny = wa.Top;
            if (nx + Width > wa.Right) nx = wa.Right - Width; if (ny + Height > wa.Bottom) ny = wa.Bottom - Height;
            if (nx == Location.X && ny == Location.Y) return;
            Location = new Point(nx, ny);
            // 拖动时让展开的温度浮层跟着走（不要在这里隐藏：鼠标 1px 抖动也会走这条分支，
            // 会导致「点第二次收不起来」——2026-09-18 实测踩到过）
            if (flyout != null && flyout.Visible) PositionFlyout();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = dragging;
            dragging = false;
            bool isClick = downLeft && Math.Abs(e.X - downPt.X) <= 4 && Math.Abs(e.Y - downPt.Y) <= 4;
            if (isClick)
            {
                if (suppressClick) { suppressClick = false; return; }   // 刚发生双击：这次抬起不算单击
                HandleClick(e.X); return;                                // 纯点击：区分「点网速看流量」「点主温度展开」
            }
            suppressClick = false;
            if (!wasDrag) return;
            SnapToEdges(); st.X = Location.X; st.Y = Location.Y; st.Save(); ApplyDock();
            if (flyout != null && flyout.Visible) PositionFlyout();
        }
        // 双击不再打开设置窗口（用户 2026-09-18 要求：只有右键打开设置）；
        // 双击时收起温度浮层，并抑制紧随其后的那次 MouseUp，避免被当成单击又把它打开
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            suppressClick = true;
            HideFlyout();
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (collapsed) { Bounds = expandBounds; collapsed = false; } }

        void ClampToWorkArea()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int nx = Location.X, ny = Location.Y;
            if (nx < wa.Left) nx = wa.Left; if (ny < wa.Top) ny = wa.Top;
            if (nx + Width > wa.Right) nx = wa.Right - Width; if (ny + Height > wa.Bottom) ny = wa.Bottom - Height;
            Location = new Point(nx, ny);
        }
        void SnapToEdges()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int snap = 22, nx = Location.X, ny = Location.Y;
            if (Math.Abs(Location.X - wa.Left) <= snap) nx = wa.Left;
            else if (Math.Abs((Location.X + Width) - wa.Right) <= snap) nx = wa.Right - Width;
            if (Math.Abs(Location.Y - wa.Top) <= snap) ny = wa.Top;
            else if (Math.Abs((Location.Y + Height) - wa.Bottom) <= snap) ny = wa.Bottom - Height;
            if (nx < wa.Left) nx = wa.Left; if (ny < wa.Top) ny = wa.Top;
            if (nx + Width > wa.Right) nx = wa.Right - Width; if (ny + Height > wa.Bottom) ny = wa.Bottom - Height;
            Location = new Point(nx, ny);
        }
        void ApplyDock()
        {
            if (!st.DockCollapse) return;
            Rectangle scr = Screen.FromControl(this).Bounds, wa = Screen.FromControl(this).WorkingArea;
            int edge = 4;
            bool atEdge = (Location.X <= scr.Left + edge) || (Location.X + Width >= wa.Right - edge) || (Location.Y <= scr.Top + edge) || (Location.Y + Height >= wa.Bottom - edge);
            if (atEdge && !collapsed)
            {
                HideFlyout();
                expandBounds = Bounds;
                if (Location.X <= scr.Left + edge) Bounds = new Rectangle(scr.Left, Location.Y, 6, Height);
                else if (Location.X + Width >= wa.Right - edge) Bounds = new Rectangle(wa.Right - 6, Location.Y, 6, Height);
                else if (Location.Y <= scr.Top + edge) Bounds = new Rectangle(Location.X, scr.Top, Width, 5);
                else Bounds = new Rectangle(Location.X, wa.Bottom - 5, Width, 5);
                collapsed = true;
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            Native.FILETIME i, k, u; Native.GetSystemTimes(out i, out k, out u);
            ulong idle = Native.U(i), tot = Native.U(k) + Native.U(u);
            ulong di = idle - pIdle, dt = tot - pTot;
            if (dt > 0) cpuPct = 100.0 * (double)(dt - di) / (double)dt;
            pIdle = idle; pTot = tot;
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(m)) memPct = m.dwMemoryLoad;
            long rx = 0, tx = 0;
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
            DateTime now = DateTime.Now;
            double sec = (now - pTime).TotalSeconds; if (sec <= 0) sec = 1;
            if (pRx > 0) { dnK = (rx - pRx) / 1024.0 / sec; upK = (tx - pTx) / 1024.0 / sec; }
            if (pRx > 0)
            {
                // 按天累计流量（只统计本程序运行期间；换网卡/重启网卡导致的负增量忽略）
                long dRx = rx - pRx, dTx = tx - pTx;
                if (dRx < 0) dRx = 0;
                if (dTx < 0) dTx = 0;
                if (dRx > 0 || dTx > 0) Traffic.Add(dRx, dTx);
                if (++flushTick >= 60) { flushTick = 0; try { Traffic.Save(); } catch { } }
            }
            pRx = rx; pTx = tx; pTime = now;
            if (st.ShowDisk && diskOk)
            { try { rdK = diskR.NextValue() / 1024.0; wrK = diskW.NextValue() / 1024.0; } catch { diskOk = false; } }
            if (++topTick >= 5) { topTick = 0; EnsureTopMost(); }

            // v4.10：DSH 手机入口哨兵（每秒检查一次；没有 3081 转发时等于不做事）
            if (st.Sentinel)
            {
                try
                {
                    List<SentinelHit> hits = LanSentinel.Scan(st, true);
                    if (hits.Count > 0)
                    {
                        List<string> smsgs = new List<string>();
                        foreach (SentinelHit h in hits)
                        {
                            smsgs.Add("陌生设备连接手机入口：" + h.Ip + (h.Mac.Length > 0 ? "（" + h.Mac + "）" : "")
                                + (h.Blocked ? "，已自动关闭入口" : "，⚠ 阻断失败，请手动运行『关闭手机入口.cmd』"));
                        }
                        if (st.AlarmSound) { try { SystemSounds.Hand.Play(); } catch { } }
                        try
                        {
                            using (AlertForm f = new AlertForm("Ray雷达 · 入口拦截", smsgs,
                                "要重新开放：运行『开放手机入口.cmd』。若是自家设备被误拦，在设置里把它的 IP/MAC 加进哨兵白名单（有『加入上次拦截』按钮）。"))
                                f.ShowDialog();
                        }
                        catch { }
                    }
                }
                catch { }
            }

            Invalidate();
            if (flyout != null && flyout.Visible) flyout.Invalidate();
        }

        // ===== 温度块：哪些显示在浮窗上 / 哪些放进展开浮层 =====
        static readonly string[] TempKeys = new string[] { "CPU", "GPU", "Hot", "Board", "Disk", "Dimm" };

        bool TempOn(string k)
        {
            switch (k)
            {
                case "CPU": return st.ShowCpuTemp;
                case "GPU": return st.ShowGpuTemp;
                case "Hot": return st.ShowGpuHot;
                case "Board": return st.ShowBoardTemp;
                case "Disk": return st.ShowDiskTemp;
                case "Dimm": return st.ShowDimmtemp;
            }
            return false;
        }
        float TempVal(string k)
        {
            if (temp == null) return -1000;
            switch (k)
            {
                case "CPU": return temp.Cpu;
                case "GPU": return temp.Gpu;
                case "Hot": return temp.Hot;
                case "Board": return temp.Board;
                case "Disk": return temp.Disk;
                case "Dimm": return temp.Dimm;
            }
            return -1000;
        }
        string TempCap(string k)
        {
            switch (k)
            {
                case "CPU": return "CPU";
                case "GPU": return "显卡";
                case "Hot": return "热点";
                case "Board": return "主板";
                case "Disk": return "硬盘";
                case "Dimm": return "内存";
            }
            return k;
        }
        // 该温度块是否画在浮窗本体上（折叠时只画主温度）
        bool ShowTemp(string k) { return TempOn(k) && (!st.CollapseTemps || k == st.MainTemp); }

        // 展开浮层要显示的内容（折叠时：勾选过、且不是主温度的那些）
        public List<string[]> FlyoutItems()
        {
            List<string[]> list = new List<string[]>();
            if (!st.CollapseTemps) return list;
            foreach (string k in TempKeys)
                if (TempOn(k) && k != st.MainTemp) list.Add(new string[] { TempCap(k), T(TempVal(k)) });
            return list;
        }
        public Color[] SkinColors() { return Skins.Get(st.Skin); }
        public Color FgColor() { return st.TextBlack ? Color.Black : Color.White; }
        public Font CapFont { get { return fCap; } }
        public Font ValFont { get { return fVal; } }

        void ToggleFlyout()
        {
            try
            {
                if (FlyoutItems().Count == 0)
                {
                    MessageBox.Show("没有可展开的温度：请在「设置 → 显示项目」里勾选其它温度。", "Ray雷达", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (flyout == null) flyout = new TempFlyout(this);
                if (flyout.Visible) { flyout.Hide(); return; }
                flyout.Rebuild();
                PositionFlyout();
                flyout.Show();
                flyout.ApplyTopMost();
            }
            catch { }
        }
        void PositionFlyout()
        {
            if (flyout == null) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int w = flyout.Width, h = flyout.Height;
            int x = Left;
            if (x + w > wa.Right) x = wa.Right - w;
            if (x < wa.Left) x = wa.Left;
            int y = Top - h - 4;                                   // 默认弹在浮窗上方
            if (y < wa.Top) { y = Top + Height + 4; if (y + h > wa.Bottom) y = wa.Top; }   // 上方放不下就放下方
            flyout.Location = new Point(x, y);
        }
        void HideFlyout() { try { if (flyout != null && flyout.Visible) flyout.Hide(); } catch { } }

        // 点的是哪一块
        string BlockAt(int x)
        {
            int bx = 0;
            if (st.ShowCpu) { if (x < bx + BW) return "cpu"; bx += BW; }
            if (st.ShowMem) { if (x < bx + BW) return "mem"; bx += BW; }
            if (st.ShowNet) { if (x < bx + BWNET) return "net"; bx += BWNET; }
            if (st.ShowDisk) { if (x < bx + BWNET) return "disk"; bx += BWNET; }
            foreach (string k in TempKeys) { if (ShowTemp(k)) { if (x < bx + BW) return k; bx += BW; } }
            return null;
        }
        void HandleClick(int x)
        {
            string key = BlockAt(x);
            if (key == null) return;
            if (key == "net") { ShowTraffic(); return; }
            if (st.CollapseTemps && key == st.MainTemp) ToggleFlyout();
        }
        void ShowTraffic()
        {
            HideFlyout();
            try { using (TrafficForm f = new TrafficForm()) f.ShowDialog(this); } catch { }
            Invalidate();
        }

        string Fmt(double k)
        {
            if (k >= 1024) return (k / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " M/S";
            return k.ToString("0", CultureInfo.InvariantCulture) + " K/S";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (collapsed) { e.Graphics.Clear(Color.FromArgb(60, 60, 60)); return; }
            Color[] sk = Skins.Get(st.Skin);
            Color fg = st.TextBlack ? Color.Black : Color.White;
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            int x = 0, idx = 0;
            if (st.ShowCpu) { Block(g, x, sk[idx++ % 3], "CPU", cpuPct.ToString("0", CultureInfo.InvariantCulture) + "%", fg, sf); x += BW; }
            if (st.ShowMem) { Block(g, x, sk[idx++ % 3], "内存", memPct.ToString("0", CultureInfo.InvariantCulture) + "%", fg, sf); x += BW; }
            if (st.ShowNet)
            {
                Fill(g, x, BWNET, sk[idx++ % 3]);
                using (SolidBrush b = new SolidBrush(fg))
                { g.DrawString("↑ " + Fmt(upK), fNet, b, new RectangleF(x, 0, BWNET, 20), sf); g.DrawString("↓ " + Fmt(dnK), fNet, b, new RectangleF(x, 20, BWNET, 20), sf); }
                x += BWNET;
            }
            if (st.ShowDisk)
            {
                Fill(g, x, BWNET, sk[idx++ % 3]);
                using (SolidBrush b = new SolidBrush(fg))
                { g.DrawString(diskOk ? ("读 " + Fmt(rdK)) : "硬盘", fNet, b, new RectangleF(x, 0, BWNET, 20), sf); g.DrawString(diskOk ? ("写 " + Fmt(wrK)) : "不可用", fNet, b, new RectangleF(x, 20, BWNET, 20), sf); }
                x += BWNET;
            }
            if (ShowTemp("CPU")) { Block(g, x, sk[idx++ % 3], "CPU", T(TempVal("CPU")), fg, sf); x += BW; }
            if (ShowTemp("GPU")) { Block(g, x, sk[idx++ % 3], "显卡", T(TempVal("GPU")), fg, sf); x += BW; }
            if (ShowTemp("Hot")) { Block(g, x, sk[idx++ % 3], "热点", T(TempVal("Hot")), fg, sf); x += BW; }
            if (ShowTemp("Board")) { Block(g, x, sk[idx++ % 3], "主板", T(TempVal("Board")), fg, sf); x += BW; }
            if (ShowTemp("Disk")) { Block(g, x, sk[idx++ % 3], "硬盘", T(TempVal("Disk")), fg, sf); x += BW; }
            if (ShowTemp("Dimm")) { Block(g, x, sk[idx++ % 3], "内存", T(TempVal("Dimm")), fg, sf); x += BW; }
        }
        void Fill(Graphics g, int x, int w, Color c) { using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, new Rectangle(x, 0, w, BH)); }
        void Block(Graphics g, int x, Color bg, string cap, string val, Color fg, StringFormat sf)
        {
            Fill(g, x, BW, bg);
            using (SolidBrush b = new SolidBrush(fg))
            { g.DrawString(cap, fCap, b, new RectangleF(x, 1, BW, 15), sf); g.DrawString(val, fVal, b, new RectangleF(x, 17, BW, 22), sf); }
        }
    }
}
namespace RayRadar
{
    public delegate void Changer(bool v);

    public class ToggleSwitch : Control
    {
        bool chk;
        public event EventHandler CheckedChanged;
        public ToggleSwitch() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); Size = new Size(46, 24); Cursor = Cursors.Hand; }
        public bool Checked { get { return chk; } set { if (chk != value) { chk = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } } }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(chk ? Color.FromArgb(24, 144, 255) : Color.FromArgb(190, 190, 190)))
                g.FillPath(b, Round(new Rectangle(0, 1, Width - 1, Height - 3), (Height - 3) / 2));
            int d = Height - 6, kx = chk ? Width - d - 3 : 3;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, 3, d, d);
        }
        static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    public class SettingsForm : Form
    {
        Settings st; RadarForm owner;
        Panel scroll;

        public SettingsForm(Settings s, RadarForm o)
        {
            st = s; owner = o;
            Text = "Ray雷达 - 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 560);
            Font = new Font("Microsoft YaHei UI", 9f);

            // 内容区：可滚动（内容比窗口高时自动出现滚动条，鼠标滚轮也能滚）
            scroll = new Panel();
            scroll.Location = new Point(0, 0);
            scroll.Size = new Size(420, 506);
            scroll.AutoScroll = true;
            scroll.BackColor = SystemColors.Control;
            Controls.Add(scroll);

            int y = 12;
            y = Toggle("始终置顶显示", y, st.TopMost, delegate(bool v) { st.TopMost = v; owner.ApplySettings(); });
            Toggle2("锁定浮窗位置", 18, y, st.LockPos, delegate(bool v) { st.LockPos = v; });
            Toggle2("贴边时收起", 210, y, st.DockCollapse, delegate(bool v) { st.DockCollapse = v; });
            y += 30;

            Lbl("浮窗样式", 18, y + 6);
            for (int i = 0; i < Skins.Sets.Length; i++)
            {
                int idx = i;
                Panel sw = new Panel();
                sw.Size = new Size(24, 18); sw.Location = new Point(180 + i * 30, y + 4);
                sw.BackColor = Skins.Get(i)[0]; sw.Cursor = Cursors.Hand; sw.Tag = "skin";
                sw.BorderStyle = (st.Skin == i) ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                sw.Click += delegate
                {
                    st.Skin = idx;
                    foreach (Control c in scroll.Controls) { Panel p = c as Panel; if (p != null && p.Tag != null && (string)p.Tag == "skin") p.BorderStyle = BorderStyle.FixedSingle; }
                    sw.BorderStyle = BorderStyle.Fixed3D; owner.ApplySettings();
                };
                scroll.Controls.Add(sw);
            }
            y += 28;

            Lbl("文字颜色", 18, y + 6);
            CheckBox cbB = new CheckBox(); cbB.Text = "黑"; cbB.Location = new Point(180, y + 3); cbB.AutoSize = true; cbB.Checked = st.TextBlack;
            CheckBox cbW = new CheckBox(); cbW.Text = "白"; cbW.Location = new Point(240, y + 3); cbW.AutoSize = true; cbW.Checked = !st.TextBlack;
            cbB.CheckedChanged += delegate { if (cbB.Checked) { cbW.Checked = false; st.TextBlack = true; owner.ApplySettings(); } };
            cbW.CheckedChanged += delegate { if (cbW.Checked) { cbB.Checked = false; st.TextBlack = false; owner.ApplySettings(); } };
            scroll.Controls.Add(cbB); scroll.Controls.Add(cbW);
            y += 28;

            Lbl("透明度调节", 18, y + 8);
            TrackBar tb = new TrackBar();
            tb.Minimum = 30; tb.Maximum = 100; tb.TickFrequency = 10; tb.Value = st.Opacity;
            tb.Location = new Point(170, y); tb.Width = 220;
            tb.ValueChanged += delegate { st.Opacity = tb.Value; owner.ApplySettings(); };
            scroll.Controls.Add(tb);
            y += 38;

            Lbl("显示项目", 18, y);
            y += 22;
            Chk("CPU使用率", 18, y, st.ShowCpu, delegate(bool v) { st.ShowCpu = v; owner.ApplySettings(); });
            Chk("内存使用率", 210, y, st.ShowMem, delegate(bool v) { st.ShowMem = v; owner.ApplySettings(); });
            Chk("流量监控", 18, y + 24, st.ShowNet, delegate(bool v) { st.ShowNet = v; owner.ApplySettings(); });
            Chk("硬盘读写", 210, y + 24, st.ShowDisk, delegate(bool v) { st.ShowDisk = v; owner.ApplySettings(); });
            Chk("CPU温度", 18, y + 48, st.ShowCpuTemp, delegate(bool v) { st.ShowCpuTemp = v; owner.ApplySettings(); });
            Chk("显卡温度", 210, y + 48, st.ShowGpuTemp, delegate(bool v) { st.ShowGpuTemp = v; owner.ApplySettings(); });
            Chk("显卡热点", 18, y + 72, st.ShowGpuHot, delegate(bool v) { st.ShowGpuHot = v; owner.ApplySettings(); });
            Chk("主板温度", 210, y + 72, st.ShowBoardTemp, delegate(bool v) { st.ShowBoardTemp = v; owner.ApplySettings(); });
            Chk("硬盘温度", 18, y + 96, st.ShowDiskTemp, delegate(bool v) { st.ShowDiskTemp = v; owner.ApplySettings(); });
            Chk("内存温度", 210, y + 96, st.ShowDimmtemp, delegate(bool v) { st.ShowDimmtemp = v; owner.ApplySettings(); });
            y += 122;

            y = Toggle("温度折叠（只显示主温度）", y, st.CollapseTemps, delegate(bool v) { st.CollapseTemps = v; owner.ApplySettings(); });
            Lbl("主温度", 18, y + 6);
            ComboBox cbMain = new ComboBox();
            cbMain.DropDownStyle = ComboBoxStyle.DropDownList;
            cbMain.Items.AddRange(new object[] { "CPU", "显卡", "热点", "主板", "硬盘", "内存" });
            string[] mkeys = new string[] { "CPU", "GPU", "Hot", "Board", "Disk", "Dimm" };
            int mi = Array.IndexOf(mkeys, st.MainTemp); if (mi < 0) mi = 0;
            cbMain.SelectedIndex = mi;
            cbMain.Location = new Point(92, y + 3); cbMain.Width = 96;
            cbMain.SelectedIndexChanged += delegate { st.MainTemp = mkeys[cbMain.SelectedIndex]; owner.ApplySettings(); };
            scroll.Controls.Add(cbMain);
            y += 30;

            Label hint2 = new Label();
            hint2.AutoSize = false; hint2.Size = new Size(378, 34); hint2.ForeColor = Color.Gray;
            hint2.Text = "点浮窗上的「网速」块 → 流量统计；点「主温度」块 → 在上方展开其它温度（只显示这里勾选过的）。";
            hint2.Location = new Point(18, y);
            scroll.Controls.Add(hint2);
            y += 38;

            Label lbAl = new Label(); lbAl.Text = "温度报警"; lbAl.ForeColor = Color.FromArgb(200, 30, 30); lbAl.Location = new Point(18, y); lbAl.AutoSize = true;
            scroll.Controls.Add(lbAl);
            y += 22;
            y = Toggle("启用温度报警（弹窗提醒）", y, st.Alarm, delegate(bool v) { st.Alarm = v; });
            y = Toggle("报警时播放提示音", y, st.AlarmSound, delegate(bool v) { st.AlarmSound = v; });
            y = Toggle("液冷异常检测（CPU 温升过快）", y, st.AlarmRise, delegate(bool v) { st.AlarmRise = v; });
            Hint2("需连续升温约 40 秒才报警，避免游戏启动等瞬时峰值误报", 18, y + 2);
            y += 24;

            Lbl("报警阈值（°C）", 18, y + 2);
            y += 26;
            Num("CPU", 18, y, st.LimCpu, delegate(int v) { st.LimCpu = v; });
            Num("显卡", 210, y, st.LimGpu, delegate(int v) { st.LimGpu = v; });
            Num("热点", 18, y + 28, st.LimHot, delegate(int v) { st.LimHot = v; });
            Num("主板", 210, y + 28, st.LimBoard, delegate(int v) { st.LimBoard = v; });
            Num("硬盘", 18, y + 56, st.LimDisk, delegate(int v) { st.LimDisk = v; });
            Num("内存", 210, y + 56, st.LimDimm, delegate(int v) { st.LimDimm = v; });
            NumR("温升(20秒)", 18, y + 84, st.RiseLimit, 5, 60, delegate(int v) { st.RiseLimit = v; });
            y += 116;

            Lbl("DSH 手机入口哨兵", 18, y + 4);
            y += 26;
            y = Toggle("监控 3081 手机入口（陌生设备报警并阻断）", y, st.Sentinel, delegate(bool v) { st.Sentinel = v; });
            y = Toggle("放行本机自测连接", y, st.SentinelSelf, delegate(bool v) { st.SentinelSelf = v; });
            TextBox tbWl = new TextBox();
            tbWl.Text = st.SentinelWhitelist; tbWl.Font = new Font("Microsoft YaHei UI", 8f);
            tbWl.Location = new Point(18, y + 3); tbWl.Width = 300;
            tbWl.TextChanged += delegate { st.SentinelWhitelist = tbWl.Text; };
            scroll.Controls.Add(tbWl);
            Button btnWl = new Button();
            btnWl.Text = "加入上次拦截"; btnWl.Font = new Font("Microsoft YaHei UI", 8f);
            btnWl.Size = new Size(84, 24); btnWl.Location = new Point(322, y + 2);
            btnWl.Click += delegate
            {
                if (LanSentinel.LastOffender.Length == 0) { MessageBox.Show("本次运行还没有拦截记录。", "Ray雷达", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                string cur = tbWl.Text.Trim().TrimEnd(',');
                tbWl.Text = (cur.Length == 0 ? "" : cur + ",") + LanSentinel.LastOffender;
            };
            scroll.Controls.Add(btnWl);
            y += 30;
            Hint2("白名单：逗号分隔的 IP 或 MAC（例 192.168.31.18,82-8B-68-6C-2F-FF）。只有本机开了 3081 转发时才生效；日志 %APPDATA%\\RayRadar\\sentinel.log", 18, y + 2);
            y += 34;

            y = Toggle("开机自启", y, st.AutoStart, delegate(bool v) { st.AutoStart = v; Settings.ApplyAutoStart(v); });

            Label tip = new Label();
            tip.AutoSize = false; tip.Size = new Size(240, 54);
            tip.Text = "温度来源：LibreHardwareMonitor + PawnIO（已签名）。\r\n驱动状态：" + (Driver.Installed() ? "已安装" : "未安装") + "。\r\n本程序固定以管理员权限运行。";
            tip.ForeColor = Color.Gray; tip.Location = new Point(18, y + 2);
            scroll.Controls.Add(tip);

            Button btnDrv = new Button();
            btnDrv.Text = Driver.Installed() ? "重装温度驱动" : "安装温度驱动";
            btnDrv.Font = new Font("Microsoft YaHei UI", 8f);
            btnDrv.Size = new Size(130, 26); btnDrv.Location = new Point(266, y + 4);
            btnDrv.Click += delegate
            {
                if (Driver.Installed() && MessageBox.Show("温度驱动已安装，要重新安装一遍吗？", "Ray雷达", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Cursor = Cursors.WaitCursor;
                bool ok = Driver.Install();
                Cursor = Cursors.Default;
                btnDrv.Text = Driver.Installed() ? "重装温度驱动" : "安装温度驱动";
                MessageBox.Show(ok ? "温度驱动已就绪。若温度仍显示 —，请关闭并重新打开 Ray雷达。" : "安装未完成（可能被取消）。可稍后再试。",
                    "Ray雷达", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            scroll.Controls.Add(btnDrv);

            CheckBox chkDrv = new CheckBox();
            chkDrv.Text = "缺驱动时启动提醒"; chkDrv.Font = new Font("Microsoft YaHei UI", 8f);
            chkDrv.Location = new Point(266, y + 34); chkDrv.AutoSize = true; chkDrv.Checked = st.DriverAsk;
            chkDrv.CheckedChanged += delegate { st.DriverAsk = chkDrv.Checked; };
            scroll.Controls.Add(chkDrv);
            y += 64;

            FormClosing += delegate { st.Save(); };
            scroll.AutoScrollMinSize = new Size(0, y + 8);

            // 底部按钮固定不滚动
            Button btn = new Button(); btn.Text = "关闭";
            btn.Size = new Size(100, 30); btn.Location = new Point(18, ClientSize.Height - 42);
            btn.Click += delegate { Close(); };
            Controls.Add(btn);

            Button btnExit = new Button(); btnExit.Text = "退出 Ray雷达";
            btnExit.Size = new Size(120, 30); btnExit.Location = new Point(ClientSize.Width - 138, ClientSize.Height - 42);
            btnExit.Click += delegate { Close(); owner.Close(); };
            Controls.Add(btnExit);

            HookWheel(this);   // 鼠标滚轮：指针在任何控件上都能滚动内容
        }

        // 滚轮滚动内容区（数字框上滚动不会改数值，见 NoWheelNum）
        public void ScrollBy(int delta)
        {
            try
            {
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3;
                int step = (delta / 120) * lines * 20;
                int cur = -scroll.AutoScrollPosition.Y;
                int max = Math.Max(0, scroll.AutoScrollMinSize.Height - scroll.ClientSize.Height);
                int next = cur - step;
                if (next < 0) next = 0;
                if (next > max) next = max;
                scroll.AutoScrollPosition = new Point(0, next);
            }
            catch { }
        }

        void HookWheel(Control c)
        {
            c.MouseWheel += delegate(object s, MouseEventArgs e) { ScrollBy(e.Delta); };
            foreach (Control ch in c.Controls) HookWheel(ch);
        }

        // 数字框默认滚轮会改数值，这里屏蔽掉、改为滚动设置内容
        class NoWheelNum : NumericUpDown
        {
            public SettingsForm F;
            protected override void OnMouseWheel(MouseEventArgs e) { if (F != null) F.ScrollBy(e.Delta); }
        }

        void Lbl(string t, int x, int y) { Label l = new Label(); l.Text = t; l.Location = new Point(x, y); l.AutoSize = true; scroll.Controls.Add(l); }
        void Hint2(string t, int x, int y) { Label l = new Label(); l.Text = t; l.Location = new Point(x, y); l.AutoSize = true; l.ForeColor = Color.Gray; l.Font = new Font("Microsoft YaHei UI", 8f); scroll.Controls.Add(l); }
        int Toggle(string text, int y, bool val, Changer onChange)
        {
            Lbl(text, 18, y + 6);
            ToggleSwitch t = new ToggleSwitch();
            t.Location = new Point(ClientSize.Width - 74, y + 2); t.Checked = val;
            t.CheckedChanged += delegate { onChange(t.Checked); };
            scroll.Controls.Add(t);
            return y + 30;
        }
        void Toggle2(string text, int x, int y, bool val, Changer onChange)
        {
            Lbl(text, x, y + 6);
            ToggleSwitch t = new ToggleSwitch();
            t.Location = new Point(x + 140, y + 2); t.Checked = val;
            t.CheckedChanged += delegate { onChange(t.Checked); };
            scroll.Controls.Add(t);
        }
        void Chk(string text, int x, int y, bool val, Changer onChange)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Location = new Point(x, y); c.AutoSize = true; c.Checked = val;
            if (onChange != null) c.CheckedChanged += delegate { onChange(c.Checked); };
            scroll.Controls.Add(c);
        }
        void Num(string label, int x, int y, int val, ChangerInt onChange) { NumR(label, x, y, val, 20, 120, onChange); }
        void NumR(string label, int x, int y, int val, int min, int max, ChangerInt onChange)
        {
            Lbl(label, x, y + 3);
            NoWheelNum n = new NoWheelNum();
            n.F = this;
            n.Minimum = min; n.Maximum = max; n.Value = Math.Max(min, Math.Min(max, val));
            n.Location = new Point(x + 82, y); n.Width = 60;
            n.ValueChanged += delegate { onChange((int)n.Value); };
            scroll.Controls.Add(n);
        }
    }
    // ===== 流量统计：按天累计（只统计 Ray雷达 运行期间；Windows 本身不提供按天历史）=====
    public static class Traffic
    {
        // 数据文件路径；可用环境变量 RAYRADAR_TRAFFIC_DATA 覆盖（自测/演示用，不影响正式数据）
        public static string FilePath
        {
            get
            {
                try
                {
                    string custom = Environment.GetEnvironmentVariable("RAYRADAR_TRAFFIC_DATA");
                    if (!string.IsNullOrEmpty(custom)) return custom;
                }
                catch { }
                return Path.Combine(Settings.DirPath, "traffic.dat");
            }
        }
        static Dictionary<string, long[]> days = new Dictionary<string, long[]>();
        static bool loaded = false;

        public static void Load()
        {
            try
            {
                days.Clear();
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length < 3) continue;
                        long rx, tx;
                        if (!long.TryParse(p[1], out rx) || !long.TryParse(p[2], out tx)) continue;
                        days[p[0]] = new long[] { rx, tx };
                    }
                }
            }
            catch { }
            loaded = true;
        }
        public static void Add(long rx, long tx)
        {
            if (!loaded) Load();
            string k = DateTime.Now.ToString("yyyy-MM-dd");
            long[] v;
            if (!days.TryGetValue(k, out v)) { v = new long[2]; days[k] = v; }
            v[0] += rx; v[1] += tx;
        }
        public static void Save()
        {
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                List<string> keys = new List<string>(days.Keys); keys.Sort();
                List<string> lines = new List<string>();
                foreach (string k in keys) lines.Add(k + "\t" + days[k][0] + "\t" + days[k][1]);
                // 先写临时文件再替换，避免写一半断电把历史数据写坏
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        // 升序返回最近 days 天（没有记录的日子补 0）——画柱状图用
        public static List<KeyValuePair<string, long[]>> RecentAsc(int count)
        {
            if (!loaded) Load();
            List<KeyValuePair<string, long[]>> list = new List<KeyValuePair<string, long[]>>();
            DateTime today = DateTime.Today;
            for (int i = count - 1; i >= 0; i--)
            {
                string k = today.AddDays(-i).ToString("yyyy-MM-dd");
                long[] v;
                if (days.ContainsKey(k)) v = days[k]; else v = new long[2];
                list.Add(new KeyValuePair<string, long[]>(k, v));
            }
            return list;
        }

        // 全部有记录的日子，按日期倒序（表格用；最多 max 条，默认保留一整年）
        public static List<KeyValuePair<string, long[]>> AllDays(int max)
        {
            if (!loaded) Load();
            List<string> keys = new List<string>(days.Keys);
            keys.Sort();
            keys.Reverse();
            List<KeyValuePair<string, long[]>> list = new List<KeyValuePair<string, long[]>>();
            foreach (string k in keys)
            {
                if (list.Count >= max) break;
                list.Add(new KeyValuePair<string, long[]>(k, days[k]));
            }
            return list;
        }
        public static DateTime FirstDay()
        {
            if (!loaded) Load();
            DateTime first = DateTime.MaxValue;
            foreach (string k in days.Keys)
            {
                DateTime d;
                if (DateTime.TryParse(k, out d) && d < first) first = d;
            }
            return first == DateTime.MaxValue ? DateTime.MinValue : first;
        }
        public static void Sum(int backDays, out long rx, out long tx)
        {
            if (!loaded) Load();
            rx = 0; tx = 0;
            DateTime today = DateTime.Today, from = today.AddDays(-(backDays - 1));
            foreach (KeyValuePair<string, long[]> kv in days)
            {
                DateTime d;
                if (!DateTime.TryParse(kv.Key, out d)) continue;
                if (d.Date < from || d.Date > today) continue;
                rx += kv.Value[0]; tx += kv.Value[1];
            }
        }
        public static List<KeyValuePair<string, long[]>> Recent(int n)
        {
            if (!loaded) Load();
            List<KeyValuePair<string, long[]>> list = new List<KeyValuePair<string, long[]>>();
            DateTime today = DateTime.Today;
            for (int i = 0; i < n; i++)
            {
                string k = today.AddDays(-i).ToString("yyyy-MM-dd");
                long[] v;
                if (days.TryGetValue(k, out v)) list.Add(new KeyValuePair<string, long[]>(k, v));
            }
            return list;
        }
        public static string Size(long bytes)
        {
            double g = bytes / 1073741824.0;
            if (g >= 1) return g.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            double m = bytes / 1048576.0;
            if (m >= 1) return m.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
    }

    // ===== 流量趋势图：手绘堆叠柱状图（下行=蓝，上行=橙），带坐标轴、图例与悬停提示 =====
    public class TrafficChart : Control
    {
        public static readonly Color ColDown = Color.FromArgb(45, 120, 240);
        public static readonly Color ColUp = Color.FromArgb(245, 158, 11);
        static readonly Color ColGrid = Color.FromArgb(237, 239, 242);
        static readonly Color ColAxis = Color.FromArgb(155, 158, 165);
        static readonly Color ColText = Color.FromArgb(80, 84, 92);

        int days = 14;
        List<KeyValuePair<string, long[]>> data = new List<KeyValuePair<string, long[]>>();
        int hover = -1;
        Point mouse = new Point(-1, -1);

        public TrafficChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }
        public int Days { get { return days; } }
        public void SetDays(int d) { days = d; Reload(); }
        public void Reload() { data = Traffic.RecentAsc(days); hover = -1; Invalidate(); }

        Rectangle Plot { get { return new Rectangle(68, 30, Math.Max(20, Width - 68 - 16), Math.Max(20, Height - 30 - 30)); } }

        public static GraphicsPath RRect(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure(); return p;
        }
        // 把上限凑成 1/2/5×10^n，坐标轴刻度才好看
        static long NiceCeil(long v)
        {
            if (v <= 4096) return 4096;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double n = v / p;
            double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
            return (long)(nice * p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            int idx = -1;
            Rectangle p = Plot;
            if (data.Count > 0 && e.X >= p.Left && e.X <= p.Right && e.Y >= p.Top - 6 && e.Y <= p.Bottom + 6)
            {
                int slot = Math.Max(1, p.Width / data.Count);
                idx = (e.X - p.Left) / slot;
                if (idx < 0 || idx >= data.Count) idx = -1;
            }
            if (idx != hover) { hover = idx; Invalidate(); }
            else if (idx >= 0) Invalidate();   // 提示框跟随鼠标
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            Rectangle p = Plot;

            // 图例
            using (Font f = new Font("Microsoft YaHei UI", 8.5f))
            using (SolidBrush bTxt = new SolidBrush(ColText))
            {
                int lx = p.Right - 132;
                using (SolidBrush b = new SolidBrush(ColDown)) g.FillRectangle(b, lx, 11, 12, 12);
                g.DrawString("下行", f, bTxt, lx + 17, 10);
                using (SolidBrush b = new SolidBrush(ColUp)) g.FillRectangle(b, lx + 62, 11, 12, 12);
                g.DrawString("上行", f, bTxt, lx + 79, 10);
            }

            long max = 1;
            foreach (KeyValuePair<string, long[]> kv in data)
            {
                long t = kv.Value[0] + kv.Value[1];
                if (t > max) max = t;
            }
            long top = NiceCeil(max);

            // Y 轴网格与刻度（自动 KB / MB / GB）
            using (Font f = new Font("Microsoft YaHei UI", 8f))
            using (Pen grid = new Pen(ColGrid))
            using (SolidBrush ax = new SolidBrush(ColAxis))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = p.Bottom - (int)((long)p.Height * i / 4);
                    g.DrawLine(grid, p.Left, y, p.Right, y);
                    string lab = (i == 0) ? "0" : Traffic.Size(top * i / 4);
                    SizeF sz = g.MeasureString(lab, f);
                    g.DrawString(lab, f, ax, p.Left - 8 - sz.Width, y - sz.Height / 2);
                }
            }

            if (data.Count == 0)
            {
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b = new SolidBrush(ColAxis))
                {
                    string s = "暂无数据";
                    SizeF sz = g.MeasureString(s, f);
                    g.DrawString(s, f, b, p.Left + (p.Width - sz.Width) / 2, p.Top + (p.Height - sz.Height) / 2);
                }
                return;
            }

            int slot = Math.Max(1, p.Width / data.Count);
            int bw = Math.Max(5, (int)(slot * 0.6));
            for (int i = 0; i < data.Count; i++)
            {
                long rx = data[i].Value[0], tx = data[i].Value[1];
                int x = p.Left + slot * i + (slot - bw) / 2;
                int hDown = (int)((double)rx / top * p.Height);
                int hUp = (int)((double)tx / top * p.Height);
                if (hDown < 1 && rx > 0) hDown = 1;
                if (hUp < 1 && tx > 0) hUp = 1;
                if (hDown > 0) using (SolidBrush b = new SolidBrush(i == hover ? ControlPaint.Dark(ColDown, 0.06f) : ColDown)) g.FillRectangle(b, new Rectangle(x, p.Bottom - hDown, bw, hDown));
                if (hUp > 0) using (SolidBrush b = new SolidBrush(i == hover ? ControlPaint.Dark(ColUp, 0.06f) : ColUp)) g.FillRectangle(b, new Rectangle(x, p.Bottom - hDown - hUp, bw, hUp));
                if (hDown + hUp == 0)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(226, 229, 233))) g.FillRectangle(b, new Rectangle(x, p.Bottom - 2, bw, 2));
                }
                bool showLab = data.Count <= 16 || i % 2 == 0;
                if (showLab)
                {
                    string dl = data[i].Key.Length >= 10 ? data[i].Key.Substring(5) : data[i].Key;
                    using (Font f = new Font("Microsoft YaHei UI", 7.5f))
                    using (SolidBrush b = new SolidBrush(ColAxis))
                    {
                        SizeF sz = g.MeasureString(dl, f);
                        g.DrawString(dl, f, b, x + bw / 2f - sz.Width / 2, p.Bottom + 5);
                    }
                }
            }

            // 悬停提示框
            if (hover >= 0 && hover < data.Count)
            {
                KeyValuePair<string, long[]> kv = data[hover];
                string l1 = kv.Key;
                string l2 = "↓ 下行　" + Traffic.Size(kv.Value[0]);
                string l3 = "↑ 上行　" + Traffic.Size(kv.Value[1]);
                string l4 = "合计　　" + Traffic.Size(kv.Value[0] + kv.Value[1]);
                using (Font f = new Font("Microsoft YaHei UI", 8.5f))
                using (Font fb = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                {
                    SizeF s1 = g.MeasureString(l1, fb), s2 = g.MeasureString(l2, f), s3 = g.MeasureString(l3, f), s4 = g.MeasureString(l4, fb);
                    int w = (int)Math.Max(Math.Max(s1.Width, s2.Width), Math.Max(s3.Width, s4.Width)) + 22;
                    int h = (int)(s1.Height + s2.Height + s3.Height + s4.Height) + 18;
                    int bx = mouse.X + 16, by = mouse.Y - h - 8;
                    if (bx + w > Width - 4) bx = mouse.X - w - 16;
                    if (bx < 4) bx = 4;
                    if (by < 4) by = mouse.Y + 18;
                    if (by + h > Height - 4) by = Height - h - 4;
                    Rectangle box = new Rectangle(bx, by, w, h);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(252, 252, 253))) g.FillPath(bg, RRect(box, 6));
                    using (Pen pen = new Pen(Color.FromArgb(205, 209, 216))) g.DrawPath(pen, RRect(box, 6));
                    float ty = by + 8;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 72))) g.DrawString(l1, fb, b, bx + 11, ty);
                    ty += s1.Height;
                    using (SolidBrush b = new SolidBrush(ColDown)) g.DrawString(l2, f, b, bx + 11, ty);
                    ty += s2.Height;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 120, 10))) g.DrawString(l3, f, b, bx + 11, ty);
                    ty += s3.Height;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 72))) g.DrawString(l4, fb, b, bx + 11, ty);
                }
            }
        }
    }

    // 流量统计窗口（点浮窗上的「网速块」打开）：上=总览卡片，中=柱状图，下=历史明细
    public class TrafficForm : Form
    {
        TrafficChart chart;
        DataGridView grid;
        Button b14, b30;

        public TrafficForm()
        {
            Text = "Ray雷达 - 流量统计";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(780, 672);
            BackColor = Color.FromArgb(247, 248, 250);
            Font = new Font("Microsoft YaHei UI", 9f);
            TopMost = true;

            Label title = new Label();
            title.Text = "流量统计"; title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(40, 44, 52); title.AutoSize = true; title.Location = new Point(20, 14);
            Controls.Add(title);

            DateTime first = Traffic.FirstDay();
            Label note = new Label();
            note.AutoSize = false; note.Size = new Size(560, 20);
            note.ForeColor = Color.FromArgb(140, 144, 152);
            note.Font = new Font("Microsoft YaHei UI", 8.5f);
            note.Text = first == DateTime.MinValue
                ? "暂无数据：Ray雷达 只在运行时统计，从今天开始累计"
                : "统计起始 " + first.ToString("yyyy-MM-dd") + "　·　只统计 Ray雷达 运行期间，按天累计（Windows 无按天历史接口）";
            note.Location = new Point(22, 40);
            Controls.Add(note);

            // ── 总览卡片 ──
            long rx, tx;
            int cx = 20;
            Traffic.Sum(1, out rx, out tx); Card("今天", "最近 1 天", rx, tx, cx); cx += 250;
            Traffic.Sum(30, out rx, out tx); Card("最近 30 天", null, rx, tx, cx); cx += 250;
            Traffic.Sum(365, out rx, out tx); Card("最近 1 年", null, rx, tx, cx);

            // ── 图表区 ──
            Label lbChart = new Label();
            lbChart.Text = "流量趋势"; lbChart.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lbChart.ForeColor = Color.FromArgb(40, 44, 52); lbChart.AutoSize = true; lbChart.Location = new Point(20, 184);
            Controls.Add(lbChart);

            b14 = MkTab("最近 14 天", 552, true);
            b30 = MkTab("最近 30 天", 646, false);
            b14.Click += delegate { SetChart(14); };
            b30.Click += delegate { SetChart(30); };

            chart = new TrafficChart();
            chart.Location = new Point(20, 210);
            chart.Size = new Size(740, 232);
            chart.SetDays(14);
            Controls.Add(chart);

            // ── 历史明细 ──
            Label lbHist = new Label();
            lbHist.Text = "历史明细（可滚动）"; lbHist.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lbHist.ForeColor = Color.FromArgb(40, 44, 52); lbHist.AutoSize = true; lbHist.Location = new Point(20, 450);
            Controls.Add(lbHist);

            grid = new DataGridView();
            grid.Location = new Point(20, 476); grid.Size = new Size(740, 148);
            grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(238, 240, 243);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 30;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(90, 94, 102);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(244, 246, 249);
            grid.RowTemplate.Height = 26;
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(60, 64, 72);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 235, 254);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 34, 40);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);   // 斑马纹
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            Controls.Add(grid);
            grid.Columns.Add("d", "日期");
            grid.Columns.Add("rx", "下行");
            grid.Columns.Add("tx", "上行");
            grid.Columns.Add("all", "合计");
            grid.Columns[1].DefaultCellStyle.ForeColor = TrafficChart.ColDown;
            grid.Columns[2].DefaultCellStyle.ForeColor = Color.FromArgb(200, 120, 10);
            grid.Columns[3].DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            foreach (KeyValuePair<string, long[]> kv in Traffic.AllDays(366))
                grid.Rows.Add(kv.Key, Traffic.Size(kv.Value[0]), Traffic.Size(kv.Value[1]), Traffic.Size(kv.Value[0] + kv.Value[1]));
            grid.ClearSelection();          // 别默认选中第一行
            grid.CurrentCell = null;

            Button ok = new Button();
            ok.Text = "关闭"; ok.Size = new Size(100, 30);
            ok.FlatStyle = FlatStyle.System;
            ok.Location = new Point(ClientSize.Width - 120, ClientSize.Height - 38);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
            AcceptButton = ok;
        }

        void SetChart(int d)
        {
            chart.SetDays(d);
            Style(b14, d == 14); Style(b30, d == 30);
        }

        Button MkTab(string text, int x, bool active)
        {
            Button b = new Button();
            b.Text = text; b.Size = new Size(88, 26); b.Location = new Point(x, 181);
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Microsoft YaHei UI", 8.5f);
            Controls.Add(b);
            Style(b, active);
            return b;
        }
        static void Style(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(45, 120, 240) : Color.FromArgb(232, 235, 240);
            b.ForeColor = active ? Color.White : Color.FromArgb(90, 94, 102);
        }

        // 总览卡片：圆角白底 + 下行(蓝)/上行(橙)/合计
        void Card(string head, string sub, long rx, long tx, int x)
        {
            Panel p = new Panel();
            p.Location = new Point(x, 62); p.Size = new Size(240, 106);
            p.BackColor = Color.White;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Rectangle box = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using (Pen pen = new Pen(Color.FromArgb(230, 233, 238))) g.DrawPath(pen, TrafficChart.RRect(box, 8));
                using (Font fh = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                using (Font fs = new Font("Microsoft YaHei UI", 7.5f))
                using (Font fv = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold))
                using (Font ft = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 72))) g.DrawString(head, fh, b, 14, 10);
                    if (sub != null) using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 154, 162))) g.DrawString(sub, fs, b, 14 + g.MeasureString(head, fh).Width + 6, 13);
                    using (SolidBrush b = new SolidBrush(TrafficChart.ColDown))
                    { g.DrawString("↓ 下行", fs, b, 14, 38); g.DrawString(Traffic.Size(rx), fv, b, 14, 52); }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 120, 10)))
                    { g.DrawString("↑ 上行", fs, b, 128, 38); g.DrawString(Traffic.Size(tx), fv, b, 128, 52); }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 74, 82)))
                        g.DrawString("合计 " + Traffic.Size(rx + tx), ft, b, 14, 80);
                }
            };
            Controls.Add(p);
        }
    }

    // 温度展开浮层（点主温度块时弹在浮窗上方；只显示设置里勾选、且不是主温度的那些）
    public class TempFlyout : Form
    {
        RadarForm owner;
        public TempFlyout(RadarForm o)
        {
            owner = o;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "Ray雷达温度"; DoubleBuffered = true;
            TopMost = true;
        }
        public void Rebuild()
        {
            int n = owner.FlyoutItems().Count;
            if (n < 1) n = 1;
            ClientSize = new Size(n * 40, 40);
            ApplyTopMost();
            Invalidate();
        }
        // 与主浮窗同样的置顶写法（直接 SetWindowPos 无效，必须切 WinForms 的 TopMost）
        public void ApplyTopMost()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (TopMost) TopMost = false;
                TopMost = true;
            }
            catch { }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            List<string[]> items = owner.FlyoutItems();
            Color[] sk = owner.SkinColors();
            Color fg = owner.FgColor();
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            int x = 0, i = 0;
            foreach (string[] it in items)
            {
                using (SolidBrush b = new SolidBrush(sk[i % 3])) g.FillRectangle(b, new Rectangle(x, 0, 40, 40));
                i++;
                using (SolidBrush b = new SolidBrush(fg))
                {
                    g.DrawString(it[0], owner.CapFont, b, new RectangleF(x, 1, 40, 15), sf);
                    g.DrawString(it[1], owner.ValFont, b, new RectangleF(x, 17, 40, 22), sf);
                }
                x += 40;
            }
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Hide(); }   // 点浮层本身即收起
    }

    public delegate void ChangerInt(int v);
}
