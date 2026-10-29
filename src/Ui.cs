// Custom-drawn controls for the panel: segmented tabs, icon buttons, filter field, chips, and the layered pill
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileFollow
{
    static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            if (c.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = sm;
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            if (c.IsEmpty) return;
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            r.Inflate(-w / 2, -w / 2);
            using (var p = Round(r, rad - w / 2)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
            g.SmoothingMode = sm;
        }

        // The folder outline from the design (a 20-unit box), scaled into box
        public static GraphicsPath Folder(RectangleF box)
        {
            float k = box.Width / 20f, x = box.X, y = box.Y;
            Func<float, float> X = v => x + v * k, Y = v => y + v * k;
            float d = 2 * k;
            var p = new GraphicsPath();
            p.AddArc(X(2.5f), Y(4.5f), d, d, 180, 90);
            p.AddLine(X(3.5f), Y(4.5f), X(7.5f), Y(4.5f));
            p.AddLine(X(7.5f), Y(4.5f), X(9.5f), Y(6.5f));
            p.AddLine(X(9.5f), Y(6.5f), X(16.5f), Y(6.5f));
            p.AddArc(X(15.5f), Y(6.5f), d, d, 270, 90);
            p.AddLine(X(17.5f), Y(7.5f), X(17.5f), Y(15.5f));
            p.AddArc(X(15.5f), Y(14.5f), d, d, 0, 90);
            p.AddLine(X(16.5f), Y(16.5f), X(3.5f), Y(16.5f));
            p.AddArc(X(2.5f), Y(14.5f), d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void DrawFolder(Graphics g, RectangleF box, Color c) { DrawIcon(g, box, c, false); }

        // Clipboard outline in the same 20-unit box and stroke as the folder: a board with a clip on top
        public static GraphicsPath Clipboard(RectangleF box)
        {
            float k = box.Width / 20f, x = box.X, y = box.Y;
            var p = new GraphicsPath();
            using (var board = Round(new RectangleF(x + 4f * k, y + 3.5f * k, 12f * k, 14f * k), 2f * k)) p.AddPath(board, false);
            using (var clip = Round(new RectangleF(x + 7f * k, y + 2f * k, 6f * k, 3.5f * k), 1f * k)) p.AddPath(clip, false);
            p.AddLine(x + 7.5f * k, y + 10f * k, x + 12.5f * k, y + 10f * k);
            p.StartFigure();
            p.AddLine(x + 7.5f * k, y + 13.5f * k, x + 11f * k, y + 13.5f * k);
            return p;
        }

        public static GraphicsPath IconPath(RectangleF box, bool clipboard) { return clipboard ? Clipboard(box) : Folder(box); }

        public static void DrawIcon(Graphics g, RectangleF box, Color c, bool clipboard)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = IconPath(box, clipboard))
            using (var pen = new Pen(c, Math.Max(1.2f, box.Width / 20f * 1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawPath(pen, p);
            g.SmoothingMode = sm;
        }

        // Pixel bounds of the ink a string leaves, relative to its DrawString origin (typographic format)
        static readonly Dictionary<string, Rectangle> inkCache = new Dictionary<string, Rectangle>();
        public static Rectangle Ink(string text, Font f)
        {
            string key = f.Name + "|" + f.Size + "|" + f.Style + "|" + text;
            Rectangle r;
            if (inkCache.TryGetValue(key, out r)) return r;
            int pad = (int)Math.Ceiling(f.GetHeight()) + 4;
            SizeF sz;
            using (var tmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(tmp)) sz = g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic);
            int w = (int)Math.Ceiling(sz.Width) + pad * 2, h = (int)Math.Ceiling(sz.Height) + pad * 2;
            using (var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(text, f, Brushes.Black, pad, pad, StringFormat.GenericTypographic);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new int[bmp.Width * bmp.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
                int l = int.MaxValue, t = int.MaxValue, rr = -1, b = -1;
                for (int yy = 0; yy < bmp.Height; yy++)
                    for (int xx = 0; xx < bmp.Width; xx++)
                        if ((px[yy * bmp.Width + xx] & 0xFF) < 200)
                        {
                            if (xx < l) l = xx; if (xx > rr) rr = xx;
                            if (yy < t) t = yy; if (yy > b) b = yy;
                        }
                r = rr < 0 ? new Rectangle(0, 0, 0, 0) : new Rectangle(l - pad, t - pad, rr - l + 1, b - t + 1);
            }
            if (inkCache.Count > 500) inkCache.Clear();
            inkCache[key] = r;
            return r;
        }
    }

    // Square icon button with a rounded hover background
    class IconButton : Control
    {
        bool hover;
        public IconButton(string glyph, Font f, int size, string tip, ToolTip tt)
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Text = glyph; Font = f; Size = new Size(size, size); Margin = Padding.Empty;
            ForeColor = Theme.Icon; BackColor = Theme.Back; Cursor = Cursors.Hand;
            tt.SetToolTip(this, tip);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (hover) Gfx.Fill(e.Graphics, new RectangleF(0, 0, Width, Height), Height * 0.22f, Theme.Hover);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, hover ? Theme.Hover : BackColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // Windows 11 style segmented control used for the tabs
    class SegTabs : Control
    {
        public string[] Items = new string[0];
        public Font BoldFont;
        public event Action<int> Picked;
        int selected, hover = -1;
        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public SegTabs()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        int Pad { get { return Math.Max(2, Height / 9); } }
        int SegW { get { return Items.Length == 0 ? 0 : (Width - Pad * 2 - (Items.Length - 1) * 2) / Items.Length; } }

        public Size Preferred(int height)
        {
            int w = 0;
            foreach (string t in Items) w = Math.Max(w, TextRenderer.MeasureText(t, BoldFont ?? Font).Width);
            int pad = Math.Max(2, height / 9);
            return new Size((w + height) * Items.Length + pad * 2 + (Items.Length - 1) * 2, height);
        }

        Rectangle Seg(int i) { return new Rectangle(Pad + i * (SegW + 2), Pad, SegW, Height - Pad * 2); }

        int HitTest(Point p)
        {
            for (int i = 0; i < Items.Length; i++) if (Seg(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && h >= 0 && Picked != null) Picked(h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            Gfx.Fill(g, new RectangleF(0, 0, Width, Height), Height * 0.25f, Theme.SegBack);
            for (int i = 0; i < Items.Length; i++)
            {
                var r = Seg(i);
                bool on = i == selected;
                Color bg = Theme.SegBack;
                if (on)
                {
                    if (!Theme.Dark) Gfx.Fill(g, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), r.Height * 0.25f, Color.FromArgb(28, 0, 0, 0));
                    Gfx.Fill(g, r, r.Height * 0.25f, Theme.SegActive);
                    bg = Theme.SegActive;
                }
                TextRenderer.DrawText(g, Items[i], on ? (BoldFont ?? Font) : Font, r, on || i == hover ? Theme.Text : Theme.Muted, bg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
    }

    // Rounded tile with the folder icon, left of the folder name
    class FolderTile : Control
    {
        public FolderTile()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            int sz = Math.Min(Width, Height);
            var r = new RectangleF(0, (Height - sz) / 2, sz, sz);
            Gfx.Fill(g, r, sz * 0.24f, Theme.HiBack);
            float ic = sz * 0.53f;
            Gfx.DrawFolder(g, new RectangleF(r.X + (sz - ic) / 2, r.Y + (sz - ic) / 2 - sz * 0.02f, ic, ic), Theme.Accent);
        }
    }

    // Path shown as breadcrumbs ("Documents › Clients › Brand"); drops leading parts when narrow
    class PathLabel : Control
    {
        public PathLabel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        static List<KeyValuePair<string, string>> roots;
        static List<KeyValuePair<string, string>> Roots()
        {
            if (roots != null) return roots;
            var r = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (p, n) => { if (!string.IsNullOrEmpty(p)) r.Add(new KeyValuePair<string, string>(p.TrimEnd('\\'), n)); };
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), L.T("桌面", "Desktop"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), L.T("文件", "Documents"));
            add(Path.Combine(up, "Downloads"), L.T("下載", "Downloads"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), L.T("圖片", "Pictures"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), L.T("影片", "Videos"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), L.T("音樂", "Music"));
            add(Environment.GetEnvironmentVariable("OneDriveCommercial"), "OneDrive");
            add(Environment.GetEnvironmentVariable("OneDriveConsumer"), "OneDrive");
            add(Environment.GetEnvironmentVariable("OneDrive"), "OneDrive");
            add(up, Path.GetFileName(up));
            roots = r.OrderByDescending(kv => kv.Key.Length).ToList();
            return roots;
        }

        public static List<string> Segments(string path)
        {
            var segs = new List<string>();
            if (string.IsNullOrEmpty(path)) return segs;
            string p = path.TrimEnd('\\');
            foreach (var kv in Roots())
                if (p.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) || p.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    segs.Add(kv.Value);
                    p = p.Substring(kv.Key.Length).TrimStart('\\');
                    break;
                }
            segs.AddRange(p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries));
            return segs;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            const string sep = " › ";
            var segs = Segments(Text);
            const TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            string s = string.Join(sep, segs);
            for (int drop = 1; drop < segs.Count && TextRenderer.MeasureText(e.Graphics, s, Font, Size.Empty, tf).Width > Width; drop++)
                s = "…" + sep + string.Join(sep, segs.Skip(drop));
            TextRenderer.DrawText(e.Graphics, s, Font, ClientRectangle, ForeColor, BackColor, tf | TextFormatFlags.EndEllipsis);
        }
    }

    // Rounded filter field: search icon, text box, shortcut hint
    class SearchField : Panel
    {
        public readonly TextBox Box;
        public Font GlyphFont;
        public string Hint = "Ctrl F";

        public SearchField(Font font)
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Back;
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text, Font = font };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
            Click += delegate { Box.Focus(); };
        }

        int IconW { get { return Height; } }
        int HintW { get { return string.IsNullOrEmpty(Hint) ? Height / 3 : TextRenderer.MeasureText(Hint, Font).Width + Height / 2; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Box.SetBounds(IconW, (Height - Box.Height) / 2, Math.Max(10, Width - IconW - HintW), Box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Gfx.Fill(g, r, Height * 0.2f, Theme.Field);
            if (Box.Focused) Gfx.Stroke(g, r, Height * 0.2f, Theme.Accent, Math.Max(1f, Height / 30f));
            if (GlyphFont != null)
                TextRenderer.DrawText(g, "", GlyphFont, new Rectangle(0, 0, IconW, Height), Theme.Muted, Theme.Field,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(Hint) && Box.TextLength == 0)
            {
                Size ts = TextRenderer.MeasureText(Hint, Font);
                int hh = ts.Height, x = Width - HintW + Height / 6;
                var hr = new Rectangle(x, (Height - hh) / 2, ts.Width, hh);
                Gfx.Stroke(g, hr, hh * 0.25f, Theme.Border, 1f);
                TextRenderer.DrawText(g, Hint, Font, hr, Theme.Muted, Theme.Field,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        public void RefreshHint() { Invalidate(); PerformLayout(); }
    }

    // "8 items ......... Modified ↓" line above the file list
    class SortBar : Control
    {
        public Font GlyphFont;
        public bool ByName;
        public event Action Toggle;
        string count = "";
        bool hover;
        public string Count { get { return count; } set { count = value; Invalidate(); } }

        public SortBar()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        string Label { get { return ByName ? L.T("名稱", "Name") : L.T("修改時間", "Modified"); } }
        Rectangle SortRect()
        {
            int w = TextRenderer.MeasureText(Label, Font).Width + Height;
            return new Rectangle(Width - w - Padding.Right, 0, w, Height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = SortRect().Contains(e.Location);
            if (h != hover) { hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && SortRect().Contains(e.Location) && Toggle != null) Toggle();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            const TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, count, Font, new Rectangle(Padding.Left, 0, Width / 2, Height), Theme.Muted, tf | TextFormatFlags.Left);
            var sr = SortRect();
            Color c = hover ? Theme.Accent : Theme.Icon;
            int gw = Height * 2 / 3;
            TextRenderer.DrawText(g, Label, Font, new Rectangle(sr.X, 0, sr.Width - gw, Height), c, tf | TextFormatFlags.Right);
            if (GlyphFont != null)
                TextRenderer.DrawText(g, ByName ? "" : "", GlyphFont, new Rectangle(sr.Right - gw, 0, gw, Height), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Filter chips above the clipboard list
    class ChipBar : Control
    {
        public string[] Items = new string[0];
        public event Action<int> Picked;
        int selected, hover = -1;
        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public ChipBar()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        List<Rectangle> Chips()
        {
            var list = new List<Rectangle>();
            int h = Math.Min(Height - 2, (int)(Font.GetHeight() * 1.75f)), x = Padding.Left, gap = h / 4;
            foreach (string t in Items)
            {
                int w = TextRenderer.MeasureText(t, Font).Width + h * 3 / 4;
                list.Add(new Rectangle(x, (Height - h) / 2, w, h));
                x += w + gap;
            }
            return list;
        }

        int HitTest(Point p) { var c = Chips(); for (int i = 0; i < c.Count; i++) if (c[i].Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && h >= 0) { Selected = h; if (Picked != null) Picked(h); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            var c = Chips();
            for (int i = 0; i < c.Count; i++)
            {
                bool on = i == selected;
                Color bg = on ? Theme.HiBack : i == hover ? Theme.Hover : Theme.Back;
                Gfx.Fill(g, c[i], c[i].Height / 2f, bg);
                Gfx.Stroke(g, c[i], c[i].Height / 2f, on ? Theme.Accent : Theme.Border, 1f);
                TextRenderer.DrawText(g, Items[i], Font, c[i], on ? Theme.Accent : Theme.Icon, bg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
    }

    // Hosts an owner-drawn list without its native scroll bar: the list is made wider than the host so the bar is
    // clipped away, and a thin Windows 11 style bar is drawn on top instead. (The unthemed list draws no stray
    // selection outlines, but its own scroll bar would be light even in dark mode.)
    class ListHost : Panel
    {
        public readonly BufferedListView List;
        readonly ThinScroll bar;
        readonly int barW;

        public ListHost(BufferedListView list, int barWidth)
        {
            List = list;
            barW = barWidth;
            BackColor = Theme.Back;
            Margin = Padding.Empty;
            list.Dock = DockStyle.None;
            bar = new ThinScroll(list) { Width = barW };
            Controls.Add(list);
            Controls.Add(bar);
            bar.BringToFront();
            list.Scrolled += bar.Sync;
        }

        // width the rows should use: everything left of the thin bar
        public int RowWidth { get { return Math.Max(10, Width - barW); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int sbw = SystemInformation.VerticalScrollBarWidth;
            List.SetBounds(0, 0, RowWidth + sbw, Height);
            bar.SetBounds(Width - barW, 0, barW, Height);
            bar.Sync();
        }
    }

    class ThinScroll : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SCROLLINFO { public int cbSize; public uint fMask; public int nMin, nMax, nPage, nPos, nTrackPos; }
        [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly BufferedListView list;
        bool hover, dragging;
        int dragOffset;
        SCROLLINFO si;
        readonly Timer timer = new Timer { Interval = 250 };

        public ThinScroll(BufferedListView l)
        {
            list = l;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            timer.Tick += delegate { if (Visible && list.IsHandleCreated) Sync(); };
            timer.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

        bool Read(out SCROLLINFO s)
        {
            s = new SCROLLINFO { cbSize = Marshal.SizeOf(typeof(SCROLLINFO)), fMask = 0x17 }; // SIF_ALL
            return list.IsHandleCreated && GetScrollInfo(list.Handle, 1, ref s) && s.nPage > 0 && s.nMax - s.nMin + 1 > s.nPage;
        }

        public void Sync()
        {
            SCROLLINFO s;
            bool can = Read(out s);
            if (!can) s = new SCROLLINFO();
            if (s.nPos != si.nPos || s.nPage != si.nPage || s.nMax != si.nMax || s.nMin != si.nMin) { si = s; Invalidate(); }
        }

        Rectangle Thumb()
        {
            int range = si.nMax - si.nMin + 1;
            if (si.nPage <= 0 || range <= si.nPage) return Rectangle.Empty;
            int pad = Width / 4;
            int track = Height - pad * 2;
            int th = Math.Max(Width * 3, track * si.nPage / range);
            int y = pad + (int)((long)(track - th) * (si.nPos - si.nMin) / Math.Max(1, range - si.nPage));
            return new Rectangle(0, y, Width, th);
        }

        void ScrollTo(int pos)
        {
            int range = si.nMax - si.nMin + 1;
            pos = Math.Max(si.nMin, Math.Min(pos, si.nMin + range - si.nPage));
            if (list.Items.Count == 0 || pos == si.nPos) return;
            int rowH = list.Items[0].Bounds.Height;
            SendMessage(list.Handle, 0x1014, IntPtr.Zero, (IntPtr)((pos - si.nPos) * rowH)); // LVM_SCROLL
            Sync();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Rectangle t = Thumb();
            if (t.IsEmpty) return;
            if (t.Contains(e.Location)) { dragging = true; dragOffset = e.Y - t.Y; Capture = true; }
            else ScrollTo(si.nPos + (e.Y < t.Y ? -si.nPage : si.nPage)); // page up / down
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Rectangle t = Thumb();
            int pad = Width / 4, track = Height - pad * 2 - t.Height;
            int range = si.nMax - si.nMin + 1;
            if (track <= 0) return;
            int y = Math.Max(0, Math.Min(track, e.Y - dragOffset - pad));
            ScrollTo(si.nMin + (int)Math.Round((double)y * (range - si.nPage) / track));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(si.nPos - Math.Sign(e.Delta) * 3);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            Rectangle t = Thumb();
            if (t.IsEmpty) return;
            bool wide = hover || dragging;
            float w = wide ? Width * 0.5f : Width * 0.3f;
            var r = new RectangleF((Width - w) / 2f, t.Y, w, t.Height);
            Color c = Theme.Dark ? Color.FromArgb(wide ? 150 : 110, 255, 255, 255) : Color.FromArgb(wide ? 140 : 100, 0, 0, 0);
            Gfx.Fill(g, r, w / 2, c);
        }
    }

    // Small status dot in the footer
    class Dot : Control
    {
        Color color = Color.Empty;
        public Color DotColor { get { return color; } set { if (value != color) { color = value; Invalidate(); } } }
        public Dot()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (color.IsEmpty) return;
            float d = Math.Max(5f, Height / 4.5f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(color)) e.Graphics.FillEllipse(b, 0, (Height - d) / 2f, d, d);
        }
    }

    // The collapsed pill: a layered window, so it can have true round ends, anti-aliased edges and a soft shadow.
    // Icon and name are placed by their measured ink, so the content looks centered both ways.
    class PillWindow : Form
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        readonly float s;
        readonly Font font;
        readonly int margin;
        string text = "";
        bool hover, clipIcon;
        public Rectangle Visual;     // the pill itself, in screen coordinates (the window adds a shadow margin)

        public PillWindow(float scale, Font f)
        {
            s = scale; font = f;
            margin = P(10);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
        }

        int P(float v) { return (int)Math.Round(v * s); }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        const int PadInk = 12, GapInk = 10, MaxW = 240, H = 32;

        Rectangle IconInk()
        {
            using (var p = Gfx.IconPath(new RectangleF(0, 0, P(16), P(16)), clipIcon))
            {
                var b = p.GetBounds();
                float w = P(16) / 20f * 1.6f / 2;
                return Rectangle.FromLTRB((int)Math.Floor(b.Left - w), (int)Math.Floor(b.Top - w), (int)Math.Ceiling(b.Right + w), (int)Math.Ceiling(b.Bottom + w));
            }
        }

        // Fits the name into the maximum width, adding an ellipsis when needed
        string Fit(string t, out Rectangle ink)
        {
            int avail = P(MaxW) - P(PadInk) * 2 - IconInk().Width - P(GapInk);
            ink = Gfx.Ink(t, font);
            if (ink.Width <= avail) return t;
            string cut = t;
            while (cut.Length > 1)
            {
                cut = cut.Substring(0, cut.Length - 1);
                ink = Gfx.Ink(cut.TrimEnd() + "…", font);
                if (ink.Width <= avail) return cut.TrimEnd() + "…";
            }
            return cut;
        }

        public Size Measure(string t)
        {
            Rectangle ink;
            Fit(t, out ink);
            return new Size(P(PadInk) * 2 + IconInk().Width + P(GapInk) + ink.Width, P(H));
        }

        public void ShowAt(Rectangle visual, string t, bool clipboard)
        {
            text = t ?? "";
            clipIcon = clipboard;
            Visual = visual;
            Render();
            if (!Visible) Show();
        }

        // Test hook: renders the pill for a name without showing the window
        public Bitmap Preview(string t)
        {
            text = t ?? "";
            Size sz = Measure(text);
            Visual = new Rectangle(Visual.X, Visual.Y, sz.Width, sz.Height);
            return Draw();
        }

        public void Hover(bool h) { if (h != hover) { hover = h; if (Visible) Render(); } }
        protected override void OnMouseEnter(EventArgs e) { Hover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover(false); base.OnMouseLeave(e); }
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (Visible) Visual = new Rectangle(Left + margin, Top + margin, Visual.Width, Visual.Height);
        }

        public Bitmap Draw()
        {
            int w = Visual.Width, h = Visual.Height, m = margin;
            var bmp = new Bitmap(w + m * 2, h + m * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // soft shadow: stacked translucent rounded rects, offset down a little
                int layers = m;
                for (int i = layers; i >= 1; i--)
                {
                    var r = new RectangleF(m - i * 0.8f, m - i * 0.8f + P(2), w + i * 1.6f, h + i * 1.6f);
                    Gfx.Fill(g, r, r.Height / 2, Color.FromArgb(Theme.Dark ? 9 : 5, 0, 0, 0));
                }
                var body = new RectangleF(m, m, w, h);
                Gfx.Fill(g, body, h / 2f, hover ? Theme.PillHover : Theme.PillBack);
                Gfx.Stroke(g, body, h / 2f, hover ? Theme.PillHoverBorder : Theme.Border, Math.Max(1f, s));

                // icon: ink box centered vertically, nudged up because the folder's weight sits low
                var ii = IconInk();
                float ix = m + P(PadInk) - ii.Left;
                float iy = m + (h - ii.Height) / 2f - ii.Top - 0.6f * s;
                Gfx.DrawIcon(g, new RectangleF(ix, iy, P(16), P(16)), Theme.Accent, clipIcon);

                // name: horizontal by its own ink, vertical by reference glyphs so descenders don't shift it
                Rectangle ink;
                string t = Fit(text, out ink);
                Rectangle refInk = Gfx.Ink("國H", font);
                float tx = m + P(PadInk) + ii.Width + P(GapInk) - ink.Left;
                float ty = m + (h - refInk.Height) / 2f - refInk.Top;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(Theme.Text)) g.DrawString(t, font, b, (float)Math.Round(tx), (float)Math.Round(ty), StringFormat.GenericTypographic);
            }
            return bmp;
        }

        void Render()
        {
            if (Visual.Width <= 0) return;
            using (var bmp = Draw())
            {
                var pos = new Point(Visual.X - margin, Visual.Y - margin);
                var size = bmp.Size;
                if (!IsHandleCreated) CreateHandle();
                Bounds = new Rectangle(pos, size);
                Visual = new Rectangle(pos.X + margin, pos.Y + margin, Visual.Width, Visual.Height);
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
                try
                {
                    var src = Point.Empty;
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
                }
                finally
                {
                    SelectObject(mem, old);
                    DeleteObject(hb);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
    }
}
