// Collapsed view on the Windows taskbar, just left of the notification area. Windows 11 has no supported taskbar
// extension (DeskBands are gone). A child window of Shell_TrayWnd would change how the taskbar is composed and break
// tools such as TranslucentTB, so this is a separate topmost window laid over the taskbar instead: it keeps itself
// above the taskbar, hides while the taskbar is hidden (full screen, auto-hide), and follows Explorer restarts.
// When it can't attach, the caller falls back to the pill.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileFollow
{
    class TaskbarWidget : NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        const int MaxW = 260, PadInk = 16, GapInk = 8, IconPx = 20;
        readonly float s;
        readonly Font font;
        IntPtr taskbar;
        RECT lastTray, lastBar;
        string text = "";
        bool hover, clipIcon;

        public event Action Clicked, RightClicked;
        public Rectangle ScreenRect;     // where the widget is, in screen coordinates
        public bool Attached { get { return Handle != IntPtr.Zero && IsWindow(taskbar); } }

        public TaskbarWidget(float scale, Font f) { s = scale; font = f; }

        public string Trace = "";   // why the last attach failed, for --taskbar-test
        int P(float v) { return (int)Math.Round(v * s); }

        // Taskbar colors follow "Windows mode", which can differ from the app mode
        static bool DarkBar()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                return !(v is int && (int)v == 1);
            }
            catch { return true; }
        }

        public bool Attach(string name)
        {
            text = name ?? "";
            Detach();
            taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;
            RECT br;
            if (!GetWindowRect(taskbar, out br) || br.R - br.L < br.B - br.T) return false; // vertical taskbars aren't supported
            var cp = new CreateParams
            {
                Caption = "FileFollowTaskbar",
                Style = unchecked((int)0x80000000),             // WS_POPUP
                ExStyle = 0x80000 | 0x80 | 0x08000000 | 0x8,    // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                Width = 1, Height = 1
            };
            try { CreateHandle(cp); } catch (Exception ex) { Trace = "create: " + ex.Message; return false; }
            if (Handle == IntPtr.Zero) { Trace = "create: no handle"; return false; }
            if (!Place(true)) { Detach(); return false; }
            Trace = "ok " + ScreenRect;
            return true;
        }

        public void Detach()
        {
            if (Handle != IntPtr.Zero) { try { DestroyHandle(); } catch { } }
        }

        // Shows the folder name, or "Clipboard" with a clipboard icon while that tab is open
        public void SetText(string name, bool clipboard)
        {
            name = name ?? "";
            if (name == text && clipboard == clipIcon) return;
            text = name;
            clipIcon = clipboard;
            if (Handle != IntPtr.Zero) Place(true);
        }

        // Called on a timer: follows taskbar moves, tray width changes and Explorer restarts
        public bool Check()
        {
            if (Handle == IntPtr.Zero || !IsWindow(taskbar)) return Attach(text);
            RECT br, nr;
            GetWindowRect(taskbar, out br);
            IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero) GetWindowRect(tray, out nr); else nr = br;
            if (!Same(br, lastBar) || !Same(nr, lastTray)) Place(true);
            return true;
        }

        // Called often (with the foreground poll): stay above the taskbar, which raises itself when clicked,
        // and step aside while the taskbar is hidden
        public void Sync()
        {
            if (Handle == IntPtr.Zero) return;
            bool show = TaskbarShown();
            if (show != IsWindowVisible(Handle)) ShowWindow(Handle, show ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
            if (show) SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        bool TaskbarShown()
        {
            if (!IsWindow(taskbar) || !IsWindowVisible(taskbar)) return false;
            RECT br;
            GetWindowRect(taskbar, out br);
            var bar = Rectangle.FromLTRB(br.L, br.T, br.R, br.B);
            Rectangle mon = Screen.FromRectangle(bar).Bounds;
            if (!mon.Contains(new Rectangle(bar.X, bar.Y, bar.Width, Math.Max(1, bar.Height - 2)))) return false; // auto-hidden
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != taskbar && fg != Handle)
            {
                var sb = new StringBuilder(64);
                GetClassName(fg, sb, 64);
                string cls = sb.ToString();
                RECT fr;
                if (cls != "Progman" && cls != "WorkerW" && GetWindowRect(fg, out fr)
                    && fr.L <= mon.Left && fr.T <= mon.Top && fr.R >= mon.Right && fr.B >= mon.Bottom
                    && Screen.FromHandle(fg).Bounds == mon)
                    return false; // a full-screen app covers this monitor
            }
            return true;
        }

        static bool Same(RECT a, RECT b) { return a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B; }

        Rectangle IconInk()
        {
            using (var p = Gfx.IconPath(new RectangleF(0, 0, P(IconPx), P(IconPx)), clipIcon))
            {
                var b = p.GetBounds();
                float w = P(IconPx) / 20f * 1.6f / 2;
                return Rectangle.FromLTRB((int)Math.Floor(b.Left - w), (int)Math.Floor(b.Top - w), (int)Math.Ceiling(b.Right + w), (int)Math.Ceiling(b.Bottom + w));
            }
        }

        string Fit(out Rectangle ink)
        {
            int avail = P(MaxW) - P(PadInk) * 2 - IconInk().Width - P(GapInk);
            string t = text;
            ink = Gfx.Ink(t, font);
            while (ink.Width > avail && t.Length > 1)
            {
                t = t.Substring(0, t.Length - 1);
                ink = Gfx.Ink(t.TrimEnd() + "…", font);
                if (ink.Width <= avail) return t.TrimEnd() + "…";
            }
            return t;
        }

        bool Place(bool render)
        {
            RECT br, nr;
            if (!GetWindowRect(taskbar, out br)) return false;
            IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out nr)) { nr = br; nr.L = br.R; }
            lastBar = br; lastTray = nr;
            Rectangle ink;
            Fit(out ink);
            int h = br.B - br.T;
            int w = P(PadInk) * 2 + IconInk().Width + P(GapInk) + ink.Width;
            int x = nr.L - br.L - P(6) - w;
            if (x < (br.R - br.L) / 2) { Trace = "no room: x=" + x; return false; } // not enough free space on the right half
            ScreenRect = new Rectangle(br.L + x, br.T, w, h);
            SetWindowPos(Handle, (IntPtr)(-1), ScreenRect.X, ScreenRect.Y, w, h, 0x0010 | (TaskbarShown() ? 0x0040u : 0x0080u)); // HWND_TOPMOST, SWP_NOACTIVATE, show/hide
            return !render || Render();
        }

        bool Render()
        {
            int w = ScreenRect.Width, h = ScreenRect.Height;
            if (w <= 0 || h <= 0) return false;
            bool dark = DarkBar();
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    // alpha 1 everywhere so the whole area takes clicks (fully transparent pixels let them through)
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // Styled like the taskbar's own search pill: 32 px tall, fully rounded, a faint fill with a slightly
                    // brighter edge, white icon and text (measured on Windows 11: fill ~6 % white over the taskbar)
                    var pr = new RectangleF(0.5f, (h - P(32)) / 2f, w - 1, P(32));
                    if (dark)
                    {
                        Gfx.Fill(g, pr, pr.Height / 2, Color.FromArgb(hover ? 30 : 16, 255, 255, 255));
                        Gfx.Stroke(g, pr, pr.Height / 2, Color.FromArgb(hover ? 34 : 22, 255, 255, 255), 1f);
                    }
                    else
                    {
                        Gfx.Fill(g, pr, pr.Height / 2, Color.FromArgb(hover ? 235 : 190, 255, 255, 255));
                        Gfx.Stroke(g, pr, pr.Height / 2, Color.FromArgb(hover ? 40 : 26, 0, 0, 0), 1f);
                    }
                    Color fg = dark ? Color.White : Color.FromArgb(20, 20, 20);
                    var ii = IconInk();
                    float ix = P(PadInk) - ii.Left;
                    float iy = (h - ii.Height) / 2f - ii.Top - 0.6f * s;
                    Gfx.DrawIcon(g, new RectangleF(ix, iy, P(IconPx), P(IconPx)), fg, clipIcon);

                    Rectangle ink;
                    string t = Fit(out ink);
                    Rectangle refInk = Gfx.Ink("國H", font);
                    float tx = P(PadInk) + ii.Width + P(GapInk) - ink.Left;
                    float ty = (h - refInk.Height) / 2f - refInk.Top;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    // drawn twice 0.4 px apart: a touch heavier than regular, lighter than bold
                    using (var b = new SolidBrush(fg))
                    {
                        g.DrawString(t, font, b, (float)Math.Round(tx), (float)Math.Round(ty), StringFormat.GenericTypographic);
                        g.DrawString(t, font, b, (float)Math.Round(tx) + 0.4f * s, (float)Math.Round(ty), StringFormat.GenericTypographic);
                    }
                }
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
                try
                {
                    var size = bmp.Size;
                    var src = Point.Empty;
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    bool ok = UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2);
                    if (!ok) Trace = "UpdateLayeredWindow error " + Marshal.GetLastWin32Error();
                    return ok;
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

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0200: // WM_MOUSEMOVE
                    if (!hover)
                    {
                        hover = true;
                        Render();
                        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 2, hwndTrack = Handle };
                        TrackMouseEvent(ref tme);
                    }
                    break;
                case 0x02A3: // WM_MOUSELEAVE
                    hover = false;
                    Render();
                    break;
                case 0x0202: // WM_LBUTTONUP
                    if (Clicked != null) Clicked();
                    break;
                case 0x0205: // WM_RBUTTONUP
                    if (RightClicked != null) RightClicked();
                    break;
                case 0x0020: // WM_SETCURSOR
                    Cursor.Current = Cursors.Hand;
                    m.Result = (IntPtr)1;
                    return;
            }
            base.WndProc(ref m);
        }
    }
}
