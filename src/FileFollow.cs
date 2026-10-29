// FileFollow (跟檔夾) — a floating panel that jumps to the folder of whatever file you are working on
// Built with the csc (C# 5) that ships with .NET Framework; see build.cmd
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace FileFollow
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint cb, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string cmd, out int argc);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string id);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public static string WindowTitle(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string[] SplitArgs(string cmd)
        {
            int argc;
            IntPtr p = CommandLineToArgvW(cmd, out argc);
            if (p == IntPtr.Zero) return new string[0];
            try
            {
                var r = new string[argc];
                for (int i = 0; i < argc; i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size));
                return r;
            }
            finally { LocalFree(p); }
        }
    }

    static class Com
    {
        public static object Get(object o, string name) { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
        public static object Item(object o, int i) { return o.GetType().InvokeMember("Item", BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, o, new object[] { i }); }
        public static void Set(object o, string name, object v) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { v }); }
        public static object Call(object o, string name, params object[] args) { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
        public static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        public static string LnkTarget(string lnk)
        {
            object sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                object sc = Call(sh, "CreateShortcut", lnk);
                try { return (string)Get(sc, "TargetPath"); } finally { Release(sc); }
            }
            finally { Release(sh); }
        }

        public static void MakeShortcut(string lnk, string target)
        {
            object sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                object sc = Call(sh, "CreateShortcut", lnk);
                Set(sc, "TargetPath", target);
                Set(sc, "WorkingDirectory", Path.GetDirectoryName(target));
                Call(sc, "Save");
                Release(sc);
            }
            finally { Release(sh); }
        }
    }

    class Detected
    {
        public string File;   // may be null when only the folder is known
        public string Dir;
        public string Source;
        public string Proc;
    }

    // Works out which file the foreground window is editing
    class Resolver
    {
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "LockApp", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "Taskmgr"
        };
        static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "whale"
        };
        // Drive paths only: never touch \\host\share paths from titles (web page titles especially) so nothing can lure us into an SMB connection that leaks NTLM credentials
        static readonly Regex RootRx = new Regex(@"[A-Za-z]:\\");

        readonly object lk = new object();
        readonly Dictionary<int, string> procNames = new Dictionary<int, string>();
        readonly Dictionary<int, string> cmdLines = new Dictionary<int, string>();
        readonly Dictionary<string, KeyValuePair<Detected, DateTime>> cache = new Dictionary<string, KeyValuePair<Detected, DateTime>>();

        public Detected Resolve(int pid, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            string proc = ProcName(pid);
            if (proc == null || Ignored.Contains(proc)) return null;

            string key = pid + "|" + title;
            lock (lk)
            {
                KeyValuePair<Detected, DateTime> hit;
                if (cache.TryGetValue(key, out hit))
                {
                    if (hit.Key != null && Exists(hit.Key)) return hit.Key;
                    if (hit.Key == null && (DateTime.Now - hit.Value).TotalSeconds < 8) return null;
                }
            }

            Detected d = null;
            try { d = FromTitlePath(title, proc); } catch { }
            if (d == null) try { d = FromOffice(proc, title); } catch { }
            if (d == null) try { d = FromCmdLine(pid, title); } catch { }
            if (d == null) try { d = FromRecent(proc, title); } catch { }
            if (d == null) try { d = FromIndex(proc, title); } catch { }
            if (d != null) d.Proc = proc;

            lock (lk) cache[key] = new KeyValuePair<Detected, DateTime>(d, DateTime.Now);
            return d;
        }

        static bool Exists(Detected d)
        {
            return d.File != null ? File.Exists(d.File) : Directory.Exists(d.Dir);
        }

        string ProcName(int pid)
        {
            lock (lk) { string n; if (procNames.TryGetValue(pid, out n)) return n; }
            string name = null;
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; } catch { }
            if (name != null) lock (lk) procNames[pid] = name;
            return name;
        }

        string CmdLine(int pid)
        {
            lock (lk) { string c; if (cmdLines.TryGetValue(pid, out c)) return c; }
            string cl = "";
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                    foreach (ManagementObject o in s.Get()) { cl = (o["CommandLine"] as string) ?? ""; o.Dispose(); }
            }
            catch { }
            lock (lk) cmdLines[pid] = cl;
            return cl;
        }

        // 1. The title contains a full path (AutoCAD, Notepad++, terminals…)
        static Detected FromTitlePath(string title, string proc)
        {
            const string stops = " ]）)\"'|*-–—[<>";
            foreach (Match m in RootRx.Matches(title))
            {
                string rest = title.Substring(m.Index);
                for (int end = rest.Length; end > 3; end--)
                {
                    if (end < rest.Length && stops.IndexOf(rest[end]) < 0) continue;
                    string c = rest.Substring(0, end).TrimEnd(' ', '*');
                    if (c.Length <= 3 || c.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                    try
                    {
                        if (File.Exists(c))
                        {
                            if (string.Equals(Path.GetFileNameWithoutExtension(c), proc, StringComparison.OrdinalIgnoreCase)) break;
                            return new Detected { File = c, Dir = Path.GetDirectoryName(c), Source = L.T("視窗標題", "window title") };
                        }
                        if (Directory.Exists(c)) return new Detected { Dir = c.TrimEnd('\\'), Source = L.T("視窗標題", "window title") };
                    }
                    catch { }
                }
            }
            return null;
        }

        // 2. Office: ask Word / Excel / PowerPoint which documents are open
        static Detected FromOffice(string proc, string title)
        {
            string prog, coll, active;
            switch (proc.ToUpperInvariant())
            {
                case "WINWORD": prog = "Word.Application"; coll = "Documents"; active = "ActiveDocument"; break;
                case "EXCEL": prog = "Excel.Application"; coll = "Workbooks"; active = "ActiveWorkbook"; break;
                case "POWERPNT": prog = "PowerPoint.Application"; coll = "Presentations"; active = "ActivePresentation"; break;
                default: return null;
            }
            object app = Marshal.GetActiveObject(prog);
            try
            {
                string best = null; int bestScore = 0;
                object c = Com.Get(app, coll);
                int n = Convert.ToInt32(Com.Get(c, "Count"));
                for (int i = 1; i <= n; i++)
                {
                    object doc = Com.Item(c, i);
                    try
                    {
                        string full = MapCloud((string)Com.Get(doc, "FullName"));
                        int sc = Score(title, (string)Com.Get(doc, "Name"), true);
                        if (full != null && sc > bestScore) { best = full; bestScore = sc; }
                    }
                    finally { Com.Release(doc); }
                }
                Com.Release(c);
                if (best == null)
                {
                    try { object a = Com.Get(app, active); best = MapCloud((string)Com.Get(a, "FullName")); Com.Release(a); } catch { }
                }
                if (best != null && File.Exists(best))
                    return new Detected { File = best, Dir = Path.GetDirectoryName(best), Source = "Office" };
            }
            finally { Com.Release(app); }
            return null;
        }

        // Office reports OneDrive files as URLs; map them back to the local sync folder
        static string MapCloud(string full)
        {
            if (string.IsNullOrEmpty(full)) return null;
            if (!full.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Path.IsPathRooted(full) ? full : null;
            try
            {
                var u = new Uri(full);
                string[] parts = Uri.UnescapeDataString(u.AbsolutePath).Trim('/').Split('/');
                string root = null; int skip = -1;
                if (u.Host.Equals("d.docs.live.net", StringComparison.OrdinalIgnoreCase))
                {
                    root = Environment.GetEnvironmentVariable("OneDriveConsumer") ?? Environment.GetEnvironmentVariable("OneDrive");
                    skip = 1;
                }
                else if (u.Host.EndsWith("sharepoint.com", StringComparison.OrdinalIgnoreCase))
                {
                    root = Environment.GetEnvironmentVariable("OneDriveCommercial");
                    int k = Array.FindIndex(parts, x => x.Equals("Documents", StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) skip = k + 1;
                }
                if (root != null && skip >= 0 && skip < parts.Length)
                    return Path.Combine(root, string.Join("\\", parts.Skip(skip)));
            }
            catch { }
            return null;
        }

        // 3. Apps opened by double-clicking a file usually get its path on the command line
        Detected FromCmdLine(int pid, string title)
        {
            string cl = CmdLine(pid);
            if (string.IsNullOrEmpty(cl)) return null;
            string[] args = Native.SplitArgs(cl);
            var cands = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                cands.Add(args[i]);
                cands.AddRange(DecodeBlob(args[i])); // Windows 11 Notepad: /SESSION:<base64>
            }
            foreach (string raw in cands)
            {
                string a = raw.Trim().Trim('"');
                if (a.Length < 4 || !(char.IsLetter(a[0]) && a[1] == ':' && a[2] == '\\')) continue;
                try
                {
                    if (File.Exists(a) && Score(title, Path.GetFileName(a), true) > 0)
                        return new Detected { File = Path.GetFullPath(a), Dir = Path.GetDirectoryName(Path.GetFullPath(a)), Source = L.T("啟動參數", "command line") };
                }
                catch { }
            }
            return null;
        }

        static readonly Regex BlobRx = new Regex(@"[A-Za-z0-9+/]{40,}={0,2}");
        static readonly Regex PathRx = new Regex(@"[A-Za-z]:\\[^\0\r\n""<>|?*]+");
        static IEnumerable<string> DecodeBlob(string arg)
        {
            var found = new List<string>();
            foreach (Match m in BlobRx.Matches(arg))
            {
                string b = m.Value;
                b = b.Substring(0, b.TrimEnd('=').Length & ~3);
                if (b.Length == 0) continue;
                try
                {
                    string text = Encoding.Unicode.GetString(Convert.FromBase64String(b));
                    foreach (Match p in PathRx.Matches(text)) found.Add(p.Value);
                }
                catch { }
            }
            return found;
        }

        // 4. Windows Recent Items: match file names that appear in the title
        static Detected FromRecent(string proc, string title)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(dir)) return null;
            bool bare = !Browsers.Contains(proc);
            var cands = new List<Tuple<int, int, DateTime, string>>();
            foreach (string f in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!Path.HasExtension(name)) continue; // folder shortcut
                int sc = Score(title, name, bare);
                if (sc > 0) cands.Add(Tuple.Create(sc, name.Length, File.GetLastWriteTime(f), f));
            }
            foreach (var c in cands.OrderByDescending(x => x.Item1).ThenByDescending(x => x.Item2).ThenByDescending(x => x.Item3).Take(6))
            {
                try
                {
                    string t = Com.LnkTarget(c.Item4);
                    if (!string.IsNullOrEmpty(t) && File.Exists(t))
                        return new Detected { File = t, Dir = Path.GetDirectoryName(t), Source = L.T("最近使用", "recent items") };
                }
                catch { }
            }
            return null;
        }

        // 5. Windows Search index: file name from the title -> most recently modified match
        static readonly Regex SepRx = new Regex(@"\s+[-–—|]\s+");
        static readonly Regex FileNameRx = new Regex(@"^[^\\/:*?""<>|]+\.[A-Za-z0-9]{1,8}$");
        static readonly Regex DecorRx = new Regex(@"^[\s*●•]+|[\s*●•]+$|\s*[\[（(](唯讀|唯讀模式|相容模式|Read-Only|Compatibility Mode)[\]）)]\s*", RegexOptions.IgnoreCase);
        static readonly HashSet<string> JunkExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".lck", ".bak", ".tmp", ".bpn", ".dwl", ".dwl2", ".lnk" };
        static readonly object idxLock = new object();
        static System.Data.OleDb.OleDbConnection idx;

        public static void WarmIndex()
        {
            try { lock (idxLock) OpenIndex(); } catch { }
        }

        static void OpenIndex()
        {
            if (idx != null && idx.State == System.Data.ConnectionState.Open) return;
            if (idx != null) idx.Dispose();
            idx = new System.Data.OleDb.OleDbConnection("Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\";");
            idx.Open();
        }

        static Detected FromIndex(string proc, string title)
        {
            bool browser = Browsers.Contains(proc);
            string[] parts = SepRx.Split(title);
            int asked = 0;
            for (int i = 0; i < parts.Length && i < 2 && asked < 2; i++)
            {
                string part = DecorRx.Replace(parts[i], "").Trim();
                if (part.Length < 3 || part.Length > 200 || string.Equals(part, proc, StringComparison.OrdinalIgnoreCase)) continue;
                string where;
                if (FileNameRx.IsMatch(part)) where = "System.FileName = '" + part.Replace("'", "''") + "'";
                else if (!browser && Weight(part) >= 4 && part.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0)
                    where = "System.FileName LIKE '" + LikeEscape(part) + ".%'";
                else continue;
                asked++;
                foreach (string p in QueryIndex(where))
                {
                    string ext = Path.GetExtension(p);
                    if (JunkExt.Contains(ext) || Path.GetFileName(p).StartsWith("~$")) continue;
                    if (File.Exists(p)) return new Detected { File = p, Dir = Path.GetDirectoryName(p), Source = L.T("搜尋索引", "search index") };
                }
            }
            return null;
        }

        static string LikeEscape(string s)
        {
            return s.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        }

        static List<string> QueryIndex(string where)
        {
            var r = new List<string>();
            lock (idxLock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        OpenIndex();
                        using (var cmd = idx.CreateCommand())
                        {
                            cmd.CommandText = "SELECT TOP 8 System.ItemUrl FROM SystemIndex WHERE " + where +
                                " AND System.Kind <> 'folder' ORDER BY System.DateModified DESC";
                            using (var rd = cmd.ExecuteReader())
                                while (rd.Read())
                                {
                                    string u = rd[0] as string;
                                    if (u == null || !u.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                                    u = u.Substring(5).Replace('/', '\\');
                                    if (u.IndexOf('%') >= 0) try { u = Uri.UnescapeDataString(u); } catch { }
                                    r.Add(u);
                                }
                        }
                        return r;
                    }
                    catch { if (idx != null) { idx.Dispose(); idx = null; } }
                }
            }
            return r;
        }

        // How well a file name matches the title: 2 = full name with extension, 1 = name without extension
        public static int Score(string title, string name, bool allowBare)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return 0;
            if (Bounded(title, name, false)) return 2;
            if (!allowBare) return 0;
            string bare = Path.GetFileNameWithoutExtension(name);
            if (Weight(bare) >= 4 && Bounded(title, bare, true)) return 1;
            return 0;
        }

        static int Weight(string s)
        {
            int w = 0;
            foreach (char c in s) w += c >= 0x2E80 ? 2 : 1;
            return w;
        }

        static bool Bounded(string title, string s, bool strictAfter)
        {
            int i = 0;
            while ((i = title.IndexOf(s, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool okB = i == 0 || !char.IsLetterOrDigit(title[i - 1]);
                int e = i + s.Length;
                bool okA = e == title.Length ||
                    (strictAfter ? " -–—|:)]•·*［（".IndexOf(title[e]) >= 0 : !char.IsLetterOrDigit(title[e]) && title[e] != '.');
                if (okB && okA) return true;
                i++;
            }
            return false;
        }
    }

    partial class MainForm : Form
    {
        const int WM_NCHITTEST = 0x84, WM_HOTKEY = 0x312, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        static readonly Color Accent = Theme.Accent;
        static readonly Color Muted = Theme.Muted;
        static readonly Color HiBack = Theme.HiBack;

        readonly float s;
        readonly string settingsPath;
        readonly Dictionary<string, string> cfg = new Dictionary<string, string>();
        readonly Resolver resolver = new Resolver();
        readonly int ownPid = Process.GetCurrentProcess().Id;

        Panel content;
        Label nameLabel, statusLabel, countLabel;
        PathLabel pathLabel;
        TextBox search;
        BufferedListView list;
        ImageList images;
        IconButton btnPin;
        PillWindow pillWin;
        TaskbarWidget tbw;
        System.Windows.Forms.Timer tbTimer;
        bool taskbarMode;
        SearchField searchField;
        SortBar sortBar;
        Dot statusDot;
        NotifyIcon tray;
        ToolTip tips = new ToolTip();
        ContextMenuStrip itemMenu, crumbMenu;
        ToolStripMenuItem miAutoHide, miStartup;
        System.Windows.Forms.Timer pollTimer, hoverTimer, reloadTimer;
        FileSystemWatcher watcher;
        Font fontUi, fontBold, fontName, fontSmall, fontGlyph, fontGlyphBig, fontGlyphSmall, fontGlyphTiny;

        string curDir, curFile, lastKey;
        bool pinned, collapsed, autoHide, sortByName, suppressSearch, hotkeyOk, dwmFrame;
        int reqId, hoverTicks, leaveTicks, pollCount, resolving;
        Size expandedSize, minSize;
        Detected lastDetected;
        List<FileSystemInfo> entries = new List<FileSystemInfo>();
        Dictionary<string, int> iconIndex = new Dictionary<string, int>();

        public MainForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileFollow");
            Directory.CreateDirectory(appDir);
            settingsPath = Path.Combine(appDir, "settings.txt");
            LoadSettings();

            string glyphFace = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            string uiFace = HasFont("Microsoft JhengHei UI") ? "Microsoft JhengHei UI" : "Segoe UI";
            fontUi = new Font(uiFace, 9f);
            fontBold = new Font(uiFace, 9f, FontStyle.Bold);
            fontName = new Font(uiFace, 11.25f, FontStyle.Bold);
            fontSmall = new Font(uiFace, 8.25f);
            fontGlyph = new Font(glyphFace, 10f);
            fontGlyphBig = new Font(glyphFace, 11f);
            fontGlyphSmall = new Font(glyphFace, 9f);
            fontGlyphTiny = new Font(glyphFace, 6.5f);

            Text = L.T("跟檔夾", "FileFollow");
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Back;
            Font = fontUi;
            KeyPreview = true;
            Icon = MakeIcon();
            minSize = new Size(P(250), P(220));
            Padding = new Padding(P(5));

            BuildUi();
            BuildTray();

            autoHide = Cfg("autohide") == "1";
            sortByName = Cfg("sort") == "name";
            sortBar.ByName = sortByName;
            miAutoHide.Checked = autoHide;
            miStartup.Checked = File.Exists(StartupLnk());

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            expandedSize = new Size(CfgInt("w", P(340)), CfgInt("h", P(470)));
            var r = new Rectangle(CfgInt("x", wa.Right - expandedSize.Width - P(16)), CfgInt("y", wa.Bottom - expandedSize.Height - P(16)),
                expandedSize.Width, expandedSize.Height);
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                r = new Rectangle(wa.Right - expandedSize.Width - P(16), wa.Bottom - expandedSize.Height - P(16), expandedSize.Width, expandedSize.Height);
            Bounds = r;
            MinimumSize = minSize;

            pollTimer = new System.Windows.Forms.Timer { Interval = 350 };
            pollTimer.Tick += delegate { Poll(); };
            hoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
            hoverTimer.Tick += delegate { HoverTick(); };
            reloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
            reloadTimer.Tick += delegate { reloadTimer.Stop(); ReadEntries(); UpdateList(false); };
            tbTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            tbTimer.Tick += delegate { TaskbarTick(); };

            SetStatus(L.T("切到任何檔案，我就跳到它的資料夾", "Switch to any file and I'll jump to its folder"));
            if (Cfg("collapsed") == "1" && !TestMode) Shown += delegate { SetCollapsed(true); };
        }

        int P(float v) { return (int)Math.Round(v * s); }

        static bool HasFont(string name)
        {
            using (var f = new Font(name, 9f)) return f.Name == name;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                // Windows 11 rounds the corners and draws the border itself
                int round = 2;
                dwmFrame = Native.DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
                if (dwmFrame) { int bc = ColorTranslator.ToWin32(Theme.Border); Native.DwmSetWindowAttribute(Handle, 34, ref bc, 4); }
            }
            catch { }
            Theme.Window(this);
            hotkeyOk = Native.RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4000, (uint)Keys.F); // Ctrl+Alt+F
            InitClipboard();
            if (Cfg("tab") == "1") SwitchTab(1);
        }

        protected override void OnShown(EventArgs e)
        {
            if (!TestMode) StartTaskbar(); // before the saved collapsed state is applied
            base.OnShown(e);
            pollTimer.Start();
            hoverTimer.Start();
            new Thread(Resolver.WarmIndex) { IsBackground = true }.Start();
            if (Cfg("welcomed") != "1")
            {
                tray.ShowBalloonTip(5000, L.T("跟檔夾已啟動", "FileFollow is running"),
                    hotkeyOk ? L.T("切到任何檔案，面板就跳到它的資料夾。Ctrl+Alt+F 顯示／收起。", "Switch to any file and the panel jumps to its folder. Ctrl+Alt+F shows or hides it.") : L.T("切到任何檔案，面板就跳到它的資料夾。", "Switch to any file and the panel jumps to its folder."), ToolTipIcon.Info);
                cfg["welcomed"] = "1";
                SaveSettings();
            }
        }

        // ───────────── UI ─────────────
        void BuildUi()
        {
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back };

            // Header: folder tile + name + breadcrumbs + buttons
            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(P(12), P(2), P(8), P(6)), BackColor = Theme.Back };
            int bs = P(28);
            var tile = new FolderTile { Dock = DockStyle.Left, Width = P(34) };
            var tileGap = new Panel { Dock = DockStyle.Left, Width = P(10), BackColor = Theme.Back };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Right, Width = bs * 4, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, P(7), 0, 0), BackColor = Theme.Back };
            var btnUp = new IconButton("", fontGlyph, bs, L.T("上一層（Backspace）", "Up one level (Backspace)"), tips);
            btnPin = new IconButton("", fontGlyph, bs, L.T("釘選：停在這個資料夾，不再跟著跳", "Pin: stay in this folder instead of following"), tips);
            var btnOpen = new IconButton("", fontGlyph, bs, L.T("在檔案總管開啟", "Open in File Explorer"), tips);
            var btnCopy = new IconButton("", fontGlyph, bs, L.T("複製資料夾路徑", "Copy folder path"), tips);
            btnUp.Click += delegate { GoUp(); };
            btnPin.Click += delegate { TogglePin(); };
            btnOpen.Click += delegate { OpenInExplorer(curFile ?? curDir); };
            btnCopy.Click += delegate { if (curDir != null) { SafeClipboard(delegate { Clipboard.SetText(curDir); }); Flash(L.T("已複製路徑", "Path copied")); } };
            btns.Controls.AddRange(new Control[] { btnUp, btnPin, btnOpen, btnCopy });

            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Padding = new Padding(0, P(3), 0, P(3)) };
            pathLabel = new PathLabel { Dock = DockStyle.Bottom, Height = P(17), Font = fontSmall, ForeColor = Muted, BackColor = Theme.Back, Cursor = Cursors.Hand };
            nameLabel = new Label { Dock = DockStyle.Fill, Font = fontName, ForeColor = Theme.Text, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, UseMnemonic = false, Text = L.T("跟檔夾", "FileFollow") };
            tips.SetToolTip(pathLabel, L.T("點一下：跳到上層任一資料夾", "Click to jump to any parent folder"));
            left.Controls.Add(pathLabel);
            left.Controls.Add(nameLabel);
            nameLabel.BringToFront();
            header.Controls.Add(left);
            header.Controls.Add(btns);
            header.Controls.Add(tileGap);
            header.Controls.Add(tile);
            left.BringToFront();
            foreach (Control c in new Control[] { header, left, nameLabel, tile, tileGap }) c.MouseDown += DragWindow;
            pathLabel.MouseUp += delegate { ShowCrumbs(); };

            // Filter field
            var searchWrap = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(P(10), P(1), P(10), P(7)), BackColor = Theme.Back };
            searchField = new SearchField(fontUi) { Dock = DockStyle.Fill, Font = fontSmall, GlyphFont = fontGlyphSmall };
            search = searchField.Box;
            searchWrap.Controls.Add(searchField);
            search.HandleCreated += delegate { Native.SendMessage(search.Handle, 0x1501, (IntPtr)1, L.T("篩選檔名", "Filter by name")); };
            search.TextChanged += delegate { searchField.Invalidate(); RefreshSearch(); };
            search.KeyDown += SearchKeyDown;

            // Count + sort line
            sortBar = new SortBar { Dock = DockStyle.Fill, Font = fontSmall, GlyphFont = fontGlyphTiny, Padding = new Padding(P(14), 0, P(12), 0), Margin = Padding.Empty };
            sortBar.Toggle += delegate { sortByName = !sortByName; sortBar.ByName = sortByName; cfg["sort"] = sortByName ? "name" : "time"; SaveSettings(); UpdateList(false); sortBar.Invalidate(); };
            sortBar.MouseDown += (o, e) => { if (e.X < sortBar.Width / 2) DragWindow(o, e); };

            // File list (owner-drawn rows)
            images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Native.GetSystemMetrics(49), P(32)) };
            list = new BufferedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.None, OwnerDraw = true,
                SmallImageList = images, Margin = Padding.Empty, Font = fontUi, AllowDrop = false
            };
            list.Columns.Add(L.T("名稱", "Name"), P(200));
            list.Columns.Add(L.T("修改時間", "Modified"), 0);
            Theme.Native(list);
            list.Resize += delegate { FitColumns(); };
            // drawn at the first-column step, after the themed list has painted its selection background
            list.DrawItem += (o, e) => DrawFileRow(e.Graphics, Rectangle.FromLTRB(0, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom), e.Item, e.ItemIndex);
            list.DrawSubItem += (o, e) => { if (e.ColumnIndex == 0) DrawFileRow(e.Graphics, Rectangle.FromLTRB(0, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom), e.Item, e.ItemIndex); };
            list.ItemDrag += delegate { DragOut(); };
            list.MouseDoubleClick += (o, e) => { if (e.Button == MouseButtons.Left) OpenSelected(); };
            list.KeyDown += ListKeyDown;
            BuildItemMenu();
            list.ContextMenuStrip = itemMenu;

            // Status bar
            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(P(12), P(1), P(12), 0), BackColor = Theme.Footer };
            footer.Paint += (o, e) => { using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            statusDot = new Dot { Dock = DockStyle.Left, Width = P(12), BackColor = Theme.Footer };
            countLabel = new Label { Dock = DockStyle.Right, AutoSize = true, Font = fontSmall, ForeColor = Muted, BackColor = Theme.Footer, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, P(7), 0, 0) };
            statusLabel = new Label { Dock = DockStyle.Fill, Font = fontSmall, ForeColor = Muted, BackColor = Theme.Footer, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
            footer.Controls.Add(countLabel);
            footer.Controls.Add(statusDot);
            footer.Controls.Add(statusLabel);
            statusLabel.BringToFront();
            footer.MouseDown += DragWindow;
            statusLabel.MouseDown += DragWindow;
            statusDot.MouseDown += DragWindow;

            content.Controls.Add(BuildLayout(header, searchWrap, sortBar, list, footer));

            pillWin = new PillWindow(s, new Font(fontBold.FontFamily, 13f * s, FontStyle.Bold, GraphicsUnit.Pixel));
            tips.SetToolTip(pillWin, L.T("點一下展開（Ctrl+Alt+F）", "Click to expand (Ctrl+Alt+F)"));
            pillWin.MouseDown += PillMouseDown;
            pillWin.MouseMove += PillMouseMove;
            pillWin.MouseUp += PillMouseUp;

            Controls.Add(content);

            crumbMenu = new ContextMenuStrip { Font = fontUi };
            Theme.Menu(crumbMenu);
        }

        void BuildItemMenu()
        {
            itemMenu = new ContextMenuStrip { Font = fontUi };
            Theme.Menu(itemMenu);
            itemMenu.Opening += (o, e) =>
            {
                itemMenu.Items.Clear();
                string[] sel = SelectedPaths();
                if (sel.Length > 0)
                {
                    itemMenu.Items.Add(L.T("開啟", "Open"), null, delegate { OpenSelected(); }).Font = fontBold;
                    if (sel.Length == 1 && File.Exists(sel[0]))
                        itemMenu.Items.Add(L.T("開啟方式…", "Open with…"), null, delegate { Run("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + sel[0]); });
                    itemMenu.Items.Add(L.T("在檔案總管中顯示", "Show in File Explorer"), null, delegate { OpenInExplorer(sel[0]); });
                    itemMenu.Items.Add(new ToolStripSeparator());
                    itemMenu.Items.Add(L.T("複製檔案（可貼到 LINE、信件）", "Copy files (paste into chats or email)"), null, delegate { CopyFiles(); });
                    itemMenu.Items.Add(L.T("複製完整路徑", "Copy full path"), null, delegate { SafeClipboard(delegate { Clipboard.SetText(string.Join("\r\n", SelectedPaths())); }); Flash(L.T("已複製路徑", "Path copied")); });
                    itemMenu.Items.Add(new ToolStripSeparator());
                }
                itemMenu.Items.Add(L.T("重新整理（F5）", "Refresh (F5)"), null, delegate { ReadEntries(); UpdateList(false); });
            };
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Font = fontUi };
            Theme.Menu(menu);
            menu.Items.Add(L.T("顯示／收起（Ctrl+Alt+F）", "Show / hide (Ctrl+Alt+F)"), null, delegate { ToggleShow(); });
            AddClipTrayItems(menu);
            miAutoHide = new ToolStripMenuItem(L.T("自動收起：滑鼠移開就收、移過去就開", "Auto-hide: collapse when the mouse leaves, expand on hover"));
            miAutoHide.Click += delegate { autoHide = !autoHide; miAutoHide.Checked = autoHide; cfg["autohide"] = autoHide ? "1" : "0"; SaveSettings(); };
            miStartup = new ToolStripMenuItem(L.T("開機時自動啟動", "Start with Windows"));
            miStartup.Click += delegate { ToggleStartup(); };
            menu.Items.Add(miAutoHide);
            menu.Items.Add(miStartup);
            var miTheme = new ToolStripMenuItem(L.T("外觀", "Appearance"));
            string mode = Theme.Mode();
            foreach (string[] t in new[] {
                new[] { "auto", L.T("跟隨 Windows", "Follow Windows") },
                new[] { "light", L.T("淺色", "Light") },
                new[] { "dark", L.T("深色", "Dark") } })
            {
                string key = t[0];
                var mi = new ToolStripMenuItem(t[1]) { Checked = key == mode || (key == "auto" && mode != "light" && mode != "dark") };
                mi.Click += delegate
                {
                    if (mi.Checked) return;
                    cfg["theme"] = key;
                    SaveSettings();
                    Program.RestartRequested = true;
                    Close();
                };
                miTheme.DropDownItems.Add(mi);
            }
            Theme.Menu(miTheme.DropDown);
            menu.Items.Add(miTheme);
            menu.Items.Add(L.En ? "中文介面（重新啟動）" : "English (restart)", null, delegate
            {
                cfg["lang"] = L.En ? "zh" : "en";
                SaveSettings();
                Program.RestartRequested = true;
                Close();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("結束", "Exit"), null, delegate { Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = L.T("跟檔夾", "FileFollow"), Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ToggleShow(); };
        }

        Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(43, 108, 176)))
                    {
                        g.FillRectangle(b, 2, 6, 13, 6);
                        g.FillRectangle(b, 2, 9, 28, 19);
                    }
                    using (var w = new Pen(Color.White, 3f))
                    {
                        g.DrawLine(w, 11, 19, 21, 19);
                        g.DrawLine(w, 17, 14, 22, 19);
                        g.DrawLine(w, 17, 24, 22, 19);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ───────────── Foreground window tracking ─────────────
        void Poll()
        {
            if (TaskbarOn) tbw.Sync();
            if (++pollCount % 170 == 0 && !collapsed && MouseButtons == MouseButtons.None) // refresh the 'min ago' labels about once a minute
            {
                if (tab == 0) UpdateList(false); else clipList.Invalidate();
            }
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == ownPid) { lastKey = null; return; }
            string title = Native.WindowTitle(h);
            string key = h.ToString() + "|" + title;
            if (key == lastKey) return;
            lastKey = key;
            if (pinned || title.Length == 0) return;

            // A busy app (e.g. Office showing a dialog) blocks detection threads; cap them so they cannot pile up
            if (Thread.VolatileRead(ref resolving) >= 4) { lastKey = null; return; }
            int my = ++reqId;
            Interlocked.Increment(ref resolving);
            var t = new Thread(() =>
            {
                Detected d = null;
                try { d = resolver.Resolve((int)pid, title); } catch { }
                finally { Interlocked.Decrement(ref resolving); }
                try { BeginInvoke((MethodInvoker)delegate { if (my == reqId) OnDetected(d); }); } catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void OnDetected(Detected d)
        {
            if (d == null || pinned) return;
            lastDetected = d;
            ShowFolder(d.Dir, d.File);
            SetStatus(DetectedText(d));
        }

        static string DetectedText(Detected d)
        {
            string app = d.Proc;
            switch ((d.Proc ?? "").ToUpperInvariant())
            {
                case "WINWORD": app = "Word"; break;
                case "EXCEL": app = "Excel"; break;
                case "POWERPNT": app = "PowerPoint"; break;
                case "ACROBAT": case "ACRORD32": app = "Acrobat"; break;
            }
            return L.T("跟隨 ", "Following ") + app + L.T("：", ": ") + (d.File != null ? Path.GetFileName(d.File) : d.Dir);
        }

        // ───────────── Folder contents ─────────────
        void ShowFolder(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (!Directory.Exists(dir)) return; } catch { return; }
            bool sameDir = string.Equals(dir, curDir, StringComparison.OrdinalIgnoreCase);
            bool sameFile = string.Equals(file, curFile, StringComparison.OrdinalIgnoreCase);
            if (sameDir && sameFile) return;
            curDir = dir;
            curFile = file;
            string name = Path.GetFileName(dir.TrimEnd('\\'));
            nameLabel.Text = string.IsNullOrEmpty(name) ? dir : name;
            pathLabel.Text = dir;
            tips.SetToolTip(nameLabel, dir);
            tips.SetToolTip(pillWin, dir + L.T("\n點一下展開（Ctrl+Alt+F）", "\nClick to expand (Ctrl+Alt+F)"));
            ShowPill();
            if (!sameDir)
            {
                if (search.Text.Length > 0) { suppressSearch = true; search.Text = ""; suppressSearch = false; }
                ReadEntries();
                Watch(dir);
            }
            UpdateList(true);
        }

        void ReadEntries()
        {
            entries = new List<FileSystemInfo>();
            if (curDir == null) return;
            try
            {
                foreach (var fsi in new DirectoryInfo(curDir).EnumerateFileSystemInfos())
                {
                    if ((fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (fsi.Name.StartsWith("~$") || fsi.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(fsi);
                    if (entries.Count >= 3000) break;
                }
            }
            catch { }
        }

        void Watch(string dir)
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            try
            {
                watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    SynchronizingObject = this
                };
                FileSystemEventHandler h = delegate { reloadTimer.Stop(); reloadTimer.Start(); };
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += delegate { reloadTimer.Stop(); reloadTimer.Start(); };
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }
        }

        void UpdateList(bool scrollToCurrent)
        {
            if (list == null) return;
            string q = search.Text.Trim();
            IEnumerable<FileSystemInfo> items = entries;
            if (q.Length > 0) items = items.Where(e => e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (sortByName)
                items = items.OrderBy(e => e is FileInfo ? 1 : 0).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
            else
                items = items.OrderByDescending(e => SafeTime(e));
            var arr = items.ToList();

            var selected = new HashSet<string>(SelectedPaths(), StringComparer.OrdinalIgnoreCase);
            int top = -1;
            try { if (list.TopItem != null) top = list.TopItem.Index; } catch { }

            list.BeginUpdate();
            list.Items.Clear();
            ListViewItem current = null;
            var now = DateTime.Now;
            foreach (var e in arr)
            {
                var it = new ListViewItem(e.Name, IconFor(e));
                DateTime t = SafeTime(e);
                it.SubItems.Add(FormatTime(t, now));
                it.Tag = e.FullName;
                if (curFile != null && string.Equals(e.FullName, curFile, StringComparison.OrdinalIgnoreCase))
                {
                    current = it;
                }
                if (selected.Contains(e.FullName)) it.Selected = true;
                list.Items.Add(it);
            }
            list.EndUpdate();
            if (scrollToCurrent)
            {
                if (current != null) current.EnsureVisible();
                else if (list.Items.Count > 0) list.Items[0].EnsureVisible();
            }
            else if (top >= 0 && top < list.Items.Count)
            {
                try { list.TopItem = list.Items[top]; } catch { }
            }
            folderCount = arr.Count + (q.Length > 0 ? L.T("／", "/") + entries.Count : "") + L.T(" 個項目", " items");
            sortBar.Count = folderCount;
            FitColumns();
        }

        static DateTime SafeTime(FileSystemInfo e)
        {
            try { return e.LastWriteTime; } catch { return DateTime.MinValue; }
        }

        void FitColumns()
        {
            if (list.Columns.Count < 2) return;
            list.Columns[1].Width = 0;
            list.Columns[0].Width = RowWidth(list);
        }

        // One file row: rounded highlight, system icon, name, time on the right
        void DrawFileRow(Graphics g, Rectangle b, ListViewItem item, int index)
        {
            using (var bg = new SolidBrush(Theme.Back)) g.FillRectangle(bg, b);
            if (item == null) return;
            var e = new { Item = item };
            bool cur = curFile != null && string.Equals((string)item.Tag, curFile, StringComparison.OrdinalIgnoreCase);
            Color back = item.Selected ? Theme.Sel : cur ? HiBack : index == list.Hot ? Theme.Hover : Color.Empty;
            Gfx.Fill(g, new RectangleF(b.X + P(6), b.Y + 1, b.Width - P(12), b.Height - 2), P(6), back);
            int x = b.X + P(14);
            if (e.Item.ImageIndex >= 0 && e.Item.ImageIndex < images.Images.Count)
            {
                images.Draw(g, x, b.Y + (b.Height - images.ImageSize.Height) / 2, e.Item.ImageIndex);
                x += images.ImageSize.Width + P(10);
            }
            string time = e.Item.SubItems.Count > 1 ? e.Item.SubItems[1].Text : "";
            const TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int tw = TextRenderer.MeasureText(g, time, fontSmall, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            int right = b.Right - P(14);
            TextRenderer.DrawText(g, time, fontSmall, new Rectangle(right - tw, b.Y, tw, b.Height), cur ? Accent : Muted, tf | TextFormatFlags.Right);
            TextRenderer.DrawText(g, e.Item.Text, cur ? fontBold : fontUi, new Rectangle(x, b.Y, Math.Max(0, right - tw - P(10) - x), b.Height),
                cur ? Accent : Theme.Text, tf | TextFormatFlags.Left);
        }

        void UpdateDot()
        {
            statusDot.Visible = tab == 0;
            statusDot.DotColor = pinned ? Accent : lastDetected != null ? Theme.Ok : Theme.Border;
        }

        static string FormatTime(DateTime t, DateTime now)
        {
            TimeSpan d = now - t;
            if (d.TotalSeconds < 0) return t.ToString("HH:mm");
            if (d.TotalMinutes < 1) return L.T("剛剛", "Just now");
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + L.T(" 分鐘前", " min ago");
            if (t.Date == now.Date) return L.T("今天 ", "Today ") + t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return L.T("昨天 ", "Yesterday ") + t.ToString("HH:mm");
            if (t.Year == now.Year) return t.ToString("M/d HH:mm");
            return t.ToString("yyyy/M/d");
        }

        int IconFor(FileSystemInfo e)
        {
            string ext = e is DirectoryInfo ? "" : (e.Name.Contains('.') ? Path.GetExtension(e.Name).ToLowerInvariant() : "");
            bool perFile = e is FileInfo && (ext == "" || ext == ".exe" || ext == ".lnk" || ext == ".ico" || ext == ".url" || ext == ".appref-ms");
            string key = e is DirectoryInfo ? "<dir>" : perFile ? e.FullName : ext;
            int idx;
            if (iconIndex.TryGetValue(key, out idx)) return idx;

            var info = new Native.SHFILEINFO();
            const uint ICON = 0x100, SMALL = 0x1, USEATTR = 0x10;
            IntPtr r;
            if (e is DirectoryInfo) r = Native.SHGetFileInfo("folder", 0x10, ref info, (uint)Marshal.SizeOf(info), ICON | SMALL | USEATTR);
            else if (perFile) r = Native.SHGetFileInfo(e.FullName, 0, ref info, (uint)Marshal.SizeOf(info), ICON | SMALL);
            else r = Native.SHGetFileInfo("x" + ext, 0x80, ref info, (uint)Marshal.SizeOf(info), ICON | SMALL | USEATTR);

            var bmp = new Bitmap(images.ImageSize.Width, images.ImageSize.Height);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                using (var ic = Icon.FromHandle(info.hIcon))
                using (var g = Graphics.FromImage(bmp))
                {
                    int w = images.ImageSize.Width;
                    g.DrawIcon(ic, new Rectangle(0, (bmp.Height - w) / 2, w, w));
                }
                Native.DestroyIcon(info.hIcon);
            }
            images.Images.Add(bmp);
            idx = images.Images.Count - 1;
            iconIndex[key] = idx;
            return idx;
        }

        // ───────────── Actions ─────────────
        string[] SelectedPaths()
        {
            if (list == null) return new string[0];
            return list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
        }

        void DragOut()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Link);
        }

        void OpenSelected()
        {
            string[] sel = SelectedPaths();
            if (sel.Length == 0) return;
            if (sel.Length == 1 && Directory.Exists(sel[0])) { Navigate(sel[0]); return; }
            foreach (string p in sel.Take(10))
                if (File.Exists(p)) Run(p, null);
        }

        void CopyFiles()
        {
            string[] sel = SelectedPaths();
            if (sel.Length == 0) return;
            var sc = new System.Collections.Specialized.StringCollection();
            sc.AddRange(sel);
            SafeClipboard(delegate { Clipboard.SetFileDropList(sc); });
            Flash(L.T("已複製 ", "Copied ") + sel.Length + L.T(" 個檔案，可直接貼上", " file(s), ready to paste"));
        }

        void Navigate(string dir)
        {
            ShowFolder(dir, null);
            if (!pinned) SetStatus(L.T("瀏覽中 · 切回工作視窗會跳回", "Browsing · switching back to your work jumps back"));
        }

        void GoUp()
        {
            if (curDir == null) return;
            try
            {
                var parent = Directory.GetParent(curDir.TrimEnd('\\'));
                if (parent == null) return;
                string from = curDir;
                Navigate(parent.FullName);
                foreach (ListViewItem it in list.Items)
                    if (string.Equals((string)it.Tag, from, StringComparison.OrdinalIgnoreCase)) { it.Selected = true; it.EnsureVisible(); }
            }
            catch { }
        }

        void TogglePin()
        {
            pinned = !pinned;
            btnPin.Text = pinned ? "" : "";
            btnPin.ForeColor = pinned ? Accent : Theme.Icon;
            tips.SetToolTip(btnPin, pinned ? L.T("取消釘選：恢復跟著目前檔案跳", "Unpin: follow the current file again") : L.T("釘選：停在這個資料夾，不再跟著跳", "Pin: stay in this folder instead of following"));
            if (pinned) SetStatus(L.T("已釘選，不跟著跳", "Pinned, not following"));
            else { lastKey = null; SetStatus(lastDetected != null ? DetectedText(lastDetected) : L.T("恢復跟隨", "Following again")); }
        }

        void ShowCrumbs()
        {
            if (curDir == null) return;
            crumbMenu.Items.Clear();
            var chain = new List<string>();
            try
            {
                for (var d = new DirectoryInfo(curDir); d != null; d = d.Parent) chain.Add(d.FullName);
            }
            catch { }
            for (int i = 0; i < chain.Count; i++)
            {
                string full = chain[i];
                string nm = Path.GetFileName(full.TrimEnd('\\'));
                if (string.IsNullOrEmpty(nm)) nm = full;
                var mi = new ToolStripMenuItem(new string('　', Math.Max(0, chain.Count - 1 - i)) + nm);
                mi.ToolTipText = full;
                if (i == 0) mi.Font = fontBold;
                mi.Click += delegate { Navigate(full); };
                crumbMenu.Items.Insert(0, mi);
            }
            crumbMenu.Items.Add(new ToolStripSeparator());
            crumbMenu.Items.Add(L.T("複製路徑", "Copy path"), null, delegate { SafeClipboard(delegate { Clipboard.SetText(curDir); }); Flash(L.T("已複製路徑", "Path copied")); });
            crumbMenu.Show(pathLabel, new Point(0, pathLabel.Height));
        }

        void OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path) || (Directory.Exists(path) && !string.Equals(path, curDir, StringComparison.OrdinalIgnoreCase)))
                Run("explorer.exe", "/select,\"" + path + "\"");
            else Run("explorer.exe", "\"" + path + "\"");
        }

        static void Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, L.T("跟檔夾", "FileFollow"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void SafeClipboard(MethodInvoker act)
        {
            for (int i = 0; i < 3; i++)
            {
                try { act(); return; } catch { Thread.Sleep(60); }
            }
        }

        void SetStatus(string t)
        {
            folderStatus = t;
            UpdateDot();
            if (tab != 0) return;
            statusLabel.Text = t;
            tips.SetToolTip(statusLabel, t);
        }

        void Flash(string t)
        {
            string keep = statusLabel.Text;
            statusLabel.ForeColor = Accent;
            statusLabel.Text = t;
            var tm = new System.Windows.Forms.Timer { Interval = 1600 };
            tm.Tick += delegate { tm.Stop(); tm.Dispose(); if (statusLabel.Text == t) { statusLabel.Text = keep; } statusLabel.ForeColor = Muted; };
            tm.Start();
        }

        void ListKeyDown(object o, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { CopyFiles(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem it in list.Items) it.Selected = true; e.Handled = true; }
        }

        void SearchKeyDown(object o, KeyEventArgs e)
        {
            if (tab == 1)
            {
                if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) && clipList.Items.Count > 0)
                {
                    clipList.Focus();
                    clipList.SelectedItems.Clear();
                    clipList.Items[0].Selected = true;
                    clipList.Items[0].Focused = true;
                    if (e.KeyCode == Keys.Enter) UseSelectedClip(e.Shift);
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape) { search.Text = ""; e.Handled = e.SuppressKeyPress = true; }
                return;
            }
            if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                list.Focus();
                list.SelectedItems.Clear();
                list.Items[0].Selected = true;
                list.Items[0].Focused = true;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && list.Items.Count > 0)
            {
                list.SelectedItems.Clear();
                list.Items[0].Selected = true;
                OpenSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape) { search.Text = ""; e.Handled = e.SuppressKeyPress = true; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { search.Focus(); search.SelectAll(); return true; }
            if (keyData == Keys.F5) { if (tab == 0) { ReadEntries(); UpdateList(false); } else UpdateClips(); return true; }
            if (keyData == (Keys.Control | Keys.Tab)) { SwitchTab(1 - tab); return true; }
            if (keyData == Keys.Escape && !search.Focused) { SetCollapsed(true); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ───────────── Collapse / expand / move ─────────────
        void DragWindow(object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        Point pillDown; bool pillMoved;
        void PillMouseDown(object o, MouseEventArgs e) { pillDown = e.Location; pillMoved = false; }
        void PillMouseMove(object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || pillMoved) return;
            if (Math.Abs(e.X - pillDown.X) + Math.Abs(e.Y - pillDown.Y) < P(5)) return;
            pillMoved = true;
            Native.ReleaseCapture();
            Native.SendMessage(pillWin.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            SaveSettings();
        }
        void PillMouseUp(object o, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !pillMoved) SetCollapsed(false);
            if (e.Button == MouseButtons.Right) tray.ContextMenuStrip.Show(Cursor.Position);
        }

        // What the collapsed view shows: the folder name, or "Clipboard" while the clipboard tab is open
        string CollapsedLabel { get { return tab == 1 ? L.T("剪貼簿", "Clipboard") : nameLabel.Text; } }

        Size PillSize() { return pillWin.Measure(CollapsedLabel); }

        // Shows the collapsed view: the taskbar widget when that mode is on and attached, otherwise the floating pill
        void ShowPill()
        {
            if (TaskbarOn) { tbw.SetText(CollapsedLabel, tab == 1); pillWin.Hide(); return; }
            if (!collapsed) return;
            Rectangle from = pillWin.Visible ? pillWin.Visual : Bounds;
            pillWin.ShowAt(AnchorRect(from, PillSize()), CollapsedLabel, tab == 1);
        }

        bool TaskbarOn { get { return taskbarMode && tbw != null && tbw.Attached; } }

        // The collapsed view lives on the taskbar; the floating pill is only a fallback while the taskbar can't be used
        // (vertical taskbar, no room, Explorer restarting)
        void StartTaskbar()
        {
            taskbarMode = true;
            // Microsoft JhengHei (微軟正黑體), not the UI variant the panel uses
            tbw = new TaskbarWidget(s, new Font(HasFont("Microsoft JhengHei") ? "Microsoft JhengHei" : fontUi.Name, 15f * s, FontStyle.Regular, GraphicsUnit.Pixel));
            tbw.Clicked += ToggleShow;
            tbw.RightClicked += delegate { tray.ContextMenuStrip.Show(Cursor.Position); };
            tbw.Attach(CollapsedLabel);
            tbw.SetText(CollapsedLabel, tab == 1);
            tbTimer.Start();
            if (collapsed) { if (TaskbarOn) pillWin.Hide(); else ShowPill(); }
        }

        // Keeps the taskbar widget attached (taskbar moved, tray resized, Explorer restarted)
        void TaskbarTick()
        {
            if (!taskbarMode || tbw == null) return;
            bool was = tbw.Attached;
            tbw.Check();
            if (collapsed && was != tbw.Attached) { if (TaskbarOn) pillWin.Hide(); else ShowPill(); }
        }

        // Collapsed, the panel hides and a separate layered pill window takes its place
        void SetCollapsed(bool c)
        {
            if (c == collapsed) { if (!c) Activate(); return; }
            if (c)
            {
                expandedSize = Size;
                collapsed = true;
                ShowPill();
                // list tooltips are separate popups and would linger after the panel hides
                foreach (ListView lv in new ListView[] { list, clipList })
                {
                    IntPtr tt = Native.SendMessage(lv.Handle, 0x104E, IntPtr.Zero, IntPtr.Zero); // LVM_GETTOOLTIPS
                    if (tt != IntPtr.Zero) Native.SendMessage(tt, 0x041C, IntPtr.Zero, IntPtr.Zero); // TTM_POP
                }
                Hide();
            }
            else
            {
                collapsed = false;
                // reopen where it was last time; only a spot that is no longer on any screen falls back to the widget
                if (TaskbarOn) Bounds = OnScreen(new Rectangle(Location, expandedSize)) ?? AnchorRect(tbw.ScreenRect, expandedSize);
                else if (pillWin.Visible) Bounds = AnchorRect(pillWin.Visual, expandedSize);
                Show();
                pillWin.Hide();
                pillWin.Hover(false);
                Activate();
                UpdateList(true);
            }
            hoverTicks = leaveTicks = 0;
            SaveSettings();
        }

        // A rectangle of the new size that stays attached to the nearest screen edges
        static Rectangle AnchorRect(Rectangle from, Size ns)
        {
            Rectangle wa = Screen.FromRectangle(from).WorkingArea;
            bool right = from.Left + from.Width / 2 > wa.Left + wa.Width / 2;
            bool bottom = from.Top + from.Height / 2 > wa.Top + wa.Height / 2;
            int x = right ? from.Right - ns.Width : from.Left;
            int y = bottom ? from.Bottom - ns.Height : from.Top;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - ns.Width));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - ns.Height));
            return new Rectangle(x, y, ns.Width, ns.Height);
        }

        // The rectangle pulled inside its screen's working area, or null when most of it is off every screen
        static Rectangle? OnScreen(Rectangle r)
        {
            Rectangle wa = Screen.FromRectangle(r).WorkingArea;
            Rectangle hit = Rectangle.Intersect(wa, r);
            if (hit.Width * hit.Height * 2 < r.Width * r.Height) return null;
            int x = Math.Max(wa.Left, Math.Min(r.X, wa.Right - r.Width));
            int y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - r.Height));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        void ToggleShow()
        {
            if (collapsed) { SetCollapsed(false); Activate(); }
            else if (!Visible) { Show(); Activate(); }
            else SetCollapsed(true);
        }

        void HoverTick()
        {
            if (!autoHide) { hoverTicks = leaveTicks = 0; return; }
            bool inside = (collapsed ? (TaskbarOn ? tbw.ScreenRect : pillWin.Visual) : Bounds).Contains(Cursor.Position);
            if (collapsed)
            {
                hoverTicks = inside ? hoverTicks + 1 : 0;
                if (hoverTicks >= 3) SetCollapsed(false);
                return;
            }
            bool busy = MouseButtons != MouseButtons.None || itemMenu.Visible || crumbMenu.Visible || clipMenu.Visible || pasteTarget != IntPtr.Zero
                || tray.ContextMenuStrip.Visible || (search.Focused && search.Text.Length > 0);
            leaveTicks = (!inside && !busy) ? leaveTicks + 1 : 0;
            if (leaveTicks >= 8) SetCollapsed(true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) { if ((int)m.WParam == 2) OpenClipTab(); else ToggleShow(); return; }
            if (m.Msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
            if (m.Msg == 0x1A && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet" && Theme.Detect() != Theme.Dark)
            {
                // Windows switched between light and dark: restart to repaint everything
                Program.RestartRequested = true;
                BeginInvoke((Action)Close);
            }
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && !collapsed && (int)m.Result == 1)
            {
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = P(6);
                bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                int ht = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
                if (ht != 0) m.Result = (IntPtr)ht;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!dwmFrame)
                using (var pen = new Pen(Theme.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveSettings(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (hotkeyOk) Native.UnregisterHotKey(Handle, 1);
            ShutdownClipboard();
            if (tbw != null) tbw.Detach();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        // ───────────── Settings ─────────────
        string StartupLnk()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string legacy = Path.Combine(dir, "跟檔夾.lnk"); // early versions used a Chinese file name
            return File.Exists(legacy) ? legacy : Path.Combine(dir, "FileFollow.lnk");
        }

        void ToggleStartup()
        {
            string lnk = StartupLnk();
            try
            {
                if (File.Exists(lnk)) File.Delete(lnk);
                else Com.MakeShortcut(lnk, Application.ExecutablePath);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, L.T("跟檔夾", "FileFollow")); }
            miStartup.Checked = File.Exists(lnk);
        }

        string Cfg(string k) { string v; return cfg.TryGetValue(k, out v) ? v : null; }
        int CfgInt(string k, int def) { int v; return int.TryParse(Cfg(k), out v) ? v : def; }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath)) return;
                foreach (string line in File.ReadAllLines(settingsPath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) cfg[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static bool TestMode;
        public void TestShow(string dir, string file)
        {
            SwitchTab(0);
            OnDetected(new Detected { Dir = dir, File = file, Proc = "WINWORD", Source = "test" });
        }
        public Bitmap TestPill() { return pillWin.Preview(CollapsedLabel); }

        public ToolStripItem[] TestMenuItems() { return tray.ContextMenuStrip.Items.Cast<ToolStripItem>().ToArray(); }

        // Test hook: opens the tray menu (and its Appearance submenu) and returns the screen area they cover
        public Rectangle TestMenu(Point at)
        {
            var m = tray.ContextMenuStrip;
            m.Show(at);
            Application.DoEvents();
            Rectangle r = m.Bounds;
            foreach (ToolStripItem it in m.Items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems && mi.Text == L.T("外觀", "Appearance"))
                {
                    mi.Select();
                    mi.ShowDropDown();
                    Application.DoEvents();
                    r = Rectangle.Union(r, mi.DropDown.Bounds);
                }
            }
            return r;
        }
        public void TestClipTab() { SwitchTab(1); }
        public Point TestClipListOrigin() { return clipList.PointToScreen(Point.Empty); }
        public void TestHoverClip(int i)
        {
            if (i >= clipList.Items.Count) return;
            Rectangle b = clipList.Items[i].Bounds;
            Cursor.Position = clipList.PointToScreen(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
        }
        public void TestScrollClips() { if (clipList.Items.Count > 0) clipList.Items[clipList.Items.Count - 1].EnsureVisible(); }
        public void TestSelectClip(int i)
        {
            if (i >= clipList.Items.Count) return;
            clipList.Focus();
            clipList.Items[i].Selected = true;
            clipList.Items[i].Focused = true;
        }

        void SaveSettings()
        {
            if (!IsHandleCreated || TestMode) return;
            Size sz = collapsed ? expandedSize : Size;
            // store the expanded position so the next start lands in the same place
            Rectangle r = AnchorRect(collapsed && pillWin.Visible ? pillWin.Visual : Bounds, sz);
            cfg["x"] = r.X.ToString();
            cfg["y"] = r.Y.ToString();
            cfg["w"] = sz.Width.ToString();
            cfg["h"] = sz.Height.ToString();
            cfg["collapsed"] = collapsed ? "1" : "0";
            try { File.WriteAllLines(settingsPath, cfg.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8); } catch { }
        }
    }

    static class Program
    {
        public static bool RestartRequested;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--probe") { Probe(args[1]); return; }
            if (args.Length == 2 && args[0] == "--taskbar-test") { TaskbarTest(args[1]); return; }
            if (args.Length == 2 && args[0] == "--menu-test") { MenuTest(args[1]); return; }
            if (args.Length == 3 && args[0] == "--list-test") { ListTest(args[1], args[2]); return; }
            if (args.Length == 4 && args[0] == "--snapshot") { Snapshot(args[1], args[2], args[3]); return; }

            bool created;
            using (var mutex = new Mutex(true, "FileFollow_SingleInstance", out created))
            {
                if (!created) return;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            if (RestartRequested) Process.Start(Application.ExecutablePath);
        }

        // Test hook: panel on screen with a clipboard row selected, then focus moved away; screenshots both states
        static void ListTest(string dir, string outPng)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            MainForm.TestMode = true;
            var f = new MainForm();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Right - f.Width - 40, wa.Top + 40);
            f.Show();
            f.TestFeedClips(dir);
            f.TestClipTab();
            f.TestSelectClip(2);
            Action<string> shot = name =>
            {
                for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                    bmp.Save(outPng.Replace(".png", "-" + name + ".png"));
                }
            };
            f.Activate();
            shot("focused");
            var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(wa.Left + 40, wa.Top + 40), Size = new Size(200, 120), Text = "other" };
            other.Show();
            other.Activate();
            shot("unfocused");
            other.Close();
            f.Hide();
            Application.DoEvents();
            f.Show();
            f.Activate();
            shot("reshown");
            f.TestScrollClips();
            shot("scrolled");
            Point savedCursor = Cursor.Position;
            f.TestHoverClip(5);
            shot("hover");
            for (int i = 0; i < 50; i++) { Application.DoEvents(); Thread.Sleep(30); } // let the row tooltip appear
            shot("dwell");
            // sweep: move across rows and grab frames as fast as possible, to catch in-between repaints
            Point top = f.TestClipListOrigin();
            const int frames = 24, fw = 140, fh = 300;
            using (var strip = new Bitmap(frames * (fw + 4), fh))
            using (var sg = Graphics.FromImage(strip))
            {
                sg.Clear(Color.Magenta);
                for (int i = 0; i < frames; i++)
                {
                    Cursor.Position = new Point(top.X + 60, top.Y + 10 + i * 11);
                    Application.DoEvents();
                    Thread.Sleep(8);
                    using (var fr = new Bitmap(fw, fh))
                    {
                        using (var g = Graphics.FromImage(fr)) g.CopyFromScreen(top, Point.Empty, fr.Size);
                        sg.DrawImage(fr, i * (fw + 4), 0);
                    }
                }
                strip.Save(outPng.Replace(".png", "-sweep.png"));
            }
            Cursor.Position = savedCursor;
            f.Close();
        }

        // Test hook: screenshots the tray menu with a submenu open
        static void MenuTest(string outPng)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            MainForm.TestMode = true;
            var f = new MainForm();
            f.Location = new Point(-6000, -6000);
            f.Show();
            Application.DoEvents();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Rectangle r = f.TestMenu(new Point(wa.Right - 700, wa.Bottom - 520));
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var sb = new StringBuilder();
            var tm = f.TestMenuItems()[0].Owner;
            sb.AppendLine("menu size=" + tm.Size + " client=" + tm.ClientSize + " padding=" + tm.Padding + " display=" + tm.DisplayRectangle);
            foreach (ToolStripItem it in f.TestMenuItems())
                sb.AppendLine(it.GetType().Name + " [" + it.Text + "] bounds=" + it.Bounds + " content=" + it.ContentRectangle + " pad=" + it.Padding);
            File.WriteAllText(outPng + ".txt", sb.ToString(), Encoding.UTF8);
            r.Inflate(24, 24);
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(outPng);
            }
            f.Close();
        }

        // Test hook: attaches the taskbar widget for a few seconds and writes what happened
        static void TaskbarTest(string outTxt)
        {
            Native.SetProcessDPIAware();
            float sc;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f;
            var w = new TaskbarWidget(sc, new Font("Microsoft JhengHei", 15f * sc, FontStyle.Regular, GraphicsUnit.Pixel));
            bool ok = w.Attach(L.T("跟檔夾", "FileFollow"));
            File.WriteAllText(outTxt, (ok ? "attached " : "failed ") + w.Trace, Encoding.UTF8);
            var end = DateTime.Now.AddSeconds(6);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(30); }
            w.Detach();
        }

        // Test hook: renders the panel (expanded and collapsed) to images without touching settings
        static void Snapshot(string dir, string file, string outPng)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            MainForm.TestMode = true;
            var f = new MainForm();
            f.Location = new Point(-6000, -6000);
            f.Show();
            Application.DoEvents();
            f.TestShow(dir, file);
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var a = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(a, new Rectangle(Point.Empty, f.Size));
            File.WriteAllText(outPng + ".txt", f.TestFeedClips(dir), Encoding.UTF8);
            f.TestClipTab();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var c = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(c, new Rectangle(Point.Empty, f.Size));
            var b = f.TestPill();
            using (var all = new Bitmap(a.Width + c.Width + b.Width + 60, Math.Max(a.Height, b.Height) + 40))
            using (var g = Graphics.FromImage(all))
            {
                g.Clear(Color.FromArgb(200, 205, 212));
                g.DrawImage(a, 20, 20);
                g.DrawImage(c, a.Width + 30, 20);
                g.DrawImage(b, a.Width + c.Width + 40, 20);
                all.Save(outPng);
            }
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPng)), Path.GetFileNameWithoutExtension(outPng));
            a.Save(stem + "-folder.png");
            c.Save(stem + "-clip.png");
            b.Save(stem + "-pill.png");
            f.Close();
        }

        // Test hook: lists the file detected for every visible window
        static void Probe(string outFile)
        {
            var r = new Resolver();
            var sb = new StringBuilder();
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                string title = Native.WindowTitle(h);
                if (title.Length == 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                Detected d = null;
                try { d = r.Resolve((int)pid, title); } catch { }
                sb.AppendLine(title + "\t→\t" + (d == null ? "-" : d.Source + L.T("：", ": ") + (d.File ?? d.Dir)));
                return true;
            }, IntPtr.Zero);
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }
    }
}
