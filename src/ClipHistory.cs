// Clipboard tab: records copied/cut text, links, files and images; copy back, paste or drag out
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FileFollow
{
    static class ClipNative
    {
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    }

    public class Clip
    {
        public string Id;
        public string Kind;      // text url email color path files image
        public string Text;
        public string[] Files;
        public bool Cut;
        public bool IsDir;       // single folder; decided at capture time so painting never touches the file system
        public string Image;    // png file name inside the clips folder
        public string Html;      // .html inside the clips folder (raw CF_HTML, keeps formatting when copied back)
        public string Rtf;
        public int W, H;
        public string Source;
        public long Time;
        public bool Pinned;
        public string Sig;
        [ScriptIgnore] public string HtmlData;
        [ScriptIgnore] public string RtfData;
        [ScriptIgnore] public byte[] PngData;
    }

    class BufferedListView : ListView
    {
        public BufferedListView() { DoubleBuffered = true; }

        // the owner-drawn rows show selection themselves; the native focus rectangle only leaves stray lines
        protected override bool ShowFocusCues { get { return false; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(Handle, 0x0127, (IntPtr)(1 | (1 << 16)), IntPtr.Zero); // WM_CHANGEUISTATE: UIS_SET, UISF_HIDEFOCUS
            SendMessage(Handle, 0x1036, (IntPtr)0x10000, (IntPtr)0x10000);      // LVM_SETEXTENDEDLISTVIEWSTYLE: LVS_EX_DOUBLEBUFFER
        }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // The themed list still outlines the focused row after the owner draw; clearing CDIS_FOCUS in
        // NMCUSTOMDRAW.uItemState at item prepaint keeps it from drawing that outline
        protected override void WndProc(ref Message m)
        {
            // owner-drawn rows cover everything; erasing first would flash the bare background on every repaint
            if (m.Msg == 0x0014 && OwnerDraw) { m.Result = (IntPtr)1; return; } // WM_ERASEBKGND
            if (m.Msg == 0x204E && OwnerDraw) // reflected WM_NOTIFY
            {
                int p = IntPtr.Size;
                if (Marshal.ReadInt32(m.LParam, 2 * p) == -12 && (Marshal.ReadInt32(m.LParam, 3 * p) & 0x10000) != 0) // NM_CUSTOMDRAW, item stage
                {
                    int stateOff = 4 * p + p + 16 + p; // hdr (3 pointers) + padded stage, hdc, RECT rc, dwItemSpec
                    Marshal.WriteInt32(m.LParam, stateOff, Marshal.ReadInt32(m.LParam, stateOff) & ~0x10); // CDIS_FOCUS
                }
            }
            base.WndProc(ref m);
            // anything that can move the scroll position: let the thin scroll bar catch up
            switch (m.Msg)
            {
                case 0x0115: case 0x020A: case 0x0100: case 0x0005: case 0x1013: case 0x1014: case 0x1008: case 0x1009:
                    if (Scrolled != null) Scrolled();
                    break;
            }
        }

        public event Action Scrolled;

        // Row under the mouse, for hover highlighting in owner-drawn rows
        public int Hot = -1;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var it = GetItemAt(e.X, e.Y);
            int h = it == null ? -1 : it.Index;
            if (h != Hot) { int old = Hot; Hot = h; Redraw(old); Redraw(h); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            int old = Hot; Hot = -1; Redraw(old);
        }
        void Redraw(int i) { if (i >= 0 && i < Items.Count) Invalidate(Items[i].Bounds); }
    }

    partial class MainForm
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        const int MaxClips = 300;
        const int MaxText = 200000;
        const int MaxFiles = 2000;
        const long MaxPixels = 40000000; // about 8000x5000; larger images are skipped to protect memory
        static readonly HashSet<string> RiskyExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".msi", ".msp", ".lnk", ".url", ".reg", ".cpl", ".jar", ".appref-ms", ".application"
        };

        int tab;                       // 0 = Folder, 1 = Clipboard
        TableLayoutPanel layout;
        Panel tabBar;
        Control folderHeader;
        SegTabs tabs;
        ChipBar chipBar;
        Panel subRow;
        int clipFilter;
        BufferedListView clipList;
        ContextMenuStrip clipMenu;
        ToolStripMenuItem miClipPause;
        System.Windows.Forms.Timer clipTimer, clipSaveTimer;
        List<Clip> clips = new List<Clip>();
        Dictionary<string, Bitmap> clipThumbs = new Dictionary<string, Bitmap>();
        Dictionary<string, Bitmap> bigIcons = new Dictionary<string, Bitmap>();
        Dictionary<string, string> appNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string clipDir, clipJson, folderStatus = "", folderCount = "";
        bool clipPaused, clipListening;
        int clipRetry;
        DateTime suppressClipUntil;
        IntPtr pasteTarget;

        static readonly Dictionary<string, string> KnownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "firefox", "Firefox" }, { "brave", "Brave" },
            { "WINWORD", "Word" }, { "EXCEL", "Excel" }, { "POWERPNT", "PowerPoint" }, { "OUTLOOK", "Outlook" }, { "olk", "Outlook" },
            { "explorer", L.T("檔案總管", "File Explorer") }, { "LINE", "LINE" }, { "Archicad", "Archicad" }, { "acad", "AutoCAD" },
            { "Code", "VS Code" }, { "Notepad", L.T("記事本", "Notepad") }, { "claude", "Claude" }, { "ms-teams", "Teams" }, { "Teams", "Teams" },
            { "Acrobat", "Acrobat" }, { "AcroRd32", "Acrobat" }, { "Photoshop", "Photoshop" }, { "Illustrator", "Illustrator" },
            { "SnippingTool", L.T("剪取工具", "Snipping Tool") }, { "ScreenClippingHost", L.T("剪取工具", "Snipping Tool") }, { "WindowsTerminal", L.T("終端機", "Terminal") },
            { "powershell", "PowerShell" }, { "cmd", L.T("命令提示字元", "Command Prompt") }, { "Telegram", "Telegram" }, { "Notion", "Notion" },
            { "FileFollow", L.T("跟檔夾", "FileFollow") }
        };

        // ───────────── Layout ─────────────
        // tab bar + folder header + filter + (file list | clipboard list) + status bar
        TableLayoutPanel BuildLayout(Control header, Control searchWrap, Control sortLine, Control folderList, Control footer)
        {
            folderHeader = header;
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Back };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(52)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(38)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(24)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(28)));

            tabBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Back };
            tabs = new SegTabs { Font = fontUi, BoldFont = fontBold, Items = new[] { L.T("資料夾", "Folder"), L.T("剪貼簿", "Clipboard") } };
            tabs.Size = tabs.Preferred(P(28));
            tabs.Location = new Point(P(10), P(8));
            tips.SetToolTip(tabs, L.T("剪貼簿：複製／剪下過的內容（", "Clipboard: everything you copied or cut (") + ClipHotkeyText + L.T("）", ")"));
            var btnMin = new IconButton("\uE921", fontGlyph, P(28), L.T("收起（Ctrl+Alt+F）", "Collapse (Ctrl+Alt+F)"), tips);
            btnMin.Top = P(8);
            btnMin.Click += delegate { SetCollapsed(true); };
            tabs.Picked += i => SwitchTab(i);
            tabBar.Controls.AddRange(new Control[] { tabs, btnMin });
            tabBar.Resize += delegate { btnMin.Left = tabBar.Width - btnMin.Width - P(8); };
            tabBar.MouseDown += DragWindow;

            chipBar = new ChipBar { Dock = DockStyle.Fill, Font = fontSmall, Padding = new Padding(P(10), 0, 0, 0), Visible = false,
                Items = new[] { L.T("全部", "All"), L.T("文字", "Text"), L.T("檔案", "Files"), L.T("圖片", "Images"), L.T("網址", "Links") } };
            chipBar.Picked += i => { clipFilter = i; UpdateClips(); };
            subRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Back };
            subRow.Controls.Add(sortLine);
            subRow.Controls.Add(chipBar);

            BuildClipList();
            var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Back };
            // both lists sit in hosts that swap the native scroll bar for a thin one
            var folderHost = new ListHost((BufferedListView)folderList, P(12)) { Dock = DockStyle.Fill };
            var clipHost = new ListHost(clipList, P(12)) { Dock = DockStyle.Fill, Visible = false };
            body.Controls.Add(folderHost);
            body.Controls.Add(clipHost);

            layout.Controls.Add(tabBar, 0, 0);
            layout.Controls.Add(header, 0, 1);
            layout.Controls.Add(searchWrap, 0, 2);
            layout.Controls.Add(subRow, 0, 3);
            layout.Controls.Add(body, 0, 4);
            layout.Controls.Add(footer, 0, 5);
            return layout;
        }

        string ClipHotkeyText { get { return "Ctrl+Alt+C"; } }

        void SwitchTab(int t)
        {
            if (clipList == null) return;
            tab = t;
            tabs.Selected = t;
            layout.SuspendLayout();
            layout.RowStyles[1].Height = t == 0 ? P(52) : 0;
            layout.RowStyles[3].Height = t == 0 ? P(24) : P(34);
            sortBar.Visible = t == 0;
            chipBar.Visible = t == 1;
            folderHeader.Visible = t == 0;
            list.Parent.Visible = t == 0;
            clipList.Parent.Visible = t == 1;
            layout.ResumeLayout();
            if (search.Text.Length > 0) { suppressSearch = true; search.Text = ""; suppressSearch = false; }
            Native.SendMessage(search.Handle, 0x1501, (IntPtr)1, t == 0 ? L.T("篩選檔名", "Filter by name") : L.T("搜尋剪貼簿紀錄", "Search clipboard history"));
            if (t == 0)
            {
                UpdateList(false);
                statusLabel.Text = folderStatus;
                countLabel.Text = "";
            }
            else
            {
                UpdateClips();
                statusLabel.Text = clipPaused ? L.T("已暫停記錄（系統匣選單可恢復）", "Recording paused (resume from the tray menu)") : L.T("按兩下放回剪貼簿 · 可直接拖出去", "Double-click to copy back · drag out to use");
            }
            cfg["tab"] = t.ToString();
            UpdateDot();
            if (pillWin != null) ShowPill();
        }

        void RefreshSearch()
        {
            if (suppressSearch) return;
            if (tab == 0) UpdateList(false); else UpdateClips();
        }

        void AddClipTrayItems(ContextMenuStrip menu)
        {
            menu.Items.Add(L.T("剪貼簿紀錄（", "Clipboard history (") + ClipHotkeyText + L.T("）", ")"), null, delegate { OpenClipTab(); });
            miClipPause = new ToolStripMenuItem(L.T("暫停記錄剪貼簿", "Pause clipboard recording"));
            miClipPause.Click += delegate
            {
                clipPaused = !clipPaused;
                miClipPause.Checked = clipPaused;
                cfg["clippause"] = clipPaused ? "1" : "0";
                SaveSettings();
                if (tab == 1) statusLabel.Text = clipPaused ? L.T("已暫停記錄（系統匣選單可恢復）", "Recording paused (resume from the tray menu)") : L.T("按兩下放回剪貼簿 · 可直接拖出去", "Double-click to copy back · drag out to use");
            };
            menu.Items.Add(miClipPause);
            menu.Items.Add(L.T("清除剪貼簿紀錄（保留釘選）", "Clear clipboard history (keep pinned)"), null, delegate { ClearClips(); });
            menu.Items.Add(new ToolStripSeparator());
        }

        // ───────────── List ─────────────
        void BuildClipList()
        {
            clipList = new BufferedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true,
                OwnerDraw = true, Margin = Padding.Empty, Font = fontUi
            };
            clipList.SmallImageList = new ImageList { ImageSize = new Size(1, P(52)) }; // sets the row height
            clipList.Columns.Add(L.T("內容", "Content"), P(300));
            clipList.Resize += delegate { if (clipList.Columns.Count > 0) clipList.Columns[0].Width = RowWidth(clipList); };
            // Rows are drawn at the first-column (sub-item) step: in Details view the themed list paints its own
            // selection background after DrawItem but before the sub-items, so anything drawn in DrawItem gets covered
            clipList.DrawItem += (o, e) => { DrawClipRow(e.Graphics, Rectangle.FromLTRB(0, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom), (Clip)e.Item.Tag, e.Item.Selected, e.ItemIndex == clipList.Hot); }; // same as the final look, so no in-between flash
            clipList.DrawSubItem += (o, e) => { if (e.ColumnIndex == 0) DrawClipRow(e.Graphics, Rectangle.FromLTRB(0, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom), (Clip)e.Item.Tag, e.Item.Selected, e.ItemIndex == clipList.Hot); };            clipList.MouseDoubleClick += (o, e) => { if (e.Button == MouseButtons.Left) UseSelectedClip(false); };
            clipList.ItemDrag += delegate { DragClip(); };
            clipList.KeyDown += ClipKeyDown;
            Theme.Native(clipList);

            clipMenu = new ContextMenuStrip { Font = fontUi };
            Theme.Menu(clipMenu);
            clipMenu.Opening += (o, e) => BuildClipMenu(e);
            clipList.ContextMenuStrip = clipMenu;

            clipTimer = new System.Windows.Forms.Timer { Interval = 120 };
            clipTimer.Tick += delegate
            {
                clipTimer.Stop();
                if (!CaptureClipboard() && ++clipRetry < 6) clipTimer.Start();
            };
            clipSaveTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            clipSaveTimer.Tick += delegate { clipSaveTimer.Stop(); SaveClipsNow(); };
        }

        List<Clip> SelectedClips()
        {
            return clipList.SelectedItems.Cast<ListViewItem>().Select(i => (Clip)i.Tag).ToList();
        }

        void UpdateClips()
        {
            if (clipList == null) return;
            string q = search.Text.Trim();
            IEnumerable<Clip> items = clips;
            if (q.Length > 0 && tab == 1)
                items = items.Where(c =>
                    (c.Text != null && c.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (c.Files != null && c.Files.Any(f => Path.GetFileName(f).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)) ||
                    (c.Source != null && c.Source.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    KindName(c).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (clipFilter > 0) items = items.Where(c => ClipGroup(c) == clipFilter);
            var arr = items.OrderByDescending(c => c.Pinned).ToList();
            var sel = new HashSet<string>(SelectedClips().Select(c => c.Id));

            clipList.BeginUpdate();
            clipList.Items.Clear();
            foreach (var c in arr)
            {
                var it = new ListViewItem("") { Tag = c, ToolTipText = ClipTip(c) };
                if (sel.Contains(c.Id)) it.Selected = true;
                clipList.Items.Add(it);
            }
            clipList.EndUpdate();
            if (clipList.Columns.Count > 0) clipList.Columns[0].Width = RowWidth(clipList);
            if (tab == 1) countLabel.Text = arr.Count + (q.Length > 0 ? L.T("／", "/") + clips.Count : "") + L.T(" 筆", " items");
        }

        // Rows span the visible part of the list, which is narrower than the list itself (its scroll bar is clipped)
        static int RowWidth(ListView lv)
        {
            var host = lv.Parent as ListHost;
            return host != null ? host.RowWidth : lv.ClientSize.Width;
        }

        // Filter chips: 1 text, 2 files and paths, 3 images, 4 links and email
        static int ClipGroup(Clip c)
        {
            switch (c.Kind)
            {
                case "files": case "path": return 2;
                case "image": return 3;
                case "url": case "email": return 4;
                default: return 1;
            }
        }

        static string OneLine(string s, int max)
        {
            if (s == null) return "";
            if (s.Length > max * 4) s = s.Substring(0, max * 4);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static string KindName(Clip c)
        {
            switch (c.Kind)
            {
                case "url": return L.T("網址", "Link");
                case "email": return "Email";
                case "color": return L.T("色碼", "Color");
                case "path": return L.T("路徑", "Path");
                case "files": return c.IsDir ? L.T("資料夾", "Folder") : L.T("檔案", "File");
                case "image": return L.T("圖片", "Image");
                default: return L.T("文字", "Text");
            }
        }

        string ClipTitle(Clip c)
        {
            switch (c.Kind)
            {
                case "files":
                    if (c.Files == null || c.Files.Length == 0) return L.T("（空）", "(empty)");
                    return Path.GetFileName(c.Files[0].TrimEnd('\\')) + (c.Files.Length > 1 ? L.T("　等 ", "  · ") + c.Files.Length + L.T(" 個", " files") : "");
                case "image": return L.T("圖片 ", "Image ") + c.W + "×" + c.H;
                default: return OneLine(c.Text, 300);
            }
        }

        string ClipMeta(Clip c)
        {
            var parts = new List<string>();
            if (c.Cut) parts.Add(L.T("剪下", "Cut"));
            switch (c.Kind)
            {
                case "files":
                    if (c.Files != null && c.Files.Length > 0)
                    {
                        string dir = Path.GetDirectoryName(c.Files[0].TrimEnd('\\'));
                        string nm = dir == null ? null : Path.GetFileName(dir.TrimEnd('\\'));
                        parts.Add(dir == null ? KindName(c) : string.IsNullOrEmpty(nm) ? dir : nm);
                    }
                    break;
                case "url":
                    try { parts.Add(new Uri(c.Text.Trim().StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + c.Text.Trim() : c.Text.Trim()).Host); }
                    catch { parts.Add(L.T("網址", "Link")); }
                    break;
                case "text":
                    int lines = c.Text.Split('\n').Length;
                    parts.Add(lines > 1 ? lines + L.T(" 行", " lines") : c.Text.Trim().Length + L.T(" 字", " chars"));
                    if (c.Html != null || c.Rtf != null) parts.Add(L.T("含格式", "formatted"));
                    break;
                default:
                    parts.Add(KindName(c));
                    break;
            }
            if (!string.IsNullOrEmpty(c.Source)) parts.Add(c.Source);
            parts.Add(FormatTime(new DateTime(c.Time), DateTime.Now));
            return string.Join(" · ", parts);
        }

        string ClipTip(Clip c)
        {
            string body;
            if (c.Kind == "files") body = string.Join("\n", (c.Files ?? new string[0]).Take(12)) + (c.Files != null && c.Files.Length > 12 ? "\n…" : "");
            else if (c.Kind == "image") body = L.T("圖片 ", "Image ") + c.W + "×" + c.H;
            else body = c.Text.Length > 600 ? c.Text.Substring(0, 600) + "…" : c.Text;
            return body + "\n\n" + new DateTime(c.Time).ToString("yyyy/M/d HH:mm:ss") + (c.Source != null ? L.T("　", "  ") + c.Source : "");
        }

        void DrawClipRow(Graphics g, Rectangle b, Clip c, bool sel, bool hot)
        {
            if (c == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Back)) g.FillRectangle(bg, b);
            Gfx.Fill(g, new RectangleF(b.X + P(6), b.Y + 1, b.Width - P(12), b.Height - 2), P(6), sel ? Theme.Sel : hot ? Theme.Hover : Color.Empty);
            int icon = P(34);
            var ir = new Rectangle(b.X + P(14), b.Y + (b.Height - icon) / 2, icon, icon);
            var boxColor = Theme.Field;

            if (c.Kind == "image")
            {
                var th = ClipThumb(c);
                if (th != null)
                {
                    float k = Math.Min((float)icon / th.Width, (float)icon / th.Height);
                    int w = Math.Max(1, (int)(th.Width * k)), h = Math.Max(1, (int)(th.Height * k));
                    Gfx.Fill(g, ir, P(7), boxColor);
                    g.DrawImage(th, ir.X + (icon - w) / 2, ir.Y + (icon - h) / 2, w, h);
                }
                else DrawGlyphBox(g, ir, "", Theme.Muted);
            }
            else if (c.Kind == "color")
            {
                Color col;
                if (TryColor(c.Text, out col))
                {
                    var sw = Rectangle.Inflate(ir, -P(4), -P(4));
                    using (var cb = new SolidBrush(col)) g.FillEllipse(cb, sw);
                    using (var pen = new Pen(Theme.Ring)) g.DrawEllipse(pen, sw);
                }
            }
            else if (c.Kind == "files" && c.Files != null && c.Files.Length == 1)
            {
                var bi = BigIcon(c.Files[0], c.IsDir);
                if (bi != null) g.DrawImage(bi, ir.X + (icon - bi.Width) / 2, ir.Y + (icon - bi.Height) / 2, bi.Width, bi.Height);
            }
            else
            {
                string glyph = c.Kind == "url" ? "" : c.Kind == "email" ? "" : c.Kind == "path" ? ""
                    : c.Kind == "files" ? "" : "";
                DrawGlyphBox(g, ir, glyph, c.Kind == "text" ? Theme.Muted : Accent);
            }

            int tx = ir.Right + P(10);
            int tw = b.Right - tx - P(10) - (c.Pinned ? P(18) : 0);
            int half = b.Height / 2;
            TextRenderer.DrawText(g, ClipTitle(c), c.Kind == "files" ? fontBold : fontUi, new Rectangle(tx, b.Y + P(3), tw, half - P(2)),
                c.Kind == "url" || c.Kind == "email" ? Accent : Theme.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Bottom | TextFormatFlags.Left);
            TextRenderer.DrawText(g, ClipMeta(c), fontSmall, new Rectangle(tx, b.Y + half + P(1), b.Right - tx - P(10), half - P(4)),
                c.Cut ? Theme.Cut : Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Top | TextFormatFlags.Left);
            if (c.Pinned)
                TextRenderer.DrawText(g, "", fontGlyph, new Rectangle(b.Right - P(26), b.Y + P(4), P(20), P(20)), Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawGlyphBox(Graphics g, Rectangle r, string glyph, Color fc)
        {
            Gfx.Fill(g, r, P(7), Theme.Field);
            TextRenderer.DrawText(g, glyph, fontGlyphBig, r, fc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static bool TryColor(string s, out Color c)
        {
            c = Color.Empty;
            s = (s ?? "").Trim();
            try
            {
                var m = Regex.Match(s, @"^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string h = m.Groups[1].Value;
                    if (h.Length == 3) h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
                    if (h.Length == 8) h = h.Substring(0, 6);
                    c = Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
                    return true;
                }
                m = Regex.Match(s, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    c = Color.FromArgb(Math.Min(255, int.Parse(m.Groups[1].Value)), Math.Min(255, int.Parse(m.Groups[2].Value)), Math.Min(255, int.Parse(m.Groups[3].Value)));
                    return true;
                }
            }
            catch { }
            return false;
        }

        Bitmap ClipThumb(Clip c)
        {
            Bitmap bm;
            if (clipThumbs.TryGetValue(c.Id, out bm)) return bm;
            bm = null;
            try
            {
                string p = Path.Combine(clipDir, c.Image ?? "");
                if (File.Exists(p))
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var src = Image.FromStream(fs))
                    {
                        int box = P(72);
                        float k = Math.Min(1f, Math.Min((float)box / src.Width, (float)box / src.Height));
                        bm = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
                        using (var g = Graphics.FromImage(bm))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, bm.Width, bm.Height);
                        }
                    }
            }
            catch { bm = null; }
            clipThumbs[c.Id] = bm;
            return bm;
        }

        // Icons by extension only: reading the real file while painting freezes the UI on a disconnected network drive
        Bitmap BigIcon(string path, bool dir)
        {
            string key = dir ? "<dir>" : Path.GetExtension(path).ToLowerInvariant();
            Bitmap bm;
            if (bigIcons.TryGetValue(key, out bm)) return bm;
            bm = null;
            var info = new Native.SHFILEINFO();
            const uint ICON = 0x100, LARGE = 0x0, USEATTR = 0x10;
            IntPtr r;
            if (dir) r = Native.SHGetFileInfo("folder", 0x10, ref info, (uint)Marshal.SizeOf(info), ICON | LARGE | USEATTR);
            else r = Native.SHGetFileInfo("x" + key, 0x80, ref info, (uint)Marshal.SizeOf(info), ICON | LARGE | USEATTR);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                using (var ic = Icon.FromHandle(info.hIcon)) bm = ic.ToBitmap();
                Native.DestroyIcon(info.hIcon);
            }
            bigIcons[key] = bm;
            return bm;
        }

        // ───────────── Clipboard listener ─────────────
        void InitClipboard()
        {
            clipDir = TestMode ? Path.Combine(Path.GetTempPath(), "ff-cliptest") : Path.Combine(Path.GetDirectoryName(settingsPath), "clips");
            clipJson = Path.Combine(clipDir, "history.json");
            try
            {
                if (TestMode && Directory.Exists(clipDir)) Directory.Delete(clipDir, true);
                Directory.CreateDirectory(clipDir);
            }
            catch { }
            LoadClips();
            clipPaused = Cfg("clippause") == "1";
            if (miClipPause != null) miClipPause.Checked = clipPaused;
            if (!TestMode) clipListening = ClipNative.AddClipboardFormatListener(Handle);
            Native.RegisterHotKey(Handle, 2, 0x1 | 0x2 | 0x4000, (uint)Keys.C); // Ctrl+Alt+C
        }

        void ShutdownClipboard()
        {
            if (clipListening) ClipNative.RemoveClipboardFormatListener(Handle);
            Native.UnregisterHotKey(Handle, 2);
            if (clipSaveTimer != null && clipSaveTimer.Enabled) { clipSaveTimer.Stop(); SaveClipsNow(); }
        }

        void OnClipboardChanged()
        {
            if (clipPaused || DateTime.Now < suppressClipUntil) return;
            clipRetry = 0;
            clipTimer.Stop();
            clipTimer.Start();
        }

        // Returns false when the clipboard is locked; the caller retries
        bool CaptureClipboard()
        {
            IDataObject d;
            try { d = Clipboard.GetDataObject(); }
            catch { return false; }
            return d == null || CaptureFrom(d, ClipSource());
        }

        bool CaptureFrom(IDataObject d, string src)
        {
            string[] fmts;
            try { fmts = d.GetFormats(false); }
            catch { return false; }

            // Password managers mark content that must not be recorded
            if (fmts.Contains("ExcludeClipboardContentFromMonitorProcessing") || fmts.Contains("Clipboard Viewer Ignore")) return true;
            if (fmts.Contains("CanIncludeInClipboardHistory") && ReadInt(d, "CanIncludeInClipboardHistory") == 0) return true;

            Clip c = null;
            try
            {
                if (d.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = d.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > MaxFiles) return true; // huge file selections are not recorded
                    if (files != null && files.Length > 0)
                    {
                        int effect = ReadInt(d, "Preferred DropEffect");
                        bool cut = effect > 0 && (effect & 2) != 0;
                        bool isDir = false;
                        if (files.Length == 1 && !files[0].StartsWith("\\\\"))
                            try { isDir = Directory.Exists(files[0]); } catch { }
                        c = new Clip { Kind = "files", Files = files, Cut = cut, IsDir = isDir, Sig = "f|" + (cut ? "x|" : "") + string.Join("|", files).ToLowerInvariant() };
                    }
                }
                if (c == null && d.GetDataPresent(DataFormats.UnicodeText))
                {
                    string text = d.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        c = MakeTextClip(text);
                        if (c.Kind == "text")
                        {
                            try { var h = d.GetData(DataFormats.Html) as string; if (h != null && h.Length < 3000000) c.HtmlData = h; } catch { }
                            try { var r = d.GetData(DataFormats.Rtf) as string; if (r != null && r.Length < 3000000) c.RtfData = r; } catch { }
                        }
                    }
                }
                if (c == null && (fmts.Contains("PNG") || d.GetDataPresent(DataFormats.Bitmap) || d.GetDataPresent(DataFormats.Dib)))
                    c = MakeImageClip(d);
            }
            catch { return false; }
            if (c == null) return true;
            c.Source = src;
            c.Time = DateTime.Now.Ticks;
            AddClip(c);
            return true;
        }

        static int ReadInt(IDataObject d, string fmt)
        {
            try
            {
                var ms = d.GetData(fmt) as MemoryStream;
                if (ms == null || ms.Length < 4) return -1;
                var b = new byte[4];
                ms.Position = 0;
                ms.Read(b, 0, 4);
                return BitConverter.ToInt32(b, 0);
            }
            catch { return -1; }
        }

        static Clip MakeTextClip(string text)
        {
            string t = text.Length > MaxText ? text.Substring(0, MaxText) : text;
            string s = t.Trim();
            string kind = "text";
            if (s.IndexOf('\n') < 0 && s.Length < 4096)
            {
                string unq = s.Trim('"');
                Color dummy;
                if (Regex.IsMatch(s, @"^(https?|ftp)://\S+$", RegexOptions.IgnoreCase) || Regex.IsMatch(s, @"^www\.[^\s.]+\.\S+$", RegexOptions.IgnoreCase)) kind = "url";
                else if (Regex.IsMatch(s, @"^[\w.+-]+@[\w-]+(\.[\w-]+)+$")) kind = "email";
                else if (TryColor(s, out dummy)) kind = "color";
                else if (Regex.IsMatch(unq, @"^([A-Za-z]:\\|\\\\[^\\\s]+\\)") && unq.IndexOfAny(Path.GetInvalidPathChars()) < 0) kind = "path";
            }
            return new Clip { Kind = kind, Text = t, Sig = "t|" + Hash(Encoding.UTF8.GetBytes(t)) };
        }

        Clip MakeImageClip(IDataObject d)
        {
            Bitmap bmp = null;
            try
            {
                var ms = d.GetData("PNG") as MemoryStream;
                if (ms != null) using (var tmp = Image.FromStream(ms, false, false))
                    if ((long)tmp.Width * tmp.Height <= MaxPixels) bmp = new Bitmap(tmp);
                    else return null;
            }
            catch { bmp = null; }
            if (bmp == null)
            {
                try
                {
                    var img = d.GetData(DataFormats.Bitmap, true) as Image;
                    if (img != null && (long)img.Width * img.Height <= MaxPixels) bmp = new Bitmap(img);
                }
                catch { }
            }
            if (bmp == null) return null;
            using (bmp)
            {
                byte[] png;
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
                return new Clip { Kind = "image", W = bmp.Width, H = bmp.Height, PngData = png, Sig = "i|" + Hash(png) };
            }
        }

        static string Hash(byte[] b)
        {
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(b)).Replace("-", "");
        }

        string ClipSource()
        {
            try
            {
                IntPtr h = ClipNative.GetClipboardOwner();
                if (h == IntPtr.Zero) h = Native.GetForegroundWindow();
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return null;
                using (var p = Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName, nice;
                    if (KnownApps.TryGetValue(n, out nice)) return nice;
                    if (appNames.TryGetValue(n, out nice)) return nice;
                    nice = n;
                    try
                    {
                        string desc = p.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 24) nice = desc.Trim();
                    }
                    catch { }
                    appNames[n] = nice;
                    return nice;
                }
            }
            catch { return null; }
        }

        void AddClip(Clip c)
        {
            var ex = clips.FirstOrDefault(x => x.Sig == c.Sig);
            if (ex != null)
            {
                ex.Time = c.Time;
                if (c.Source != null) ex.Source = c.Source;
                clips.Remove(ex);
                clips.Insert(0, ex);
            }
            else
            {
                c.Id = NewId();
                try
                {
                    if (c.PngData != null)
                    {
                        c.Image = UniqueName(L.T("剪貼圖_", "Clip_") + DateTime.Now.ToString("yyyyMMdd-HHmmss"), ".png");
                        File.WriteAllBytes(Path.Combine(clipDir, c.Image), c.PngData);
                    }
                    if (c.HtmlData != null) { c.Html = c.Id + ".html"; File.WriteAllText(Path.Combine(clipDir, c.Html), c.HtmlData, new UTF8Encoding(false)); }
                    if (c.RtfData != null) { c.Rtf = c.Id + ".rtf"; File.WriteAllText(Path.Combine(clipDir, c.Rtf), c.RtfData, new UTF8Encoding(false)); }
                }
                catch { }
                c.PngData = null; c.HtmlData = null; c.RtfData = null;
                clips.Insert(0, c);
                TrimClips();
            }
            ScheduleClipSave();
            if (tab == 1) UpdateClips();
        }

        static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 12); }

        string UniqueName(string stem, string ext)
        {
            string n = stem + ext;
            for (int i = 2; File.Exists(Path.Combine(clipDir, n)); i++) n = stem + "-" + i + ext;
            return n;
        }

        void TrimClips()
        {
            int keep = 0;
            foreach (var c in clips.ToList())
            {
                if (c.Pinned) continue;
                if (++keep > MaxClips) RemoveClip(c);
            }
        }

        void RemoveClip(Clip c)
        {
            clips.Remove(c);
            foreach (string f in new[] { c.Image, c.Html, c.Rtf })
                if (f != null) try { File.Delete(Path.Combine(clipDir, f)); } catch { }
            Bitmap bm;
            if (clipThumbs.TryGetValue(c.Id, out bm)) { if (bm != null) bm.Dispose(); clipThumbs.Remove(c.Id); }
        }

        void ClearClips()
        {
            int n = clips.Count(c => !c.Pinned);
            if (n == 0) return;
            if (MessageBox.Show(L.T("清除 ", "Clear ") + n + L.T(" 筆剪貼簿紀錄？（釘選的會保留）", " clipboard items? Pinned items are kept."), L.T("跟檔夾", "FileFollow"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (var c in clips.Where(x => !x.Pinned).ToList()) RemoveClip(c);
            ScheduleClipSave();
            UpdateClips();
        }

        // ───────────── Copy back, paste, drag out ─────────────
        bool PutBack(Clip c, bool plain)
        {
            var d = new DataObject();
            if (c.Kind == "files")
            {
                var exist = (c.Files ?? new string[0]).Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
                if (exist.Length == 0) { Flash(L.T("原檔案已不存在", "The original file no longer exists")); return false; }
                var sc = new StringCollection();
                sc.AddRange(exist);
                d.SetFileDropList(sc); // always copied back as Copy so files are never moved by accident
            }
            else if (c.Kind == "image")
            {
                string p = Path.Combine(clipDir, c.Image ?? "");
                if (!File.Exists(p)) { Flash(L.T("圖片檔已不存在", "The image file no longer exists")); return false; }
                byte[] png = File.ReadAllBytes(p);
                using (var ms = new MemoryStream(png))
                using (var tmp = Image.FromStream(ms))
                    d.SetImage(new Bitmap(tmp));
                d.SetData("PNG", new MemoryStream(png));
            }
            else
            {
                d.SetText(c.Text, TextDataFormat.UnicodeText);
                if (!plain)
                {
                    try { if (c.Html != null) d.SetText(File.ReadAllText(Path.Combine(clipDir, c.Html), Encoding.UTF8), TextDataFormat.Html); } catch { }
                    try { if (c.Rtf != null) d.SetText(File.ReadAllText(Path.Combine(clipDir, c.Rtf), Encoding.UTF8), TextDataFormat.Rtf); } catch { }
                }
            }
            suppressClipUntil = DateTime.Now.AddMilliseconds(700);
            bool ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                try { Clipboard.SetDataObject(d, true, 3, 80); ok = true; } catch { System.Threading.Thread.Sleep(60); }
            }
            if (!ok) { Flash(L.T("剪貼簿被其他程式占用，請再試一次", "Another app is using the clipboard. Try again.")); return false; }
            c.Time = DateTime.Now.Ticks;
            clips.Remove(c);
            clips.Insert(0, c);
            ScheduleClipSave();
            UpdateClips();
            return true;
        }

        void UseSelectedClip(bool plain)
        {
            var sel = SelectedClips();
            if (sel.Count == 0) return;
            Clip c = sel[0];
            if (sel.Count > 1 && sel.All(x => x.Kind != "files" && x.Kind != "image"))
                c = new Clip { Kind = "text", Text = string.Join("\r\n", sel.Select(x => x.Text)), Id = "", Sig = "" };
            bool merged = c.Id == "";
            if (merged)
            {
                var d = new DataObject();
                d.SetText(c.Text, TextDataFormat.UnicodeText);
                suppressClipUntil = DateTime.Now.AddMilliseconds(100); // the merged text becomes a new entry
                try { Clipboard.SetDataObject(d, true, 3, 80); } catch { Flash(L.T("剪貼簿被占用", "The clipboard is busy")); return; }
            }
            else if (!PutBack(c, plain)) return;

            IntPtr t = pasteTarget;
            pasteTarget = IntPtr.Zero;
            if (t != IntPtr.Zero && ClipNative.IsWindow(t))
            {
                if (autoHide) SetCollapsed(true);
                ClipNative.SetForegroundWindow(t);
                var tm = new System.Windows.Forms.Timer { Interval = 150 };
                tm.Tick += delegate { tm.Stop(); tm.Dispose(); try { SendKeys.SendWait("^v"); } catch { } };
                tm.Start();
            }
            else Flash((merged ? L.T("已合併 ", "Merged ") + sel.Count + L.T(" 筆", " items") : plain ? L.T("已放回純文字", "Copied back as plain text") : L.T("已放回剪貼簿", "Copied back to the clipboard")) + L.T("，Ctrl+V 貼上", ". Press Ctrl+V to paste"));
        }

        void DragClip()
        {
            var sel = SelectedClips();
            if (sel.Count == 0) return;
            var d = new DataObject();
            var paths = new List<string>();
            foreach (var c in sel)
            {
                if (c.Kind == "files" && c.Files != null) paths.AddRange(c.Files.Where(f => File.Exists(f) || Directory.Exists(f)));
                else if (c.Kind == "image" && c.Image != null && File.Exists(Path.Combine(clipDir, c.Image))) paths.Add(Path.Combine(clipDir, c.Image));
            }
            if (paths.Count > 0) d.SetData(DataFormats.FileDrop, paths.ToArray());
            var texts = sel.Where(c => c.Text != null).Select(c => c.Text).ToList();
            if (texts.Count > 0) d.SetText(string.Join("\r\n", texts), TextDataFormat.UnicodeText);
            if (paths.Count == 0 && texts.Count == 0) return;
            clipList.DoDragDrop(d, DragDropEffects.Copy | DragDropEffects.Link);
        }

        void OpenClipTab()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            if (pid == ownPid && tab == 1 && !collapsed && Visible) { pasteTarget = IntPtr.Zero; SetCollapsed(true); return; }
            pasteTarget = pid != ownPid ? fg : IntPtr.Zero;
            if (collapsed) SetCollapsed(false);
            else if (!Visible) Show();
            if (tab != 1) SwitchTab(1);
            Activate();
            clipList.Focus();
            if (clipList.Items.Count > 0)
            {
                clipList.SelectedItems.Clear();
                clipList.Items[0].Selected = true;
                clipList.Items[0].Focused = true;
                clipList.Items[0].EnsureVisible();
            }
            if (pasteTarget != IntPtr.Zero) statusLabel.Text = L.T("Enter 貼到剛才的視窗 · Esc 取消", "Enter pastes into the previous window · Esc cancels");
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (pasteTarget != IntPtr.Zero)
            {
                pasteTarget = IntPtr.Zero;
                if (tab == 1) statusLabel.Text = L.T("按兩下放回剪貼簿 · 可直接拖出去", "Double-click to copy back · drag out to use");
            }
        }

        void ClipKeyDown(object o, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { UseSelectedClip(e.Shift); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelectedClips(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { var s = SelectedClips(); if (s.Count > 0 && PutBack(s[0], false)) Flash(L.T("已放回剪貼簿", "Copied back to the clipboard")); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem it in clipList.Items) it.Selected = true; e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.P) { TogglePinClips(); e.Handled = true; }
        }

        void DeleteSelectedClips()
        {
            var sel = SelectedClips();
            if (sel.Count == 0) return;
            int idx = clipList.SelectedIndices.Count > 0 ? clipList.SelectedIndices[0] : 0;
            foreach (var c in sel) RemoveClip(c);
            ScheduleClipSave();
            UpdateClips();
            if (clipList.Items.Count > 0)
            {
                var it = clipList.Items[Math.Min(idx, clipList.Items.Count - 1)];
                it.Selected = true; it.Focused = true;
            }
        }

        void TogglePinClips()
        {
            var sel = SelectedClips();
            if (sel.Count == 0) return;
            bool pin = !sel.All(c => c.Pinned);
            foreach (var c in sel) c.Pinned = pin;
            ScheduleClipSave();
            UpdateClips();
        }

        void BuildClipMenu(System.ComponentModel.CancelEventArgs e)
        {
            clipMenu.Items.Clear();
            var sel = SelectedClips();
            if (sel.Count == 0) { e.Cancel = true; return; }
            Clip c = sel[0];
            clipMenu.Items.Add(sel.Count > 1 && sel.All(x => x.Kind != "files" && x.Kind != "image") ? L.T("合併 ", "Merge ") + sel.Count + L.T(" 筆放回剪貼簿", " items to the clipboard") : L.T("放回剪貼簿（Enter）", "Copy back to clipboard (Enter)"),
                null, delegate { UseSelectedClip(false); }).Font = fontBold;
            if (sel.Count == 1 && (c.Html != null || c.Rtf != null))
                clipMenu.Items.Add(L.T("以純文字放回（Shift+Enter）", "Copy back as plain text (Shift+Enter)"), null, delegate { UseSelectedClip(true); });
            if (sel.Count == 1)
            {
                if (c.Kind == "url")
                    clipMenu.Items.Add(L.T("開啟網址", "Open link"), null, delegate
                    {
                        string u = c.Text.Trim();
                        Run(u.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + u : u, null);
                    });
                if (c.Kind == "email") clipMenu.Items.Add(L.T("寫信給這個地址", "Email this address"), null, delegate { Run("mailto:" + c.Text.Trim(), null); });
                string target = c.Kind == "path" ? c.Text.Trim().Trim('"') : c.Kind == "image" ? Path.Combine(clipDir, c.Image ?? "") :
                    c.Kind == "files" && c.Files != null && c.Files.Length > 0 ? c.Files[0] : null;
                if (target != null && (File.Exists(target) || Directory.Exists(target)))
                {
                    clipMenu.Items.Add(L.T("開啟", "Open"), null, delegate
                    {
                        if (Directory.Exists(target)) { Run("explorer.exe", "\"" + target + "\""); return; }
                        // A path on the clipboard may come from a web page or a message; ask before running executables
                        if (RiskyExt.Contains(Path.GetExtension(target)) &&
                            MessageBox.Show(L.T("這是可以執行的檔案：\n", "This file can run programs:\n") + target + L.T("\n\n確定要執行嗎？", "\n\nRun it anyway?"), L.T("跟檔夾", "FileFollow"),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                        Run(target, null);
                    });
                    clipMenu.Items.Add(L.T("在檔案總管中顯示", "Show in File Explorer"), null, delegate { OpenInExplorer(target); });
                    string dir = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
                    if (c.Kind != "image")
                        clipMenu.Items.Add(L.T("在「資料夾」分頁開啟", "Open in the Folder tab"), null, delegate { SwitchTab(0); Navigate(dir); });
                }
            }
            clipMenu.Items.Add(new ToolStripSeparator());
            clipMenu.Items.Add(sel.All(x => x.Pinned) ? L.T("取消釘選（Ctrl+P）", "Unpin (Ctrl+P)") : L.T("釘選，不會被自動清掉（Ctrl+P）", "Pin so it is never cleared (Ctrl+P)"), null, delegate { TogglePinClips(); });
            clipMenu.Items.Add(L.T("刪除（Delete）", "Delete (Delete)"), null, delegate { DeleteSelectedClips(); });
            clipMenu.Items.Add(new ToolStripSeparator());
            clipMenu.Items.Add(L.T("清除全部（保留釘選）", "Clear all (keep pinned)"), null, delegate { ClearClips(); });
        }

        // Test hook: feeds sample clipboard contents and returns how they were parsed
        public string TestFeedClips(string sampleDir)
        {
            Func<string, string, DataObject> rich = (plain, html) =>
            {
                var o = new DataObject();
                o.SetText(plain, TextDataFormat.UnicodeText);
                o.SetText("Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000000\r\nStartFragment:00000000\r\nEndFragment:00000000\r\n<html><body><!--StartFragment-->" + html + "<!--EndFragment--></body></html>", TextDataFormat.Html);
                return o;
            };
            var bmp = new Bitmap(320, 200);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(43, 108, 176)); g.FillEllipse(Brushes.Gold, 110, 50, 100, 100); }
            var cut = new DataObject();
            cut.SetData(DataFormats.FileDrop, new[] { Path.Combine(sampleDir, L.T("會議記錄.docx", "Meeting notes.docx")), Path.Combine(sampleDir, "Banner_1200x628.png") });
            cut.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 2, 0, 0, 0 }));
            var secret = new DataObject();
            secret.SetText("SECRET-should-not-appear");
            secret.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
            var one = new DataObject();
            one.SetData(DataFormats.FileDrop, new[] { Path.Combine(sampleDir, L.T("報價單_1029.xlsx", "Quote_1029.xlsx")) });

            var feed = new List<KeyValuePair<object, string>> {
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, L.T("第一段複製的文字\r\n第二行說明\r\n第三行", "First copied paragraph\r\nSecond line\r\nThird line")), L.T("記事本", "Notepad")),
                new KeyValuePair<object, string>(one, L.T("檔案總管", "File Explorer")),
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, "https://www.example.com/brand/2026/proposal?id=42"), "Chrome"),
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, "#2B6CB0"), "Illustrator"),
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, "client@example.com"), "Outlook"),
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, L.T("C:\\Users\\Public\\Documents\\客戶\\品牌", "C:\\Users\\Public\\Documents\\Clients\\Brand")), L.T("檔案總管", "File Explorer")),
                new KeyValuePair<object, string>(rich(L.T("粗體標題 與一般文字", "Bold heading and plain text"), L.T("<b>粗體標題</b> 與一般文字", "<b>Bold heading</b> and plain text")), "Word"),
                new KeyValuePair<object, string>(cut, L.T("檔案總管", "File Explorer")),
                new KeyValuePair<object, string>(new DataObject(DataFormats.Bitmap, bmp), L.T("剪取工具", "Snipping Tool")),
                new KeyValuePair<object, string>(secret, "1Password"),
                new KeyValuePair<object, string>(new DataObject(DataFormats.UnicodeText, L.T("第一段複製的文字\r\n第二行說明\r\n第三行", "First copied paragraph\r\nSecond line\r\nThird line")), L.T("記事本", "Notepad")),
            };
            foreach (var kv in feed) CaptureFrom((IDataObject)kv.Key, kv.Value);
            clips[clips.Count - 1].Pinned = true;
            UpdateClips();
            return string.Join("\r\n", clips.Select(c => c.Kind + "\tcut=" + c.Cut + "\t" + c.Source + "\t" + ClipTitle(c) +
                (c.Html != null ? "\t[html " + new FileInfo(Path.Combine(clipDir, c.Html)).Length + "B]" : "") +
                (c.Image != null ? "\t[" + c.Image + " " + new FileInfo(Path.Combine(clipDir, c.Image)).Length + "B]" : "")));
        }

        // ───────────── Persistence ─────────────
        void LoadClips()
        {
            try
            {
                if (!File.Exists(clipJson)) return;
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var l = ser.Deserialize<List<Clip>>(File.ReadAllText(clipJson, Encoding.UTF8));
                if (l != null) clips = l.Where(c => c != null && c.Id != null && c.Kind != null).ToList();
            }
            catch { }
        }

        void ScheduleClipSave()
        {
            clipSaveTimer.Stop();
            clipSaveTimer.Start();
        }

        void SaveClipsNow()
        {
            if (TestMode || clipJson == null) return;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string tmp = clipJson + ".tmp";
                File.WriteAllText(tmp, ser.Serialize(clips), new UTF8Encoding(false));
                if (File.Exists(clipJson)) File.Replace(tmp, clipJson, null);
                else File.Move(tmp, clipJson);
            }
            catch { }
        }
    }
}
