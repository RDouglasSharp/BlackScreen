// BlackScreen - blacks out every monitor so an OLED panel is protected while a
// long GPU job runs, WITHOUT letting Windows dim, blank or power down the display.
//
// Why: on some laptops (seen on an ASUS RTX Spark / N1X), when Windows dims or
// turns off the display - screen saver, display timeout, "dim when I look away",
// "turn off my screen when I leave" - the GPU drops to a low power state (~8 W)
// and AI workloads crawl. So we keep the display "on" (showing black) instead.
//
// While running, BlackScreen:
//   * covers all monitors with a black, topmost window and hides the pointer
//   * holds a "system + display required" request (like PowerToys Awake with
//     "keep screen on") - Windows releases this automatically when we exit
//   * disables the screen saver (for this session only, not saved)
//   * sets display timeout, dim timeout and the presence-sensing (look-away /
//     walk-away) dim and off timeouts to "never" in the active power plan
// On exit (mouse click or Esc) everything is put back exactly as it was.
// If it is ever killed before it can restore, the saved originals are restored
// the next time it starts.
//
// Build: build.cmd  (uses the .NET Framework csc.exe that ships with Windows)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--auto", StringComparison.OrdinalIgnoreCase))
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            AutoMode.Run();   // resident mode, see AutoMode.cs
            return;
        }

        bool first;
        using (var mutex = new Mutex(true, "Local\\BlackScreen.SingleInstance", out first))
        {
            if (!first) return; // already running (e.g. hotkey pressed twice)

            SetProcessDPIAware(); // so the window bounds are real pixels on scaled displays
            Application.EnableVisualStyles();

            PowerGuard.RestoreLeftovers(true); // in case a previous run was killed
            PowerGuard.Engage();
            try
            {
                Application.Run(new BlackForm());
            }
            finally
            {
                PowerGuard.Release();
            }
        }
    }
}

class BlackForm : Form
{
    public BlackForm()
    {
        Text = "BlackScreen";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen; // all monitors
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Cursor.Hide();
        Activate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Cursor.Show();
        base.OnFormClosed(e);
    }
}

// Turns off everything that would dim or blank the display, and restores it.
static class PowerGuard
{
    // ---- Win32 ----
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, ref bool pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);
    const uint SPI_GETSCREENSAVEACTIVE = 0x0010, SPI_SETSCREENSAVEACTIVE = 0x0011;

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    // ---- Power plan settings forced to 0 ("never" / disabled) while running ----
    static readonly Guid SUB_VIDEO    = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid SUB_PRESENCE = new Guid("8619b916-e004-4dd8-9b66-dae86f806698");
    static readonly Guid[][] Settings =
    {
        new[] { SUB_VIDEO,    new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e") }, // Turn off display after
        new[] { SUB_VIDEO,    new Guid("17aaa29b-8b43-4b94-aafe-35f64daaf1ee") }, // Dim display after
        new[] { SUB_PRESENCE, new Guid("0a7d6ab6-ac83-4ad1-8282-eca5b58308f3") }, // Presence: away -> display off
        new[] { SUB_PRESENCE, new Guid("a79c8e0e-f271-482d-8f8a-5db9a18312de") }, // Presence: away -> dim
        new[] { SUB_PRESENCE, new Guid("cf8c6097-12b8-4279-bbdd-44601ee5209d") }, // Presence: looking away -> dim
        new[] { SUB_PRESENCE, new Guid("ee16691e-6ab3-4619-bb48-1c77c9357e5a") }, // Presence: looking away -> display off
    };

    static string StateFile
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackScreen");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "restore.txt");
        }
    }

    // State file lines:  scheme=<guid> | saver=<0/1> | <sub> <setting> <ac> <dc>
    public static void Engage()
    {
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

        var lines = new List<string>();
        Guid scheme;
        bool haveScheme = TryGetActiveScheme(out scheme);
        if (haveScheme) lines.Add("scheme=" + scheme);

        bool saver = false;
        bool haveSaver = SystemParametersInfo(SPI_GETSCREENSAVEACTIVE, 0, ref saver, 0);
        if (haveSaver) lines.Add("saver=" + (saver ? "1" : "0"));

        if (haveScheme)
        {
            foreach (var s in Settings)
            {
                Guid sub = s[0], set = s[1];
                uint ac, dc;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out ac) != 0) continue; // not on this PC
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out dc) != 0) continue;
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", sub, set, ac, dc));
            }
        }

        // Save originals BEFORE changing anything, so a crash can be undone later.
        File.WriteAllLines(StateFile, lines.ToArray());

        if (haveSaver && saver)
            SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, 0, IntPtr.Zero, 0); // session only, not persisted

        if (haveScheme)
        {
            foreach (var s in Settings)
            {
                Guid sub = s[0], set = s[1];
                PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0);
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0);
            }
            PowerSetActiveScheme(IntPtr.Zero, ref scheme); // apply
        }
    }

    public static void Release()
    {
        SetThreadExecutionState(ES_CONTINUOUS);
        RestoreLeftovers(false);
    }

    // Restores whatever is recorded in the state file, then deletes it.
    // afterCrash: the file was left by a run that was killed. The user may have
    // changed settings since, so only restore values still at what we set
    // (screen saver off, timeouts 0) - never overwrite a newer choice.
    public static void RestoreLeftovers(bool afterCrash)
    {
        string path = StateFile;
        if (!File.Exists(path)) return;

        Guid scheme = Guid.Empty;
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("scheme="))
                {
                    scheme = new Guid(line.Substring(7));
                }
                else if (line.StartsWith("saver="))
                {
                    uint on = line.Substring(6) == "1" ? 1u : 0u;
                    bool nowOn = false;
                    SystemParametersInfo(SPI_GETSCREENSAVEACTIVE, 0, ref nowOn, 0);
                    if (!afterCrash || !nowOn)
                        SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, on, IntPtr.Zero, 0);
                }
                else if (line.Length > 0 && scheme != Guid.Empty)
                {
                    string[] p = line.Split(' ');
                    if (p.Length != 4) continue;
                    Guid sub = new Guid(p[0]), set = new Guid(p[1]);
                    uint ac = uint.Parse(p[2], CultureInfo.InvariantCulture);
                    uint dc = uint.Parse(p[3], CultureInfo.InvariantCulture);
                    uint curAc, curDc;
                    bool acOk = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out curAc) == 0;
                    bool dcOk = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out curDc) == 0;
                    if (!afterCrash || (acOk && curAc == 0))
                        PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ac);
                    if (!afterCrash || (dcOk && curDc == 0))
                        PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, dc);
                }
            }
            if (scheme != Guid.Empty) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            File.Delete(path);
        }
        catch (Exception)
        {
            // Leave the file in place so the next run can try again.
        }
    }

    static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return false;
        scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
        LocalFree(p);
        return true;
    }
}
