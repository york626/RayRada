// SimMain.cs -- RayRadar rise-alarm simulator (TEST RIG ONLY; not part of the shipped app)
//
// Purpose: feed a synthetic CPU temperature curve into the REAL RadarForm.CheckAlarm()
// code path, so the alarm decision logic (20 s window + rise threshold + 20 s re-check)
// can be verified without heating the real CPU.
//
// How it works: RadarForm / TempProvider are created with FormatterServices.GetUninitializedObject
// (their constructors are skipped on purpose: no scheduled-task registration, no timers,
// no real sensor access, no floating window). Only the fields CheckAlarm() reads are injected.
//
// Build with the same references as build.ps1:
//   csc /nologo /target:exe /main:SimMain /out:sim.exe SimMain.cs RayRadar.cs
//       /reference:LibreHardwareMonitorLib.dll /reference:System.dll
//       /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using RayRadar;

static class SimMain
{
    static RadarForm form;
    static TempProvider fake;
    static MethodInfo checkAlarm;
    static Timer tick, watch;
    static DateTime t0, alertShownAt = DateTime.MinValue;
    static bool alarmSeen, alertClosed, busy;
    static string alarmText = "";
    static int mode;                                  // 0 = ramp, 1 = spike
    const float StartC = 46f;                         // below LimCpu(90) so only the RISE alarm can fire
    const float PerSec = 0.9f;                        // ramp: +18 C per 20 s window (>= RiseLimit 15)
    const int TotalSec = 45;                          // covers the "40 s" wording of the alarm text
    const int PeriodMs = 3000;                        // same interval as the real tempTimer

    [STAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        if (args.Length > 0 && args[0].ToLowerInvariant() == "spike") mode = 1;

        Settings st = Settings.Load();                // real, user settings -- read only, never saved
        Console.WriteLine("=== RayRadar rise-alarm simulation ===");
        Console.WriteLine("mode            : " + (mode == 0
            ? "RAMP  (sustained rise ~40s  == liquid-cooling failure / leak scenario)"
            : "SPIKE (+20C in 6s then flat == game-launch / compile load spike)"));
        Console.WriteLine("settings in use : Alarm=" + st.Alarm + "  AlarmRise=" + st.AlarmRise
            + "  RiseLimit=" + st.RiseLimit + "C/20s  LimCpu=" + st.LimCpu + "C  AlarmSound=" + st.AlarmSound);
        Console.WriteLine("sample interval : " + PeriodMs + " ms");
        Console.WriteLine("curve           : start " + StartC + " C, " + (mode == 0 ? "+" + PerSec + " C/s" : "+20 C then flat")
            + ", total " + TotalSec + " s");
        Console.WriteLine();

        fake = (TempProvider)FormatterServices.GetUninitializedObject(typeof(TempProvider));
        fake.Ready = true;
        fake.Cpu = StartC;

        form = (RadarForm)FormatterServices.GetUninitializedObject(typeof(RadarForm));
        Set("st", st);
        Set("temp", fake);
        Set("cpuHist", new List<KeyValuePair<DateTime, float>>());
        Set("lastAlarm", new Dictionary<string, DateTime>());
        // lastPopup / risePending keep DateTime.MinValue, startedAt keeps default(DateTime)
        // -> the 180 s warm-up gate is satisfied automatically (now - 0001-01-01 is huge).
        checkAlarm = typeof(RadarForm).GetMethod("CheckAlarm", BindingFlags.NonPublic | BindingFlags.Instance);
        if (checkAlarm == null) { Console.WriteLine("FATAL: CheckAlarm() not found"); Environment.Exit(9); }

        t0 = DateTime.Now;
        tick = new Timer(); tick.Interval = PeriodMs; tick.Tick += OnTick; tick.Start();
        watch = new Timer(); watch.Interval = 250; watch.Tick += OnWatch; watch.Start();
        Application.Run();
    }

    static Assembly Resolve(object sender, ResolveEventArgs a)
    {
        try
        {
            string name = new AssemblyName(a.Name).Name;
            string p = @"C:\Tool\RayRadar\lib\" + name + ".dll";
            if (File.Exists(p)) return Assembly.LoadFrom(p);
        }
        catch { }
        return null;
    }

    static void Set(string name, object v)
    {
        FieldInfo f = typeof(RadarForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) { Console.WriteLine("FATAL: field not found: " + name); Environment.Exit(9); }
        f.SetValue(form, v);
    }

    static float Curve(double el)
    {
        if (mode == 1) return (el >= 6.0) ? StartC + 20f : StartC + (float)(20.0 * el / 6.0);
        return StartC + PerSec * (float)el;
    }

    static void OnTick(object s, EventArgs e)
    {
        double el = (DateTime.Now - t0).TotalSeconds;
        if (busy) { Console.WriteLine(string.Format("  t={0,5:0.0}s   (alert dialog open -> sample skipped)", el)); return; }
        float c = Curve(el);
        fake.Cpu = c;
        Console.WriteLine(string.Format("  t={0,5:0.0}s   CPU={1,5:0.0} C   -> CheckAlarm()", el, c));
        busy = true;
        try { checkAlarm.Invoke(form, null); }
        catch (Exception ex) { Console.WriteLine("  [CheckAlarm threw] " + ex.Message); }
        busy = false;
        if (el >= TotalSec) Finish();
    }

    static void OnWatch(object s, EventArgs e)
    {
        double el = (DateTime.Now - t0).TotalSeconds;
        if (el > TotalSec + 60 && !alarmSeen) { Console.WriteLine("  [timeout] no alert within " + (TotalSec + 60) + " s"); Finish(); return; }
        foreach (Form f in Application.OpenForms)
        {
            AlertForm af = f as AlertForm;
            if (af == null) continue;
            if (!alarmSeen)
            {
                alarmSeen = true; alertShownAt = DateTime.Now;
                StringBuilder sb = new StringBuilder();
                foreach (Control c in af.Controls) { Label lb = c as Label; if (lb != null) sb.AppendLine("      | " + lb.Text); }
                alarmText = sb.ToString();
                Console.WriteLine();
                Console.WriteLine(">>> ALERT WINDOW APPEARED at t=" + el.ToString("0.0") + " s");
                Console.WriteLine(">>> caption : " + af.Text);
                Console.WriteLine(">>> body    :");
                Console.Write(alarmText);
                Console.WriteLine();
            }
            if (!alertClosed && (DateTime.Now - alertShownAt).TotalSeconds >= 7)
            {
                alertClosed = true;
                Console.WriteLine(">>> closing the alert window (shown 7 s) so the test can finish");
                try { af.Close(); } catch { }
            }
        }
    }

    static void Finish()
    {
        try { tick.Stop(); watch.Stop(); } catch { }
        Console.WriteLine();
        if (mode == 0)
            Console.WriteLine(alarmSeen
                ? "RESULT: ALARM FIRED  -- sustained 40 s rise is detected  [OK]"
                : "RESULT: NO ALARM     -- sustained rise NOT detected        [BUG]");
        else
            Console.WriteLine(alarmSeen
                ? "RESULT: ALARM FIRED  -- spike caused a false alarm         [BUG]"
                : "RESULT: NO ALARM     -- spike correctly ignored             [OK]");
        Environment.Exit(alarmSeen == (mode == 0) ? 0 : 2);
    }
}
