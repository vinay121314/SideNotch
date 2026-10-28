@echo off
rem Builds SideNotch.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
cd /d "%~dp0"
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:dist\SideNotch.exe ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" ^
  SideNotch.cs
exit /b %ERRORLEVEL%
