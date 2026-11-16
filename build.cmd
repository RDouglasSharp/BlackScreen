@echo off
cd /d "%~dp0"
if not exist BlackScreen.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:BlackScreen.ico /out:BlackScreen.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BlackScreen.cs
