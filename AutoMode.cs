// BlackScreen --auto: a resident replacement for Windows' own screen-off, dim and
// "lock when away" on AC power, which on some laptops throttle the GPU.
//
// Set Windows (AC) display-off, dim, presence screen-off/dim and the lock-screen
// display-off timeout to Never, and let this do the job instead:
//   * no input for idle_minutes   -> black out (unless you're looking at the screen,
//                                     or an app such as a video player asks to keep it on)
//   * presence sensor says you've left for away_seconds -> black out (also needs no input
//                                     for away_seconds and no video playing: the sensor
//                                     can't see you in a dark room)
//   * any input, or the sensor sees you come back       -> screen back
//   * blacked out and no input for lock_minutes         -> real Windows lock (Win+L)
// On battery it does nothing, so Windows' normal timeouts apply.
// Settings: %LOCALAPPDATA%\BlackScreen\auto.ini   Log: auto.log (same folder)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class AutoMode
{
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint esFlags);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out uint outBuf, uint outLen);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    const int SystemExecutionState = 16;

    // ---- settings ----
    static int IdleMinutes = 5, AwaySeconds = 30, LockMinutes = 15;
    static bool WakeOnApproach = true, OnBattery = false;

    static Overlay overlay;
    static NotifyIcon tray;
    static ToolStripMenuItem pauseItem;
    static DateTime pausedUntil = DateTime.MinValue;
    static bool locked;
    static uint inputAtBlackout, inputAtDim;
    static DateTime lastSensorCheck = DateTime.MinValue;
    public static volatile bool Locked;   // for the sensor's event thread (raw logging)
    static string lastPresence = "Unknown";
    static DateTime awaySince = DateTime.MaxValue;
    static DateTime lastActive = DateTime.Now;
    static Presence sensor;

    static string Dir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackScreen");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static void Run()
    {
        bool first;
        using (var mutex = new Mutex(true, "Local\\BlackScreen.Auto", out first))
        {
            if (!first) return;
            LoadSettings();
            Log(string.Format("start: idle {0} min, away {1} s, lock {2} min, wake on approach {3}, on battery {4}",
                IdleMinutes, AwaySeconds, LockMinutes, WakeOnApproach, OnBattery));

            sensor = Presence.TryOpen();
            Log(sensor != null ? "presence sensor: available" : "presence sensor: not available (idle timer only)");

            overlay = new Overlay();
            overlay.CreateControl();
            var h = overlay.Handle;   // so BeginInvoke works before the window is first shown
            SystemEvents.SessionSwitch += (s, e) => overlay.BeginInvoke((MethodInvoker)delegate
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    locked = true; Locked = true; Log("session locked"); HideOverlay("session locked");
                    SetLockScreenTimeout(0, "locked");   // make sure the lock screen stays on
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    locked = false; Locked = false; awaySince = DateTime.MaxValue; Log("session unlocked");
                    UndimPanel("unlocked");
                    SetLockScreenTimeout(0, "unlocked");
                }
            });

            lockScreenTimeout = -1;
            SetLockScreenTimeout(0, "start");   // baseline; also undoes a 1-minute value left by a crash
            UndimPanel("start");                 // undoes a dimmed panel left by a crash
            BuildTray();
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log("tick error: " + ex.Message); } };
            timer.Start();
            Application.Run();
            SetLockScreenTimeout(0, "exit");
            UndimPanel("exit");
            tray.Visible = false;
        }
    }

    // ---- Lock screen: always keep the display on (AC) ----
    // Windows turns the display off on the lock screen after "Console lock display off
    // timeout" (VIDEOCONLOCK), and display-off throttles the GPU. So it's kept at Never,
    // and the static lock-screen clock is protected by dimming the panel to 0 when nobody
    // is there (see DimPanel below).
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
    static readonly Guid SUB_VIDEO = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid VIDEOCONLOCK = new Guid("8ec4b3a5-6868-48c2-be75-4f3044be88a7");
    static int lockScreenTimeout = -1;

    public static void SetLockScreenTimeout(int seconds, string why)
    {
        if (seconds == lockScreenTimeout) return;
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return;
        Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
        LocalFree(p);
        Guid sub = SUB_VIDEO, set = VIDEOCONLOCK;
        if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)seconds) != 0) return;
        PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        lockScreenTimeout = seconds;
        Log("lock-screen display off: " + (seconds == 0 ? "never" : seconds + " s") + " (" + why + ")");
    }

    // ---- Lock screen dimming: while locked and nobody there, turn the panel
    // brightness down to 0; restore it when someone comes back.
    // Brightness is not the "display off" state, so it doesn't throttle the GPU.
    static int savedBrightness = -1;
    static string DimFile { get { return Path.Combine(Dir, "brightness-restore.txt"); } }

    static int GetBrightness()
    {
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (System.Management.ManagementObject o in q.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception) { }
        return -1;
    }

    static bool SetBrightness(int level)
    {
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)level });
                    return true;
                }
        }
        catch (Exception ex) { Log("brightness error: " + ex.Message); }
        return false;
    }

    static void DimPanel()
    {
        if (savedBrightness >= 0) return;
        int b = GetBrightness();
        if (b <= 0) return;
        File.WriteAllText(DimFile, b.ToString(CultureInfo.InvariantCulture));   // crash safety
        if (SetBrightness(0)) { savedBrightness = b; Log("lock screen dimmed (brightness " + b + " -> 0)"); }
    }

    public static void UndimPanel(string why)
    {
        int b = savedBrightness;
        if (b < 0 && File.Exists(DimFile))   // left over from a crash
            int.TryParse(File.ReadAllText(DimFile).Trim(), out b);
        if (b < 0) return;
        if (SetBrightness(b)) Log("brightness restored to " + b + " (" + why + ")");
        savedBrightness = -1;
        try { File.Delete(DimFile); } catch (Exception) { }
    }

    static void Tick()
    {
        string presence = "Unknown", engagement = "Unknown";
        if (sensor != null) sensor.Read(out presence, out engagement);

        // Track how long the sensor has reported nobody there.
        if (presence == "NotPresent") { if (awaySince == DateTime.MaxValue) awaySince = DateTime.Now; }
        else awaySince = DateTime.MaxValue;
        bool cameBack = lastPresence == "NotPresent" && presence == "Present";
        if (presence != lastPresence) Log("presence: " + lastPresence + " -> " + presence);
        lastPresence = presence;

        if (locked)
        {
            // Backup for a sensor that doesn't notice you: any key or mouse input
            // on the lock screen brings the brightness back.
            if (savedBrightness >= 0 && LastInput() != inputAtDim) { UndimPanel("input"); awaySince = DateTime.MaxValue; return; }
            bool away = awaySince != DateTime.MaxValue && (DateTime.Now - awaySince).TotalSeconds >= AwaySeconds
                        && IdleMs() >= (uint)AwaySeconds * 1000u;   // not while someone is typing
            if (away && savedBrightness < 0) { DimPanel(); inputAtDim = LastInput(); }
            else if (savedBrightness >= 0 && presence == "Present") UndimPanel("presence");
            // Diagnostics: once a minute while locked, compare the subscription with a direct poll.
            if (sensor != null && (DateTime.Now - lastSensorCheck).TotalSeconds >= 60)
            {
                lastSensorCheck = DateTime.Now;
                Log("locked check: " + sensor.Describe() + (savedBrightness >= 0 ? ", dimmed" : ""));
            }
            return;
        }

        // Being seen looking at the screen counts as activity (like Windows'
        // adaptive dimming), so a momentary sensor flicker can't trigger the idle blackout.
        if (presence == "Present" && engagement == "Engaged") lastActive = DateTime.Now;
        uint idleMs = IdleMs();
        uint sinceActive = (uint)Math.Min(uint.MaxValue, Math.Max(0, (DateTime.Now - lastActive).TotalMilliseconds));
        uint effectiveIdle = Math.Min(idleMs, sinceActive);

        if (overlay.Visible)
        {
            if (LastInput() != inputAtBlackout) { HideOverlay("input"); return; }
            if (cameBack && WakeOnApproach) { HideOverlay("presence: approached"); return; }
            if (idleMs >= (uint)LockMinutes * 60000u)
            {
                Log("lock: no input for " + (idleMs / 60000) + " min");
                HideOverlay("locking");
                LockWorkStation();
            }
            return;
        }

        if (DateTime.Now < pausedUntil) return;
        if (!OnBattery && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) return;

        // Away also needs no input for AwaySeconds: if the sensor misses you (or input just brought the
        // screen back), typing or touching the touchpad must not be blacked out again on the next tick.
        if (awaySince != DateTime.MaxValue && (DateTime.Now - awaySince).TotalSeconds >= AwaySeconds
            && idleMs >= (uint)AwaySeconds * 1000u
            && !OtherAppWantsDisplay())   // a playing video keeps the screen on; the sensor is blind in the dark
        {
            ShowOverlay("presence: away " + AwaySeconds + " s");
            return;
        }
        if (effectiveIdle >= (uint)IdleMinutes * 60000u)
        {
            if (OtherAppWantsDisplay()) return;                               // e.g. a video is playing
            ShowOverlay("idle " + (effectiveIdle / 60000) + " min");
        }
    }

    static void ShowOverlay(string why)
    {
        Log("black out: " + why);
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        overlay.Bounds = SystemInformation.VirtualScreen;
        overlay.Show();
        overlay.Activate();
        Cursor.Hide();
        inputAtBlackout = LastInput();
    }

    public static void HideOverlay(string why)
    {
        if (!overlay.Visible) return;
        Log("screen back: " + why);
        lastActive = DateTime.Now;   // restart the idle timer, however the screen came back
        Cursor.Show();
        overlay.Hide();
        SetThreadExecutionState(ES_CONTINUOUS);
    }

    static bool OtherAppWantsDisplay()
    {
        uint state;
        if (CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out state, 4) != 0) return false;
        return (state & ES_DISPLAY_REQUIRED) != 0;
    }

    static uint LastInput()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref lii);
        return lii.dwTime;
    }

    static uint IdleMs() { return unchecked((uint)Environment.TickCount - LastInput()); }

    static void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Black out now", null, (s, e) => ShowOverlay("menu"));
        menu.Items.Add("Lock now", null, (s, e) => LockWorkStation());
        pauseItem = new ToolStripMenuItem("Pause for 1 hour", null, (s, e) =>
        {
            if (DateTime.Now < pausedUntil) { pausedUntil = DateTime.MinValue; Log("resumed"); }
            else { pausedUntil = DateTime.Now.AddHours(1); Log("paused for 1 hour"); }
            pauseItem.Checked = DateTime.Now < pausedUntil;
        });
        menu.Items.Add(pauseItem);
        menu.Items.Add("Settings...", null, (s, e) => System.Diagnostics.Process.Start("notepad.exe", "\"" + IniPath + "\""));
        menu.Items.Add("Open log", null, (s, e) => System.Diagnostics.Process.Start("notepad.exe", "\"" + Path.Combine(Dir, "auto.log") + "\""));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => { Log("exit"); HideOverlay("exit"); Application.ExitThread(); });
        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Text = "BlackScreen (auto)",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (s, e) => ShowOverlay("tray");
    }

    static string IniPath { get { return Path.Combine(Dir, "auto.ini"); } }

    static void LoadSettings()
    {
        if (!File.Exists(IniPath))
        {
            File.WriteAllLines(IniPath, new[]
            {
                "# BlackScreen --auto settings. Restart BlackScreen (tray > Exit, then sign out/in or run it) after editing.",
                "idle_minutes=5        # no input for this long -> black out (skipped while you look at the screen)",
                "away_seconds=30       # presence sensor reports nobody for this long -> black out",
                "lock_minutes=15       # blacked out and no input for this long (since last input) -> lock",
                "wake_on_approach=1    # 1 = screen comes back when the sensor sees you return",
                "on_battery=0          # 1 = also run on battery (normally Windows' own timeouts apply there)",
            });
        }
        foreach (string raw in File.ReadAllLines(IniPath))
        {
            string line = raw.Split('#')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
            switch (k)
            {
                case "idle_minutes": IdleMinutes = Math.Max(1, n); break;
                case "away_seconds": AwaySeconds = Math.Max(5, n); break;
                case "lock_minutes": LockMinutes = Math.Max(1, n); break;
                case "wake_on_approach": WakeOnApproach = n != 0; break;
                case "on_battery": OnBattery = n != 0; break;
            }
        }
    }

    static readonly object logLock = new object();
    public static void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                string path = Path.Combine(Dir, "auto.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1000000)
                {
                    File.Copy(path, path + ".old", true);   // keep one older log
                    File.Delete(path);
                }
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + msg + Environment.NewLine);
            }
        }
        catch (Exception) { }
    }
}

// The black window used by auto mode. Unlike the manual BlackForm it hides
// instead of closing, and AutoMode decides when to show and hide it.
class Overlay : Form
{
    public Overlay()
    {
        Text = "BlackScreen";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
    }
    protected override void OnMouseDown(MouseEventArgs e) { AutoMode.HideOverlay("click"); }
    protected override void OnKeyDown(KeyEventArgs e) { AutoMode.HideOverlay("key"); e.Handled = true; }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; AutoMode.HideOverlay("close"); }
        base.OnFormClosing(e);
    }
}

// Human presence sensor (Windows 11), read through WinRT. Returns null if the
// PC has no sensor or apps aren't allowed to use it.
class Presence
{
    // The sensor only takes fresh readings while something is subscribed to
    // ReadingChanged. Polling alone returns a stale cached value (seen on the
    // ProArt P16 with Windows' own presence features turned off).
    readonly Windows.Devices.Sensors.HumanPresenceSensor s;
    volatile string lastPresence = "Unknown", lastEngagement = "Unknown";
    DateTime lastEvent = DateTime.MinValue;
    Presence(Windows.Devices.Sensors.HumanPresenceSensor s)
    {
        this.s = s;
        s.ReadingChanged += (sender, e) =>
        {
            var r = e.Reading;
            if (r == null) return;
            lastPresence = r.Presence.ToString();
            lastEngagement = r.Engagement.ToString();
            lastEvent = DateTime.Now;
            if (AutoMode.Locked) AutoMode.Log("sensor event: " + Format(r));   // raw, while locked only
        };
    }

    static string Format(Windows.Devices.Sensors.HumanPresenceSensorReading r)
    {
        uint? mm = r.DistanceInMillimeters;
        return r.Presence + "/" + r.Engagement + (mm.HasValue ? " " + mm.Value + " mm" : "");
    }

    // For the once-a-minute check while locked.
    public string Describe()
    {
        string poll;
        try { var r = s.GetCurrentReading(); poll = r == null ? "none" : Format(r) + " @" + r.Timestamp.ToLocalTime().ToString("HH:mm:ss"); }
        catch (Exception ex) { poll = "error " + ex.Message; }
        string ev = lastEvent == DateTime.MinValue ? "never" : ((int)(DateTime.Now - lastEvent).TotalSeconds) + " s ago";
        return "last event " + ev + " (" + lastPresence + "), poll " + poll;
    }

    public static Presence TryOpen()
    {
        try
        {
            var op = Windows.Devices.Sensors.HumanPresenceSensor.GetDefaultAsync();
            var until = DateTime.Now.AddSeconds(5);
            while (op.Status == Windows.Foundation.AsyncStatus.Started && DateTime.Now < until) Thread.Sleep(20);
            if (op.Status != Windows.Foundation.AsyncStatus.Completed) return null;
            var sensor = op.GetResults();
            return sensor == null ? null : new Presence(sensor);
        }
        catch (Exception ex) { AutoMode.Log("presence sensor error: " + ex.Message); return null; }
    }

    public void Read(out string presence, out string engagement)
    {
        presence = "Unknown"; engagement = "Unknown";
        if (lastEvent != DateTime.MinValue)   // live data from the subscription (events may stop while nothing changes)
        {
            presence = lastPresence; engagement = lastEngagement;
            return;
        }
        try
        {
            var r = s.GetCurrentReading();
            if (r == null) return;
            presence = r.Presence.ToString();
            engagement = r.Engagement.ToString();
        }
        catch (Exception) { }
    }
}
