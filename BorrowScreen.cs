using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Native
{
    [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }
    [DllImport("user32")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32")] internal static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32")] internal static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("dwmapi", EntryPoint="DwmGetWindowAttribute")] internal static extern int GetCloaked(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32", CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32")] internal static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [DllImport("user32")] internal static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    internal delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint thread);
    [DllImport("user32")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    internal static IntPtr At(Point point) { return GetAncestor(WindowFromPoint(point), 2); }
    internal static bool Down(Keys key) { return GetAsyncKeyState((int)key) < 0; }
}

internal sealed class LivePreview : Form
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public Native.Rect Destination, Source;
        public byte Opacity;
        public int Visible, ClientOnly;
    }
    [DllImport("dwmapi")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    private readonly Rectangle area;
    private readonly int anchor;
    private static readonly double[] scales = { 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };
    private readonly Size viewport;
    private int scaleIndex;
    private Rectangle imageBounds;
    private bool captureAttempted;
    private IntPtr thumbnail, sourceWindow;
    [DllImport("PixelCapture.dll")] private static extern IntPtr OpenCapture(IntPtr source);
    [DllImport("PixelCapture.dll")] private static extern int ReadCrop(IntPtr capture, Native.Rect outer,
        Native.Rect visible, Native.Rect crop, IntPtr pixels, int stride);
    [DllImport("PixelCapture.dll")] private static extern void CloseCapture(IntPtr capture);
    private IntPtr capture;
    private Bitmap pixels;
    private bool showingPixels;
    private Rectangle lastCrop;
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } // 不激活、不进 Alt+Tab
    }
    private static int Clamp(int value, int low, int high)
    {
        return Math.Max(low, Math.Min(value, high));
    }

    internal LivePreview(Point pointer, int zoom, bool larger = false, int anchor = -1)
    {
        this.anchor = anchor;
        scaleIndex = Array.IndexOf(scales, (double)zoom);
        IntPtr foreground = Native.GetForegroundWindow();
        Screen display = foreground == IntPtr.Zero ? Screen.FromPoint(pointer) : Screen.FromHandle(foreground);
        area = display.WorkingArea;
        // 按承载浮窗的屏幕计算物理像素。1080p 和 4K 上占用相近的屏幕比例。
        double screenScale = Math.Min(display.Bounds.Width / 1920.0, display.Bounds.Height / 1080.0);
        double factor = screenScale * zoom * (larger ? 1.25 : 1.0);
        viewport = new Size(Math.Max(1, Math.Min((int)Math.Round(640 * factor), area.Width * 45 / 100 - 2)),
            Math.Max(1, Math.Min((int)Math.Round(400 * factor), area.Height * 50 / 100 - 2)));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 33, 37);
        Text = "余光漫游 · 实时预览";
        Bounds = new Rectangle(area.Location, new Size(32, 32));
    }

    private Rectangle Placement(Point pointer, int width, int height)
    {
        int x = area.Left + (area.Width - width) / 2, y = area.Top + (area.Height - height) / 2;
        if (anchor >= 0)
        {
            int margin = Math.Max(8, area.Width / 160);
            int col = anchor % 3, row = anchor / 3;
            if (col == 0) x = area.Left + margin;
            if (col == 2) x = area.Right - width - margin;
            if (row == 0) y = area.Top + margin;
            if (row == 2) y = area.Bottom - height - margin;
        }
        else if (area.Contains(pointer))
        {
            x = pointer.X + 20 + width <= area.Right ? pointer.X + 20 : pointer.X - 20 - width;
            x = Clamp(x, area.Left, area.Right - width);
            y = Clamp(pointer.Y - height / 2, area.Top, area.Bottom - height);
        }
        return new Rectangle(x, y, width, height);
    }

    private void ClearCapture()
    {
        if (capture != IntPtr.Zero) { CloseCapture(capture); capture = IntPtr.Zero; }
        if (pixels != null) { pixels.Dispose(); pixels = null; }
        if (showingPixels) { showingPixels = false; Invalidate(); }
        captureAttempted = false;
    }
    internal void StepZoom(int steps)
    {
        int next = Clamp(scaleIndex + steps, 0, scales.Length - 1);
        if (next == scaleIndex) return;
        if (scales[next] < 2 && captureAttempted) ClearCapture();
        scaleIndex = next;
        lastCrop = Rectangle.Empty;
        Invalidate();
    }
    private void ClearSource()
    {
        ClearCapture();
        if (thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(thumbnail);
        thumbnail = IntPtr.Zero;
        sourceWindow = IntPtr.Zero;
        lastCrop = Rectangle.Empty;
    }

    internal void Track(Point pointer)
    {
        // 鼠标快速移入上一帧浮窗的位置时，先让开，再命中下方真实窗口。
        if (anchor < 0 && Visible && Bounds.Contains(pointer)) Bounds = Placement(pointer, Width, Height);
        IntPtr source = Native.At(pointer);
        if (Visible && source == Handle)
        {
            // 固定位置时指针可能进入浮窗；沿 Z 序命中它下面的可见窗口。
            for (source=Native.GetWindow(Handle,2);source!=IntPtr.Zero;source=Native.GetWindow(source,2))
            {
                Native.Rect r; int cloaked;
                if (Native.IsWindowVisible(source) && !Native.IsIconic(source) &&
                    (Native.GetCloaked(source,14,out cloaked,4)!=0 || cloaked==0) &&
                    Native.GetWindowRect(source,out r) && Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom).Contains(pointer)) break;
            }
        }
        Native.Rect frame;
        int style = Native.GetWindowLong(source, -16), extended = Native.GetWindowLong(source, -20);
        if (source == IntPtr.Zero || ((style & 0x00C40000) == 0 && (extended & 0x40000) == 0) ||
            (extended & 0x80) != 0 || Native.IsIconic(source) || !Native.GetWindowRect(source, out frame))
        {
            if (Visible) Hide();
            ClearSource();
            return;
        }

        if (source != sourceWindow)
        {
            ClearSource();
            IntPtr nextThumbnail;
            if (DwmRegisterThumbnail(Handle, source, out nextThumbnail) != 0) { Hide(); return; }
            thumbnail = nextThumbnail;
            sourceWindow = source;
        }
        double zoom = scales[scaleIndex];
        if (zoom >= 2 && !captureAttempted)
        {
            captureAttempted = true;
            try { capture = OpenCapture(source); }
            catch (DllNotFoundException) { capture = IntPtr.Zero; }
        }
        // DWM 取景坐标从窗口外框算起。可见边框用于避免取到窗口外的像素。
        Native.Rect visible;
        if (Native.DwmGetWindowAttribute(source, 9, out visible, 16) != 0 || visible.Width <= 0 || visible.Height <= 0)
            visible = frame;
        // 浮窗沿用本次打开时的大小，滚轮只改变取景范围。小窗口缩小时居中留边。
        int viewWidth = viewport.Width, viewHeight = viewport.Height;
        int w = Math.Min(visible.Width, Math.Max(1, (int)(viewWidth / zoom)));
        int h = Math.Min(visible.Height, Math.Max(1, (int)(viewHeight / zoom)));
        if (w < 1 || h < 1) { Hide(); ClearSource(); return; }
        int x = Clamp(pointer.X - w / 2, visible.Left, visible.Right - w) - frame.Left;
        int y = Clamp(pointer.Y - h / 2, visible.Top, visible.Bottom - h) - frame.Top;
        Rectangle crop = new Rectangle(x, y, w, h);
        int imageWidth = Math.Max(1, (int)Math.Round(w * zoom));
        int imageHeight = Math.Max(1, (int)Math.Round(h * zoom));
        imageBounds = new Rectangle(1 + (viewWidth - imageWidth) / 2, 1 + (viewHeight - imageHeight) / 2, imageWidth, imageHeight);
        Rectangle destination = Placement(pointer, viewWidth + 2, viewHeight + 2);
        if (Bounds != destination) Bounds = destination;
        if (capture != IntPtr.Zero)
        {
            int result = ReadPixels(frame, visible, crop);
            if (result > 0)
            {
                if (!showingPixels)
                {
                    ThumbnailProperties hide = new ThumbnailProperties { Flags = 8, Visible = 0 };
                    if (DwmUpdateThumbnailProperties(thumbnail, ref hide) != 0) { Hide(); ClearSource(); return; }
                    showingPixels = true;
                }
                if (result == 1) Invalidate();
                if (!Visible) Show();
                return;
            }
            if (result < 0) { CloseCapture(capture); capture = IntPtr.Zero; }
            if (showingPixels) { showingPixels = false; lastCrop = Rectangle.Empty; Invalidate(); }
        }
        if (crop != lastCrop)
        {
            ThumbnailProperties props = new ThumbnailProperties { Flags = 11, Visible = 1,
                Source = new Native.Rect { Left = x, Top = y, Right = x + w, Bottom = y + h },
                Destination = new Native.Rect { Left = imageBounds.Left, Top = imageBounds.Top,
                    Right = imageBounds.Right, Bottom = imageBounds.Bottom } };
            if (DwmUpdateThumbnailProperties(thumbnail, ref props) != 0) { Hide(); ClearSource(); return; }
            lastCrop = crop;
        }
        if (!Visible) Show();
    }
    private int ReadPixels(Native.Rect frame, Native.Rect visible, Rectangle crop)
    {
        if (pixels == null || pixels.Size != crop.Size)
        {
            if (pixels != null) pixels.Dispose();
            pixels = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppRgb);
        }
        Native.Rect screenCrop = new Native.Rect { Left = frame.Left + crop.Left, Top = frame.Top + crop.Top,
            Right = frame.Left + crop.Right, Bottom = frame.Top + crop.Bottom };
        BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, crop.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
        try { return ReadCrop(capture, frame, visible, screenCrop, data.Scan0, data.Stride); }
        finally { pixels.UnlockBits(data); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!showingPixels || pixels == null) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(pixels, imageBounds,
            0, 0, pixels.Width, pixels.Height, GraphicsUnit.Pixel);
    }
    protected override void Dispose(bool disposing)
    {
        ClearSource();
        base.Dispose(disposing);
    }
}

internal sealed class TrayWindow : NativeWindow, IDisposable
{
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private LivePreview active;
    private bool held;
    private Binding shortcut;
    private int hotkeyId = 1;
    private Preferences preferences;
    private SettingsForm settings;
    private readonly ToolStripMenuItem instructions;
    private readonly ToolStripMenuItem doubleZoom;
    private readonly ToolStripMenuItem largerPreview;
    private readonly Native.MouseProc wheelCallback;
    private IntPtr wheelHook;
    private int wheelDelta;
    internal TrayWindow()
    {
        wheelCallback = OnMouse;
        CreateHandle(new CreateParams { Caption = "BorrowScreen.Hotkey", Parent = new IntPtr(-3) });
        preferences = Preferences.Load();
        foreach (Binding candidate in new Binding[] { preferences.Shortcut, new Binding(Keys.F8,0), new Binding(Keys.F9,0), new Binding(Keys.F10,0) })
        {
            if (!Native.RegisterHotKey(Handle, hotkeyId, 0x4000 | candidate.Mods, (uint)candidate.Key)) continue;
            shortcut = candidate;
            break;
        }
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置…", null, delegate { OpenSettings(); });
        instructions = new ToolStripMenuItem("",null,delegate { ShowHelp(); });
        menu.Items.Add(instructions);
        doubleZoom = new ToolStripMenuItem("清晰两倍放大") { CheckOnClick = true };
        menu.Items.Add(doubleZoom);
        largerPreview = new ToolStripMenuItem("加大浮窗") { CheckOnClick = true };
        menu.Items.Add(largerPreview);
        menu.Items.Add("退出余光漫游", null, delegate { Application.ExitThread(); });
        tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            ContextMenuStrip = menu, Visible = true };
        UpdateLabels();
        tray.DoubleClick += delegate { OpenSettings(); };
        timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += delegate {
            if (!shortcut.Held()) End();
            else if (active != null)
            {
                int steps = wheelDelta / 120;
                wheelDelta %= 120;
                if (steps != 0) active.StepZoom(steps);
                active.Track(Cursor.Position);
            }
        };
        if (shortcut.Key==Keys.None) OpenSettings();
        else if (!shortcut.Equals(preferences.Shortcut)) tray.ShowBalloonTip(3000,"余光漫游","已保存的快捷键被占用，本次临时使用 "+shortcut+"。",ToolTipIcon.Warning);
        else ShowHelp();
    }
    private void UpdateLabels()
    {
        string label=shortcut.Key==Keys.None?"请选择快捷键":"按住 "+shortcut;
        instructions.Text=label; tray.Text="余光漫游 · "+label;
    }
    internal void OpenSettings()
    {
        if(settings!=null) { if(settings.WindowState==FormWindowState.Minimized) settings.WindowState=FormWindowState.Normal; settings.Activate(); return; }
        End();
        settings=new SettingsForm(shortcut.Key==Keys.None?preferences.Shortcut:shortcut,preferences.Position,Apply);
        settings.FormClosed+=delegate { settings=null; };
        settings.Show(); settings.Activate();
    }
    private string Apply(Preferences next)
    {
        bool changed=!next.Shortcut.Equals(shortcut);
        int newId=hotkeyId==1?2:1;
        if(changed && !Native.RegisterHotKey(Handle,newId,0x4000|next.Shortcut.Mods,(uint)next.Shortcut.Key))
            return next.Shortcut+" 已被占用或由系统保留，请换一个。";
        try { next.Save(); }
        catch(Exception ex) {
            if(changed) Native.UnregisterHotKey(Handle,newId);
            if(!(ex is System.IO.IOException) && !(ex is UnauthorizedAccessException)) throw;
            return "保存失败："+ex.Message;
        }
        if(changed) {
            if(shortcut.Key!=Keys.None) Native.UnregisterHotKey(Handle,hotkeyId);
            hotkeyId=newId; shortcut=next.Shortcut;
        }
        preferences=next; UpdateLabels(); return null;
    }
    private void ShowHelp()
    {
        tray.ShowBalloonTip(2600, "余光漫游 · " + shortcut, "鼠标指向参考窗口，按住 " + shortcut +
            " 移动取景，上滚放大，下滚缩小，松手收起。", ToolTipIcon.Info);
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() == 0x020A && active != null && active.Visible && shortcut.Held())
        {
            // MSLLHOOKSTRUCT 的 POINT 后紧跟 mouseData，高 16 位为有符号滚动量。
            wheelDelta += (short)(Marshal.ReadInt32(data, 8) >> 16);
            return new IntPtr(1);
        }
        return Native.CallNextHookEx(wheelHook, code, message, data);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x8001) { OpenSettings(); return; }
        if (m.Msg == 0x312 && m.WParam.ToInt32() == hotkeyId && settings!=null && settings.ContainsFocus)
        { settings.Recorded(shortcut); return; }
        if (m.Msg == 0x312 && m.WParam.ToInt32() == hotkeyId && !held && shortcut.Held())
        {
            held = true;
            if (!Native.Down(Keys.LButton) && !Native.Down(Keys.RButton))
            {
                Point pointer = Cursor.Position;
                active = new LivePreview(pointer, doubleZoom.Checked ? 2 : 1, largerPreview.Checked, preferences.Position);
                wheelHook = Native.SetWindowsHookEx(14, wheelCallback, Native.GetModuleHandle(null), 0);
                if (wheelHook == IntPtr.Zero)
                    tray.ShowBalloonTip(1800, "余光漫游", "本次未能接管滚轮，松开快捷键后可重试。", ToolTipIcon.Warning);
                active.Track(pointer);
            }
            timer.Start();
        }
        base.WndProc(ref m);
    }
    private void End()
    {
        timer.Stop();
        if (wheelHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(wheelHook); wheelHook = IntPtr.Zero; }
        wheelDelta = 0;
        if (active != null) { active.Dispose(); active = null; }
        held = false;
    }
    public void Dispose()
    {
        End();
        if(settings!=null) settings.Close();
        Native.UnregisterHotKey(Handle, hotkeyId);
        timer.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Icon.Dispose();
        tray.Dispose();
        DestroyHandle();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        bool owner;
        using (Mutex mutex = new Mutex(true, @"Local\BorrowScreen.Tray.v1", out owner))
        {
            if (!owner) { Native.PostMessage(Native.FindWindowEx(new IntPtr(-3),IntPtr.Zero,null,"BorrowScreen.Hotkey"),0x8001,IntPtr.Zero,IntPtr.Zero); return; }
            try { using (TrayWindow window = new TrayWindow()) { if(Array.IndexOf(args,"--settings")>=0) window.OpenSettings(); Application.Run(); } }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "余光漫游"); }
        }
    }
}
