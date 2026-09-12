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
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    internal const string AppName = "Modern Recycle Bin";
    internal const string Version = "1.0.0";
    internal const string Clsid = "{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>True in --silent / /S mode: no dialogs at all.</summary>
    internal static bool Silent;

    [STAThread]
    private static void Main(string[] args)
    {
        bool uninstall = Has(args, "--uninstall");
        bool silent = Has(args, "--silent") || Has(args, "/S");
        bool noTakeover = Has(args, "--no-takeover");
        bool noShortcut = Has(args, "--no-shortcut");
        Silent = silent;

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

internal sealed class SetupForm : Form
{
    private readonly bool _uninstall;
    private readonly CheckBox _desktop = new CheckBox();
    private readonly CheckBox _takeover = new CheckBox();
    private readonly Button _go = new Button();
    private readonly Label _status = new Label();
    private readonly ProgressBar _bar = new ProgressBar();

    internal SetupForm(bool uninstall)
    {
        _uninstall = uninstall;

        Text = Program.AppName + " " + Program.Version;
        ClientSize = new Size(460, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(47, 49, 52);
        ForeColor = Color.FromArgb(236, 239, 242);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var title = new Label();
        title.Text = uninstall ? "卸载 Modern Recycle Bin" : "安装 Modern Recycle Bin";
        title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(24, 22);
        Controls.Add(title);

        var sub = new Label();
        sub.Text = uninstall
            ? "将移除程序文件、桌面快捷方式，并把桌面回收站恢复为系统默认。"
            : "一个更现代、更快、更像 Windows 11 的回收站。" + Environment.NewLine +
              "支持图片预览、还原到任意位置、按类型筛选、深色主题。";
        sub.AutoSize = true;
        sub.ForeColor = Color.FromArgb(160, 165, 172);
        sub.Location = new Point(26, 62);
        Controls.Add(sub);

        _desktop.Text = "创建桌面快捷方式";
        _desktop.Checked = true;
        _desktop.Location = new Point(28, 118);
        _desktop.AutoSize = true;
        _desktop.Visible = !uninstall;
        Controls.Add(_desktop);

        _takeover.Text = "让桌面上的「回收站」用它打开（可随时卸载还原）";
        _takeover.Checked = true;
        _takeover.Location = new Point(28, 148);
        _takeover.AutoSize = true;
        _takeover.Visible = !uninstall;
        Controls.Add(_takeover);

        _status.Text = uninstall ? "点击下方按钮开始卸载。" : "点击下方按钮开始安装。";
        _status.ForeColor = Color.FromArgb(154, 157, 161);
        _status.AutoSize = true;
        _status.Location = new Point(28, 192);
        Controls.Add(_status);

        _bar.Location = new Point(28, 216);
        _bar.Size = new Size(404, 6);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 0;
        Controls.Add(_bar);

        _go.Text = uninstall ? "卸载" : "安装";
        _go.Size = new Size(120, 36);
        _go.Location = new Point(312, 244);
        _go.FlatStyle = FlatStyle.Flat;
        _go.FlatAppearance.BorderSize = 0;
        _go.BackColor = Color.FromArgb(255, 138, 61);
        _go.ForeColor = Color.White;
        _go.Click += OnGo;
        Controls.Add(_go);
    }

    private void OnGo(object sender, EventArgs e)
    {
        _go.Enabled = false;
        _bar.Value = 30;
        Application.DoEvents();

        try
        {
            if (_uninstall)
            {
                Installer.Uninstall(delegate(string m) { _status.Text = m; Application.DoEvents(); });
                _bar.Value = 100;
                Application.DoEvents();
                Close();
            }
            else
            {
                Installer.Install(Installer.TargetDir, _desktop.Checked, _takeover.Checked,
                    delegate(string m) { _status.Text = m; Application.DoEvents(); });
                _bar.Value = 100;
                Application.DoEvents();

                string dir = Installer.TargetDir;
                if (MessageBox.Show(this,
                        "安装完成！" + Environment.NewLine + Environment.NewLine +
                        "程序位置：" + dir + Environment.NewLine +
                        "以后想卸载，运行该目录下的 Uninstall.cmd 即可。" + Environment.NewLine + Environment.NewLine +
                        "现在打开回收站看看吗？",
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(Path.Combine(dir, "RecycleBin.exe")); } catch { }
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            _go.Enabled = true;
            _bar.Value = 0;
            _status.Text = "出错了：" + ex.Message;
            MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
