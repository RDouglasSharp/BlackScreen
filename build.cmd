@echo off
cd /d "%~dp0"
if not exist BlackScreen.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
set OUT=%1
if "%OUT%"=="" set OUT=BlackScreen.exe
rem The WinRT references are only for the presence sensor used by --auto mode.
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:BlackScreen.ico /out:%OUT% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Devices.winmd" ^
  BlackScreen.cs AutoMode.cs
