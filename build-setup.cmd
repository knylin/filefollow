@echo off
rem Builds FileFollow.exe, then wraps it into dist\FileFollow-Setup.exe (per-user installer, no admin needed)
call "%~dp0build.cmd" || exit /b 1
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%~dp0dist" mkdir "%~dp0dist"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0dist\FileFollow-Setup.exe" /win32icon:"%~dp0assets\FileFollow.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:"%~dp0FileFollow.exe",FileFollow.exe /resource:"%~dp0docs\guide.zh.html",guide.zh.html /resource:"%~dp0docs\guide.en.html",guide.en.html /resource:"%~dp0LICENSE",LICENSE.txt "%~dp0installer\Setup.cs" "%~dp0src\I18n.cs"
if errorlevel 1 (echo SETUP BUILD FAILED & exit /b 1)
echo OK: %~dp0dist\FileFollow-Setup.exe
