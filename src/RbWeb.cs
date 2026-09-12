// RecycleBin.exe - a Recycle Bin for the Files-based setup.
//
// Architecture (option B):
//   The UI is HTML/CSS rendered by WebView2 inside OUR OWN window, so the
//   taskbar still shows the Recycle Bin icon. CSS gives real Windows 11
//   fidelity - Fluent type sizes, 40 px rows, 4 px radii, sub-pixel text
//   rendering - which hand-painted GDI+ controls never reached.
//
//   Only Microsoft.Web.WebView2.Core is referenced, not the WinForms wrapper:
//   that wrapper targets netstandard and needs facade assemblies .NET Framework
//   does not ship. The Core assembly is a plain .NET Framework build, and it can
//   host its controller straight into our window handle.
//
//   Data and file operations stay in C#: enumerating the shell's Recycle Bin,
//   parsing the $R (payload) + $I (metadata) pairs, and restore / delete /
//   empty. The two sides talk over chrome.webview.postMessage.
//
// Why not Explorer: this machine flashes the whole screen black for ~0.5 s when
// a WinUI (Files) window appears, and in Windows 11 the taskbar is
// per-application, so an Explorer window can never carry a Recycle Bin icon
// there (verified by pixel-diffing the taskbar).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try { SetProcessDPIAware(); }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Boot the WebView2 runtime NOW, in parallel with building the form. This is
        // the slowest single step (a cold runtime can take a second or two) and it
        // has nothing to do with the form, so overlapping them removes that time
        // from the user-visible path.
        System.Threading.Tasks.Task<CoreWebView2Environment> envTask = null;
        try
        {
            string dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecycleBinWeb");
            System.IO.Directory.CreateDirectory(dataDir);
            envTask = CoreWebView2Environment.CreateAsync(null, dataDir, null);
        }
        catch { }

        Application.Run(new RecycleBinWindow(envTask));
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

internal sealed class RecycleBinWindow : Form
{
    private const int SSF_BITBUCKET = 10;

    private CoreWebView2Controller _ctl;
    private readonly Timer _boundsSync = new Timer { Interval = 200 };
    private readonly List<Item> _items = new List<Item>();
    private readonly Dictionary<string, string> _thumbCache = new Dictionary<string, string>();
    private readonly System.Threading.Tasks.Task<CoreWebView2Environment> _envTask;
    private readonly Label _splash = new Label();

    private sealed class Item
    {
        public string Path;
        public string MetaPath;
        public string Original;
        public string Name;
        public string Type;
        public string Size;
        public long Bytes;
        public long DeletedTs;
        public long ModifiedTs;
        public string IconDataUrl;
        public string PreviewDataUrl;
    }

    public RecycleBinWindow(System.Threading.Tasks.Task<CoreWebView2Environment> envTask)
    {
        _envTask = envTask;

        Text = "回收站";
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(47, 49, 52);   // matches the page surface (#2F3134)
        AutoScaleMode = AutoScaleMode.None;
        Icon = BinIcon(true) ?? SystemIcons.Application;

        // Shown until the page has painted. Without it the WebView2 control is a
        // black rectangle for however long the runtime takes to start, which is
        // exactly what read as a long black screen.
        _splash.Dock = DockStyle.Fill;
        _splash.BackColor = Color.FromArgb(47, 49, 52);
        _splash.ForeColor = Color.FromArgb(154, 157, 161);
        _splash.TextAlign = ContentAlignment.MiddleCenter;
        _splash.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        _splash.Text = "正在打开回收站…";
        Controls.Add(_splash);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // dark title bar
        }
        catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncBounds();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // WinForms' DPI auto-scaling shrinks ClientSize on a HiDPI display (1100
        // asked for, ~894 delivered), which left the page laid out wider than the
        // window and clipped the right hand columns. Re-assert it after scaling.
        float k = 1f;
        using (var g = CreateGraphics()) k = g.DpiX / 96f;
        ClientSize = new Size((int)Math.Round(1100 * k), (int)Math.Round(700 * k));
        SyncBounds();

        // Start the WebView2 environment here rather than in OnShown: OnLoad runs
        // as soon as the handle exists, which shaves time off first paint.
        await InitWebView();
    }

    private async System.Threading.Tasks.Task InitWebView()
    {
        try
        {
            CoreWebView2Environment env = _envTask != null
                ? await _envTask
                : await CoreWebView2Environment.CreateAsync(null,
                    System.IO.Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "RecycleBinWeb"), null);

            _ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
            _ctl.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);

            // Hidden until the page has actually painted: a visible WebView2 before
            // that is just a black rectangle over the window.
            _ctl.IsVisible = false;
            try { _ctl.DefaultBackgroundColor = Color.FromArgb(47, 49, 52); } catch { }

            CoreWebView2Settings s = _ctl.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;    // Ctrl+wheel zoom, as Explorer does
            try { s.AreBrowserAcceleratorKeysEnabled = false; } catch { }

            _ctl.CoreWebView2.WebMessageReceived += OnWebMessage;
            _ctl.CoreWebView2.NavigationCompleted += delegate
            {
                // page is up: swap the splash for the real UI
                try { _ctl.IsVisible = true; } catch { }
                try { _splash.Visible = false; } catch { }
            };

            // Load the page by content, NOT over a virtual host name. Navigating to
            // https://<vhost>/index.html made the WebView walk the network stack
            // (proxy auto-discovery), which cost ~2 s before anything rendered.
            // The page is fully self-contained (inline CSS/JS, data: URLs for every
            // image), so it needs no URL at all.
            string page = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location), "ui", "index.html");

            if (File.Exists(page))
                _ctl.CoreWebView2.NavigateToString(File.ReadAllText(page, Encoding.UTF8));
            else
                _ctl.CoreWebView2.NavigateToString(
                    "<body style='background:#2f3134;color:#ccc;font:14px sans-serif'>缺少 ui\\index.html</body>");

            // The controller does not reliably follow WM_SIZE on its own, which
            // left the page laid out wider than the window and clipped the right
            // hand columns. Poll the client size and resync.
            _boundsSync.Tick += delegate { SyncBounds(); };
            _boundsSync.Start();
        }
        catch (Exception ex)
        {
            try { _splash.Text = "回收站打开失败：" + ex.Message; } catch { }
        }
    }

    private void SyncBounds()
    {
        if (_ctl == null) return;
        var want = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        try
        {
            if (_ctl.Bounds != want)
            {
                _ctl.Bounds = want;
                // the page does not always reflow when only the host window rect
                // changes, so nudge it
                if (_ctl.CoreWebView2 != null)
                    _ctl.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))");
            }
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SyncBounds();
    }

    // ------------------------------------------------------- web bridge

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd = null, how = null, mode = null, previewPath = null;
        string[] paths = new string[0];
        int delta = 0;

        try
        {
            var ser = new JavaScriptSerializer();
            var map = ser.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);

            if (map != null)
            {
                object v;
                if (map.TryGetValue("cmd", out v) && v != null) cmd = v.ToString();
                if (map.TryGetValue("how", out v) && v != null) how = v.ToString();
                if (map.TryGetValue("mode", out v) && v != null) mode = v.ToString();
                if (map.TryGetValue("path", out v) && v != null) previewPath = v.ToString();
                if (map.TryGetValue("delta", out v) && v != null) int.TryParse(v.ToString(), out delta);
                if (map.TryGetValue("paths", out v) && v is object[])
                    paths = Array.ConvertAll((object[])v, o => o == null ? string.Empty : o.ToString());
            }
        }
        catch { }

        switch (cmd)
        {
            case "ready":
            case "refresh":
                SendItems();
                break;
            case "restore":
                Restore(paths, false);
                break;
            case "delete":
                Delete(paths);
                break;
            case "empty":
                EmptyBin();
                break;
            case "restoreAll":
                RestoreAll();
                break;
            case "openWith":
                if (how == "files") OpenWithFiles(); else OpenWithExplorer();
                break;
            case "props":
                ShowProperties(paths);
                break;
            case "openLocation":
                OpenLocation(paths);
                break;
            case "copyPath":
                CopyOriginalPaths(paths);
                break;
            case "restoreTo":
                RestoreTo(paths, mode);
                break;
            case "copyTo":
                CopyTo(paths);
                break;
            case "zoom":
                Zoom(delta);
                break;
            case "preview":
                SendPreview(previewPath);
                break;
            case "debug":
                LogDebug(e.WebMessageAsJson);
                break;
        }
    }

    private void LogDebug(string json)
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss")
                + " window=" + ClientSize.Width + "x" + ClientSize.Height
                + " bounds=" + (_ctl != null ? _ctl.Bounds.Width + "x" + _ctl.Bounds.Height : "?")
                + " raster=" + (_ctl != null ? _ctl.RasterizationScale.ToString("0.00") : "?")
                + " " + json;
            File.AppendAllText(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location), "webview-debug.log"), line + Environment.NewLine);
        }
        catch { }
    }

    // Restore into a folder the user picks - the single most requested feature
    // Explorer's Recycle Bin lacks, since it can only restore to the old location.
    private void RestoreTo(string[] paths, string mode)
    {
        if (paths.Length == 0) return;
        string target = PickFolder("选择还原到的位置");
        if (string.IsNullOrEmpty(target)) return;

        int ok = 0, skipped = 0;
        var problems = new StringBuilder();

        foreach (string p in paths)
        {
            Item e = _items.Find(x => x.Path == p);
            if (e == null) continue;

            try
            {
                string name = System.IO.Path.GetFileName(e.Original);
                string dest = System.IO.Path.Combine(target, name);

                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    if (mode == "keepboth")
                    {
                        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
                        string ext = System.IO.Path.GetExtension(name);
                        int n = 2;
                        do { dest = System.IO.Path.Combine(target, stem + " (" + n + ")" + ext); n++; }
                        while (File.Exists(dest) || Directory.Exists(dest));
                    }
                    else if (mode == "skip") { skipped++; continue; }
                    else
                    {
                        if (Directory.Exists(dest)) Directory.Delete(dest, true);
                        else File.Delete(dest);
                    }
                }

                if (Directory.Exists(e.Path)) Directory.Move(e.Path, dest);
                else if (File.Exists(e.Path)) File.Move(e.Path, dest);
                else { skipped++; continue; }

                TryDelete(e.MetaPath);
                ok++;
            }
            catch (Exception ex)
            {
                problems.AppendLine(System.IO.Path.GetFileName(e.Original) + "：" + ex.Message);
            }
        }

        SendItems();

        NotifyRecycleBinChanged();
        if (problems.Length > 0)
            MessageBox.Show(this, problems.ToString(), "部分项目未能还原", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Copies the payload out without removing it from the bin - Explorer
    /// gives you no way to take a copy and keep the original in place.</summary>
    private void CopyTo(string[] paths)
    {
        if (paths.Length == 0) return;
        string target = PickFolder("选择复制到的位置");
        if (string.IsNullOrEmpty(target)) return;

        int ok = 0;
        var problems = new StringBuilder();

        foreach (string p in paths)
        {
            Item e = _items.Find(x => x.Path == p);
            if (e == null) continue;

            try
            {
                string name = System.IO.Path.GetFileName(e.Original);
                string dest = System.IO.Path.Combine(target, name);
                int n = 2;
                while (File.Exists(dest) || Directory.Exists(dest))
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(name);
                    string ext = System.IO.Path.GetExtension(name);
                    dest = System.IO.Path.Combine(target, stem + " (" + n + ")" + ext);
                    n++;
                }

                if (Directory.Exists(e.Path)) CopyDirectory(e.Path, dest);
                else File.Copy(e.Path, dest, true);
                ok++;
            }
            catch (Exception ex)
            {
                problems.AppendLine(System.IO.Path.GetFileName(e.Original) + "：" + ex.Message);
            }
        }

        SendItems();
        NotifyRecycleBinChanged();
        if (problems.Length > 0)
            MessageBox.Show(this, problems.ToString(), "部分项目未能复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDirectory(d, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(d)));
    }

    private string PickFolder(string title)
    {
        try
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            uint opts;
            dlg.GetOptions(out opts);                       // C# 5: no inline out declarations
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dlg.SetTitle(title);
            dlg.SetOkButtonLabel("选择此文件夹");
            if (dlg.Show(Handle) != 0) return null;

            IShellItem item;
            dlg.GetResult(out item);
            string path;
            item.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return path;
        }
        catch { return null; }
    }

    private void Zoom(int delta)
    {
        if (_ctl == null) return;
        try
        {
            double f = _ctl.ZoomFactor;
            f = delta == 0 ? 1.0 : f + delta * 0.1;
            _ctl.ZoomFactor = Math.Max(0.5, Math.Min(3.0, f));
        }
        catch { }
    }

    private void ShowProperties(string[] paths)
    {
        if (paths.Length == 0) return;
        try { SHObjectProperties(Handle, SHOP_FILEPATH, paths[0], null); }
        catch { }
    }

    private void OpenLocation(string[] paths)
    {
        if (paths.Length == 0) return;
        Item e = _items.Find(x => x.Path == paths[0]);
        if (e == null) return;

        string folder = System.IO.Path.GetDirectoryName(e.Original);
        try
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
        }
        catch { }
    }

    private void CopyOriginalPaths(string[] paths)
    {
        var sb = new StringBuilder();
        foreach (string p in paths)
        {
            Item e = _items.Find(x => x.Path == p);
            if (e != null) sb.AppendLine(e.Original);
        }
        try { if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd()); }
        catch { }
    }

    private void SendItems()
    {
        _items.Clear();
        _thumbCache.Clear();

        // self-heal on every load: leftovers from an earlier session would keep
        // Explorer's desktop icon showing "full" until something else changed
        SweepOrphanMetadata();

        try
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object bin = Invoke(shell, "NameSpace", SSF_BITBUCKET);
            object items = Invoke(bin, "Items");
            int count = Convert.ToInt32(Get(items, "Count"));

            for (int i = 0; i < count; i++)
            {
                object it = Invoke(items, "Item", i);
                Item e = ReadItem(Str(it, "Path"));
                if (e != null) _items.Add(e);
            }

            // Icons are NOT generated here. The system image list (comctl32) is not
            // thread-safe and GDI+ encoding has a global lock, so running them
            // concurrently made most icons fail at random. They are produced
            // serially on a dedicated STA thread and pushed as a second message,
            // which also gets the list on screen sooner.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取回收站失败：\n" + ex.Message, "回收站",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool isEmpty = _items.Count == 0;
        Icon icon = BinIcon(isEmpty);
        if (icon != null)
        {
            Icon old = Icon;
            Icon = icon;
            if (old != null && old != SystemIcons.Application) old.Dispose();
        }

        var payload = new Dictionary<string, object>();
        payload["type"] = "items";
        payload["emptyIcon"] = IconDataUrl(BinIconLarge(true)) ?? string.Empty;

        var list = new List<Dictionary<string, object>>();
        foreach (Item e in _items)
        {
            var d = new Dictionary<string, object>();
            d["path"] = e.Path;
            d["name"] = e.Name;
            d["original"] = System.IO.Path.GetDirectoryName(e.Original) ?? e.Original;
            d["deleted"] = e.DeletedTs > 0 ? new DateTime(e.DeletedTs).ToString("yyyy/M/d H:mm") : string.Empty;
            d["modified"] = e.ModifiedTs > 0 ? new DateTime(e.ModifiedTs).ToString("yyyy/M/d H:mm") : string.Empty;
            d["type"] = e.Type;
            d["isDir"] = e.Type == "文件夹";
            d["size"] = e.Size;
            d["bytes"] = e.Bytes;
            d["deletedTs"] = e.DeletedTs;
            d["modifiedTs"] = e.ModifiedTs;
            d["icon"] = e.IconDataUrl ?? string.Empty;
            d["preview"] = e.PreviewDataUrl ?? string.Empty;            list.Add(d);
        }
        payload["items"] = list;

        try
        {
            if (_ctl != null && _ctl.CoreWebView2 != null)
                _ctl.CoreWebView2.PostWebMessageAsJson(new JavaScriptSerializer().Serialize(payload));
        }
        catch { }

        SendIconsAsync();
    }

    /// <summary>
    /// Builds row icons one at a time on a dedicated STA thread and pushes them
    /// as a path -> data URL map. Serial on purpose: the shell's system image
    /// list is not thread-safe, and concurrent access made icons fail randomly.
    /// </summary>
    private void SendIconsAsync()
    {
        if (_items.Count == 0) return;

        var snapshot = new List<Item>(_items);

        var t = new System.Threading.Thread(() =>
        {
            var map = new Dictionary<string, object>();
            foreach (Item e in snapshot)
            {
                try
                {
                    string data = IconDataUrl(ShellIconJumbo(e.Path));
                    if (!string.IsNullOrEmpty(data)) map[e.Path] = data;
                }
                catch { }
            }
            if (map.Count == 0) return;

            var payload = new Dictionary<string, object>();
            payload["type"] = "icons";
            payload["map"] = map;
            string json = new JavaScriptSerializer().Serialize(payload);

            try
            {
                BeginInvoke((Action)delegate
                {
                    try { if (_ctl != null && _ctl.CoreWebView2 != null) _ctl.CoreWebView2.PostWebMessageAsJson(json); }
                    catch { }
                });
            }
            catch { }
        });

        t.IsBackground = true;
        try { t.SetApartmentState(System.Threading.ApartmentState.STA); } catch { }
        t.Start();
    }

    // ----------------------------------------------------------- reading

    private static Item ReadItem(string dataPath)
    {
        if (string.IsNullOrEmpty(dataPath)) return null;

        string meta = MetaPathFor(dataPath);
        if (meta == null || !File.Exists(meta)) return null;

        string original = null;
        long deletedTs = 0;

        try
        {
            byte[] b = File.ReadAllBytes(meta);
            if (b.Length >= 28)
            {
                int chars = BitConverter.ToInt32(b, 24);
                if (chars > 0 && 28 + chars * 2 <= b.Length)
                {
                    string s = Encoding.Unicode.GetString(b, 28, chars * 2);
                    int nul = s.IndexOf('\0');
                    if (nul >= 0) s = s.Substring(0, nul);
                    original = s.Trim();
                }
                deletedTs = DateTime.FromFileTime(BitConverter.ToInt64(b, 16)).Ticks;
            }
        }
        catch { }

        if (string.IsNullOrEmpty(original)) return null;

        var e = new Item
        {
            Path = dataPath,
            MetaPath = meta,
            Original = original,
            DeletedTs = deletedTs
        };

        try
        {
            string name = System.IO.Path.GetFileName(original);
            e.Name = string.IsNullOrEmpty(name) ? original : name;

            if (Directory.Exists(dataPath))
            {
                e.Type = "文件夹";
                e.ModifiedTs = Directory.GetLastWriteTime(dataPath).Ticks;
            }
            else if (File.Exists(dataPath))
            {
                var info = new FileInfo(dataPath);
                e.Bytes = info.Length;
                e.ModifiedTs = info.LastWriteTime.Ticks;
                string ext = System.IO.Path.GetExtension(original);
                e.Type = string.IsNullOrEmpty(ext) ? "文件" : ext.TrimStart('.').ToUpperInvariant() + " 文件";
                e.Size = HumanSize(info.Length);
            }
            else return null;
        }
        catch { return null; }

        return e;
    }

    private static string HumanSize(long bytes)
    {
        double v = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? bytes + " B" : v.ToString("0.0") + " " + u[i];
    }

    private static string MetaPathFor(string dataPath)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(dataPath);
            string file = System.IO.Path.GetFileName(dataPath);
            if (dir == null || file.Length < 3) return null;
            if (file[0] != '$' || (file[1] != 'R' && file[1] != 'r')) return null;
            return System.IO.Path.Combine(dir, "$I" + file.Substring(2));
        }
        catch { return null; }
    }

    // ----------------------------------------------------------- actions

    private void Restore(string[] paths, bool quiet)
    {
        if (paths.Length == 0) return;

        int ok = 0, skipped = 0;
        var problems = new StringBuilder();

        foreach (string p in paths)
        {
            Item e = _items.Find(x => x.Path == p);
            if (e == null) continue;

            try
            {
                string dest = e.Original;

                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    DialogResult answer = MessageBox.Show(this,
                        "目标位置已存在同名项目：\n" + dest + "\n\n要覆盖它吗？",
                        "还原", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (answer == DialogResult.Cancel) break;
                    if (answer == DialogResult.No) { skipped++; continue; }
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    else File.Delete(dest);
                }

                string folder = System.IO.Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                if (Directory.Exists(e.Path)) Directory.Move(e.Path, dest);
                else if (File.Exists(e.Path)) File.Move(e.Path, dest);
                else { skipped++; continue; }

                TryDelete(e.MetaPath);
                ok++;
            }
            catch (Exception ex)
            {
                problems.AppendLine(System.IO.Path.GetFileName(e.Original) + "：" + ex.Message);
            }
        }

        SendItems();

        NotifyRecycleBinChanged();
        if (problems.Length > 0)
            MessageBox.Show(this, problems.ToString(), "部分项目未能还原", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RestoreAll()
    {
        if (_items.Count == 0) return;

        if (MessageBox.Show(this, "要还原回收站中的全部 " + _items.Count + " 个项目吗？", "还原所有项目",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var all = new List<string>();
        foreach (Item e in _items) all.Add(e.Path);
        Restore(all.ToArray(), false);
    }

    private void Delete(string[] paths)
    {
        if (paths.Length == 0) return;

        string firstName = string.Empty;
        Item first = _items.Find(x => x.Path == paths[0]);
        if (first != null) firstName = System.IO.Path.GetFileName(first.Original);

        string question = paths.Length == 1
            ? "确实要永久删除“" + firstName + "”吗？"
            : "确实要永久删除这 " + paths.Length + " 个项目吗？";

        if (MessageBox.Show(this, question, "删除文件", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        // Deleting the last items with the shell (rather than behind its back)
        // keeps the desktop Recycle Bin icon in step. With items still left the
        // icon would not change either way, so the direct purge is fine there.
        bool emptying = paths.Length >= _items.Count;
        if (emptying)
        {
            int hr = -1;
            try
            {
                hr = SHEmptyRecycleBin(Handle, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }
            catch { }
            if (hr == 0) { SendItems(); NotifyRecycleBinChanged(); return; }
        }

        foreach (string p in paths)
        {
            Item e = _items.Find(x => x.Path == p);
            if (e != null) Purge(e);
        }

        SendItems();
        NotifyRecycleBinChanged();
    }

    private void EmptyBin()
    {
        if (_items.Count == 0) return;

        if (MessageBox.Show(this, "确实要永久删除这 " + _items.Count + " 个项目吗？", "删除多个项目",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        // Let the SHELL do the emptying. Deleting the payload behind its back
        // leaves the desktop Recycle Bin icon stuck on "full", and no change
        // notification refreshes it - only an operation the shell performs does.
        // This call is silent, takes ~100 ms, and needs an STA thread (we are one).
        int hr = -1;
        try
        {
            hr = SHEmptyRecycleBin(Handle, null,
                SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        }
        catch { }

        if (hr != 0)
        {
            // Falls back to the direct purge. Also the normal path when the bin is
            // already empty, where SHEmptyRecycleBin answers E_UNEXPECTED.
            foreach (Item e in _items) Purge(e);
        }

        // anything the shell left behind, plus a final icon notification
        SendItems();
        NotifyRecycleBinChanged();
    }

    private static bool Purge(Item e)
    {
        try
        {
            if (Directory.Exists(e.Path)) Directory.Delete(e.Path, true);
            else if (File.Exists(e.Path)) File.Delete(e.Path);

            // both halves must go: a leftover $I makes Explorer's desktop icon
            // keep showing "full" even though the bin is empty
            ForceDelete(e.MetaPath);
            return !File.Exists(e.Path) && !Directory.Exists(e.Path) && !File.Exists(e.MetaPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes an orphaned $I metadata file - one whose $R payload is already gone.
    /// Explorer decides the desktop Recycle Bin icon by scanning the folder, not
    /// via the shell namespace, so a single stray $I keeps that icon showing
    /// "full" forever. Run after every mutation so the state always self-heals.
    /// </summary>
    private static void SweepOrphanMetadata()
    {
        try
        {
            foreach (System.IO.DriveInfo d in System.IO.DriveInfo.GetDrives())
            {
                string bin;
                try { bin = System.IO.Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"); }
                catch { continue; }
                if (!System.IO.Directory.Exists(bin)) continue;

                string[] subs;
                try { subs = System.IO.Directory.GetDirectories(bin); }
                catch { continue; }

                foreach (string sub in subs)
                {
                    string[] metas;
                    try { metas = System.IO.Directory.GetFiles(sub, "$I*"); }
                    catch { continue; }        // other SIDs are not readable

                    foreach (string meta in metas)
                    {
                        try
                        {
                            string name = System.IO.Path.GetFileName(meta);
                            if (name.Length < 3) continue;
                            string payload = System.IO.Path.Combine(sub, "$R" + name.Substring(2));
                            if (File.Exists(payload) || Directory.Exists(payload)) continue;

                            ForceDelete(meta);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>Deletes a file and reports whether it really is gone (FileInfo
    /// caches existence, so re-check with the static API).</summary>
    private static bool ForceDelete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (!File.Exists(path)) return true;

            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch { }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        ForceDelete(path);
    }

    private void OpenWithFiles()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "files-stable:?folder=shell:RecycleBinFolder") { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenWithExplorer()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------- icons

    private static Icon BinIcon(bool empty)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon"))
            {
                if (key == null) return null;

                object raw = key.GetValue(empty ? "Empty" : "Full")
                          ?? key.GetValue(empty ? "Full" : "Empty")
                          ?? key.GetValue(string.Empty);

                string spec = raw as string;
                if (string.IsNullOrEmpty(spec)) return null;

                int comma = spec.LastIndexOf(',');
                string file = Environment.ExpandEnvironmentVariables(comma > 0 ? spec.Substring(0, comma) : spec);
                int index = 0;
                if (comma > 0) int.TryParse(spec.Substring(comma + 1), out index);

                // multi-size, so the 16 px title bar gets a real 16 px frame
                Icon multi = BuildMultiSizeIcon(file, index);
                if (multi != null) return multi;

                IntPtr large, small;
                if (SHDefExtractIcon(file, index, 0, out large, out small, 0) != 0) return null;

                IntPtr h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) return null;

                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    if (small != IntPtr.Zero && small != large) DestroyIcon(small);
                }
            }
        }
        catch { return null; }
    }

    /// <summary>Big single-size version (256 px) used for the empty-state picture,
    /// where a multi-frame icon's first frame (16 px) would look tiny.</summary>
    private static Icon BinIconLarge(bool empty)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon"))
            {
                if (key == null) return null;

                object raw = key.GetValue(empty ? "Empty" : "Full")
                          ?? key.GetValue(empty ? "Full" : "Empty")
                          ?? key.GetValue(string.Empty);

                string spec = raw as string;
                if (string.IsNullOrEmpty(spec)) return null;

                int comma = spec.LastIndexOf(',');
                string file = Environment.ExpandEnvironmentVariables(comma > 0 ? spec.Substring(0, comma) : spec);
                int index = 0;
                if (comma > 0) int.TryParse(spec.Substring(comma + 1), out index);

                IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
                if (SHDefExtractIcon(file, index, 0, out large, out small, 256 | (256 << 16)) != 0) return null;

                IntPtr h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) return null;

                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    if (small != IntPtr.Zero && small != large) DestroyIcon(small);
                }
            }
        }
        catch { return null; }
    }

    /// <summary>Assembles a multi-frame .ico (PNG frames) from the shell icon.</summary>
    private static Icon BuildMultiSizeIcon(string file, int index)
    {
        int[] sizes = { 16, 32, 48, 256 };
        var frames = new List<byte[]>();
        var frameSizes = new List<int>();

        foreach (int s in sizes)
        {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                uint packed = (uint)(s | (s << 16));   // low word large, high word small
                if (SHDefExtractIcon(file, index, 0, out large, out small, packed) != 0) continue;

                IntPtr h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) continue;

                using (Icon one = (Icon)Icon.FromHandle(h).Clone())
                using (var bmp = one.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                    frameSizes.Add(bmp.Width);
                }
            }
            catch { }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero && small != large) DestroyIcon(small);
            }
        }

        if (frames.Count == 0) return null;

        // Add the sizes that DPI scaling actually asks for. At 125% the 16 px
        // title-bar icon is stretched to 20 physical px; giving Windows an exact
        // 20 px frame avoids that non-integer resample. 24/40 cover 150%/200%.
        try
        {
            int srcIdx = frameSizes.IndexOf(256);
            if (srcIdx < 0) srcIdx = frames.Count - 1;

            using (var srcMs = new MemoryStream(frames[srcIdx]))
            using (var src = new Bitmap(srcMs))
            {
                int[] extra = { 20, 24, 40, 64 };
                foreach (int s in extra)
                {
                    if (frameSizes.Contains(s)) continue;
                    using (var bmp = new Bitmap(s, s))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.Clear(Color.Transparent);
                            g.DrawImage(src, new Rectangle(0, 0, s, s));
                        }
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            frames.Add(ms.ToArray());
                            frameSizes.Add(s);
                        }
                    }
                }
            }
        }
        catch { }

        if (frames.Count == 0) return null;

        try
        {
            using (var ico = new MemoryStream())
            using (var w = new BinaryWriter(ico))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type: icon
                w.Write((ushort)frames.Count);   // frame count

                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    int s = frameSizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));   // width  (0 means 256)
                    w.Write((byte)(s >= 256 ? 0 : s));   // height
                    w.Write((byte)0);                     // palette count
                    w.Write((byte)0);                     // reserved
                    w.Write((ushort)1);                   // colour planes
                    w.Write((ushort)32);                  // bits per pixel
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);

                ico.Position = 0;
                return new Icon(ico);
            }
        }
        catch { return null; }
    }

    /// <summary>The 32 px icon upscaled is what looked soft in the details pane, so
    /// pull the shell's 256 px (jumbo) image instead and let the UI scale down.</summary>
    private static Icon ShellIconJumbo(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
                SHGFI_SYSICONINDEX | SHGFI_SMALLICON);
            if (res == IntPtr.Zero) return null;

            IntPtr himl;
            Guid iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out himl) == 0 && himl != IntPtr.Zero)
            {
                IntPtr h = ImageList_GetIcon(himl, info.iIcon, ILD_TRANSPARENT);
                if (h != IntPtr.Zero)
                {
                    try { return (Icon)Icon.FromHandle(h).Clone(); }
                    finally { DestroyIcon(h); }
                }
            }
        }
        catch { }

        return ShellIcon(path);   // fall back to the 32 px image
    }

    private static Icon ShellIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var info = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
        finally { DestroyIcon(info.hIcon); }
    }

    private void SendPreview(string path)
    {
        string data = null;

        if (!string.IsNullOrEmpty(path) && IsImage(path))
        {
            if (!_thumbCache.TryGetValue(path, out data))
            {
                data = MakeThumbnail(path, 256);
                if (data != null) _thumbCache[path] = data;   // cache hits keep it instant
            }
        }

        var payload = new Dictionary<string, object>();
        payload["type"] = "preview";
        payload["path"] = path;
        payload["data"] = data ?? string.Empty;
        try { _ctl.CoreWebView2.PostWebMessageAsJson(new JavaScriptSerializer().Serialize(payload)); }
        catch { }
    }

    /// <summary>
    /// The shell's own thumbnail provider: works for images, and for videos it
    /// extracts a poster frame, so "what is this?" is answerable without
    /// restoring. Generated on demand - building thumbnails for every row up
    /// front would stall the list.
    /// </summary>
    private static readonly string[] ImageExt = {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".webp",
        ".ico", ".tif", ".tiff", ".heic", ".avif"
    };

    private static bool IsImage(string path)
    {
        try { return Array.IndexOf(ImageExt, System.IO.Path.GetExtension(path).ToLowerInvariant()) >= 0; }
        catch { return false; }
    }

    private static string MakeThumbnail(string path, int size)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            IShellItemImageFactory factory;
            Guid iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;

            var sz = new SIZE();
            sz.cx = size;
            sz.cy = size;

            IntPtr hbm;
            if (factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;

            try
            {
                using (var src = Image.FromHbitmap(hbm))
                using (var outBmp = new Bitmap(src.Width, src.Height))
                {
                    // keep the source aspect ratio; padding it into a square with a
                    // dark fill drew an ugly black frame around the picture
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
                    }
                    using (var ms = new MemoryStream())
                    {
                        outBmp.Save(ms, ImageFormat.Png);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    private static string ImagePreview(string path)
    {
        try
        {
            const int max = 220;
            using (var src = Image.FromFile(path))
            {
                double k = Math.Min((double)max / src.Width, (double)max / src.Height);
                int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));

                using (var sq = new Bitmap(max, max))
                {
                    using (var g = Graphics.FromImage(sq))
                    {
                        g.Clear(Color.FromArgb(28, 28, 28));
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, (max - w) / 2, (max - h) / 2, w, h);
                    }
                    using (var ms = new MemoryStream())
                    {
                        sq.Save(ms, ImageFormat.Png);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }
        catch { return null; }
    }

    private static string IconDataUrl(Icon icon)
    {
        if (icon == null) return null;
        try
        {
            // 128 px so the details pane renders it crisp; the list scales it down
            const int size = 128;
            using (icon)
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(icon.ToBitmap(), new Rectangle(0, 0, size, size));
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch { return null; }
    }

    // ---------------------------------------------------------- plumbing

    /// <summary>
    /// The bin is modified by deleting the $R/$I files directly (the shell's own
    /// verbs block on an invisible dialog), so Explorer has no idea anything
    /// changed and its desktop Recycle Bin icon keeps showing "full". Tell the
    /// shell the bit-bucket folder changed so the desktop icon re-evaluates.
    /// </summary>
    private static void NotifyRecycleBinChanged()
    {
        // clean up any metadata whose payload is already gone, otherwise the
        // desktop icon stays "full" no matter what we notify
        SweepOrphanMetadata();

        try
        {
            IntPtr pidl;
            if (SHGetFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, IntPtr.Zero, 0, out pidl) == 0
                && pidl != IntPtr.Zero)
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST, pidl, IntPtr.Zero);
                CoTaskMemFree(pidl);
            }
        }
        catch { }

        // belt and braces: also poke each volume's $Recycle.Bin folder
        try
        {
            foreach (System.IO.DriveInfo d in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    string p = System.IO.Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                    if (System.IO.Directory.Exists(p))
                        SHChangeNotifyPath(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero);
                }
                catch { }
            }
        }
        catch { }
    }

    private const int CSIDL_BITBUCKET = 0x000a;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;

    // SHEmptyRecycleBin: the only reliable way to make the desktop icon follow.
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetFolderLocation(IntPtr hwndOwner, int nFolder, IntPtr hToken,
        uint dwReserved, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotifyPath(int wEventId, uint uFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string dwItem1, IntPtr dwItem2);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static object Invoke(object target, string method, params object[] args)
    {
        return target.GetType().InvokeMember(method,
            System.Reflection.BindingFlags.InvokeMethod, null, target, args);
    }

    private static object Get(object target, string property)
    {
        return target.GetType().InvokeMember(property,
            System.Reflection.BindingFlags.GetProperty, null, target, null);
    }

    private static string Str(object target, string property)
    {
        try
        {
            object v = Get(target, property);
            return v == null ? string.Empty : v.ToString();
        }
        catch { return string.Empty; }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ---- modern folder picker (IFileOpenDialog with FOS_PICKFOLDERS) --------
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    private const uint SHOP_FILEPATH = 0x00000002;

    // jumbo (256 px) system image list
    private const int SHIL_JUMBO = 4;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint ILD_TRANSPARENT = 0x00000001;
    private static readonly Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    // ---- shell thumbnail provider (images AND video poster frames) ----------
    private static readonly Guid IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string pszObjectName, string pszPropertyPage);
}
