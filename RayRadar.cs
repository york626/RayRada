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
        // v4.15「竞彩计算器服务器」：把竞彩计算器的本地 node 静态服务器并进雷达管理
        // （雷达由计划任务开机自启 ⇒ 勾上就等于开机自启，而且全程免 UAC、不弹黑窗口）
        public bool CalcServer = true;           // 随雷达一起启动
        public string CalcDir = "";              // 网页目录；留空 = 我的文档\DSH常用\竞彩计算器

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
                        case "CalcServer": s.CalcServer = b; break;
                        case "CalcDir": s.CalcDir = v; break;
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
                ab("CalcServer", CalcServer); L.Add("CalcDir=" + CalcDir);
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
        // 左边那个「动作」按钮被点了（v4.13：入口拦截弹窗用它做「重开手机入口」）
        public bool AltClicked = false;

        // 按内容自动换行排版：长句不再被窗口右侧裁掉（用户 2026-09-18 反馈）
        public AlertForm(string title, List<string> lines, string hint) : this(title, lines, hint, null, null, null) { }

        // headText：弹窗里那行红色大字。不传＝「温度报警」；入口拦截等其它报警自己传（v4.12 修：以前写死成温度报警）
        public AlertForm(string title, List<string> lines, string hint, string headText) : this(title, lines, hint, headText, null, null) { }

        // okText：右下按钮（默认「知道了」）；altText：左下按钮（传了才出现，例如「重开手机入口」）
        public AlertForm(string title, List<string> lines, string hint, string headText, string okText, string altText)
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
            head.Text = string.IsNullOrEmpty(headText) ? "⚠ 温度报警" : headText; head.Font = fHead;
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
            int bw = 110, bh = 30, bgap = 12;
            bool two = (altText != null && altText.Length > 0);
            int left = (ClientSize.Width - (two ? bw * 2 + bgap : bw)) / 2;
            int top = ClientSize.Height - 42;
            if (two)
            {
                Button alt = new Button();
                alt.Text = altText; alt.Size = new Size(bw, bh); alt.Location = new Point(left, top);
                alt.Click += delegate { AltClicked = true; Close(); };
                Controls.Add(alt);
            }
            Button ok = new Button();
            ok.Text = (okText == null || okText.Length == 0) ? "知道了" : okText;
            ok.Size = new Size(bw, bh);
            ok.Location = new Point(two ? left + bw + bgap : left, top);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
            AcceptButton = ok;      // 回车＝右边那个（入口拦截时是「保持关闭」，不会误触重开）
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

        // ===== v4.11：手机入口的开/关做进设置窗（雷达本身就是管理员运行，所以不用再点 UAC）=====

        static int RunNetsh(string args, out string output)
        {
            output = "";
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        // portproxy 里指定端口的所有转发行（listenaddress, listenport）
        static List<string[]> EntryRows(int port)
        {
            List<string[]> rows = new List<string[]>();
            string outp;
            RunNetsh("interface portproxy show v4tov4", out outp);
            foreach (string line in outp.Split('\n'))
            {
                Match mm = Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3})\s+(\d+)\s+(\d{1,3}(?:\.\d{1,3}){3})\s+(\d+)");
                if (!mm.Success) continue;
                if (mm.Groups[2].Value != port.ToString()) continue;
                rows.Add(new string[] { mm.Groups[1].Value, mm.Groups[2].Value });
            }
            return rows;
        }

        // 当前内网 IPv4：优先「有默认网关」的那块网卡（跳过回环与 169.254 自动地址）
        public static string LanIp()
        {
            try
            {
                string fallback = "";
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool hasGw = (props.GatewayAddresses.Count > 0);
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        if (hasGw) return s;
                        if (fallback.Length == 0) fallback = s;
                    }
                }
                return fallback;
            }
            catch { return ""; }
        }

        public static bool EntryOpen() { return RefreshEntry() && ListenPort > 0; }

        public static string StateText()
        {
            if (!EntryOpen()) return "未开启（手机现在连不上这台电脑）";
            return "已开启：" + ListenIp + ":" + ListenPort.ToString() + " → 127.0.0.1:3082";
        }

        // 开：先删掉该端口的旧转发 → 按当前内网 IP 重建 → 确保防火墙放行
        public static string OpenEntry()
        {
            string ip = LanIp();
            if (ip.Length == 0) return "找不到内网 IP：请先连上路由器（网线或 Wi-Fi）。";
            string outp;
            foreach (string[] r in EntryRows(3081)) RunNetsh("interface portproxy delete v4tov4 listenaddress=" + r[0] + " listenport=" + r[1], out outp);
            int rc = RunNetsh("interface portproxy add v4tov4 listenaddress=" + ip + " listenport=3081 connectaddress=127.0.0.1 connectport=3082", out outp);
            if (rc != 0)
            {
                Log("⚠ 重开手机入口失败（netsh exit " + rc.ToString() + "）：" + outp.Trim());
                return "开启失败（netsh exit " + rc.ToString() + "）：" + outp.Trim();
            }
            string fw;
            RunNetsh("advfirewall firewall show rule name=\"DSH Web LAN 3081\"", out fw);
            if (fw.IndexOf("DSH Web LAN 3081") < 0)
                RunNetsh("advfirewall firewall add rule name=\"DSH Web LAN 3081\" dir=in action=allow protocol=TCP localport=3081 remoteip=localsubnet", out fw);
            entryCheckedAt = DateTime.MinValue;
            lastAlarm.Clear();   // v4.13：重开入口后重新判定 —— 陌生设备立刻再拦一次，不享受 60 秒冷却
            Log("已重开手机入口（手动）：" + ip + ":3081 → 127.0.0.1:3082");
            return "已开启：" + ip + ":3081 → 127.0.0.1:3082";
        }

        // 关：删掉该端口的所有转发（等于把入口关掉）
        public static string CloseEntry()
        {
            List<string[]> rows = EntryRows(3081);
            if (rows.Count == 0) return "入口当前未开启，无需关闭。";
            string outp;
            foreach (string[] r in rows) RunNetsh("interface portproxy delete v4tov4 listenaddress=" + r[0] + " listenport=" + r[1], out outp);
            entryCheckedAt = DateTime.MinValue;
            Log("已在设置窗关闭手机入口（删掉 " + rows.Count.ToString() + " 条转发）");
            return "已关闭手机入口（删掉 " + rows.Count.ToString() + " 条转发）。手机现在连不上。";
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
                    if (wl.ContainsKey(ip.ToUpperInvariant())) continue;                        // IP 命中白名单（先判 IP，省掉白名单设备的 ARP 查询）
                    string mac = MacOf(ip);                                                     // v4.14：ARP 放到 IP 判定之后
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
                h.Why = why[i];
                Log("⚠ 陌生设备连接入口：" + ip + " —— 立即阻断");
                if (doBlock)
                {
                    int k = KillConnections(ip, ListenPort);   // 先切断对方已建立的连接（双保险）
                    h.Blocked = Block();                       // 再删掉转发，让新连接也进不来
                    h.Mac = MacOf(ip);                         // MAC 只为写日志，放在阻断之后（ARP 慢也不拖慢拦截）
                    LastOffender = h.Ip + (h.Mac.Length > 0 ? "," + h.Mac : "");
                    Log(h.Blocked
                        ? ("已自动关闭手机入口并切断其连接（阻断 " + ip + "，切断 " + k.ToString() + " 条 TCP）")
                        : "⚠ 自动阻断失败（需要管理员权限）");
                }
                else
                {
                    h.Mac = MacOf(ip);
                    LastOffender = h.Ip + (h.Mac.Length > 0 ? "," + h.Mac : "");
                }
                hits.Add(h);
            }
            return hits;
        }

        // ===== v4.14：① 扫描挪到后台线程（界面永不卡） ② 阻断时顺手切断对方已建立的 TCP 连接 =====
        static Thread worker = null;
        static volatile bool workerStop = false;
        public static int ScanMs = 500;                              // 扫描间隔（毫秒）
        public static Action<List<SentinelHit>> OnHits = null;       // RadarForm 设置；内部会切回 UI 线程弹窗

        public static void StartWorker(Settings st)
        {
            if (worker != null || st == null) return;
            workerStop = false;
            worker = new Thread(delegate()
            {
                while (!workerStop)
                {
                    try { Thread.Sleep(ScanMs); } catch { }
                    if (workerStop) break;
                    try
                    {
                        List<SentinelHit> hits = Scan(st, true);
                        if (hits.Count > 0 && OnHits != null) OnHits(hits);
                    }
                    catch { }
                }
            });
            worker.IsBackground = true;
            try { worker.Start(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW
        {
            public int dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort;
        }

        [DllImport("iphlpapi.dll")]
        static extern int SetTcpEntry(ref MIB_TCPROW row);

        static int NetPort(int p) { return ((p & 0xFF) << 8) | ((p >> 8) & 0xFF); }

        // 强行切断某台设备在本机入口上的 TCP 连接（MIB_TCP_STATE_DELETE_TCB）——让它正在用的页面立刻断线
        public static int KillConnections(string ip, int port)
        {
            int n = 0;
            try
            {
                foreach (TcpConnectionInformation c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (c.LocalEndPoint.Port != port) continue;
                    if (c.RemoteEndPoint.Address.ToString() != ip) continue;
                    MIB_TCPROW row = new MIB_TCPROW();
                    row.dwState = 12;   // DELETE_TCB
                    row.dwLocalAddr = BitConverter.ToInt32(c.LocalEndPoint.Address.GetAddressBytes(), 0);
                    row.dwLocalPort = NetPort(c.LocalEndPoint.Port);
                    row.dwRemoteAddr = BitConverter.ToInt32(c.RemoteEndPoint.Address.GetAddressBytes(), 0);
                    row.dwRemotePort = NetPort(c.RemoteEndPoint.Port);
                    if (SetTcpEntry(ref row) == 0) n++;
                }
            }
            catch { }
            return n;
        }
    }

    // ===== v4.15：竞彩计算器本地服务器 =====
    // 竞彩计算器 = 一个单文件网页（index.html）+ 一个零依赖的 node 静态服务器（serve.mjs）。
    // 以前靠黑窗口或开机自启脚本，现在并进雷达统一托管：设置窗一个开关 + 启动/停止按钮 + 状态行。
    // 雷达本身以管理员运行、由计划任务开机自启 ⇒ 这个服务器跟着开机自启，且起停都不弹 UAC。
    // 服务器只监听本机端口（局域网可访问），**不转发任何数据**：赔率/开奖都是手机直接找竞彩官网取。
    public static class CalcServer
    {
        public static int Port = 8000;
        static System.Diagnostics.Process proc = null;   // 本程序启动的那个 node（用户在别处手动启动时为 null）
        static bool userStopped = false;                 // 用户在设置窗点了「停止服务」⇒ 看门狗不要自作主张拉起来

        // 网页目录：设置里填了就用填的，留空则用「我的文档\DSH常用\竞彩计算器」
        public static string Dir(Settings st)
        {
            if (st != null && st.CalcDir != null && st.CalcDir.Trim().Length > 0) return st.CalcDir.Trim();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                Path.Combine("DSH常用", "竞彩计算器"));
        }
        public static string ScriptPath(Settings st) { return Path.Combine(Dir(st), "serve.mjs"); }

        // node.exe：先看几个常见安装位置，再退回 PATH 里的 where node
        public static string NodeExe()
        {
            string[] cand = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"nodejs\node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\nodejs\node.exe")
            };
            foreach (string c in cand) { try { if (File.Exists(c)) return c; } catch { } }
            try
            {
                string outp;
                if (Run("where.exe", "node", out outp) == 0)
                    foreach (string line in outp.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length > 3 && File.Exists(t)) return t;
                    }
            }
            catch { }
            return "";
        }

        // 通用命令执行（照 LanSentinel.RunNetsh 的写法，只是命令名可变）
        static int Run(string exe, string args, out string output)
        {
            output = "";
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        // 监听该端口的进程 PID（netstat 才给 PID，netsh 不给）
        public static List<int> ListenerPids()
        {
            List<int> r = new List<int>();
            string outp;
            Run("netstat.exe", "-ano", out outp);
            if (outp.Length == 0) return r;
            foreach (string line in outp.Split('\n'))
            {
                string t = line.Trim();
                if (t.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string[] parts = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                string local = parts[1];
                int c = local.LastIndexOf(':');
                if (c < 0) continue;
                int p;
                if (!int.TryParse(local.Substring(c + 1), out p) || p != Port) continue;
                int pid;
                if (int.TryParse(parts[parts.Length - 1], out pid) && pid > 0) r.Add(pid);
            }
            return r;
        }

        public static bool Running() { return ListenerPids().Count > 0; }

        public static string Url()
        {
            string ip = LanSentinel.LanIp();
            if (ip.Length == 0) ip = "127.0.0.1";
            return "http://" + ip + ":" + Port.ToString() + "/";
        }

        public static string StateText(Settings st)
        {
            if (!File.Exists(ScriptPath(st))) return "未找到网页文件（" + ScriptPath(st) + "）";
            if (NodeExe().Length == 0) return "未找到 node.exe（请先安装 Node.js）";
            if (!Running()) return "未运行（手机现在打不开计算器）";
            return "运行中：" + Url();
        }

        public static string Start(Settings st)
        {
            string script = ScriptPath(st);
            if (!File.Exists(script))
                return "找不到网页文件：\r\n" + script + "\r\n\r\n该目录里应有 index.html 与 serve.mjs。";
            string node = NodeExe();
            if (node.Length == 0) return "找不到 node.exe：请先安装 Node.js（https://nodejs.org）。";
            if (Running()) return "服务器已经在运行：\r\n" + Url();
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(node, "\"" + script + "\" " + Port.ToString());
                psi.WorkingDirectory = Dir(st);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;                      // 不弹黑窗口
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                proc = System.Diagnostics.Process.Start(psi);
                userStopped = false;
                for (int i = 0; i < 25 && !Running(); i++) System.Threading.Thread.Sleep(200);
                if (Running()) return "已启动：\r\n" + Url();
                return "启动命令已发出，但端口 " + Port.ToString() + " 没在监听。\r\n（node 启动失败，或该端口被别的程序占用）";
            }
            catch (Exception ex) { return "启动失败：" + ex.Message; }
        }

        public static string Stop()
        {
            List<int> pids = ListenerPids();
            if (pids.Count == 0) return "服务器本来就没在运行。";
            userStopped = true;                          // 用户主动停的，看门狗别再拉起来
            int ok = 0;
            foreach (int pid in pids)
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); ok++; }
                catch
                {
                    string outp;
                    Run("taskkill.exe", "/PID " + pid.ToString() + " /F", out outp);
                    ok++;
                }
            }
            proc = null;
            for (int i = 0; i < 10 && Running(); i++) System.Threading.Thread.Sleep(200);
            return Running() ? "停止命令已发出，但端口仍在监听（可能有别的程序占着）。"
                             : "已停止（关闭 " + ok.ToString() + " 个进程）。";
        }

        // 随雷达启动；没勾选就什么都不做。失败只写日志，不打扰用户。
        public static void AutoStart(Settings st)
        {
            if (st == null || !st.CalcServer) return;
            try
            {
                if (!File.Exists(ScriptPath(st))) { LanSentinel.Log("竞彩计算器服务器：未启用（找不到 " + ScriptPath(st) + "）"); return; }
                string r = Start(st);
                LanSentinel.Log("竞彩计算器服务器：" + r.Replace("\r\n", " "));
            }
            catch { }
        }

        // ===== v4.16 看门狗：每 30 秒看一眼，进程没了就自动拉起来 =====
        // 起因（2026-09-24 用户反馈「网页打不开了」）：node 进程可能因外部原因被杀
        //（实测那次是别的程序清理进程树时把它带走了），而雷达只在启动时拉一次 ⇒ 之后就再也没人管。
        // 规则：① 开关关着 = 不管；② 用户手动停过 = 不管（不跟用户对着干）；③ 已经在监听 = 不管。
        public static void Watchdog(Settings st)
        {
            try
            {
                if (st == null || !st.CalcServer) return;
                if (userStopped) return;
                if (proc != null && !proc.HasExited) return;                  // 我们起的那个还活着（零成本判断）
                if (!File.Exists(ScriptPath(st))) return;
                if (NodeExe().Length == 0) return;
                if (proc == null && Running()) return;                        // 别处起的，不去抢
                bool wasDead = (proc != null && proc.HasExited);
                proc = null;
                string r = Start(st);
                LanSentinel.Log("竞彩计算器服务器（看门狗" + (wasDead ? "·进程已退出" : "") + "）：" + r.Replace("\r\n", " "));
            }
            catch { }
        }
    }

    public class RadarForm : Form
    {
        Settings st;
        System.Windows.Forms.Timer timer, tempTimer, calcTimer;
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
        int appTick = 0;                              // v4.17：每 5 秒把按应用增量搬一次（见 OnTick）

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
            // v4.16：竞彩计算器服务器看门狗（30 秒一次；核心判断不 spawn 进程，开销可忽略）
            calcTimer = new System.Windows.Forms.Timer(); calcTimer.Interval = 30000;
            calcTimer.Tick += delegate { CalcServer.Watchdog(st); }; calcTimer.Start();
            Shown += delegate
            {
                ApplyTopMost(); OnTempTick(null, null);
                LanSentinel.OnHits = OnSentinelHits;   // v4.14：哨兵在后台线程扫描，命中后切回 UI 线程弹窗
                LanSentinel.StartWorker(st);
                CalcServer.AutoStart(st);              // v4.15：竞彩计算器服务器（没勾选就什么都不做）
            };
            try { Traffic.Load(); } catch { }
            // v4.17：按应用流量统计（ETW）。起不来就优雅降级 —— 界面会写明原因，其余功能不受影响。
            try { AppTraffic.Prune(); } catch { }
            try { NetMonitor.Start(); } catch { }
            FormClosing += delegate
            {
                try { Traffic.Save(); } catch { }
                try { AppTraffic.Save(); } catch { }
                try { NetMonitor.Stop(); } catch { }
                HideFlyout();
            };
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
            // v4.17：每 5 秒把 ETW 采集到的「按应用增量」搬进 AppTraffic（重活在 NetMonitor 内部丢线程池）
            if (++appTick >= 5) { appTick = 0; try { NetMonitor.Tick(); } catch { } }
            pRx = rx; pTx = tx; pTime = now;
            if (st.ShowDisk && diskOk)
            { try { rdK = diskR.NextValue() / 1024.0; wrK = diskW.NextValue() / 1024.0; } catch { diskOk = false; } }
            if (++topTick >= 5) { topTick = 0; EnsureTopMost(); }

            // v4.14：DSH 手机入口哨兵已挪到后台线程（LanSentinel.StartWorker），界面线程不再做 netsh/ARP，
            // 所以浮窗不会再被扫描卡住；命中后由 OnSentinelHits 切回 UI 线程弹非模态窗。

            Invalidate();
            if (flyout != null && flyout.Visible) flyout.Invalidate();
        }

        // ===== v4.14：入口拦截弹窗（非模态，不冻结浮窗）=====
        static AlertForm sentinelAlert = null;   // 同一时间只留一个拦截弹窗

        void OnSentinelHits(List<SentinelHit> hits)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate { ShowSentinelAlert(hits); });   // 从后台线程切回 UI 线程
            }
            catch { }
        }

        void ShowSentinelAlert(List<SentinelHit> hits)
        {
            try
            {
                if (sentinelAlert != null && !sentinelAlert.IsDisposed) return;      // 已经有一个在显示，不再堆叠
                List<string> smsgs = new List<string>();
                foreach (SentinelHit h in hits)
                {
                    smsgs.Add("陌生设备连接手机入口：" + h.Ip + (h.Mac.Length > 0 ? "（" + h.Mac + "）" : "")
                        + (h.Blocked ? "，已自动关闭入口并切断连接" : "，⚠ 阻断失败，请手动运行『关闭手机入口.cmd』"));
                }
                if (st.AlarmSound) { try { SystemSounds.Hand.Play(); } catch { } }
                AlertForm f = new AlertForm("Ray雷达 · 入口拦截", smsgs,
                    "「重开手机入口」只是把入口开回来：白名单里的设备能进，陌生设备照样会被拦。若是自家设备被误拦，请先在设置里把它加进白名单再重开。",
                    "⚠ 陌生设备接入", "保持关闭", "重开手机入口");
                sentinelAlert = f;
                f.FormClosed += delegate
                {
                    sentinelAlert = null;
                    try
                    {
                        if (f.AltClicked)
                        {
                            string rr = LanSentinel.OpenEntry();
                            MessageBox.Show(rr, "Ray雷达 · 手机入口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }
                    try { f.Dispose(); } catch { }
                };
                f.Show();   // 非模态：浮窗继续刷新，哨兵继续扫描
            }
            catch { }
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

            Label lbEntry = new Label();
            lbEntry.AutoSize = true; lbEntry.Font = new Font("Microsoft YaHei UI", 8f); lbEntry.ForeColor = Color.Gray;
            lbEntry.Location = new Point(18, y + 4);
            lbEntry.Text = "入口状态：" + LanSentinel.StateText();
            scroll.Controls.Add(lbEntry);

            Button btnOpen = new Button();
            btnOpen.Text = "重开手机入口"; btnOpen.Font = new Font("Microsoft YaHei UI", 8f);
            btnOpen.Size = new Size(96, 24); btnOpen.Location = new Point(18, y + 24);
            btnOpen.Click += delegate
            {
                string r = LanSentinel.OpenEntry();
                lbEntry.Text = "入口状态：" + LanSentinel.StateText();
                MessageBox.Show(r + "\r\n\r\n若手机仍打不开：跑一次『开放手机入口.cmd』（它会同时更新信任栅栏，改完需重启 DSH）。",
                    "Ray雷达 · 手机入口", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            scroll.Controls.Add(btnOpen);

            Button btnClose = new Button();
            btnClose.Text = "关闭手机入口"; btnClose.Font = new Font("Microsoft YaHei UI", 8f);
            btnClose.Size = new Size(96, 24); btnClose.Location = new Point(122, y + 24);
            btnClose.Click += delegate
            {
                string r = LanSentinel.CloseEntry();
                lbEntry.Text = "入口状态：" + LanSentinel.StateText();
                MessageBox.Show(r, "Ray雷达 · 手机入口", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            scroll.Controls.Add(btnClose);
            y += 54;

            Hint2("白名单：逗号分隔的 IP 或 MAC（例 192.168.31.18,82-8B-68-6C-2F-FF）。只有本机开了 3081 转发时才生效；日志 %APPDATA%\\RayRadar\\sentinel.log", 18, y + 2);
            y += 34;

            Lbl("竞彩计算器服务器", 18, y + 4);
            y += 26;
            y = Toggle("随雷达一起启动（后台运行，不弹黑窗口）", y, st.CalcServer, delegate(bool v) { st.CalcServer = v; });
            TextBox tbCalc = new TextBox();
            tbCalc.Text = st.CalcDir; tbCalc.Font = new Font("Microsoft YaHei UI", 8f);
            tbCalc.Location = new Point(18, y + 3); tbCalc.Width = 300;
            tbCalc.TextChanged += delegate { st.CalcDir = tbCalc.Text; };
            scroll.Controls.Add(tbCalc);
            y += 30;

            Label lbCalc = new Label();
            lbCalc.AutoSize = true; lbCalc.Font = new Font("Microsoft YaHei UI", 8f); lbCalc.ForeColor = Color.Gray;
            lbCalc.Location = new Point(18, y + 4);
            lbCalc.Text = "服务器状态：" + CalcServer.StateText(st);
            scroll.Controls.Add(lbCalc);

            Button btnCalcOn = new Button();
            btnCalcOn.Text = "启动服务"; btnCalcOn.Font = new Font("Microsoft YaHei UI", 8f);
            btnCalcOn.Size = new Size(96, 24); btnCalcOn.Location = new Point(18, y + 24);
            btnCalcOn.Click += delegate
            {
                string r = CalcServer.Start(st);
                lbCalc.Text = "服务器状态：" + CalcServer.StateText(st);
                MessageBox.Show(r, "Ray雷达 · 竞彩计算器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            scroll.Controls.Add(btnCalcOn);

            Button btnCalcOff = new Button();
            btnCalcOff.Text = "停止服务"; btnCalcOff.Font = new Font("Microsoft YaHei UI", 8f);
            btnCalcOff.Size = new Size(96, 24); btnCalcOff.Location = new Point(122, y + 24);
            btnCalcOff.Click += delegate
            {
                string r = CalcServer.Stop();
                lbCalc.Text = "服务器状态：" + CalcServer.StateText(st);
                MessageBox.Show(r, "Ray雷达 · 竞彩计算器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            scroll.Controls.Add(btnCalcOff);
            y += 54;

            Hint2("网页目录：留空则用『我的文档\\DSH常用\\竞彩计算器』（该目录需含 index.html 与 serve.mjs）。手机连同一 Wi-Fi 打开状态行里的网址即可；赔率与开奖由手机直接向竞彩官网取，本服务器只发网页、不转发数据。v4.16 起带看门狗：进程意外退出会自动拉起（你自己点过「停止服务」则不再自动拉）。", 18, y + 2);
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

    // ===== 按应用（进程）流量统计（v4.17）=====
    //
    // 数据源：**ETW 实时会话**，provider「Microsoft-Windows-Kernel-Network」
    //   · GUID {7DD42A49-5329-4832-8DFD-43D979153A88}（本机 `logman query providers` 实测）
    //   · 内核在每次 TCP/UDP 收发时发一个事件：**事件头里带 PID**、载荷里带字节数
    //     ⇒ 不用抓包、不做「五元组 → PID」映射、不用内核驱动、不用第三方库（任务管理器「网络」列同源）
    //   · 本机 `wevtutil gp Microsoft-Windows-Kernel-Network` 实测事件号：
    //       TCPv4 10=发送 / 11=接收　TCPv6 26/27　UDPv4 42/43　UDPv6 58/59
    //     载荷（IPv4）：PID(4) size(4) 目的地址(4) 源地址(4) 目的端口(2) 源端口(2)
    //   · 为什么**不用** Npcap / WFP：前者要装驱动 + 用户态逐包解析（常驻浮窗的 CPU 不可接受），
    //     后者要写内核驱动 + 签名；两者都会破坏「单文件 exe、无驱动、无新依赖」的交付模型。
    // 硬约束：
    //   · 起 ETW 会话需要管理员 ⇒ 本程序 manifest 已是 requireAdministrator ✓（普通权限下会优雅降级）
    //   · ProcessTrace 是**阻塞**调用 ⇒ 必须跑在后台线程（同入口哨兵：放界面线程会冻住浮窗）
    //   · 只统计**本程序运行期间**的流量（与现有总量口径一致；Windows 的按天历史在 SRUM 里，未采用）
    //   · **回环（127.x / ::1）不计** —— 现有总量来自网卡计数器、本来就不含回环，这样两边才可比
    public static class AppTraffic
    {
        // 数据目录：%APPDATA%\RayRadar\apptraffic\YYYY-MM-DD.dat，每行「应用名 \t 下行 \t 上行」（TSV）
        // ⚠️ 按天分文件（不是一个大文件）：一天只有几十~几百行，每 60 秒整写一次也就十几 KB；
        //    若合成一个大文件，一年 3~4 MB、每分钟重写一遍 ⇒ 每天几个 GB 的无谓磁盘写入。
        // 可用 RAYRADAR_APPTRAFFIC_DIR 覆盖（自测/演示用，不影响正式数据）
        public static string DirPath
        {
            get
            {
                try
                {
                    string custom = Environment.GetEnvironmentVariable("RAYRADAR_APPTRAFFIC_DIR");
                    if (!string.IsNullOrEmpty(custom)) return custom;
                }
                catch { }
                return Path.Combine(Settings.DirPath, "apptraffic");
            }
        }
        public const int KeepDays = 400;        // 保留约 13 个月，够画「最近 1 年」

        static Dictionary<string, Dictionary<string, long[]>> byDay = new Dictionary<string, Dictionary<string, long[]>>();
        static readonly HashSet<string> dirty = new HashSet<string>();
        static bool loaded = false;

        public static void Load()
        {
            byDay.Clear();
            dirty.Clear();
            try
            {
                if (Directory.Exists(DirPath))
                {
                    string[] files = Directory.GetFiles(DirPath, "*.dat");
                    Array.Sort(files);
                    foreach (string f in files)
                    {
                        string day = Path.GetFileNameWithoutExtension(f);
                        if (day.Length != 10) continue;
                        Dictionary<string, long[]> m = new Dictionary<string, long[]>();
                        if (ReadInto(f, m) > 0) byDay[day] = m;
                    }
                }
            }
            catch { }
            loaded = true;
        }
        static void Ensure()
        {
            if (!loaded) Load();
        }
        static int ReadInto(string path, Dictionary<string, long[]> m)
        {
            int n = 0;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 3 || p[0].Length == 0) continue;
                    long rx, tx;
                    if (!long.TryParse(p[1], out rx) || !long.TryParse(p[2], out tx)) continue;
                    long[] v;
                    if (!m.TryGetValue(p[0], out v)) { v = new long[2]; m[p[0]] = v; }
                    v[0] += rx; v[1] += tx; n++;
                }
            }
            catch { }
            return n;
        }

        /** 把一批「应用 → [下行,上行]」累加到某一天（由 NetMonitor 每 5 秒调一次） */
        public static void Add(string day, Dictionary<string, long[]> apps)
        {
            Ensure();
            if (apps == null || apps.Count == 0) return;
            Dictionary<string, long[]> m;
            if (!byDay.TryGetValue(day, out m)) { m = new Dictionary<string, long[]>(); byDay[day] = m; }
            foreach (KeyValuePair<string, long[]> kv in apps)
            {
                long[] v;
                if (!m.TryGetValue(kv.Key, out v)) { v = new long[2]; m[kv.Key] = v; }
                v[0] += kv.Value[0]; v[1] += kv.Value[1];
            }
            dirty.Add(day);
        }

        /** 落盘：只整写「有改动的那几天」（通常就是今天；跨零点时顺带补写昨天）—— 原子写，写坏不了 */
        public static void Save()
        {
            try
            {
                if (dirty.Count == 0) return;
                Directory.CreateDirectory(DirPath);
                string[] days = new string[dirty.Count];
                dirty.CopyTo(days);
                foreach (string d in days)
                {
                    if (WriteDay(d)) dirty.Remove(d);
                }
            }
            catch { }
        }
        public static void SaveNow() { Save(); }

        static bool WriteDay(string day)
        {
            Dictionary<string, long[]> m;
            if (!byDay.TryGetValue(day, out m)) return true;
            try
            {
                List<string> keys = new List<string>(m.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                List<string> lines = new List<string>();
                foreach (string k in keys)
                {
                    long[] v = m[k];
                    if (v[0] == 0 && v[1] == 0) continue;
                    lines.Add(k + "\t" + v[0] + "\t" + v[1]);
                }
                string path = Path.Combine(DirPath, day + ".dat");
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        /** 裁掉 KeepDays 天以前的历史（按文件名判断，整文件删） */
        public static void Prune()
        {
            try
            {
                if (!Directory.Exists(DirPath)) return;
                DateTime cut = DateTime.Today.AddDays(-KeepDays);
                foreach (string f in Directory.GetFiles(DirPath, "*.dat"))
                {
                    DateTime d;
                    if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(f), out d)) continue;
                    if (d.Date < cut) File.Delete(f);
                }
            }
            catch { }
        }

        /** 最近 backDays 天（含今天）按应用合计，降序（合计相等的按名字排，稳定） */
        public static List<KeyValuePair<string, long[]>> Rank(int backDays)
        {
            Ensure();
            Dictionary<string, long[]> sum = new Dictionary<string, long[]>();
            DateTime today = DateTime.Today, from = today.AddDays(-(backDays - 1));
            foreach (KeyValuePair<string, Dictionary<string, long[]>> kv in byDay)
            {
                DateTime d;
                if (!DateTime.TryParse(kv.Key, out d)) continue;
                if (d.Date < from || d.Date > today) continue;
                foreach (KeyValuePair<string, long[]> a in kv.Value)
                {
                    long[] v;
                    if (!sum.TryGetValue(a.Key, out v)) { v = new long[2]; sum[a.Key] = v; }
                    v[0] += a.Value[0]; v[1] += a.Value[1];
                }
            }
            List<KeyValuePair<string, long[]>> list = new List<KeyValuePair<string, long[]>>(sum);
            list.Sort(delegate (KeyValuePair<string, long[]> x, KeyValuePair<string, long[]> y)
            {
                long bx = x.Value[0] + x.Value[1], by = y.Value[0] + y.Value[1];
                if (bx != by) return by.CompareTo(bx);
                return string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /** 排行取前 topN，其余合并成「其他 N 项」（画横条用，免得尾巴太长） */
        public static List<KeyValuePair<string, long[]>> RankTop(int backDays, int topN)
        {
            List<KeyValuePair<string, long[]>> all = Rank(backDays);
            if (all.Count <= topN) return all;
            List<KeyValuePair<string, long[]>> res = new List<KeyValuePair<string, long[]>>();
            long orx = 0, otx = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (i < topN) res.Add(all[i]);
                else { orx += all[i].Value[0]; otx += all[i].Value[1]; }
            }
            if (orx + otx > 0) res.Add(new KeyValuePair<string, long[]>("其他 " + (all.Count - topN) + " 项", new long[] { orx, otx }));
            return res;
        }

        /** 某个应用的逐日序列（升序，缺的日子补 0）—— 趋势图用 */
        public static List<KeyValuePair<string, long[]>> AppSeries(string app, int days)
        {
            Ensure();
            List<KeyValuePair<string, long[]>> list = new List<KeyValuePair<string, long[]>>();
            DateTime today = DateTime.Today;
            for (int i = days - 1; i >= 0; i--)
            {
                string k = today.AddDays(-i).ToString("yyyy-MM-dd");
                long[] v = DayApp(k, app);
                list.Add(new KeyValuePair<string, long[]>(k, v));
            }
            return list;
        }
        /** 某天某应用 */
        public static long[] DayApp(string day, string app)
        {
            Ensure();
            Dictionary<string, long[]> m;
            if (byDay.TryGetValue(day, out m))
            {
                long[] v;
                if (m.TryGetValue(app, out v)) return v;
            }
            return new long[2];
        }
        /** 某天所有应用的合计（用来跟网卡总量对账：按应用合计通常略小） */
        public static void DayTotal(string day, out long rx, out long tx)
        {
            Ensure();
            rx = 0; tx = 0;
            Dictionary<string, long[]> m;
            if (!byDay.TryGetValue(day, out m)) return;
            foreach (KeyValuePair<string, long[]> kv in m) { rx += kv.Value[0]; tx += kv.Value[1]; }
        }
        public static DateTime FirstDay()
        {
            Ensure();
            DateTime first = DateTime.MaxValue;
            foreach (string k in byDay.Keys)
            {
                DateTime d;
                if (DateTime.TryParse(k, out d) && d < first) first = d;
            }
            return first == DateTime.MaxValue ? DateTime.MinValue : first;
        }
    }

    /** ETW 采集器：把「进程 → 收发字节」搬到 AppTraffic。采集本身不落盘、不碰界面。 */
    public static class NetMonitor
    {
        const string SessionName = "RayRadarNet";
        static readonly Guid ProviderGuid = new Guid("7dd42a49-5329-4832-8dfd-43d979153a88");

        const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        static readonly Guid SessionGuid = new Guid("5a7c1f42-9d3b-4c88-b1e2-6f0d4a7c9e11");   // 本程序自己的会话 GUID

        static readonly object sync = new object();
        static Dictionary<int, long[]> pending = new Dictionary<int, long[]>();     // PID → [收, 发]（自上次 Flush）
        static Dictionary<int, string> nameCache = new Dictionary<int, string>();
        static Thread worker;
        static ulong sessionHandle;
        static IntPtr sessionProps = IntPtr.Zero;
        // ⚠️ EVENT_TRACE_LOGFILE 必须**自己分配非托管内存、整场会话保活**：
        //    若写成 `OpenTraceW(ref logfile)` 传托管结构体，marshaller 建的原生副本在调用返回后立刻被释放，
        //    而 ETW 在 ProcessTrace 期间仍持有那个指针 ⇒ 读已释放内存 ⇒ 0xc0000005 崩溃（2026-09-25 实测踩过：
        //    sim-etw.exe 启动约 3 秒后 APPCRASH / clr.dll / 0xc0000005）。
        static IntPtr logfileMem = IntPtr.Zero;
        static IntPtr loggerNameMem = IntPtr.Zero;
        static EventRecordCallback callback;        // 回调委托也必须保活：被回收 = 原生回调打到野指针
        static BufferCallbackThunk bufferCb;        // 同上（缓冲区回调，仅诊断/统计用）
        static volatile bool running;
        static volatile string lastError = "";
        static int busy, saveTick, nameTick;

        /** 采集是否在跑（false 时看 LastError 知道为什么） */
        public static bool Running { get { return running; } }
        public static string LastError { get { return lastError; } }

        // ---- 运行诊断（都很便宜：几个计数器 + 有上限的小列表；自测装置与排错时会读）----
        public static bool DebugCapture = false;    // 打开后记录前 12 条原始事件（排查「PID/字节数读错」用）
        public static readonly List<string> DebugLines = new List<string>();
        public static long CallbackCount;           // 记录回调被调用的次数（不管事件号）
        public static long BufferCallbackCount;     // 缓冲区回调次数
        public static uint ProcessTraceRc;          // ProcessTrace 的返回码
        public static int ProcessTraceErr;          // 其 GetLastWin32Error
        public static long EventCount;              // 收到的事件总数
        public static long ByteCount;               // 累计字节（收+发）
        /** 按事件号分项统计 [次数, 字节]（键=事件 ID）—— 排查「某个方向对不上」用 */
        public static readonly Dictionary<int, long[]> ById = new Dictionary<int, long[]>();
        /** 被丢弃的事件数：长度不够 / 回环 / PID<=0 / size<=0 */
        public static long DropLen, DropLoop, DropPid, DropSize;
        /** 进程名解析失败的记录（最多 10 条）—— 显示成「PID 1234」时看这里 */
        public static readonly List<string> NameErrors = new List<string>();
        /** 每个 PID 首次出现的时间与解析结果（最多 40 条） */
        public static readonly List<string> NameTrace = new List<string>();
        public static readonly List<string> TraceLog = new List<string>();   // 启动分步进度（定位卡在哪一步）
        static void T(string s) { try { lock (TraceLog) TraceLog.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s); } catch { } }

        public static bool Start()
        {
            return StartCore(true, SessionName);
        }
        /** 诊断/备用：挂到一个**已存在**的会话上（不自己 StartTrace / EnableTrace） */
        public static bool Attach(string sessionName)
        {
            return StartCore(false, sessionName);
        }
        static bool StartCore(bool createSession, string sessionName)
        {
            if (running) return true;
            if (worker != null && worker.IsAlive) return false;      // 正在启动/运行
            string why = LayoutProblem();
            if (why != null) { lastError = why; return false; }
            lastError = "";
            createOwnSession = createSession;
            sessionToUse = sessionName;
            worker = new Thread(Worker);
            worker.IsBackground = true;
            worker.Name = "RayRadarNetMonitor";
            worker.Start();
            for (int i = 0; i < 60 && !running && worker.IsAlive && lastError.Length == 0; i++) Thread.Sleep(50);
            if (!running && lastError.Length == 0)
                lastError = "ETW 采集线程提前退出（没报错，可能会话被占用或权限不足）";
            return running;
        }
        static bool createOwnSession = true;
        static string sessionToUse = SessionName;

        public static void Stop()
        {
            try
            {
                IntPtr p = AllocProps(SessionName);
                try { ControlTraceW(0, SessionName, p, EVENT_TRACE_CONTROL_STOP); }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch { }
            Thread t = worker;
            if (t != null) { try { t.Join(3000); } catch { } }
            worker = null;
            running = false;
        }

        /**
         * 由界面那个 1 秒定时器调用（建议每 5 秒一次）：
         * 把采集到的增量搬进 AppTraffic（每 5 秒）、每 60 秒落一次盘。
         * ⚠️ 重活丢线程池 —— 解析进程名要走系统调用，别占界面线程。
         */
        public static void Tick()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate (object o)
            {
                try
                {
                    Flush();
                    if (++saveTick >= 12) { saveTick = 0; AppTraffic.Save(); }
                }
                catch { }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }

        /** 把挂起的增量按「应用名」合并后交 AppTraffic */
        public static void Flush()
        {
            Dictionary<int, long[]> snap;
            lock (sync)
            {
                if (pending.Count == 0) return;
                snap = pending;
                pending = new Dictionary<int, long[]>();
                if (++nameTick >= 120) { nameTick = 0; nameCache.Clear(); }   // 每 10 分钟清一次名字缓存（防 PID 复用认错人）
            }
            Dictionary<string, long[]> byApp = new Dictionary<string, long[]>();
            foreach (KeyValuePair<int, long[]> kv in snap)
            {
                string app = NameOf(kv.Key);
                long[] v;
                if (!byApp.TryGetValue(app, out v)) { v = new long[2]; byApp[app] = v; }
                v[0] += kv.Value[0]; v[1] += kv.Value[1];
            }
            AppTraffic.Add(DateTime.Now.ToString("yyyy-MM-dd"), byApp);
        }

        static string NameOf(int pid)
        {
            lock (sync)
            {
                string cached;
                if (nameCache.TryGetValue(pid, out cached)) return cached;
            }
            string n = ResolveName(pid);
            return n != null ? n : (pid == 0 ? "System Idle" : (pid == 4 ? "System" : "PID " + pid));
        }

        /** 解析进程名并写缓存；失败返回 null（**不缓存失败**，下次再试） */
        static string ResolveName(int pid)
        {
            string n = null;
            try { n = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch (Exception ex) { if (NameErrors.Count < 10) NameErrors.Add(pid + ": " + ex.GetType().Name + " " + ex.Message); }
            if (NameTrace.Count < 40)
            {
                try
                {
                    NameTrace.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  PID " + pid + " → " +
                                  (string.IsNullOrEmpty(n) ? "失败" : n));
                }
                catch { }
            }
            if (string.IsNullOrEmpty(n)) return null;
            lock (sync) { nameCache[pid] = n; }
            return n;
        }

        // ---------- x64 结构体布局自检：不通过就整体降级（宁可不统计，也不乱读内存） ----------
        // ---------- 布局自检用到的偏移（x64 期望值，全部由 Marshal.OffsetOf 算出后核对）----------
        public static int OffLoggerName, OffProcessTraceMode, OffCallback, SizeLogfile;      // 8 / 28 / 392 / 416
        public static int OffPid, OffEventId, OffUserDataLen, OffUserData;                   // 12 / 40 / 86 / 96
        static string LayoutProblem()
        {
            if (IntPtr.Size != 8) return "按应用统计只支持 64 位系统（当前 " + (IntPtr.Size * 8) + " 位）";
            try
            {
                OffLoggerName = (int)Marshal.OffsetOf(typeof(EVENT_TRACE_LOGFILE), "LoggerName");
                OffProcessTraceMode = (int)Marshal.OffsetOf(typeof(EVENT_TRACE_LOGFILE), "ProcessTraceMode");
                OffCallback = (int)Marshal.OffsetOf(typeof(EVENT_TRACE_LOGFILE), "EventRecordCallback");
                OffBufferCallback = (int)Marshal.OffsetOf(typeof(EVENT_TRACE_LOGFILE), "BufferCallback");
                SizeLogfile = Marshal.SizeOf(typeof(EVENT_TRACE_LOGFILE));
                OffPid = (int)Marshal.OffsetOf(typeof(EVENT_HEADER), "ProcessId");
                OffEventId = (int)Marshal.OffsetOf(typeof(EVENT_HEADER), "Id");
                OffUserDataLen = (int)Marshal.OffsetOf(typeof(EVENT_RECORD), "UserDataLength");
                OffUserData = (int)Marshal.OffsetOf(typeof(EVENT_RECORD), "UserData");

                string bad = null;
                if (OffLoggerName != 8) bad = "LoggerName=" + OffLoggerName;
                else if (OffProcessTraceMode != 28) bad = "ProcessTraceMode=" + OffProcessTraceMode;
                else if (OffBufferCallback != 400) bad = "缓冲回调偏移=" + OffBufferCallback;
                else if (OffCallback != 424) bad = "记录回调偏移=" + OffCallback;
                else if (SizeLogfile != 448) bad = "LOGFILE 大小=" + SizeLogfile;
                else if (OffPid != 12 || OffEventId != 40) bad = "事件头 PID/ID=" + OffPid + "/" + OffEventId;
                else if (OffUserDataLen != 86 || OffUserData != 96) bad = "载荷长度/指针=" + OffUserDataLen + "/" + OffUserData;
                if (bad != null)
                    return "ETW 结构体布局自检未通过（" + bad + "）⇒ 已停用按应用统计，其余功能不受影响";
                return null;
            }
            catch (Exception ex) { return "ETW 自检异常：" + ex.Message; }
        }

        static void Worker()
        {
            ulong trace = 0;
            string sess = sessionToUse;
            try
            {
                T("worker 启动（" + (createOwnSession ? "自建会话" : "挂到已有会话") + " " + sess + "）");
                if (createOwnSession)
                {
                    string useName = sess;
                    sessionProps = AllocProps(useName);
                    uint rc = StartTraceW(out sessionHandle, useName, sessionProps);
                    T("StartTraceW(" + useName + ") → " + rc);
                    if (rc == 183)                // ERROR_ALREADY_EXISTS：上次崩溃留下的同名会话 ⇒ 停掉重来
                    {
                        ControlTraceW(0, useName, sessionProps, EVENT_TRACE_CONTROL_STOP);
                        Thread.Sleep(300);
                        Marshal.FreeHGlobal(sessionProps);
                        sessionProps = AllocProps(useName);
                        rc = StartTraceW(out sessionHandle, useName, sessionProps);
                        T("同名会话残留，停掉重试 → " + rc);
                    }
                    if (rc != 0)
                    {
                        lastError = rc == 5 ? "启动 ETW 会话被拒（需要管理员权限）" : "启动 ETW 会话失败（错误 " + rc + "）";
                        return;
                    }
                    sess = useName;
                    Guid provider = ProviderGuid;      // ⚠️ 静态只读字段不能按 ref 传，得先拷一份局部变量
                    rc = EnableTraceEx2(sessionHandle, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5,
                                        0xFFFFFFFFFFFFFFFFUL, 0, 0, IntPtr.Zero);
                    T("EnableTraceEx2 → " + rc);
                    if (rc != 0) { lastError = "启用 Kernel-Network provider 失败（错误 " + rc + "）"; return; }
                }

                // EVENT_TRACE_LOGFILE 建在**非托管内存**里并整场保活（见字段处注释：用 ref 传托管结构体会崩）
                // ⚠️ 故意**多给 4KB 余量**：ETW 会往这块内存里回写 LogfileHeader 等字段，
                //    万一真实原生结构体比我们算出来的 416 字节大，多给的余量能兜住（否则就是堆越界 ⇒ 0xC0000409）。
                callback = new EventRecordCallback(OnRecord);
                bufferCb = new BufferCallbackThunk(OnBuffer);
                int alloc = SizeLogfile + 4096;
                logfileMem = Marshal.AllocHGlobal(alloc);
                for (int i = 0; i < alloc; i++) Marshal.WriteByte(logfileMem, i, 0);
                loggerNameMem = Marshal.StringToHGlobalUni(sess);
                Marshal.WriteIntPtr(logfileMem, OffLoggerName, loggerNameMem);
                Marshal.WriteInt32(logfileMem, OffProcessTraceMode,
                                   (int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD));
                Marshal.WriteIntPtr(logfileMem, OffCallback, Marshal.GetFunctionPointerForDelegate(callback));
                // 诊断：把回调指针在 368~448 每个 8 字节槽位都写一遍 —— 无论 ETW 真正读哪个偏移都会调到我们，
                // 从而**反推真实偏移**（正式运行不开）。
                // 诊断：缓冲区回调也装上（它每收到一个缓冲区就调一次）—— 用来判断「插槽对不对」
                Marshal.WriteIntPtr(logfileMem, OffBufferCallback, Marshal.GetFunctionPointerForDelegate(bufferCb));
                trace = OpenTraceW(logfileMem);
                T("OpenTraceW → " + trace.ToString("X") + "（MaxValue=失败）");
                if (trace == ulong.MaxValue)
                {
                    lastError = "OpenTrace 失败（错误 " + Marshal.GetLastWin32Error() + "）";
                    return;
                }
                running = true;
                T("开始 ProcessTrace（阻塞）");
                ProcessTraceRc = ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero);   // 阻塞，直到会话被停
                ProcessTraceErr = Marshal.GetLastWin32Error();
                T("ProcessTrace 返回 rc=" + ProcessTraceRc + " err=" + ProcessTraceErr +
                  "，缓冲回调 " + BufferCallbackCount + " 次 / 事件回调 " + CallbackCount + " 次 / 网络事件 " + EventCount + " 条");
            }
            catch (Exception ex) { lastError = "ETW 异常：" + ex.Message; T("异常：" + ex.Message); }
            finally
            {
                running = false;
                try { if (trace != 0 && trace != ulong.MaxValue) CloseTrace(trace); } catch { }
                try { if (logfileMem != IntPtr.Zero) { Marshal.FreeHGlobal(logfileMem); logfileMem = IntPtr.Zero; } } catch { }
                try { if (loggerNameMem != IntPtr.Zero) { Marshal.FreeHGlobal(loggerNameMem); loggerNameMem = IntPtr.Zero; } } catch { }
                try { if (sessionProps != IntPtr.Zero) { Marshal.FreeHGlobal(sessionProps); sessionProps = IntPtr.Zero; } } catch { }
            }
        }

        /**
         * ETW 回调。**每一条事件都走这里，所以要极省**：
         *   直接按偏移读原生内存（不用 Marshal.PtrToStructure 拆结构体），取值前先查载荷长度。
         * EVENT_RECORD(x64)：事件头 80 字节；PID 在 12、事件号在 40；
         *   载荷长度在 86、载荷指针在 96 —— 偏移全部经 LayoutProblem() 自检。
         */
        /** 缓冲区回调（诊断/统计）：返回 1 = 继续处理，0 = 停止 */
        static uint OnBuffer(IntPtr logfile)
        {
            try { BufferCallbackCount++; } catch { }
            return 1;
        }

        static void OnRecord(IntPtr ptr)
        {            try
            {
                CallbackCount++;
                ushort id = (ushort)Marshal.ReadInt16(ptr, OffEventId);
                bool v4, recv, isData;
                if (id == 10 || id == 42) { v4 = true; recv = false; isData = true; }       // TCPv4/UDPv4 发送
                else if (id == 11 || id == 43) { v4 = true; recv = true; isData = true; }   // TCPv4/UDPv4 接收
                else if (id == 26 || id == 58) { v4 = false; recv = false; isData = true; } // IPv6 发送
                else if (id == 27 || id == 59) { v4 = false; recv = true; isData = true; }  // IPv6 接收
                else if (id >= 10 && id <= 20) { v4 = true; recv = false; isData = false; } // 连接/断开/重传…（IPv4）
                else if (id >= 26 && id <= 40) { v4 = false; recv = false; isData = false; }// 同上（IPv6）
                else if (id >= 42 && id <= 52) { v4 = true; recv = false; isData = false; } // UDP 连接类（IPv4）
                else if (id >= 58 && id <= 68) { v4 = false; recv = false; isData = false; }// UDP 连接类（IPv6）
                else return;
                EventCount++;
                int len0 = (int)Marshal.ReadInt16(ptr, OffUserDataLen);
                IntPtr u0 = len0 >= 4 ? Marshal.ReadIntPtr(ptr, OffUserData) : IntPtr.Zero;
                if (!isData)
                {
                    // ★ 非数据事件（连接/断开…）只用来**预热进程名**：连接建立时进程一定还活着，
                    //   等第一个数据事件再解析就晚了（短命进程已经退出 ⇒ 只能显示「PID 14016」，实测踩过）。
                    if (u0 != IntPtr.Zero)
                    {
                        int pw = Marshal.ReadInt32(u0, 0);
                        if (pw > 0)
                        {
                            bool need;
                            lock (sync) { need = !nameCache.ContainsKey(pw); }
                            if (need) ResolveName(pw);
                        }
                    }
                    return;
                }
                lock (sync)
                {
                    long[] bv;
                    if (!ById.TryGetValue(id, out bv)) { bv = new long[2]; ById[id] = bv; }
                    bv[0]++;
                }
                int len = len0;
                if (len < (v4 ? 20 : 44)) { DropLen++; return; }                // ⚠️ 先看长度，再按偏移取
                IntPtr u = Marshal.ReadIntPtr(ptr, OffUserData);
                if (u == IntPtr.Zero) { DropLen++; return; }
                // ⚠️ **PID 要读载荷第 1 个字段，不能读事件头**：内核网络事件的 EVENT_HEADER.ProcessId 恒为 4（System），
                //    真正的主人（curl / msedge / node…）写在载荷偏移 0（2026-09-25 实测：头PID=4、载荷PID=8516）。
                //    读错的话所有流量都会算到「System」头上。
                int pid = Marshal.ReadInt32(u, 0);
                if (pid <= 0) { DropPid++; return; }
                // 进程名**第一次见到这个 PID 时就解析**（之后走缓存，零开销）——
                // 若拖到 Flush 时再解析，短命进程（curl 这种）已经退出，只能显示成「PID 14928」（实测踩过）。
                bool needName;
                lock (sync) { needName = !nameCache.ContainsKey(pid); }
                if (needName) ResolveName(pid);
                int size = Marshal.ReadInt32(u, 4);                             // 载荷：PID(4) size(4) …
                if (size <= 0) { DropSize++; return; }
                // 回环不计（IPv4 地址各 4 字节：目的 8、源 12；IPv6 各 16 字节：目的 8、源 24）
                if (v4)
                {
                    if (Marshal.ReadByte(u, 8) == 127 || Marshal.ReadByte(u, 12) == 127) { DropLoop++; return; }
                }
                else
                {
                    if (IsV6Loopback(u, 8) || IsV6Loopback(u, 24)) { DropLoop++; return; }
                }
                if (DebugCapture && DebugLines.Count < 12)
                {
                    DebugLines.Add("id=" + id + " 头PID=" + pid + " 载荷PID=" + Marshal.ReadInt32(u, 0) +
                                   " size=" + size + " len=" + len + " 头16字节=" + Hex(u, 16));
                }
                ByteCount += size;
                lock (sync) { long[] bv; if (ById.TryGetValue(id, out bv)) bv[1] += size; }
                lock (sync)
                {
                    long[] v;
                    if (!pending.TryGetValue(pid, out v)) { v = new long[2]; pending[pid] = v; }
                    v[recv ? 0 : 1] += size;
                }
            }
            catch { }
        }

        static string Hex(IntPtr p, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(Marshal.ReadByte(p, i).ToString("x2"));
            return sb.ToString();
        }

        static bool IsV6Loopback(IntPtr u, int off)
        {
            for (int i = 0; i < 15; i++) if (Marshal.ReadByte(u, off + i) != 0) return false;
            return Marshal.ReadByte(u, off + 15) == 1;
        }


        static IntPtr AllocProps(string name)
        {
            int size = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES));
            byte[] nb = Encoding.Unicode.GetBytes(name + "\0");
            IntPtr p = Marshal.AllocHGlobal(size + nb.Length);
            for (int i = 0; i < size + nb.Length; i++) Marshal.WriteByte(p, i, 0);
            EVENT_TRACE_PROPERTIES pr = new EVENT_TRACE_PROPERTIES();
            pr.Wnode.BufferSize = (uint)(size + nb.Length);
            // ★ 这三行是「会话建起来但收不到事件」的元凶（2026-09-25 实测：漏了 Flags 时 StartTrace 返回 0、
            //   EnableTraceEx2 返回 0、OpenTrace 也成功，但**一条事件都不来**）：
            pr.Wnode.Flags = WNODE_FLAG_TRACED_GUID;   // 0x00020000：告诉 ETW 这个 WNODE 描述的是跟踪会话
            pr.Wnode.ClientContext = 1;                // 时间戳用 QPC（性能计数器）
            pr.Wnode.Guid = SessionGuid;               // 固定的会话 GUID（按名字起会话时 ETW 会用它）
            pr.BufferSize = 64;                  // 每个缓冲区 64KB
            pr.MinimumBuffers = 4;
            pr.MaximumBuffers = 16;
            pr.LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
            pr.FlushTimer = 1;                   // 1 秒刷一次 ⇒ 数据够实时
            Marshal.StructureToPtr(pr, p, false);
            Marshal.WriteInt32(p, (int)Marshal.OffsetOf(typeof(EVENT_TRACE_PROPERTIES), "LoggerNameOffset"), size);
            Marshal.WriteInt32(p, (int)Marshal.OffsetOf(typeof(EVENT_TRACE_PROPERTIES), "LogFileNameOffset"), 0);
            Marshal.Copy(nb, 0, (IntPtr)(p.ToInt64() + size), nb.Length);
            return p;
        }

        // ---------- P/Invoke（全部来自 Windows 自带的 advapi32.dll ⇒ 不新增任何文件/依赖） ----------
        // ⚠️ StartTrace / ControlTrace / OpenTrace 在 DLL 里的**真实导出名带 W 后缀**（StartTraceW …），
        //    写 ExactSpelling=true 又写不带后缀的名字会报「找不到入口点」（2026-09-25 实测踩过）。
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern uint CloseTrace(ulong traceHandle);

        delegate void EventRecordCallback(IntPtr eventRecord);
        /** 缓冲区回调：每收到一个缓冲区调一次（返回 0 继续；仅诊断用，判断回调插槽是否正确） */
        delegate uint BufferCallbackThunk(IntPtr logfile);
        public static int OffBufferCallback;        // 应为 368

        [StructLayout(LayoutKind.Sequential)]
        struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_HEADER
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;
            public ushort EventProperty;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public ushort Id;
            public byte Version;
            public byte Channel;
            public byte Level;
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
            public ulong ProcessorTime;
            public Guid ActivityId;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public uint BufferContext;
            public ushort ExtendedDataCount;
            public ushort UserDataLength;
            public IntPtr ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct EVENT_TRACE_LOGFILE
        {
            // ⚠️ 这个结构体的**后半段偏移与本机 SDK 头文件对不上**，别照抄网上的常量：
            //    按 C 定义手算出来 BufferCallback=368 / EventRecordCallback=392，
            //    但本机（Windows 11 + .NET 4.8）实测真值是 **400 / 424**（整段差 +32 字节）。
            //    怎么测出来的：把「回调指针」在 368~448 每个 8 字节槽位各放一个**独立计数器**，
            //    跑一次看哪个计数器在涨 —— 只有 400（每缓冲区一次）与 424（每事件一次）有读数。
            //    ⇒ 中间那段（CurrentEvent + LogfileHeader + 对齐）用不透明字节数组占位，不建模。
            public IntPtr LogFileName;          // 0
            public IntPtr LoggerName;           // 8
            public long CurrentTime;            // 16
            public uint BuffersRead;            // 24
            public uint ProcessTraceMode;       // 28
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 368)] public byte[] Opaque;   // 32..400
            public IntPtr BufferCallback;       // 400 ← 实测
            public uint BufferSize;             // 408
            public uint Filled;                 // 412
            public uint EventsLost;             // 416
            public IntPtr EventRecordCallback;  // 424 ← 实测（每事件一次）
            public uint IsKernelTrace;          // 432
            public IntPtr Context;              // 440
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
        public void SetDays(int d) { days = d; custom = null; Reload(); }
        public void Reload() { data = custom != null ? custom : Traffic.RecentAsc(days); hover = -1; Invalidate(); }
        // v4.17：也可以直接喂一组序列（看「某个应用」的逐日趋势用；传 null 回到总量）
        List<KeyValuePair<string, long[]>> custom = null;
        public void SetCustom(List<KeyValuePair<string, long[]>> d) { custom = d; data = d != null ? d : Traffic.RecentAsc(days); hover = -1; Invalidate(); }

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
    /** v4.17：应用流量排行（横向堆叠条：下行=蓝、上行=橙）——风格与 TrafficChart 一致 */
    public class AppRankChart : Control
    {
        static readonly Color ColText = Color.FromArgb(80, 84, 92);
        static readonly Color ColMuted = Color.FromArgb(150, 154, 162);
        static readonly Color ColTrack = Color.FromArgb(243, 245, 248);
        List<KeyValuePair<string, long[]>> data = new List<KeyValuePair<string, long[]>>();
        string empty = "暂无按应用数据";

        public AppRankChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }
        public void SetData(List<KeyValuePair<string, long[]>> d, string emptyText)
        {
            data = d != null ? d : new List<KeyValuePair<string, long[]>>();
            if (emptyText != null) empty = emptyText;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (Pen pen = new Pen(Color.FromArgb(230, 233, 238))) g.DrawPath(pen, TrafficChart.RRect(box, 8));

            if (data.Count == 0)
            {
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b = new SolidBrush(ColMuted))
                    g.DrawString(empty, f, b, 16, 16);
                return;
            }
            long max = 1;
            foreach (KeyValuePair<string, long[]> kv in data)
            {
                long t = kv.Value[0] + kv.Value[1];
                if (t > max) max = t;
            }
            int top = 10, rowH = Math.Max(16, (Height - 20) / Math.Max(1, data.Count));
            int labelW = 96, valW = 74;
            using (Font fl = new Font("Microsoft YaHei UI", 8.5f))
            using (Font fv = new Font("Microsoft YaHei UI", 8f))
            {
                for (int i = 0; i < data.Count; i++)
                {
                    int y = top + i * rowH;
                    if (y + rowH > Height - 4) break;
                    long rx = data[i].Value[0], tx = data[i].Value[1], tot = rx + tx;
                    int barX = labelW + 6, barW = Math.Max(10, Width - barX - valW);
                    int full = (int)Math.Round(barW * (double)tot / max); if (full < 2) full = 2;
                    int downW = tot > 0 ? (int)Math.Round(full * (double)rx / tot) : full;
                    Rectangle track = new Rectangle(barX, y + 3, barW, rowH - 8);
                    using (SolidBrush b = new SolidBrush(ColTrack)) g.FillPath(b, TrafficChart.RRect(track, 3));
                    if (downW > 0)
                    {
                        Rectangle rd = new Rectangle(barX, y + 3, downW, rowH - 8);
                        using (SolidBrush b = new SolidBrush(TrafficChart.ColDown)) g.FillPath(b, TrafficChart.RRect(rd, 3));
                    }
                    int upW = full - downW;
                    if (upW > 0)
                    {
                        Rectangle ru = new Rectangle(barX + downW, y + 3, upW, rowH - 8);
                        using (SolidBrush b = new SolidBrush(TrafficChart.ColUp)) g.FillPath(b, TrafficChart.RRect(ru, 3));
                    }
                    System.Windows.Forms.TextRenderer.DrawText(g, data[i].Key, fl,
                        new Rectangle(12, y, labelW - 6, rowH), ColText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    System.Windows.Forms.TextRenderer.DrawText(g, Traffic.Size(tot), fv,
                        new Rectangle(barX + barW, y, valW - 4, rowH), ColMuted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    public class TrafficForm : Form
    {
        TrafficChart chart;
        AppRankChart rank;                       // v4.17：应用流量排行
        DataGridView grid, gridApps;             // 按天明细 / 按应用明细
        Button b14, b30, r1, r30, r365;
        ComboBox cbApp;                           // 「流量趋势」看全部还是某个应用
        TextBox txtSearch;                        // 按应用明细的搜索框
        Label lbAppStatus;
        int rankDays = 1;

        public TrafficForm()
        {
            Text = "Ray雷达 - 流量统计";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 712);
            BackColor = Color.FromArgb(247, 248, 250);
            Font = new Font("Microsoft YaHei UI", 9f);
            TopMost = true;

            Label title = new Label();
            title.Text = "流量统计"; title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(40, 44, 52); title.AutoSize = true; title.Location = new Point(20, 14);
            Controls.Add(title);

            DateTime first = Traffic.FirstDay();
            Label note = new Label();
            note.AutoSize = false; note.Size = new Size(940, 20);
            note.ForeColor = Color.FromArgb(140, 144, 152);
            note.Font = new Font("Microsoft YaHei UI", 8.5f);
            note.Text = first == DateTime.MinValue
                ? "暂无数据：Ray雷达 只在运行时统计，从今天开始累计"
                : "统计起始 " + first.ToString("yyyy-MM-dd") + "　·　只统计 Ray雷达 运行期间，按天累计（Windows 无按天历史接口）";
            note.Location = new Point(22, 40);
            Controls.Add(note);

            // ── v4.17：按应用统计的状态行（采集起不来时把原因写在这里，不让用户猜）──
            lbAppStatus = new Label();
            lbAppStatus.AutoSize = false; lbAppStatus.Size = new Size(940, 20);
            lbAppStatus.ForeColor = Color.FromArgb(140, 144, 152);
            lbAppStatus.Font = new Font("Microsoft YaHei UI", 8.5f);
            lbAppStatus.Location = new Point(22, 59);
            Controls.Add(lbAppStatus);

            // ── 总览卡片 ──
            long rx, tx;
            int cx = 20;
            Traffic.Sum(1, out rx, out tx); Card("今天", "最近 1 天", rx, tx, cx, 82); cx += 320;
            Traffic.Sum(30, out rx, out tx); Card("最近 30 天", null, rx, tx, cx, 82); cx += 320;
            Traffic.Sum(365, out rx, out tx); Card("最近 1 年", null, rx, tx, cx, 82);

            // ── 图表区（左：流量趋势 + 应用下拉；右：应用排行）──
            Label lbChart = new Label();
            lbChart.Text = "流量趋势"; lbChart.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lbChart.ForeColor = Color.FromArgb(40, 44, 52); lbChart.AutoSize = true; lbChart.Location = new Point(20, 202);
            Controls.Add(lbChart);

            cbApp = new ComboBox();
            cbApp.DropDownStyle = ComboBoxStyle.DropDownList;
            cbApp.Font = new Font("Microsoft YaHei UI", 8.5f);
            cbApp.Location = new Point(96, 199); cbApp.Size = new Size(190, 24);
            cbApp.SelectedIndexChanged += delegate { RefreshChart(); };
            Controls.Add(cbApp);

            b14 = MkTab("最近 14 天", 470, 199, 88, true);
            b30 = MkTab("最近 30 天", 562, 199, 88, false);
            b14.Click += delegate { SetChart(14); };
            b30.Click += delegate { SetChart(30); };

            chart = new TrafficChart();
            chart.Location = new Point(20, 228);
            chart.Size = new Size(620, 232);
            chart.SetDays(14);
            Controls.Add(chart);

            Label lbRank = new Label();
            lbRank.Text = "应用流量排行"; lbRank.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lbRank.ForeColor = Color.FromArgb(40, 44, 52); lbRank.AutoSize = true; lbRank.Location = new Point(660, 202);
            Controls.Add(lbRank);

            r1 = MkTab("今天", 800, 199, 46, true);
            r30 = MkTab("30 天", 850, 199, 54, false);
            r365 = MkTab("1 年", 908, 199, 52, false);
            r1.Click += delegate { SetRank(1); };
            r30.Click += delegate { SetRank(30); };
            r365.Click += delegate { SetRank(365); };

            rank = new AppRankChart();
            rank.Location = new Point(660, 228);
            rank.Size = new Size(300, 232);
            Controls.Add(rank);

            // ── 明细区：两个 Tab（按天 / 按应用）──
            TabControl tabs = new TabControl();
            tabs.Location = new Point(20, 470); tabs.Size = new Size(940, 196);
            tabs.Font = new Font("Microsoft YaHei UI", 9f);
            Controls.Add(tabs);

            TabPage pDay = new TabPage("按天明细");
            pDay.BackColor = Color.White; tabs.TabPages.Add(pDay);
            TabPage pApp = new TabPage("按应用明细（可搜索）");
            pApp.BackColor = Color.White; tabs.TabPages.Add(pApp);

            grid = new DataGridView();
            grid.Location = new Point(8, 10); grid.Size = new Size(916, 148);
            MkGrid(grid);
            pDay.Controls.Add(grid);
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

            // 搜索框（一年的按应用明细会很长，必须能搜 —— 用户提示里那条说得对）
            Label lbSearch = new Label();
            lbSearch.Text = "搜索应用："; lbSearch.AutoSize = true;
            lbSearch.ForeColor = Color.FromArgb(120, 124, 132); lbSearch.Font = new Font("Microsoft YaHei UI", 8.5f);
            lbSearch.Location = new Point(10, 14);
            pApp.Controls.Add(lbSearch);
            txtSearch = new TextBox();
            txtSearch.Location = new Point(78, 11); txtSearch.Size = new Size(180, 24);
            txtSearch.Font = new Font("Microsoft YaHei UI", 9f);
            txtSearch.TextChanged += delegate { FillAppGrid(); };
            pApp.Controls.Add(txtSearch);

            gridApps = new DataGridView();
            gridApps.Location = new Point(8, 42); gridApps.Size = new Size(916, 116);
            MkGrid(gridApps);
            pApp.Controls.Add(gridApps);
            gridApps.Columns.Add("a", "应用");
            gridApps.Columns.Add("d1", "今天");
            gridApps.Columns.Add("d30", "最近 30 天");
            gridApps.Columns.Add("d365", "最近 1 年");
            gridApps.Columns.Add("all", "合计（1 年）");
            gridApps.Columns[1].DefaultCellStyle.ForeColor = TrafficChart.ColDown;
            gridApps.Columns[3].DefaultCellStyle.ForeColor = TrafficChart.ColDown;
            gridApps.Columns[4].DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            Button ok = new Button();
            ok.Text = "关闭"; ok.Size = new Size(100, 30);
            ok.FlatStyle = FlatStyle.System;
            ok.Location = new Point(ClientSize.Width - 120, ClientSize.Height - 38);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
            AcceptButton = ok;

            RefreshApps();                  // v4.17：填应用下拉、排行、按应用明细、采集状态
        }

        // ===== v4.17：按应用相关 =====
        /** 只给截图装置用：切到某个明细页 */
        public void SelectTabForShot(int index)
        {
            foreach (Control c in Controls)
            {
                TabControl t = c as TabControl;
                if (t != null && index < t.TabPages.Count) { t.SelectedIndex = index; return; }
            }
        }

        void SetRank(int d)
        {
            rankDays = d;
            Style(r1, d == 1); Style(r30, d == 30); Style(r365, d == 365);
            rank.SetData(AppTraffic.RankTop(d, 10), d == 1 ? "今天还没有按应用数据" : "这段时间还没有按应用数据");
        }

        void SetChart(int d)
        {
            chartDays = d;
            Style(b14, d == 14); Style(b30, d == 30);
            RefreshChart();
        }
        int chartDays = 14;

        /** 趋势图：选「全部应用」看总量，选具体应用看它的逐日曲线 */
        void RefreshChart()
        {
            string app = cbApp.SelectedItem as string;
            if (string.IsNullOrEmpty(app) || app == AllApps)
            {
                chart.SetDays(chartDays);
                return;
            }
            chart.SetCustom(AppTraffic.AppSeries(app, chartDays));
        }
        const string AllApps = "全部应用";

        /** 把按应用的数据填进界面（下拉 + 排行 + 明细表 + 状态行） */
        void RefreshApps()
        {
            SuspendLayout();
            try
            {
                // 下拉：全部应用 + 一年内有流量的应用（按用量降序）
                string keep = cbApp.SelectedItem as string;
                cbApp.Items.Clear();
                cbApp.Items.Add(AllApps);
                foreach (KeyValuePair<string, long[]> kv in AppTraffic.Rank(365))
                    if (kv.Value[0] + kv.Value[1] > 0) cbApp.Items.Add(kv.Key);
                int idx = keep != null ? cbApp.Items.IndexOf(keep) : -1;
                cbApp.SelectedIndex = idx >= 0 ? idx : 0;

                SetRank(rankDays);
                FillAppGrid();

                lbAppStatus.Text = NetMonitor.Running
                    ? "按应用统计：运行中（ETW）　·　与上面的总量同口径：只统计 Ray雷达 运行期间；不含纯 ACK 与链路层头，"
                      + "所以「按应用合计」通常略小于网卡总量；回环(127.0.0.1)不计"
                    : "按应用统计：未启用" + (NetMonitor.LastError.Length > 0 ? " —— " + NetMonitor.LastError : "");
            }
            catch { }
            ResumeLayout();
        }

        /** 按应用明细（带搜索）。三个时间窗各查一次，再合并成一行 */
        void FillAppGrid()
        {
            try
            {
                string q = txtSearch != null ? txtSearch.Text.Trim() : "";
                Dictionary<string, long[]> d1 = ToMap(AppTraffic.Rank(1));
                Dictionary<string, long[]> d30 = ToMap(AppTraffic.Rank(30));
                gridApps.Rows.Clear();
                foreach (KeyValuePair<string, long[]> kv in AppTraffic.Rank(365))
                {
                    long tot = kv.Value[0] + kv.Value[1];
                    if (tot <= 0) continue;
                    if (q.Length > 0 && kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    long[] a = Look(d1, kv.Key), b = Look(d30, kv.Key);
                    gridApps.Rows.Add(kv.Key, Traffic.Size(a[0] + a[1]), Traffic.Size(b[0] + b[1]),
                                      Traffic.Size(tot), Traffic.Size(tot));
                }
                gridApps.ClearSelection();
                gridApps.CurrentCell = null;
            }
            catch { }
        }
        static Dictionary<string, long[]> ToMap(List<KeyValuePair<string, long[]>> list)
        {
            Dictionary<string, long[]> m = new Dictionary<string, long[]>();
            foreach (KeyValuePair<string, long[]> kv in list) m[kv.Key] = kv.Value;
            return m;
        }
        static long[] Look(Dictionary<string, long[]> m, string k)
        {
            long[] v;
            return m.TryGetValue(k, out v) ? v : new long[2];
        }

        Button MkTab(string text, int x, int y, int w, bool active)
        {
            Button b = new Button();
            b.Text = text; b.Size = new Size(w, 26); b.Location = new Point(x, y);
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Microsoft YaHei UI", 8.5f);
            Controls.Add(b);
            Style(b, active);
            return b;
        }
        /** DataGridView 的统一外观（两张表共用，免得样式漂移） */
        static void MkGrid(DataGridView g)
        {
            g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false; g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
            g.BackgroundColor = Color.White; g.BorderStyle = BorderStyle.FixedSingle;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.GridColor = Color.FromArgb(238, 240, 243);
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 30;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 249);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(90, 94, 102);
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(244, 246, 249);
            g.RowTemplate.Height = 26;
            g.DefaultCellStyle.BackColor = Color.White;
            g.DefaultCellStyle.ForeColor = Color.FromArgb(60, 64, 72);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 235, 254);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 34, 40);
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);   // 斑马纹
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        }
        static void Style(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(45, 120, 240) : Color.FromArgb(232, 235, 240);
            b.ForeColor = active ? Color.White : Color.FromArgb(90, 94, 102);
        }

        // 总览卡片：圆角白底 + 下行(蓝)/上行(橙)/合计
        void Card(string head, string sub, long rx, long tx, int x, int y)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y); p.Size = new Size(310, 106);
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
