@echo off
rem Build with the .NET Framework compiler bundled with Windows (no install needed)
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /win32icon:app.ico /out:FlowmodoroTimer.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll FlowmodoroTimer.cs
if errorlevel 1 pause
