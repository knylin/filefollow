// Per-user installer for FileFollow: no admin rights needed.
// The same exe is copied into the install folder as uninstall.exe and run with /uninstall.
//   FileFollow-Setup.exe            interactive install / update
//   FileFollow-Setup.exe /S         silent install (Start menu + start with Windows, then launch)
//   uninstall.exe /uninstall [/S]   remove
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileFollow
{
    static class Setup
    {
        const string Version = AppInfo.Version; // src/Version.cs
        const string Publisher = "knylin";
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileFollow";
        static readonly string[] Payload = { "FileFollow.exe", "guide.zh.html", "guide.en.html", "LICENSE.txt" };

        static string AppName { get { return L.T("跟檔夾", "FileFollow"); } }
        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FileFollow"); } }
        static string AppExe { get { return Path.Combine(InstallDir, "FileFollow.exe"); } }
        static string StartMenuDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Programs); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
        static string StartupDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            try
            {
                if (uninstall) return Uninstall(silent);
                if (silent) { Install(false, true, true); return 0; }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ───────────── Install ─────────────
        public static bool IsInstalled { get { return File.Exists(AppExe); } }

        public static void Install(bool desktop, bool startup, bool launch)
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (string name in Payload)
                using (var src = asm.GetManifestResourceStream(name))
                using (var dst = File.Create(Path.Combine(InstallDir, name)))
                    src.CopyTo(dst);
            string self = Application.ExecutablePath;
            string un = Path.Combine(InstallDir, "uninstall.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(un), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, un, true);

            Shortcut(Path.Combine(StartMenuDir, AppName + ".lnk"), AppExe, null, L.T("顯示目前檔案所在的資料夾，記錄剪貼簿", "Shows the folder of the file you are working on, plus clipboard history"));
            Shortcut(Path.Combine(StartMenuDir, AppName + L.T(" 使用說明", " guide") + ".lnk"), Path.Combine(InstallDir, L.En ? "guide.en.html" : "guide.zh.html"), null, null);
            if (desktop) Shortcut(DesktopLnk, AppExe, null, null);
            else DeleteFile(DesktopLnk);
            // Same file name the app's own "Start with Windows" menu item uses
            DeleteFile(Path.Combine(StartupDir, "跟檔夾.lnk"));
            if (startup) Shortcut(Path.Combine(StartupDir, "FileFollow.lnk"), AppExe, null, null);
            else DeleteFile(Path.Combine(StartupDir, "FileFollow.lnk"));

            using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
            {
                k.SetValue("DisplayName", L.T("跟檔夾 FileFollow", "FileFollow"));
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("DisplayIcon", AppExe + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /uninstall /S");
                k.SetValue("URLInfoAbout", "https://github.com/knylin/filefollow");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = Directory.GetFiles(InstallDir).Sum(f => new FileInfo(f).Length) / 1024;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            if (launch) Process.Start(new ProcessStartInfo(AppExe) { WorkingDirectory = InstallDir });
        }

        // ───────────── Uninstall ─────────────
        static int Uninstall(bool silent)
        {
            if (!silent && MessageBox.Show(L.T("要移除跟檔夾嗎？", "Remove FileFollow?"), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            StopRunning();
            foreach (string lnk in new[] {
                Path.Combine(StartMenuDir, AppName + ".lnk"),
                Path.Combine(StartMenuDir, AppName + L.T(" 使用說明", " guide") + ".lnk"),
                Path.Combine(StartMenuDir, "跟檔夾.lnk"), Path.Combine(StartMenuDir, "跟檔夾 使用說明.lnk"),
                Path.Combine(StartMenuDir, "FileFollow.lnk"), Path.Combine(StartMenuDir, "FileFollow guide.lnk"),
                DesktopLnk, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "跟檔夾.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FileFollow.lnk"),
                Path.Combine(StartupDir, "FileFollow.lnk"), Path.Combine(StartupDir, "跟檔夾.lnk") })
                DeleteFile(lnk);
            try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }

            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileFollow");
            if (!silent && Directory.Exists(data) &&
                MessageBox.Show(L.T("也要刪除設定和剪貼簿紀錄嗎？\n\n選「否」會保留，之後重新安裝可以接著用。", "Also delete settings and clipboard history?\n\nChoose No to keep them for a later reinstall."),
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                try { Directory.Delete(data, true); } catch { }

            // uninstall.exe lives in the folder it removes, so let cmd finish the job after we exit
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!silent) MessageBox.Show(L.T("跟檔夾已移除。", "FileFollow has been removed."), AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // ───────────── Helpers ─────────────
        // Asks a running FileFollow to close the normal way, so it saves its clipboard history and settings. Its panel
        // is usually hidden (collapsed to the taskbar), which CloseMainWindow can't reach, so WM_CLOSE goes to its
        // windows directly; only one that doesn't exit in time is ended by force.
        static void StopRunning()
        {
            foreach (var p in Process.GetProcessesByName("FileFollow"))
                try
                {
                    uint pid = (uint)p.Id;
                    EnumWindows((h, l) =>
                    {
                        uint owner;
                        GetWindowThreadProcessId(h, out owner);
                        if (owner == pid)
                        {
                            var t = new System.Text.StringBuilder(64);
                            GetWindowText(h, t, 64);
                            if (t.ToString() == "跟檔夾" || t.ToString() == "FileFollow") PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        static void DeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        static void Shortcut(string lnk, string target, string args, string desc)
        {
            object sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                object sc = sh.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                try
                {
                    Action<string, object> set = (n, v) => sc.GetType().InvokeMember(n, BindingFlags.SetProperty, null, sc, new[] { v });
                    set("TargetPath", target);
                    set("WorkingDirectory", Path.GetDirectoryName(target));
                    if (args != null) set("Arguments", args);
                    if (desc != null) set("Description", desc);
                    sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                }
                finally { Marshal.ReleaseComObject(sc); }
            }
            finally { Marshal.ReleaseComObject(sh); }
        }
    }

    class SetupForm : Form
    {
        public SetupForm()
        {
            bool update = Setup.IsInstalled;
            Text = L.T("安裝 跟檔夾", "Install FileFollow");
            Font = new Font(L.En ? "Segoe UI" : "Microsoft JhengHei UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(22, 18, 22, 14) };
            var title = new Label { Text = L.T("跟檔夾 FileFollow", "FileFollow"), Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            var intro = new Label
            {
                AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 14),
                Text = update
                    ? L.T("已經安裝過，按「更新」會換成這一版，設定和剪貼簿紀錄都會保留。", "FileFollow is already installed. Update keeps your settings and clipboard history.")
                    : L.T("浮在螢幕角落的小面板：切到哪個檔案，就顯示它所在的資料夾；另一頁記錄複製過的內容。\n\n安裝在你的使用者資料夾，不需要系統管理員權限。",
                          "A small floating panel that shows the folder of the file you are working on, plus a clipboard history.\n\nInstalls just for you; no administrator rights needed.")
            };
            var cbStart = new CheckBox { Text = L.T("開機時自動啟動", "Start with Windows"), AutoSize = true, Checked = true };
            var cbDesk = new CheckBox { Text = L.T("建立桌面捷徑", "Create a desktop shortcut"), AutoSize = true };
            var cbRun = new CheckBox { Text = L.T("完成後立即啟動", "Launch when finished"), AutoSize = true, Checked = true, Margin = new Padding(3, 3, 3, 16) };

            var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            var ok = new Button { Text = update ? L.T("更新", "Update") : L.T("安裝", "Install"), AutoSize = true, MinimumSize = new Size(90, 30) };
            var cancel = new Button { Text = L.T("取消", "Cancel"), AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.Cancel };
            btns.Controls.Add(ok);
            btns.Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            ok.Click += delegate
            {
                ok.Enabled = cancel.Enabled = false;
                Cursor = Cursors.WaitCursor;
                try
                {
                    Setup.Install(cbDesk.Checked, cbStart.Checked, cbRun.Checked);
                    Cursor = Cursors.Default;
                    MessageBox.Show(this, L.T("安裝完成。\n\n面板會出現在螢幕右下角，系統匣也有藍色資料夾圖示。\n快速鍵：Ctrl+Alt+F 顯示／收起，Ctrl+Alt+C 剪貼簿紀錄。\n開始功能表裡有「跟檔夾 使用說明」。",
                        "Installed.\n\nThe panel appears in the bottom-right corner, with a blue folder icon in the tray.\nShortcuts: Ctrl+Alt+F show/hide, Ctrl+Alt+C clipboard history.\nThe Start menu has a \"FileFollow guide\" entry."),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ok.Enabled = cancel.Enabled = true;
                }
            };

            flow.Controls.AddRange(new Control[] { title, intro, cbStart, cbDesk, cbRun, btns });
            Controls.Add(flow);
        }
    }
}
