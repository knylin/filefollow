using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileFollow
{
    // Frosted glass behind the panel: a separate acrylic-blur window sits right under the panel, which is drawn
    // translucent on top of it. (Acrylic only shows through transparent pixels, and the panel's controls all paint
    // opaque, so the blur cannot be applied to the panel itself.)
    class GlassBackdrop : NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags; public uint Color; public int Anim; }
        [StructLayout(LayoutKind.Sequential)] struct CompData { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref CompData d);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        readonly Form owner;
        readonly EventHandler follow;

        public GlassBackdrop(Form panel)
        {
            owner = panel;
            var cp = new CreateParams
            {
                Caption = "FileFollowGlass",
                Style = unchecked((int)0x80000000),          // WS_POPUP
                ExStyle = 0x80 | 0x08 | 0x08000000 | 0x20,   // TOOLWINDOW | TOPMOST | NOACTIVATE | TRANSPARENT (clicks go to the panel)
                X = panel.Left, Y = panel.Top, Width = panel.Width, Height = panel.Height,
            };
            CreateHandle(cp);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // DWMWCP_ROUND
            Accent();
            follow = delegate { Follow(); };
            panel.Move += follow;
            panel.Resize += follow;
            panel.VisibleChanged += follow;
            panel.Activated += follow;
        }

        public void Close()
        {
            owner.Move -= follow;
            owner.Resize -= follow;
            owner.VisibleChanged -= follow;
            owner.Activated -= follow;
            DestroyHandle();
        }

        void Accent()
        {
            // ACCENT_ENABLE_ACRYLICBLURBEHIND with a tint in AABBGGRR
            uint tint = Theme.Dark ? 0x66202020u : 0x33F3F3F3u;
            var ap = new AccentPolicy { State = 4, Flags = 0, Color = tint };
            int size = Marshal.SizeOf(ap);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(ap, p, false);
                var d = new CompData { Attr = 19, Data = p, Size = size }; // WCA_ACCENT_POLICY
                SetWindowCompositionAttribute(Handle, ref d);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Same bounds as the panel, directly beneath it in the z-order
        public void Follow()
        {
            if (Handle == IntPtr.Zero) return;
            if (!owner.Visible || owner.WindowState == FormWindowState.Minimized) { ShowWindow(Handle, 0); return; }
            SetWindowPos(Handle, owner.Handle, owner.Left, owner.Top, owner.Width, owner.Height, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST: HTTRANSPARENT
            if (m.Msg == 0x0014) { m.Result = (IntPtr)1; return; }   // WM_ERASEBKGND: leave it to the blur
            base.WndProc(ref m);
        }
    }
}
