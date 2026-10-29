using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileFollow
{
    // Light / dark palette, following the Windows "app mode" setting
    static class Theme
    {
        public static readonly bool Dark = Detect();

        public static bool Detect()
        {
            string env = Environment.GetEnvironmentVariable("FILEFOLLOW_THEME");
            if (env == "dark") return true;
            if (env == "light") return false;
            string mode = Mode();
            if (mode == "dark") return true;
            if (mode == "light") return false;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
            catch { return false; }
        }

        // theme=light/dark in settings overrides Windows; anything else follows Windows
        public static string Mode()
        {
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileFollow", "settings.txt");
                if (File.Exists(p))
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                        if (line.StartsWith("theme=")) return line.Substring(6);
            }
            catch { }
            return "auto";
        }

        static Color C(int lr, int lg, int lb, int dr, int dg, int db) { return Dark ? Color.FromArgb(dr, dg, db) : Color.FromArgb(lr, lg, lb); }

        public static readonly Color Back = C(255, 255, 255, 32, 32, 32);
        public static readonly Color Footer = C(250, 250, 251, 28, 28, 28);
        public static readonly Color Field = C(243, 244, 246, 45, 45, 45);
        public static readonly Color Hover = C(240, 242, 245, 48, 48, 48);
        public static readonly Color Line = C(238, 240, 243, 46, 46, 46);
        public static readonly Color Border = C(218, 221, 226, 58, 58, 58);
        public static readonly Color Ring = C(210, 212, 216, 90, 90, 90);
        public static readonly Color Text = C(27, 27, 27, 237, 237, 237);
        public static readonly Color Icon = C(95, 102, 112, 181, 181, 181);
        public static readonly Color Muted = C(107, 114, 128, 163, 163, 163);
        public static readonly Color Accent = C(31, 95, 168, 138, 184, 240);
        public static readonly Color HiBack = C(232, 240, 250, 38, 58, 82);
        public static readonly Color Sel = C(214, 228, 247, 48, 76, 110);
        public static readonly Color Cut = C(180, 83, 31, 235, 145, 85);
        public static readonly Color Ok = C(46, 158, 91, 76, 194, 126);
        public static readonly Color Notice = C(232, 17, 35, 255, 72, 72); // bright red: something new needs a look (update available)
        public static readonly Color SegBack = C(240, 242, 245, 43, 43, 43);
        public static readonly Color SegActive = C(255, 255, 255, 58, 58, 58);
        public static readonly Color PillBack = C(255, 255, 255, 43, 43, 43);
        public static readonly Color PillHover = C(242, 247, 253, 51, 56, 63);
        public static readonly Color PillHoverBorder = C(185, 204, 230, 74, 93, 117);

        // Native controls: dark scrollbars, selection and column header
        public static void Native(ListView lv)
        {
            lv.BackColor = Back;
            lv.ForeColor = Text;
            lv.HandleCreated += delegate
            {
                // owner-drawn lists stay unthemed: the Explorer themes outline the selected / focused row on top of
                // the owner draw. Their scroll bar is replaced by ListHost's thin bar.
                if (!lv.OwnerDraw) FileFollow.Native.SetWindowTheme(lv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                if (Dark)
                {
                    IntPtr hdr = FileFollow.Native.SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
                    if (hdr != IntPtr.Zero) FileFollow.Native.SetWindowTheme(hdr, "DarkMode_ItemsView", null);
                    new HeaderText().AssignHandle(lv.Handle);
                }
            };
        }

        // The dark header theme keeps black text; recolor it through NM_CUSTOMDRAW
        class HeaderText : NativeWindow
        {
            [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x4E) // WM_NOTIFY
                {
                    int p = IntPtr.Size;
                    int code = Marshal.ReadInt32(m.LParam, 2 * p);
                    IntPtr from = Marshal.ReadIntPtr(m.LParam);
                    if (code == -12 && from == FileFollow.Native.SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero)) // NM_CUSTOMDRAW from the header
                    {
                        int stage = Marshal.ReadInt32(m.LParam, 3 * p);
                        if (stage == 1) { m.Result = (IntPtr)0x20; return; } // CDDS_PREPAINT → CDRF_NOTIFYITEMDRAW
                        if (stage == 0x10001) // CDDS_ITEMPREPAINT
                        {
                            SetTextColor(Marshal.ReadIntPtr(m.LParam, 4 * p), ColorTranslator.ToWin32(Muted));
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }
                }
                base.WndProc(ref m);
            }
        }

        public static void Window(Form f)
        {
            if (!Dark) return;
            try { int on = 1; FileFollow.Native.DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } // DWMWA_USE_IMMERSIVE_DARK_MODE
        }

        // Windows 11 style context menus: rounded window, roomy rows, inset rounded highlight, Fluent check marks.
        // Applies to the menu, its items, and every submenu.
        public static void Menu(ToolStrip m)
        {
            if (m.Renderer is Win11Menu) return;
            m.Renderer = Win11Menu.Instance;
            m.BackColor = MenuBack;
            m.ForeColor = MenuText;
            foreach (ToolStripItem it in m.Items) StyleItem(it);
            EdgeMargins(m);
            m.ItemAdded += (o, e) => { StyleItem(e.Item); EdgeMargins(m); };
            m.ItemRemoved += (o, e) => EdgeMargins(m);
            var dd = m as ToolStripDropDown;
            if (dd != null)
            {
                dd.DropShadowEnabled = false; // the rounded DWM window brings its own shadow
                dd.Opened += delegate { Round(dd); };
            }
        }

        static void StyleItem(ToolStripItem it)
        {
            it.ForeColor = MenuText;
            if (it is ToolStripSeparator) { it.AutoSize = false; it.Height = Px(9); return; }
            var mi = it as ToolStripMenuItem;
            if (mi == null) return;
            mi.Padding = new Padding(0, Px(5), 0, Px(5));
            Menu(mi.DropDown);
        }

        // Drop-down menus ignore their own Padding, so the space above the first row and below the last
        // comes from those items' margins
        static void EdgeMargins(ToolStrip m)
        {
            int n = m.Items.Count;
            for (int i = 0; i < n; i++)
                m.Items[i].Margin = new Padding(0, i == 0 ? Px(3) : 0, 0, i == n - 1 ? Px(3) : 0);
        }

        // Highlights and separators are laid out against the menu's width, not the item's, so both sides match
        static RectangleF RowRect(ToolStripItem it, float inset)
        {
            int w = it.Owner != null ? it.Owner.Width : it.Width;
            return new RectangleF(inset - it.Bounds.X, 0, w - inset * 2, it.Height);
        }

        static void Round(Control c)
        {
            try
            {
                int round = 2; // DWMWCP_ROUND
                FileFollow.Native.DwmSetWindowAttribute(c.Handle, 33, ref round, 4);
                if (Dark) { int on = 1; FileFollow.Native.DwmSetWindowAttribute(c.Handle, 20, ref on, 4); }
            }
            catch { }
        }

        static float scale;
        static int Px(float v)
        {
            if (scale == 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            return (int)Math.Round(v * scale);
        }

        static readonly Color MenuBack = C(249, 249, 249, 44, 44, 44);
        static readonly Color MenuHover = C(235, 235, 235, 61, 61, 61);
        static readonly Color MenuSep = C(229, 229, 229, 64, 64, 64);
        static readonly Color MenuText = C(27, 27, 27, 240, 240, 240);
        static readonly Color MenuDisabled = C(160, 160, 160, 120, 120, 120);

        class Win11Menu : ToolStripRenderer
        {
            public static readonly Win11Menu Instance = new Win11Menu();
            Font glyph;
            Font Glyph
            {
                get
                {
                    if (glyph == null)
                    {
                        string face = "Segoe Fluent Icons";
                        using (var f = new Font(face, 9f)) if (f.Name != face) face = "Segoe MDL2 Assets";
                        glyph = new Font(face, 8f);
                    }
                    return glyph;
                }
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(MenuBack); }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var it = e.Item;
                if (!it.Selected || !it.Enabled) return;
                var r = RowRect(it, Px(4));
                r.Inflate(0, -1);
                Gfx.Fill(e.Graphics, r, Px(4), MenuHover);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var r = RowRect(e.Item, Px(4));
                int y = e.Item.Height / 2;
                using (var pen = new Pen(MenuSep)) e.Graphics.DrawLine(pen, r.Left, y, r.Right, y);
            }

            // Text is centered by its ink (reference glyphs 國H), not by the font's line box, so it sits in the middle
            // of the row highlight
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var g = e.Graphics;
                Font f = e.TextFont ?? e.Item.Font;
                Rectangle ink = Gfx.Ink("國H", f);
                float y = (float)Math.Round((e.Item.Height - ink.Height) / 2f - ink.Top);
                var old = g.TextRenderingHint;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var b = new SolidBrush(e.Item.Enabled ? MenuText : MenuDisabled))
                    g.DrawString(e.Text, f, b, e.TextRectangle.X, y, StringFormat.GenericTypographic);
                g.TextRenderingHint = old;
            }

            static Rectangle FullHeight(Rectangle r, ToolStripItem it) { return new Rectangle(r.X, 0, r.Width, it.Height); }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics, "", Glyph, FullHeight(e.ImageRectangle, e.Item), MenuText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics, "", Glyph, FullHeight(e.ArrowRectangle, e.Item), e.Item.Enabled ? MenuText : MenuDisabled,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
