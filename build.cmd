@echo off
rem Build with the .NET Framework compiler that ships with Windows (no install needed)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
taskkill /im FileFollow.exe /f >nul 2>&1
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0FileFollow.exe" /win32manifest:"%~dp0assets\app.manifest" /win32icon:"%~dp0assets\FileFollow.ico" /r:System.Management.dll /r:System.Data.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\*.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: %~dp0FileFollow.exe
