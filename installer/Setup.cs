// ModernRecycleBinSetup.exe - single-file installer for Modern Recycle Bin.
//
// The whole application payload is embedded as a zip resource, so the user only
// needs this one file. Installing needs no administrator rights: everything goes
// under %LOCALAPPDATA% and the (optional) Recycle Bin takeover is a per-user
// registry key.
//
//   ModernRecycleBinSetup.exe             install (GUI)
//   ModernRecycleBinSetup.exe --uninstall remove everything it created
//   ModernRecycleBinSetup.exe --silent    install with defaults, no GUI
//
// .NET Framework only, compiled with the in-box csc (C# 5 syntax).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    internal const string AppName = "Modern Recycle Bin";
    internal const string Version = "1.1.0";
    internal const string Clsid = "{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>True in --silent / /S mode: no dialogs at all.</summary>
    internal static bool Silent;

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        bool uninstall = Has(args, "--uninstall");
        bool silent = Has(args, "--silent") || Has(args, "/S");
        bool noTakeover = Has(args, "--no-takeover");
        bool noShortcut = Has(args, "--no-shortcut");
        Silent = silent;

        // Crisp text at 125% / 150% scaling: handle DPI ourselves, before any
        // window or font exists. Without this Windows scales the finished bitmap
        // and every glyph turns soft.
        try { SetProcessDPIAware(); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            if (silent)
            {
                if (uninstall) Installer.Uninstall(null);
                else Installer.Install(Installer.TargetDir, !noShortcut, !noTakeover, null);
                return;
            }
            Application.Run(new SetupForm(uninstall));
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool Has(string[] args, string flag)
    {
        foreach (string a in args)
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The app renders its UI with WebView2, so the Evergreen Runtime has to be
    /// present. It ships with Windows 11 and current Windows 10, but a clean or
    /// heavily trimmed install can lack it - in that case the app would only show
    /// an error. Detect it up front and point the user at Microsoft's installer.
    /// </summary>
    internal static bool WebView2RuntimeInstalled()
    {
        string[] keys = new string[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };
        foreach (string sub in keys)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(sub))
                {
                    if (k != null)
                    {
                        string pv = k.GetValue("pv") as string;
                        if (!string.IsNullOrEmpty(pv)) return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    internal const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
}

internal static class Installer
{
    internal static string TargetDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModernRecycleBin");
        }
    }

    /// <summary>
    /// The uninstaller lives NEXT TO the install folder, never inside it. Windows
    /// will not let a running exe delete itself, so an uninstaller kept inside the
    /// folder could never remove that folder.
    /// </summary>
    private static string UninstallerPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModernRecycleBin-Uninstall.exe");
        }
    }

    private static string DesktopLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "回收站.lnk"); }
    }

    internal static void Install(string dir, bool desktopShortcut, bool takeOverRecycleBin, Action<string> log)
    {
        if (!Program.WebView2RuntimeInstalled())
        {
            Say(log, "提示：未检测到 WebView2 运行时，程序可能无法显示界面。" +
                     "请安装微软官方组件：" + Program.WebView2DownloadUrl);
        }

        Say(log, "正在解压文件…");
        Directory.CreateDirectory(dir);

        // 1) payload
        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream s = asm.GetManifestResourceStream("PAYLOAD"))
        {
            if (s == null) throw new Exception("安装包损坏：找不到内嵌的程序文件。");

            string zip = Path.Combine(Path.GetTempPath(), "mrb-payload-" + Guid.NewGuid().ToString("N") + ".zip");
            using (FileStream fs = File.Create(zip)) s.CopyTo(fs);

            ExtractZip(zip, dir);
            try { File.Delete(zip); } catch { }
        }

        // 2) the uninstaller, kept OUTSIDE the folder it has to delete
        try
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string un = UninstallerPath;
            if (!string.Equals(self, un, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, un, true);
        }
        catch { }
        WriteUninstallCmd(dir);

        // 3) desktop shortcut
        if (desktopShortcut)
        {
            Say(log, "正在创建桌面快捷方式…");
            try { CreateShortcut(DesktopLink, Path.Combine(dir, "RecycleBin.exe"), dir); }
            catch { }
        }

        // 4) optionally make the desktop Recycle Bin use this app
        if (takeOverRecycleBin)
        {
            Say(log, "正在接管桌面回收站…");
            SetTakeover(true, dir);
        }
        else
        {
            SetTakeover(false, dir);
        }

        Say(log, "安装完成");
    }

    internal static void Uninstall(Action<string> log)
    {
        string dir = Installer.TargetDir;

        // If this copy somehow runs from inside the install folder, hand over to the
        // sibling copy first: a running exe cannot delete its own folder.
        // NOTE: compare against dir + separator. A plain prefix test also matches the
        // sibling "ModernRecycleBin-Uninstall.exe", which sent the uninstaller into an
        // endless relaunch loop that silently did nothing.
        string self = Assembly.GetExecutingAssembly().Location;
        string dirWithSep = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (self.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string un = UninstallerPath;
                if (!File.Exists(un)) File.Copy(self, un, true);
                Process.Start(new ProcessStartInfo(un, "--uninstall") { UseShellExecute = false });
                return;
            }
            catch { }
        }

        Say(log, "正在恢复回收站…");
        SetTakeover(false, dir);

        Say(log, "正在关闭程序…");
        // The app may still be open (a user can uninstall at any time) and it holds
        // the WebView2 DLLs, which makes the folder undeletable. Close only our own
        // copy - identified by its path - never an unrelated process.
        try
        {
            foreach (Process p in Process.GetProcessesByName("RecycleBin"))
            {
                try
                {
                    string exe = p.MainModule.FileName;
                    if (exe != null && exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(4000);
                    }
                }
                catch { }
            }
        }
        catch { }
        System.Threading.Thread.Sleep(600);

        Say(log, "正在删除桌面快捷方式…");
        try { if (File.Exists(DesktopLink)) File.Delete(DesktopLink); } catch { }

        Say(log, "正在清理文件…");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) break;
                Directory.Delete(dir, true);
                if (!Directory.Exists(dir)) break;
            }
            catch { }
            System.Threading.Thread.Sleep(400);
        }

        // Backstop: after this process exits, keep retrying both the folder and our
        // own copy. WebView2 child processes can hold the app's DLLs for a while, so
        // the immediate delete above is not always enough.
        try
        {
            string script = Path.Combine(Path.GetTempPath(), "mrb-clean-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                "for /l %%i in (1,1,30) do (\r\n" +
                "  del /f /q \"" + self + "\" >nul 2>&1\r\n" +
                "  rd /s /q \"" + dir + "\" >nul 2>&1\r\n" +
                "  if not exist \"" + dir + "\" if not exist \"" + self + "\" goto done\r\n" +
                "  ping 127.0.0.1 -n 2 >nul\r\n" +
                ")\r\n" +
                ":done\r\n" +
                "del /f /q \"%~f0\" >nul 2>&1\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }

        Say(log, "卸载完成");
        if (log == null && !Program.Silent)
            MessageBox.Show("已卸载。" + Environment.NewLine + "桌面回收站已恢复为系统默认。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Turns the per-user "open the Recycle Bin with this app" redirect on/off.</summary>
    private static void SetTakeover(bool on, string dir)
    {
        try
        {
            string baseKey = @"Software\Classes\CLSID\" + Program.Clsid + @"\shell";
            using (RegistryKey shell = Registry.CurrentUser.CreateSubKey(baseKey))
            {
                if (shell == null) return;

                foreach (string verb in new string[] { "open", "opennewwindow" })
                {
                    if (on)
                    {
                        using (RegistryKey cmd = shell.CreateSubKey(verb + @"\command"))
                        {
                            if (cmd != null)
                                cmd.SetValue(string.Empty, "\"" + Path.Combine(dir, "RecycleBin.exe") + "\"");
                        }
                    }
                    else
                    {
                        // Only remove the redirect when it points at THIS install.
                        // Deleting unconditionally would clobber a configuration the
                        // user set up themselves (learned the hard way in testing).
                        try
                        {
                            using (RegistryKey cmd = shell.OpenSubKey(verb + @"\command"))
                            {
                                string current = cmd == null ? null : cmd.GetValue(string.Empty) as string;
                                if (current != null &&
                                    current.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    shell.DeleteSubKeyTree(verb, false);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static void WriteUninstallCmd(string dir)
    {
        try
        {
            string body =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                "echo 正在卸载 " + Program.AppName + " ...\r\n" +
                "\"" + UninstallerPath + "\" --uninstall\r\n" +
                "if errorlevel 1 pause\r\n";
            File.WriteAllText(Path.Combine(dir, "Uninstall.cmd"), body, System.Text.Encoding.Default);
        }
        catch { }
    }

    private static void CreateShortcut(string linkPath, string target, string workDir)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "打开回收站（Modern Recycle Bin）" });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    private static void ExtractZip(string zip, string dir)
    {
        using (ZipArchive a = ZipFile.OpenRead(zip))
        {
            foreach (ZipArchiveEntry e in a.Entries)
            {
                string dest = Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                if (e.FullName.EndsWith("/"))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
            }
        }
    }

    private static void Say(Action<string> log, string msg)
    {
        if (log != null) log(msg);
    }
}

// ---------------------------------------------------------------------------
//  Palette and small drawing helpers shared by the whole window
// ---------------------------------------------------------------------------

internal static class Theme
{
    internal static readonly Color Bg        = Color.FromArgb(0x1E, 0x20, 0x23);
    internal static readonly Color BgTop     = Color.FromArgb(0x24, 0x27, 0x2B);
    internal static readonly Color Card      = Color.FromArgb(0x2A, 0x2D, 0x32);
    internal static readonly Color CardHover = Color.FromArgb(0x31, 0x35, 0x3A);
    internal static readonly Color Line      = Color.FromArgb(0x3A, 0x3E, 0x44);
    internal static readonly Color Ink       = Color.FromArgb(0xEC, 0xEF, 0xF2);
    internal static readonly Color InkDim    = Color.FromArgb(0x9A, 0xA0, 0xA8);
    internal static readonly Color InkFaint  = Color.FromArgb(0x6E, 0x74, 0x7C);
    internal static readonly Color Accent    = Color.FromArgb(0xFF, 0x8A, 0x3D);
    internal static readonly Color AccentHi  = Color.FromArgb(0xFF, 0x9E, 0x5E);
    internal static readonly Color Track     = Color.FromArgb(0x35, 0x39, 0x3F);
    internal static readonly Color Warn      = Color.FromArgb(0xFF, 0xC1, 0x7A);

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static Font Ui(float size, FontStyle style)
    {
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }
}

// ---------------------------------------------------------------------------
//  The window: borderless, DPI aware, drawn entirely with GDI+
// ---------------------------------------------------------------------------

internal sealed class SetupForm : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int PAD = 30;
    private const int CARD_H = 74;

    private readonly bool _uninstall;
    private readonly bool _needRuntime;
    private readonly float _s;
    private readonly Font _fH1, _fHead, _fBody, _fSmall, _fBtn;

    private bool _optDesktop = true;
    private bool _optTakeover = true;
    private string _status;
    private int _progress;
    private bool _busy;
    private int _hover;

    internal SetupForm(bool uninstall)
    {
        _uninstall = uninstall;
        _needRuntime = !uninstall && !Program.WebView2RuntimeInstalled();

        using (var g = CreateGraphics()) _s = g.DpiX / 96f;

        _fH1    = Theme.Ui(21f, FontStyle.Regular);
        _fHead  = Theme.Ui(12f, FontStyle.Regular);
        _fBody  = Theme.Ui(10.5f, FontStyle.Regular);
        _fSmall = Theme.Ui(9.5f, FontStyle.Regular);
        _fBtn   = Theme.Ui(11f, FontStyle.Regular);

        _status = uninstall ? "点击下方按钮开始卸载。" : "点击下方按钮开始安装。";

        Text = Program.AppName;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        ClientSize = new Size(S(640), S(uninstall ? 330 : (_needRuntime ? 470 : 430)));

        MouseDown += OnAnyMouseDown;
        MouseMove += OnAnyMouseMove;
        MouseClick += OnAnyMouseClick;
        KeyPreview = true;
        KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private int S(int v) { return (int)Math.Round(v * _s); }
    private float SF(float v) { return v * _s; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    private int PAD_S { get { return S(PAD); } }

    private Rectangle CloseRect
    {
        get { return new Rectangle(Width - PAD_S - S(12), S(34) - S(12), S(24), S(24)); }
    }

    private Rectangle ButtonRect
    {
        get
        {
            int w = S(124), h = S(40);
            return new Rectangle(Width - PAD_S - w, Height - S(72), w, h);
        }
    }

    private Rectangle CardRect(int index)
    {
        int y = S(84) + S(38) + S(26);
        if (index == 0) return new Rectangle(PAD_S, y, Width - PAD_S * 2, S(CARD_H));
        return new Rectangle(PAD_S, y + S(CARD_H) + S(10), Width - PAD_S * 2, S(CARD_H));
    }

    // ---------------------------------------------------------------- paint

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Theme.BgTop, Theme.Bg, 90f))
            g.FillRectangle(b, 0, 0, Width, Height);

        DrawTitleBar(g);

        int y = S(84);
        TextRenderer.DrawText(g, _uninstall ? "卸载 Modern Recycle Bin" : "安装 Modern Recycle Bin",
            _fH1, new Point(PAD_S, y), Theme.Ink, TextFormatFlags.NoPadding);
        y += S(38);

        TextRenderer.DrawText(g,
            _uninstall ? "移除程序文件与快捷方式，并把桌面回收站恢复为系统默认。"
                       : "一个更现代、更快、更像 Windows 11 的回收站。",
            _fBody, new Point(PAD_S, y), Theme.InkDim, TextFormatFlags.NoPadding);
        y += S(26);

        if (!_uninstall)
        {
            DrawOptionCard(g, 0, "创建桌面快捷方式", "在桌面放一个「回收站」快捷方式", _optDesktop, _hover == 31);
            DrawOptionCard(g, 1, "接管桌面回收站", "双击桌面回收站就用本程序打开，卸载即还原", _optTakeover, _hover == 32);

            if (_needRuntime)
            {
                var r = new Rectangle(PAD_S, CardRect(1).Bottom + S(16), Width - PAD_S * 2, S(38));
                _warnRect = r;
                using (var p = Theme.Round(r, S(10)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(0x3A, 0x30, 0x22))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(0x6E, Theme.Warn), SF(1f))) g.DrawPath(pen, p);
                }
                var sz = TextRenderer.MeasureText(g, "未检测到 WebView2 运行时", _fSmall,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "未检测到 WebView2 运行时", _fSmall,
                    new Point(r.X + S(14), r.Y + (r.Height - sz.Height) / 2), Theme.Warn, TextFormatFlags.NoPadding);
                var link = "点此安装（微软官方）";
                var lsz = TextRenderer.MeasureText(g, link, _fSmall, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                var lp = new Point(r.Right - S(14) - lsz.Width, r.Y + (r.Height - lsz.Height) / 2);
                TextRenderer.DrawText(g, link, _fSmall, lp, Theme.Accent, TextFormatFlags.NoPadding);
                _linkRect = new Rectangle(lp.X - S(4), r.Y, lsz.Width + S(8), r.Height);
            }
        }

        // status sits below whatever ended last, so the warning card never collides
        int statusY = _uninstall ? S(190) : CardRect(1).Bottom + S(18);
        if (_needRuntime && _warnRect.Height > 0) statusY = _warnRect.Bottom + S(14);
        TextRenderer.DrawText(g, _status, _fSmall, new Point(PAD_S, statusY), Theme.InkFaint, TextFormatFlags.NoPadding);

        if (_progress > 0)
        {
            var track = new Rectangle(PAD_S, ButtonRect.Y - S(26), Width - PAD_S * 2, S(6));
            using (var p = Theme.Round(track, track.Height / 2))
            using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
            int fw = (int)Math.Round(track.Width * (_progress / 100.0));
            if (fw < track.Height) fw = track.Height;
            var fill = new Rectangle(track.X, track.Y, fw, track.Height);
            using (var p = Theme.Round(fill, fill.Height / 2))
            using (var b = new LinearGradientBrush(fill, Theme.AccentHi, Theme.Accent, 0f)) g.FillPath(b, p);
        }

        DrawButtons(g);
        DrawFooter(g);
    }

    private Rectangle _linkRect = Rectangle.Empty;
    private Rectangle _warnRect = Rectangle.Empty;

    private void DrawTitleBar(Graphics g)
    {
        int cx = PAD_S, cy = S(34), r = S(9);
        using (var p = Theme.Round(new Rectangle(cx - r, cy - r, r * 2, r * 2), S(6)))
        using (var b = new LinearGradientBrush(new Rectangle(cx - r, cy - r, r * 2, r * 2), Theme.AccentHi, Theme.Accent, 45f))
            g.FillPath(b, p);

        TextRenderer.DrawText(g, Program.AppName + "  " + Program.Version, _fSmall,
            new Point(cx + S(18), cy - S(8)), Theme.InkFaint, TextFormatFlags.NoPadding);

        var box = CloseRect;
        if (_hover == 2)
        {
            using (var p = Theme.Round(box, S(6)))
            using (var b = new SolidBrush(Color.FromArgb(0x3A, 0x2B, 0x2E))) g.FillPath(b, p);
        }
        using (var pen = new Pen(_hover == 2 ? Color.FromArgb(0xFF, 0xB4, 0xB4) : Theme.InkDim, SF(1.4f)))
        {
            int m = S(8);
            g.DrawLine(pen, box.Left + m, box.Top + m, box.Right - m, box.Bottom - m);
            g.DrawLine(pen, box.Right - m, box.Top + m, box.Left + m, box.Bottom - m);
        }
    }

    private void DrawOptionCard(Graphics g, int index, string title, string sub, bool on, bool hover)
    {
        var rect = CardRect(index);
        using (var p = Theme.Round(rect, S(12)))
        {
            using (var b = new SolidBrush(hover ? Theme.CardHover : Theme.Card)) g.FillPath(b, p);
            using (var pen = new Pen(on ? Color.FromArgb(0x2E, Theme.Accent) : Theme.Line, SF(1f))) g.DrawPath(pen, p);
        }

        int bs = S(20);
        var boxRect = new Rectangle(rect.X + S(18), rect.Y + (rect.Height - bs) / 2, bs, bs);
        using (var p = Theme.Round(boxRect, S(6)))
        {
            if (on) { using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p); }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(0x22, 0x25, 0x29))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Line, SF(1.2f))) g.DrawPath(pen, p);
            }
        }
        if (on)
        {
            using (var pen = new Pen(Color.White, SF(2f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                int bx = boxRect.X, by = boxRect.Y, s2 = bs;
                g.DrawLine(pen, bx + s2 * 0.26f, by + s2 * 0.52f, bx + s2 * 0.44f, by + s2 * 0.70f);
                g.DrawLine(pen, bx + s2 * 0.44f, by + s2 * 0.70f, bx + s2 * 0.76f, by + s2 * 0.30f);
            }
        }

        int tx = boxRect.Right + S(16);
        TextRenderer.DrawText(g, title, _fHead, new Point(tx, rect.Y + S(14)), on ? Theme.Ink : Theme.InkDim, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, sub, _fSmall, new Point(tx, rect.Y + S(40)), Theme.InkFaint, TextFormatFlags.NoPadding);
    }

    private void DrawButtons(Graphics g)
    {
        var r = ButtonRect;
        Color fill = _busy ? Color.FromArgb(0x4A, 0x3E, 0x35) : (_hover == 1 ? Theme.AccentHi : Theme.Accent);

        using (var p = Theme.Round(r, S(10)))
        using (var b = new SolidBrush(fill)) g.FillPath(b, p);

        string label = _uninstall ? "卸载" : "一键安装";
        var sz = TextRenderer.MeasureText(g, label, _fBtn, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, label, _fBtn,
            new Point(r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2),
            Color.White, TextFormatFlags.NoPadding);
    }

    private void DrawFooter(Graphics g)
    {
        var r = ButtonRect;
        string text = "改动写入当前用户注册表，可随时卸载还原";
        var sz = TextRenderer.MeasureText(g, text, _fSmall, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, text, _fSmall,
            new Point(PAD_S, r.Y + (r.Height - sz.Height) / 2), Theme.InkFaint, TextFormatFlags.NoPadding);
    }

    // ---------------------------------------------------------------- input

    private void OnAnyMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (CloseRect.Contains(e.Location) || ButtonRect.Contains(e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnAnyMouseMove(object sender, MouseEventArgs e)
    {
        int h = 0;
        if (CloseRect.Contains(e.Location)) h = 2;
        else if (ButtonRect.Contains(e.Location)) h = 1;
        else if (!_uninstall && !_busy)
        {
            if (CardRect(0).Contains(e.Location)) h = 31;
            else if (CardRect(1).Contains(e.Location)) h = 32;
        }
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    private void OnAnyMouseClick(object sender, MouseEventArgs e)
    {
        if (_busy) return;
        if (CloseRect.Contains(e.Location)) { Close(); return; }
        if (ButtonRect.Contains(e.Location)) { Start(); return; }
        if (!_uninstall)
        {
            if (CardRect(0).Contains(e.Location)) { _optDesktop = !_optDesktop; Invalidate(); return; }
            if (CardRect(1).Contains(e.Location)) { _optTakeover = !_optTakeover; Invalidate(); return; }
        }
        if (_needRuntime && _linkRect.Contains(e.Location))
        {
            try { Process.Start(Program.WebView2DownloadUrl); } catch { }
        }
    }

    private void Start()
    {
        _busy = true;
        _progress = 8;
        Pump();

        try
        {
            if (_uninstall)
            {
                Installer.Uninstall(delegate(string m) { _status = m; _progress = Math.Min(92, _progress + 20); Pump(); });
                _progress = 100; Pump();
                Close();
            }
            else
            {
                Installer.Install(Installer.TargetDir, _optDesktop, _optTakeover,
                    delegate(string m) { _status = m; _progress = Math.Min(92, _progress + 20); Pump(); });
                _progress = 100;
                _status = "安装完成";
                Pump();

                if (MessageBox.Show(this,
                        "安装完成。" + Environment.NewLine + Environment.NewLine +
                        "程序位置：" + Installer.TargetDir + Environment.NewLine +
                        "以后想卸载，运行该目录下的 Uninstall.cmd 即可。" + Environment.NewLine + Environment.NewLine +
                        "现在打开回收站看看吗？",
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(Path.Combine(Installer.TargetDir, "RecycleBin.exe")); } catch { }
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            _busy = false;
            _progress = 0;
            _status = "出错了：" + ex.Message;
            Invalidate();
            MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Pump() { Invalidate(); Update(); Application.DoEvents(); }
}
