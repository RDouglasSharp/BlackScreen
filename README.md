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

### Why: the display turning off, by design

The trigger is the display turning off for inactivity. On AC power, with Sleep set
to Never, the GPU goes from P0 to P8 within a second of the screen going off, and
comes back within seconds of it turning on. The CPU is cut too: in our test it got
about 40 % less work done while the screen was off.

**It isn't Modern Standby.** On this laptop the display-off and Modern Standby
entry happen at the same moment, so we first blamed standby (and an earlier
version of this README said so). But with Modern Standby disabled
(`PlatformAoAcOverride=0`, no Kernel-Power 506 event), the throttle was exactly the
same:

| Time     | Event                                   | GPU (96 % busy throughout) |
|----------|-----------------------------------------|----------------------------|
| 08:27:50 | display on                              | 55–77 W, ~2,120 MHz        |
| 08:28:58 | display off (no Modern Standby entry)   |                            |
| 08:29:01 |                                         | **3.7–7.7 W, ~740 MHz**    |
| 08:30:00 | display back on                         | 55–75 W from 08:30:02      |

What does and doesn't trigger it, all under a sustained GPU load:

| Throttles                                         | Doesn't throttle                                  |
|---------------------------------------------------|---------------------------------------------------|
| Display off after the idle timeout                | Panel brightness 0 (display still on)             |
| Display off from "turn off my screen when I leave" | A screen saver with the display still powered    |
| Lid closed while the display idle timeout is set  | The lock screen with the display on               |
|                                                   | Lid closed, lid action "Do nothing", display timeout Never |

None of these changed anything: ASUS Sleep Assistant off, Battery Care off, an EC
reset, Full-speed fan mode, and NVIDIA's "Prefer maximum performance".

**ASUS says it's intended.** We reported it to ASUS with the logs above. After review
by their R&D team, ASUS replied that the behaviour is "normal and consistent with the
platform's intended power management design" and "not considered a hardware or
system abnormality". There's no setting to turn it off. We think slowing a busy
GPU on AC power with Sleep set to Never is a poor policy, but it's the
manufacturer's call. A Lenovo RTX Spark owner reported no throttle, so other
brands may be configured differently.

To check your own machine, run a GPU job, watch `nvidia-smi` (power and clock),
and let the screen time out.

So a keep-awake tool needs its "keep screen on" option for this. Keeping the
*display* on is what avoids the throttle, and that's why BlackScreen keeps the
display on but black.

Anything that turns the panel off can do this:

- Display timeout ("Turn off my screen after…")
- Presence sensing: "Turn off my screen when I leave"
- Sleep, and screen savers that power the panel down

(BlackScreen also turns off "Dim my screen when I look away" and the dim timeout. We
didn't test whether dimming alone throttles, and brightness 0 didn't.)

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
   yourself from the ~800 lines of source.)
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

Without a presence sensor it falls back to the idle timer only. The sensor can also
stop reporting without any error (ours went silent for two days). So auto mode ignores
a reading older than 3 minutes, and reconnects to the sensor after 10 minutes of
silence and after an unlock or resume.

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
- ASUS **MyASUS → Customization → OLED Care → Pixel Refresh** can stay on. It's a
  screen saver that blanks the panel when idle (even with Windows' screen saver set to
  None), but the display stays powered, so the GPU isn't throttled (tested under a
  ComfyUI load). It makes a useful burn-in backup if BlackScreen isn't running.
- Tested on Windows 11 (ARM64) on an ASUS laptop with an RTX Spark N1X. Other
  machines may throttle for different reasons; check your GPU power draw
  (`nvidia-smi`) with the display on and off.

## License

MIT
