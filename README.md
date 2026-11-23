# BlackScreen

A tiny Windows utility that blacks out your screen **without** letting Windows dim or
turn off the display, so a long GPU job (LLM inference, image/video generation,
training) keeps running at full speed while your OLED panel is protected from burn-in.

## The problem

On some Windows laptops (found on an ASUS laptop with the NVIDIA **RTX Spark N1X**),
the GPU drops to a low-power state as soon as Windows turns the display off. In our
test, a GPU job running at ~65 W fell to **~5 W within seconds** of the 2-minute
display timeout, while still reporting 96% utilisation. The job didn't stop, it just
crawled. Waking the screen brought it straight back to ~65 W.

### Why: Modern Standby

This laptop, like all Windows on ARM laptops and many recent x64 ones, uses
**Modern Standby** (S0 Low Power Idle) instead of classic S3 sleep. It works like
a phone: when the display turns off for inactivity, Windows enters standby, and
the platform cuts power to everything, including a GPU that's busy with your job.
**This happens even with Sleep set to Never.** On a Modern Standby machine, the
display timeout is effectively the sleep timeout.

You can see it in the Windows System event log. From our test (display timeout
2 min, sleep Never):

| Time     | GPU                 | System log (Kernel-Power)                             |
|----------|---------------------|-------------------------------------------------------|
| 00:58:44 | 64.6 W, 96 %        |                                                       |
| 00:58:48 |                     | 506: Entering Modern Standby. Reason: Idle Timeout    |
| 00:58:50 | **5.3 W, 96 %**     |                                                       |
| 00:59:12 |                     | 507: Exiting Modern Standby. Reason: Input Touchpad   |
| 00:59:17 | 56 W → ~65 W        |                                                       |

Check your own machine (no admin needed):

```powershell
powercfg /a    # "Standby (S0 Low Power Idle)" means Modern Standby
Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';Id=506,507} -MaxEvents 20 |
  Format-List TimeCreated,Message
```

So a keep-awake tool needs its "keep screen on" option for this. Keeping the
*display* on is what keeps standby away, and that's why BlackScreen keeps the
display on but black.
(Thanks to the Reddit commenter who pointed at Modern Standby.)

Anything that turns the panel off or dims it can do this:

- Display timeout ("Turn off my screen after…")
- Presence sensing: "Turn off my screen when I leave", "Dim my screen when I look away"
- Sleep, and screen savers that power the panel down

The usual workaround is to switch all of those off and run PowerToys Awake with
"keep screen on". That works, but it leaves an **OLED screen showing a static image
for hours**, which is exactly what causes burn-in. And you lose features you
actually want the rest of the time.

## What BlackScreen does

While it runs, BlackScreen:

- covers **every monitor** with a black window and hides the mouse pointer
  (a black OLED pixel is off, so there's nothing to burn in)
- keeps the PC awake and the display on, the same as PowerToys Awake's
  "keep awake indefinitely" + "keep screen on"
- turns off the screen saver
- sets display timeout, dim timeout, "turn off my screen when I leave" and
  "dim my screen when I look away" to never

When you **click the mouse or press Esc**, it exits and puts every setting back
exactly as it was. So you can leave all your normal power-saving and presence
features on, and just run BlackScreen when you walk away from a GPU job.

If BlackScreen is ever killed (e.g. from Task Manager) it can't clean up, so it
saves your original settings first and restores them the next time it starts.
It only restores a setting that's still at the value BlackScreen set, so it never
overwrites a change you made in between.

No admin rights needed. Nothing is installed; settings are changed only in your
active power plan and only while it runs.

## Usage

1. Download `BlackScreen.exe` from [Releases](../../releases), or build it (below).
   The exe isn't code-signed, so the first time you run it Windows SmartScreen may
   say "Windows protected your PC". Click **More info → Run anyway**. (Or build it
   yourself from the ~700 lines of source.)
2. Start your GPU job, run `BlackScreen.exe`, walk away.
3. Come back and click (or press Esc).

## Auto mode (`BlackScreen.exe --auto`)

Manual mode is for "I'm walking away now". Auto mode is for everyday use: it stays in the
tray and **replaces Windows' own screen-off, dim and "lock when I leave" on AC power**,
so you get the convenience of those features without the throttle.

On AC power, set Windows' display-off, dim, "turn off my screen when I leave" and
"dim my screen when I look away" to **Never**, then run `BlackScreen.exe --auto`. It
handles those jobs itself:

- **No input for 5 minutes:** black out. Skipped while the presence sensor sees you
  looking at the screen, or while an app asks Windows to keep the display on (a video
  playing in a browser or player does this).
- **The presence sensor says you've left for 30 s:** black out. This needs no keyboard
  or mouse input in that time either, and is skipped while a video is playing (see the
  dark-room note below).
- **Any input, or the sensor sees you come back:** the screen comes back.
- **Blacked out with no input for 15 minutes:** a real Windows lock (as Win+L).
- **On the lock screen:** Windows normally turns the display off after a minute there
  ("console lock display off timeout"), which throttles the GPU just the same. Auto
  mode keeps it at Never. To protect the panel from the static lock-screen clock, it
  turns the brightness down to 0 when the sensor says nobody is there, and back up when
  you return or touch a key. Low brightness isn't "display off", so it doesn't throttle.
- **On battery** it does nothing, so Windows' normal power saving applies.

The tray icon has **Black out now**, **Lock now**, **Pause for 1 hour**, **Settings...**
and **Open log**. Double-click the icon to black out.

Settings are in `%LOCALAPPDATA%\BlackScreen\auto.ini` (`idle_minutes`, `away_seconds`,
`lock_minutes`, `wake_on_approach`, `on_battery`), and every decision is logged to
`auto.log` in the same folder. To start it at sign-in, add it to
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`:

```
reg add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v BlackScreenAuto /d "\"C:\path\to\BlackScreen.exe\" --auto"
```

Without a presence sensor it falls back to the idle timer only.

### The presence sensor is blind in the dark

On our laptop the presence sensor is part of the webcam module and works from the
normal colour camera, not the IR camera (whose light only comes on for Windows Hello).
Within a second of the room light going off it reported "not present", and kept
saying so with someone sitting right there. That's why an "away" blackout also needs
30 s with no input and no playing video. Otherwise the screen flickered on and off
every second as each touch brought it back and the next check blacked it out again.
If you often sit in the dark reading without touching anything, raise `away_seconds`.

### Optional: a hotkey

With [PowerToys](https://learn.microsoft.com/windows/powertoys/) installed:
**Keyboard Manager → Remap a shortcut → Add**, press **Win+Shift+B**, set the action
to **Run Program** and point it at `BlackScreen.exe`. (Win+Shift+B isn't used by
Windows or by PowerToys' defaults. Avoid Win+Ctrl+Shift+B, which resets the graphics
driver.) Only one manual copy runs at a time, so pressing it twice is harmless.

If you also run auto mode, set the shortcut's **If running** option to **Start another**.
With the default (**Show window**) PowerToys sees the auto-mode `BlackScreen.exe` already
running, tries to show its window (there isn't one), and the hotkey silently does
nothing.

If the hotkey does nothing, check the program path in the editor. A leading space
or trailing full stop picked up by copy-paste breaks it silently. Also note the
editor records which Win/Shift key you pressed (e.g. "Win (Left)").

## Build

Uses the C# compiler that ships with Windows (.NET Framework 4.x), no SDK needed:

```
build.cmd
```

Produces `BlackScreen.exe` (AnyCPU, runs on x64 and ARM64). `make-icon.ps1` draws the
icon if `BlackScreen.ico` is missing. `build.cmd other.exe` writes to a different name,
which is handy while the running copy has `BlackScreen.exe` open. The WinRT references
in `build.cmd` are only for the presence sensor.

## Notes

- Leave "Detect when other people are looking at my screen" off. BlackScreen doesn't
  override it, and it dims the screen.
- Vendor features BlackScreen can't control need to stay off. On ASUS, that's
  **MyASUS → Customization → OLED Care → Pixel Refresh**, which blanks the panel
  when idle even with Windows' screen saver set to None.
- Tested on Windows 11 (ARM64) on an ASUS laptop with an RTX Spark N1X. Other
  machines may throttle for different reasons; check your GPU power draw
  (`nvidia-smi`) with the display on and off.

## License

MIT
