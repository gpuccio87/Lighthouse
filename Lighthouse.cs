// Lighthouse - a lightweight Windows tray manager for rclone mounts and other startup programs.
// It starts them, watches them, restarts them when they crash and lets you stop/restart them from the menu.
// Build with build.bat: it uses the C# compiler that ships with Windows, no external dependencies.
// Usage: Lighthouse.exe [path\to\file.ini]   (default: Lighthouse.ini next to the exe)
//
// This file: entry point, tray icon and menu, process supervision, health checks, run-at-login.
// Import.cs: first-run setup and import of existing Windows startup entries.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Lighthouse
{
    // State of a managed entry; each one has its own tray color.
    // External: an rclone drive letter is mounted, but by a process Lighthouse did not start.
    // Warning: running, but something is wrong (repeated crashes, hung window, rclone files in error...).
    enum State { Stopped, Starting, Running, Stopping, External, Error, Warning }

    // One managed entry, i.e. one [section] of the ini. Despite the name it is either an rclone mount
    // (no exe= key) or a generic program (exe= key set).
    class Mount
    {
        public string Name, Args, Drive;
        public string Exe, WorkDir;    // generic programs only (Exe == null means rclone mount)
        public string Origin, OriginArgs, OriginWorkDir;   // original command, when exe/script are copies in programs\
        public int Port;               // local rclone rc port (rclone mounts only)
        public bool IsRclone { get { return Exe == null; } }
        public string Title { get { return Drive.Length > 0 ? Name + " (" + Drive + ":)" : Name; } }
        public bool AutoStart;
        public Process Proc;           // the process we manage (started or adopted by us)
        public bool Wanted;            // the user wants this entry running
        public bool Busy;              // a stop is in progress
        public State State = State.Stopped;
        public string Detail = "fermo";
        public DateTime StartedAt, NextRetry = DateTime.MaxValue;
        public int Failures;           // consecutive failures, drives the restart back-off
        // signals used for the Warning state
        public List<DateTime> Crashes = new List<DateTime>();   // recent unexpected exits
        public int HungCount, RcFails;
        public DateTime NextRcCheck, PendingSince = DateTime.MinValue;
        public long LastBytes = -1;
        public string RcWarning, Acked;   // Acked: warning the user already marked as seen
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // command-line helpers that run without the tray:
            // --make-icon is used by build.bat to generate the exe icon; --scan and --import-test are diagnostics
            if (args.Length == 2 && args[0] == "--make-icon") { Art.WriteIco(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--scan") { TrayApp.ScanToFile(args[1], args.Length > 2 ? args[2] : null); return; }
            if (args.Length >= 2 && args[0] == "--import-test") { TrayApp.ImportTest(Path.GetFullPath(args[1]), args.Length > 2 ? args[2] : null); return; }
            bool created;
            using (new Mutex(true, "Lighthouse.SingleInstance", out created))
            {
                if (!created) return;   // only one instance per user session
                // Task Scheduler starts tasks at below-normal priority, and child processes (rclone) would inherit it
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { }
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    MessageBox.Show("Errore imprevisto:\n" + e.ExceptionObject, "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show("Errore imprevisto:\n" + e.Exception, "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args.Length > 0 ? Path.GetFullPath(args[0]) : null));
            }
        }
    }

    // The lighthouse icon, drawn in code on a 32x32 grid and scaled to the requested size,
    // so the exe needs no image resources and every state color is generated on the fly.
    static class Art
    {
        public static Bitmap Lighthouse(int sz, Color c, Color fg)
        {
            var b = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(b))
            using (var br = new SolidBrush(c))
            using (var fgb = new SolidBrush(fg))
            using (var pen = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (sz < 24)
                {
                    // simplified, pixel-aligned version on a 16x16 grid: at 16-20 px the thin beams turn into a blur
                    g.ScaleTransform(sz / 16f, sz / 16f);
                    using (var beam = new SolidBrush(Color.FromArgb(150, c)))
                    {
                        g.FillPolygon(beam, new[] { new PointF(6f, 4f), new PointF(0f, 1.5f), new PointF(0f, 6.5f) });
                        g.FillPolygon(beam, new[] { new PointF(10f, 4f), new PointF(16f, 1.5f), new PointF(16f, 6.5f) });
                    }
                    g.FillPolygon(br, new[] { new PointF(6f, 2.5f), new PointF(8f, 0.5f), new PointF(10f, 2.5f) });
                    g.FillRectangle(br, 6f, 2.5f, 4f, 3f);
                    g.FillRectangle(fgb, 7f, 3.5f, 2f, 1f);
                    g.FillPolygon(br, new[] { new PointF(6f, 6f), new PointF(10f, 6f), new PointF(11f, 14f), new PointF(5f, 14f) });
                    g.FillRectangle(fgb, 5f, 9f, 6f, 1.5f);
                    g.FillRectangle(br, 4f, 14f, 8f, 2f);
                    return b;
                }
                g.ScaleTransform(sz / 32f, sz / 32f);
                // light beams
                g.DrawLine(pen, 11f, 8.5f, 3.5f, 5f);
                g.DrawLine(pen, 11f, 8.5f, 3.5f, 12f);
                g.DrawLine(pen, 21f, 8.5f, 28.5f, 5f);
                g.DrawLine(pen, 21f, 8.5f, 28.5f, 12f);
                // roof, lantern room with window, tower with stripe, base
                g.FillPolygon(br, new[] { new PointF(12f, 6f), new PointF(16f, 2.5f), new PointF(20f, 6f) });
                g.FillRectangle(br, 13f, 6.5f, 6f, 4f);
                g.FillRectangle(fgb, 14.6f, 7.6f, 2.8f, 1.8f);
                g.FillPolygon(br, new[] { new PointF(12.6f, 12f), new PointF(19.4f, 12f), new PointF(21.2f, 27f), new PointF(10.8f, 27f) });
                g.FillRectangle(fgb, 12f, 17f, 8f, 3f);
                g.FillRectangle(br, 8.5f, 27f, 15f, 3f);
            }
            return b;
        }

        // Writes a multi-size .ico (PNG-compressed entries) in the "running" green.
        // Layout: ICONDIR header, one 16-byte ICONDIRENTRY per size, then the PNG payloads.
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Lighthouse(s, Color.FromArgb(40, 170, 70), Color.White))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }

    // The tray application. All mutable state of the entries is guarded by `sync`; slow work
    // (rc calls, WMI queries, waiting for a process to exit) runs on thread-pool threads outside
    // the lock, and UI updates are marshalled to the UI thread through the hidden `ui` control.
    partial class TrayApp : ApplicationContext
    {
        const int MaxRetries = 5;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string RcUser = "mounttray";   // historical name: mounts that are already running use it for rc auth

        // indexed by State
        static readonly Color[] Colors = {
            Color.FromArgb(150, 150, 150),  // Stopped (grey)
            Color.FromArgb(120, 195, 240),  // Starting (light blue)
            Color.FromArgb(40, 170, 70),    // Running (green)
            Color.FromArgb(120, 195, 240),  // Stopping (light blue)
            Color.FromArgb(40, 90, 200),    // External (blue)
            Color.FromArgb(215, 50, 50),    // Error (red)
            Color.FromArgb(235, 180, 0),    // Warning (yellow)
        };
        static readonly string[] Labels = { "fermo", "avvio...", "attivo", "arresto...", "esterno", "errore", "avviso" };
        // the tray icon shows the color of the most severe state among all entries
        static readonly int[] Severity = { 0, 3, 1, 3, 2, 5, 4 };
        static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(30), UploadStall = TimeSpan.FromMinutes(15);

        readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
        readonly string iniPath, logDir;
        string rclonePath, rcPass, rcloneConfig;
        bool autoRestart = true;
        readonly List<Mount> mounts = new List<Mount>();
        readonly object sync = new object();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Control ui = new Control();          // hidden control, used only to invoke on the UI thread
        readonly System.Threading.Timer timer;        // runs Check() every 5 s
        int checking;                                 // re-entrancy guard for Check()
        Icon[] icons;
        Bitmap[] dots;

        public TrayApp(string ini)
        {
            iniPath = ini ?? Path.Combine(dir, "Lighthouse.ini");
            logDir = Path.Combine(Path.GetDirectoryName(iniPath), "logs");
            try { Directory.CreateDirectory(logDir); } catch { }
            ui.CreateControl();
            BuildIcons();

            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray = new NotifyIcon { Icon = icons[0], Text = "Lighthouse", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) =>
            {
                // open the menu on left click too; NotifyIcon only exposes this through a private method
                if (e.Button == MouseButtons.Left)
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tray, null);
            };

            // first run on this PC: create the configuration and offer to import the startup programs
            bool firstRun = !File.Exists(iniPath);
            try { if (firstRun) File.WriteAllText(iniPath, IniTemplate.Replace("{rclone}", FindRclone())); }
            catch (Exception ex)
            {
                MessageBox.Show("Non riesco a creare la configurazione:\n" + iniPath + "\n\n" + ex.Message +
                    "\n\nCopia Lighthouse.exe in una cartella scrivibile, per esempio C:\\Tools\\Lighthouse.",
                    "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
                tray.Visible = false;
                Environment.Exit(1);
            }
            lock (sync) LoadConfig();
            timer = new System.Threading.Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
            // adopting/starting the entries can take a while: keep it off the UI thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Initialize();
                SyncAutostart();
                if (firstRun) ui.BeginInvoke(new Action(FirstRun));
            });
        }

        // ---------- configuration ----------

        // Minimal ini reader: [section] -> key -> value, case-insensitive; ';' and '#' start a comment.
        static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var r = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> cur = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                if (t[0] == '[' && t.EndsWith("]"))
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    r[t.Substring(1, t.Length - 2).Trim()] = cur;
                    continue;
                }
                int eq = t.IndexOf('=');
                if (eq > 0 && cur != null) cur[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
            return r;
        }

        void SetIniValue(string section, string key, string value) { SetIniValue(iniPath, section, key, value); }

        // Sets one key in place, keeping the rest of the file (comments included) untouched. A missing key is
        // added after the last line of its section; a missing section is appended at the end of the file.
        static void SetIniValue(string iniPath, string section, string key, string value)
        {
            var lines = new List<string>(File.ReadAllLines(iniPath));
            string cur = null;
            int insertAt = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    cur = t.Substring(1, t.Length - 2).Trim();
                    if (string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)) insertAt = i + 1;
                    continue;
                }
                if (!string.Equals(cur, section, StringComparison.OrdinalIgnoreCase) || t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                insertAt = i + 1;
                int eq = t.IndexOf('=');
                if (eq > 0 && string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    File.WriteAllLines(iniPath, lines);
                    return;
                }
            }
            if (insertAt < 0) { lines.Add(""); lines.Add("[" + section + "]"); insertAt = lines.Count; }
            lines.Insert(insertAt, key + "=" + value);
            File.WriteAllLines(iniPath, lines);
        }

        // value of a key, or def when it is missing or empty
        static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        // Reads the ini: updates the existing entries in place (keeping their running process), adds the
        // new ones and returns the ones that were removed from the file. Call with the lock held.
        List<Mount> LoadConfig()
        {
            var ini = ReadIni(iniPath);
            Dictionary<string, string> g;
            if (!ini.TryGetValue("general", out g)) g = new Dictionary<string, string>();
            rclonePath = Get(g, "rclone", "rclone.exe");
            // rclone uses the rclone.conf next to its own exe, if present: a copy in programs\ would no
            // longer find it, so the path is always passed explicitly
            rcloneConfig = Environment.ExpandEnvironmentVariables(Get(g, "rcloneconfig", ""));
            autoRestart = Get(g, "autorestart", "1") != "0";
            // password for the rclone remote-control API: random, generated once and saved in the ini
            rcPass = Get(g, "rcpass", "");
            if (rcPass.Length == 0)
            {
                var b = new byte[18];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
                rcPass = Convert.ToBase64String(b).Replace('+', 'x').Replace('/', 'y');
                SetIniValue("general", "rcpass", rcPass);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 0;
            foreach (var kv in ini)
            {
                if (kv.Key.Equals("general", StringComparison.OrdinalIgnoreCase)) continue;
                idx++;
                string args = Get(kv.Value, "args", "");
                string exe = Get(kv.Value, "exe", null);
                if (args.Length == 0 && exe == null) continue;
                var m = mounts.FirstOrDefault(x => x.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (m == null) { m = new Mount { Name = kv.Key }; mounts.Add(m); }
                m.Args = args;
                m.Exe = exe;
                m.WorkDir = Get(kv.Value, "workdir", exe != null ? Path.GetDirectoryName(exe) : null);
                m.Origin = Get(kv.Value, "origin", null);
                m.OriginArgs = Get(kv.Value, "originargs", "");
                m.OriginWorkDir = Get(kv.Value, "originworkdir", m.Origin != null ? Path.GetDirectoryName(m.Origin) : null);
                if (m.IsRclone)
                {
                    // drive letter from "mount remote: X:" unless given explicitly; the port of a running
                    // mount is never changed, or we would lose control of it
                    var dm = Regex.Match(args, @"\bmount\s+\S+\s+""?([A-Za-z]):");
                    m.Drive = Get(kv.Value, "drive", dm.Success ? dm.Groups[1].Value : "").TrimEnd(':').ToUpperInvariant();
                    if (m.Proc == null) m.Port = int.Parse(Get(kv.Value, "port", (5580 + idx).ToString()));
                }
                else { m.Drive = ""; m.Port = 0; }
                m.AutoStart = Get(kv.Value, "autostart", "1") != "0";
                seen.Add(m.Name);
            }
            var removed = mounts.Where(x => !seen.Contains(x.Name)).ToList();
            foreach (var m in removed) mounts.Remove(m);
            return removed;
        }

        // ---------- rclone ----------

        // Calls the rclone remote-control API of a mount (POST http://127.0.0.1:<port>/<cmd>, basic auth).
        // Returns the JSON response, or null if rclone does not answer within 3 s.
        string Rc(Mount m, string cmd)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + m.Port + "/" + cmd);
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = 3000;
                req.ContentType = "application/json";
                req.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(RcUser + ":" + rcPass));
                var body = Encoding.UTF8.GetBytes("{}");
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }

        // files still waiting to be uploaded from the VFS cache (in progress + queued)
        int PendingUploads(Mount m)
        {
            string j = Rc(m, "vfs/stats");
            if (j == null) return 0;
            int n = 0;
            foreach (Match x in Regex.Matches(j, "\"uploads(?:InProgress|Queued)\"\\s*:\\s*(\\d+)")) n += int.Parse(x.Groups[1].Value);
            return n;
        }

        // Process helpers that never throw: a process we cannot query is treated as gone.
        static bool Exited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        static bool WaitExit(Process p, int ms)
        {
            try { return p.WaitForExit(ms); } catch { return true; }
        }

        static bool DriveExists(string d)
        {
            return d.Length > 0 && Directory.Exists(d + ":\\");
        }

        string LogPath(Mount m) { return Path.Combine(logDir, m.Name + ".log"); }

        // Starts an entry. Generic programs are started as configured; rclone gets --rc (with credentials
        // passed through environment variables, so they never appear on the command line), its log file
        // and, if known, the --config path. Call with the lock held.
        void Start(Mount m)
        {
            if (m.Proc != null && !Exited(m.Proc)) return;
            m.Wanted = true;
            m.NextRetry = DateTime.MaxValue;
            if (!m.IsRclone)
            {
                var gpsi = new ProcessStartInfo(m.Exe, m.Args) { UseShellExecute = false };
                if (m.WorkDir != null) gpsi.WorkingDirectory = m.WorkDir;
                try
                {
                    m.Proc = Process.Start(gpsi);
                    m.StartedAt = DateTime.Now;
                    Set(m, State.Running, "attivo");
                }
                catch (Exception ex)
                {
                    m.Proc = null;
                    Fail(m, "impossibile avviarlo: " + ex.Message);
                }
                return;
            }
            string log = LogPath(m);
            try
            {
                // simple log rotation: above 10 MB the log becomes .old and a new one is started
                if (File.Exists(log) && new FileInfo(log).Length > 10 << 20)
                {
                    File.Delete(log + ".old");
                    File.Move(log, log + ".old");
                }
            }
            catch { }
            var psi = new ProcessStartInfo(rclonePath,
                m.Args + " --rc --rc-addr=127.0.0.1:" + m.Port + " --log-file=\"" + log + "\"" +
                (rcloneConfig.Length > 0 && !Regex.IsMatch(m.Args, @"(^|\s)--config\b") ? " --config=\"" + rcloneConfig + "\"" : ""));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.IsPathRooted(rclonePath) ? Path.GetDirectoryName(rclonePath) : dir;
            psi.EnvironmentVariables["RCLONE_RC_USER"] = RcUser;
            psi.EnvironmentVariables["RCLONE_RC_PASS"] = rcPass;
            try
            {
                m.Proc = Process.Start(psi);
                m.StartedAt = DateTime.Now;
                Set(m, State.Starting, "avvio in corso");
            }
            catch (Exception ex)
            {
                m.Proc = null;
                Fail(m, "impossibile avviare rclone: " + ex.Message);
            }
        }

        // Stops a managed entry, or kills the external process that holds its drive letter.
        // Blocking (it waits for the process to exit): call it from a background thread, without the lock.
        void Stop(Mount m)
        {
            Process p;
            bool external;
            lock (sync)
            {
                m.Wanted = false;
                m.NextRetry = DateTime.MaxValue;
                p = m.Proc;
                external = p == null && m.State == State.External;
                if (p == null && !external) { Set(m, State.Stopped, "fermo"); return; }
                m.Busy = true;
                Set(m, State.Stopping, "arresto in corso");
            }
            RefreshUi();
            try
            {
                if (p != null && !m.IsRclone)
                {
                    // graceful close first (e.g. AutoHotkey runs its OnExit handler), then forced
                    CloseWindows(p.Id);
                    if (!WaitExit(p, 5000)) { try { p.Kill(); } catch { } WaitExit(p, 5000); }
                }
                else if (p != null)
                {
                    // rclone: clean shutdown through the rc API (unmounts and flushes), kill after 15 s
                    Rc(m, "core/quit");
                    if (!WaitExit(p, 15000)) { try { p.Kill(); } catch { } WaitExit(p, 5000); }
                }
                else
                {
                    foreach (int pid in FindExternal(m))
                    {
                        try { var ep = Process.GetProcessById(pid); ep.Kill(); WaitExit(ep, 5000); } catch { }
                    }
                }
                // wait (up to 5 s) for Windows to release the drive letter
                for (int i = 0; i < 20 && DriveExists(m.Drive); i++) Thread.Sleep(250);
            }
            finally
            {
                lock (sync)
                {
                    m.Proc = null;
                    m.Busy = false;
                    m.Failures = 0;
                    Set(m, State.Stopped, "fermo");
                }
            }
        }

        // PIDs of already running processes that match the entry (found through WMI): rclone mounting the
        // same drive letter, or the same executable with the same arguments
        List<int> FindExternal(Mount m)
        {
            var r = new List<int>();
            Func<string, string, bool> match;
            string name;
            if (m.IsRclone)
            {
                if (m.Drive.Length == 0) return r;
                var rx = new Regex(@"\bmount\s+\S+\s+""?" + Regex.Escape(m.Drive) + ":", RegexOptions.IgnoreCase);
                name = "rclone.exe";
                match = (path, cl) => rx.IsMatch(cl);
            }
            else
            {
                // the original command matches too: an imported and copied program may still be running
                // from its install folder, and must be adopted rather than started a second time
                string args = Norm(m.Args), oargs = Norm(m.OriginArgs ?? "");
                string exe = m.Exe, origin = m.Origin;
                name = Path.GetFileName(exe);
                match = (path, cl) =>
                    (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase) && Norm(cl).Contains(args)) ||
                    (origin != null && string.Equals(path, origin, StringComparison.OrdinalIgnoreCase) && Norm(cl).Contains(oargs));
                if (origin != null && !Path.GetFileName(origin).Equals(name, StringComparison.OrdinalIgnoreCase))
                    name = name + "' OR Name='" + Path.GetFileName(origin);
            }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='" + name.Replace("'", "") + "'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var cl = o["CommandLine"] as string;
                        var path = o["ExecutablePath"] as string;
                        if (cl != null && match(path, cl)) r.Add(Convert.ToInt32(o["ProcessId"]));
                    }
            }
            catch { }
            return r;
        }

        // normalizes a command line for comparison: no quotes, single spaces, lower case
        static string Norm(string s)
        {
            return Regex.Replace(s.Replace("\"", ""), @"\s+", " ").Trim().ToLowerInvariant();
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        // posts WM_CLOSE to every top-level window of the process, like closing it by hand
        static void CloseWindows(int pid)
        {
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == pid) PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        // ---------- state ----------

        // Changes the state of an entry and shows a notification on the transitions that matter
        // (to Error, to Warning, and back to Running after an error). Call with the lock held.
        void Set(Mount m, State s, string detail)
        {
            State prev = m.State;
            m.State = s;
            m.Detail = detail;
            if (s == State.Error && prev != State.Error)
                Balloon(m.Title + " non funziona", detail, ToolTipIcon.Error);
            else if (s == State.Warning && prev != State.Warning)
                Balloon(m.Title + ": attenzione", detail, ToolTipIcon.Warning);
            else if (s == State.Running && prev == State.Error)
                Balloon(m.Title, "Di nuovo attivo.", ToolTipIcon.Info);
        }

        // Marks an entry as failed and, if it should be running, schedules a restart with exponential
        // back-off: 10, 20, 40, 80, 160 s, then gives up until the user starts it again.
        void Fail(Mount m, string why)
        {
            if (m.Wanted && autoRestart && m.Failures < MaxRetries)
            {
                int wait = 10 << m.Failures;
                m.Failures++;
                m.NextRetry = DateTime.Now.AddSeconds(wait);
                why += " - nuovo tentativo tra " + wait + " s";
            }
            else if (m.Wanted && autoRestart) why += " - tentativi esauriti";
            Set(m, State.Error, why);
        }

        // startup: adopt or start every entry, then begin the periodic checks
        void Initialize()
        {
            Mount[] list;
            lock (sync) list = mounts.ToArray();
            foreach (var m in list) AdoptOrStart(m);
            Check();
            timer.Change(5000, 5000);
        }

        // If the entry is already running (e.g. Lighthouse was closed and reopened) take it over,
        // otherwise start it if autostart is on. This is what avoids duplicate processes.
        void AdoptOrStart(Mount m)
        {
            int pid = -1;
            if (m.IsRclone)
            {
                // an rclone started by a previous Lighthouse session answers on its rc port
                string j = Rc(m, "core/pid");
                var pm = j == null ? Match.Empty : Regex.Match(j, "\"pid\"\\s*:\\s*(\\d+)");
                if (pm.Success) pid = int.Parse(pm.Groups[1].Value);
            }
            else
            {
                // a generic program that is already running (started by Lighthouse or by anyone else) is adopted
                var found = FindExternal(m);
                if (found.Count > 0) pid = found[0];
            }
            if (pid > 0)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    lock (sync) { m.Proc = p; m.Wanted = true; m.StartedAt = DateTime.Now; Set(m, State.Starting, "ripreso in gestione"); }
                    return;
                }
                catch { }
            }
            // a drive letter already mounted by someone else is left alone (it shows as External)
            bool drive = DriveExists(m.Drive);
            lock (sync) if (!drive && m.AutoStart) Start(m);
        }

        // Periodic check (every 5 s): probes each entry, updates its state, restarts what crashed,
        // then refreshes status.txt and the tray. Overlapping runs are skipped.
        void Check()
        {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            try
            {
                Mount[] list;
                lock (sync) list = mounts.ToArray();
                foreach (var m in list)
                {
                    // outside the lock: these can be slow
                    bool drive = DriveExists(m.Drive);
                    string health = Health(m);
                    lock (sync) Evaluate(m, drive, health);
                }
            }
            finally { checking = 0; }
            WriteStatus();
            RefreshUi();
        }

        // logs\status.txt: state of every entry, for diagnostics (rewritten only when it changes)
        string lastStatus;
        void WriteStatus()
        {
            string s;
            lock (sync) s = string.Concat(mounts.Select(m => m.Name + "\t" + Labels[(int)m.State] + "\t" + m.Detail + Environment.NewLine));
            if (s == lastStatus) return;
            lastStatus = s;
            try { File.WriteAllText(Path.Combine(logDir, "status.txt"), s); } catch { }
        }

        // The state machine of one entry, given the probe results. Call with the lock held.
        //   process alive         -> Running / Warning (rclone also needs its drive letter, 60 s grace at start)
        //   process just exited   -> Error, restart scheduled by Fail()
        //   no process, wanted    -> start again when the back-off delay has passed
        //   no process, not wanted-> Stopped, or External if someone else mounted the drive letter
        void Evaluate(Mount m, bool drive, string health)
        {
            if (m.Busy) return;
            if (m.Proc != null && !Exited(m.Proc))
            {
                if (drive || !m.IsRclone)
                {
                    m.Failures = 0;
                    var now = DateTime.Now;
                    m.Crashes.RemoveAll(t => now - t > CrashWindow);
                    string warn = health ?? (m.Crashes.Count >= 2
                        ? "si è chiuso ed è ripartito " + m.Crashes.Count + " volte negli ultimi " + (int)CrashWindow.TotalMinutes + " minuti"
                        : null);
                    if (warn == null) m.Acked = null;            // problem solved: a future warning must be shown again
                    if (warn != null && warn != m.Acked) Set(m, State.Warning, warn);
                    else Set(m, State.Running, "attivo");
                }
                else if ((DateTime.Now - m.StartedAt).TotalSeconds < 60) Set(m, State.Starting, "avvio in corso");
                else if (m.State != State.Error) Set(m, State.Error, "rclone è attivo ma l'unità " + m.Drive + ": non risponde");
            }
            else if (m.Proc != null)
            {
                string code = "";
                try { code = " (codice " + m.Proc.ExitCode + ")"; } catch { }
                m.Proc = null;
                m.Crashes.Add(DateTime.Now);
                Fail(m, m.IsRclone ? "rclone si è chiuso" + code + ", vedi il log" : "si è chiuso" + code);
            }
            else if (m.Wanted)
            {
                if (DateTime.Now >= m.NextRetry) Start(m);
            }
            else if (drive) Set(m, State.External, "l'unità " + m.Drive + ": è montata fuori da Lighthouse");
            else Set(m, State.Stopped, "fermo");
        }

        // Warning signals: for any program, a hung window; for rclone, what rclone itself reports on
        // its rc port. Logs are never parsed. Returns null when there is no problem.
        string Health(Mount m)
        {
            Process p;
            lock (sync)
            {
                p = m.Proc;
                if (p == null || m.Busy || (m.State != State.Running && m.State != State.Warning)) return null;
            }
            if (Exited(p)) return null;

            if (!m.IsRclone)
            {
                // like Task Manager's "Not responding", but only after two checks in a row
                m.HungCount = IsHung(p.Id) ? m.HungCount + 1 : 0;
                return m.HungCount >= 2 ? "il programma non risponde" : null;
            }

            var now = DateTime.Now;
            if (now < m.NextRcCheck) return m.RcWarning;    // for rclone one check every 30 s is enough
            m.NextRcCheck = now.AddSeconds(30);
            string vfs = Rc(m, "vfs/stats");
            if (vfs == null)
            {
                m.RcFails++;
                return m.RcWarning = m.RcFails >= 2 ? "rclone non risponde ai comandi" : null;
            }
            m.RcFails = 0;
            int errored = Num(vfs, "erroredFiles");
            int pending = Num(vfs, "uploadsInProgress") + Num(vfs, "uploadsQueued");
            string w = null;
            if (Regex.IsMatch(vfs, "\"outOfSpace\"\\s*:\\s*true")) w = "la cache di rclone ha finito lo spazio su disco";
            else if (errored > 0) w = errored + " file in errore nella cache, non caricati";
            if (pending > 0)
            {
                // uploads are queued, but the transferred byte counter has not moved for a while
                string stats = Rc(m, "core/stats");
                var bm = stats == null ? Match.Empty : Regex.Match(stats, "\"bytes\"\\s*:\\s*(\\d+)");
                long bytes = bm.Success ? long.Parse(bm.Groups[1].Value) : -1;
                if (bytes != m.LastBytes || m.PendingSince == DateTime.MinValue) { m.LastBytes = bytes; m.PendingSince = now; }
                else if (w == null && now - m.PendingSince > UploadStall)
                    w = pending + " upload fermi da più di " + (int)UploadStall.TotalMinutes + " minuti";
            }
            else m.PendingSince = DateTime.MinValue;
            return m.RcWarning = w;
        }

        // reads an integer field from a JSON response (no JSON library needed for these few flat fields)
        static int Num(string json, string key)
        {
            var mm = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
            return mm.Success ? int.Parse(mm.Groups[1].Value) : 0;
        }

        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        // Active probe: an empty message (WM_NULL) to each window of the process; if one does not answer
        // within 3 s the program is hung. IsHungAppWindow is not enough: Windows only flags "not responding"
        // if input arrives in the meantime. All windows are probed because some (e.g. the console) belong to Windows.
        static bool IsHung(int pid)
        {
            var windows = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == pid) windows.Add(h);
                return true;
            }, IntPtr.Zero);
            const uint SMTO_ABORTIFHUNG = 0x2;
            foreach (var h in windows)
            {
                IntPtr res;
                if (SendMessageTimeout(h, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out res) == IntPtr.Zero)
                    return true;
            }
            return false;
        }

        // ---------- user interface ----------

        // one tray icon and one menu dot per state, at the system small-icon size (DPI aware)
        void BuildIcons()
        {
            int sz = SystemInformation.SmallIconSize.Width;
            icons = new Icon[Colors.Length];
            dots = new Bitmap[Colors.Length];
            for (int i = 0; i < Colors.Length; i++)
            {
                // on light colors (yellow, light blue) dark details read better than white
                var c = Colors[i];
                var fg = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160 ? Color.FromArgb(26, 26, 26) : Color.White;
                icons[i] = Icon.FromHandle(Art.Lighthouse(sz, Colors[i], fg).GetHicon());
                dots[i] = Dot(16, Colors[i]);
            }
        }

        // colored status dot shown next to each entry in the menu
        static Bitmap Dot(int sz, Color c)
        {
            var b = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float pad = sz * 0.22f;
                var r = new RectangleF(pad, pad, sz - 2 * pad - 1, sz - 2 * pad - 1);
                using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
                using (var pen = new Pen(ControlPaint.Dark(c), Math.Max(1f, sz / 16f))) g.DrawEllipse(pen, r);
            }
            return b;
        }

        void RefreshUi()
        {
            if (ui.IsHandleCreated) ui.BeginInvoke(new Action(UpdateTray));
        }

        // tray icon = most severe state; tooltip = first error, else first warning, else running count
        void UpdateTray()
        {
            string text;
            State worst = State.Stopped;
            lock (sync)
            {
                foreach (var m in mounts) if (Severity[(int)m.State] > Severity[(int)worst]) worst = m.State;
                var bad = mounts.FirstOrDefault(x => x.State == State.Error);
                var warn = mounts.FirstOrDefault(x => x.State == State.Warning);
                text = bad != null ? "Lighthouse: " + bad.Name + " in errore"
                    : warn != null ? "Lighthouse: " + warn.Name + " - " + warn.Detail
                    : "Lighthouse: " + mounts.Count(x => x.State == State.Running || x.State == State.Warning) + "/" + mounts.Count + " attivi";
            }
            tray.Icon = icons[(int)worst];
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon tooltip limit
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            if (ui.IsHandleCreated) ui.BeginInvoke(new Action(() => tray.ShowBalloonTip(5000, title, text, icon)));
        }

        // yes/no question on the UI thread, callable from any thread; the TopMost owner keeps it above other windows
        bool Ask(string text)
        {
            return (bool)ui.Invoke(new Func<bool>(() =>
            {
                using (var f = new Form { TopMost = true })
                    return MessageBox.Show(f, text, "Lighthouse", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }));
        }

        // runs a menu action in the background, so a slow stop never freezes the menu
        static void Bg(Action a) { ThreadPool.QueueUserWorkItem(_ => a()); }

        static ToolStripMenuItem Item(string text, Action a, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            if (a != null) it.Click += (s, e) => a();
            return it;
        }

        // the menu is rebuilt every time it opens, so it always reflects the current state
        void BuildMenu()
        {
            menu.Items.Clear();
            lock (sync)
            {
                menu.Items.Add(Item("Lighthouse - " + mounts.Count(x => x.State == State.Running || x.State == State.Warning) + "/" + mounts.Count + " attivi", null, false));
                menu.Items.Add(new ToolStripSeparator());
                for (int i = 0; i < mounts.Count; i++)
                {
                    // separator between the rclone mounts and the other programs
                    if (i > 0 && mounts[i].IsRclone != mounts[i - 1].IsRclone) menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(MountMenu(mounts[i]));
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Item("Avvia tutti", () => Bg(() =>
                {
                    lock (sync) foreach (var m in mounts) if (m.State == State.Stopped || m.State == State.Error) Start(m);
                    Check();
                })));
                menu.Items.Add(Item("Ferma tutti", () => Bg(() => { StopAll(); Check(); })));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Modifica configurazione", () => Process.Start("notepad.exe", "\"" + iniPath + "\"")));
            menu.Items.Add(Item("Ricarica configurazione", () => Bg(Reload)));
            menu.Items.Add(Item("Apri cartella log", () => Process.Start("explorer.exe", "\"" + logDir + "\"")));
            menu.Items.Add(Item("Importa programmi di avvio...", RunImport));
            var auto = Item("Avvia Lighthouse con Windows", ToggleRunAtLogin);
            auto.Checked = RunAtLogin();
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Esci (i mount restano attivi)", Quit));
            menu.Items.Add(Item("Esci e ferma i mount", () => Bg(() => { StopAll(); ui.BeginInvoke(new Action(Quit)); })));
        }

        // submenu of one entry: status detail, start/stop/restart, open drive or log, autostart toggle
        ToolStripMenuItem MountMenu(Mount m)
        {
            var it = new ToolStripMenuItem(m.Title + " - " + Labels[(int)m.State]) { Image = dots[(int)m.State] };
            var sub = it.DropDownItems;
            sub.Add(Item(m.Detail, null, false));
            if (m.State == State.Warning)
                sub.Add(Item("Segna come visto", () => { lock (sync) { m.Acked = m.Detail; m.Crashes.Clear(); } Bg(Check); }));
            sub.Add(new ToolStripSeparator());
            bool busy = m.Busy || m.State == State.Stopping;
            if (m.State == State.External)
            {
                sub.Add(Item("Prendi in gestione (riavvia)", () => Bg(() =>
                {
                    if (!Ask("Il mount " + m.Drive + ": avviato fuori da Lighthouse verrà terminato e riavviato.\n" +
                             "Eventuali upload in corso riprenderanno al riavvio dalla cache.\n\nProcedere?")) return;
                    Stop(m);
                    lock (sync) Start(m);
                    Check();
                })));
                sub.Add(Item("Ferma", () => Bg(() => { if (Ask("Terminare il mount esterno " + m.Drive + ":?")) { Stop(m); Check(); } })));
            }
            else
            {
                bool running = m.Proc != null;
                sub.Add(Item("Avvia", () => Bg(() => { lock (sync) { m.Failures = 0; Start(m); } Check(); }), !running && !busy));
                sub.Add(Item("Ferma", () => Bg(() => { if (ConfirmStop(m)) { Stop(m); Check(); } }), !busy && (running || m.Wanted)));
                sub.Add(Item("Riavvia", () => Bg(() =>
                {
                    if (!ConfirmStop(m)) return;
                    Stop(m);
                    lock (sync) Start(m);
                    Check();
                }), !busy));
            }
            sub.Add(new ToolStripSeparator());
            if (m.IsRclone)
            {
                sub.Add(Item("Apri " + m.Drive + ":", () => Process.Start("explorer.exe", m.Drive + ":\\"),
                    m.State == State.Running || m.State == State.External));
                sub.Add(Item("Apri log", () => Process.Start("notepad.exe", "\"" + LogPath(m) + "\""), File.Exists(LogPath(m))));
            }
            else if (m.Origin != null)
                sub.Add(Item("Usa l'eseguibile originale", () => Bg(() => UseOriginal(m)), !busy));
            var auto = Item("Avvio automatico", () =>
            {
                lock (sync) m.AutoStart = !m.AutoStart;
                SetIniValue(m.Name, "autostart", m.AutoStart ? "1" : "0");
            });
            auto.Checked = m.AutoStart;
            sub.Add(auto);
            return it;
        }

        // the copy in programs\ does not work (e.g. it misses files from its original folder): go back to the original command
        void UseOriginal(Mount m)
        {
            if (!Ask("\"" + m.Name + "\" userà di nuovo il programma originale:\n" + m.Origin + "\n\nLo riavvio adesso?")) return;
            bool was = m.Proc != null;
            if (was) Stop(m);
            SetIniValue(m.Name, "exe", m.Origin);
            SetIniValue(m.Name, "args", m.OriginArgs);
            SetIniValue(m.Name, "workdir", m.OriginWorkDir ?? "");
            SetIniValue(m.Name, "origin", "");
            SetIniValue(m.Name, "originargs", "");
            SetIniValue(m.Name, "originworkdir", "");
            lock (sync)
            {
                LoadConfig();
                m.Failures = 0;
                if (was || m.Wanted || m.State == State.Error) Start(m);
            }
            Check();
        }

        // before stopping an rclone mount, ask for confirmation if files are still waiting to be uploaded
        bool ConfirmStop(Mount m)
        {
            if (m.Proc == null || !m.IsRclone) return true;
            int n = PendingUploads(m);
            return n == 0 || Ask(m.Name + " ha " + n + " file ancora da caricare.\n" +
                                 "Se lo fermi ora, l'upload riprenderà al prossimo avvio del mount.\n\nFermarlo comunque?");
        }

        void StopAll()
        {
            Mount[] list;
            lock (sync) list = mounts.Where(x => x.Proc != null || x.Wanted).ToArray();
            foreach (var m in list) Stop(m);
        }

        // re-reads the ini: stops the entries that were removed and adopts/starts the new ones;
        // entries that are still there keep running with their updated settings
        void Reload()
        {
            List<Mount> removed;
            Mount[] fresh;
            lock (sync)
            {
                var before = new HashSet<Mount>(mounts);
                removed = LoadConfig();
                fresh = mounts.Where(x => !before.Contains(x)).ToArray();   // new entries only
            }
            foreach (var m in removed) if (m.Proc != null) Stop(m);
            foreach (var m in fresh) AdoptOrStart(m);
            Check();
        }

        // ---------- run at login ----------

        // Starting with Windows uses an "at logon" scheduled task: Explorer sometimes skipped the
        // entry in the Run key without leaving any trace.
        // Runs schtasks.exe and returns its output, or null if it failed.
        static string Schtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? output : null;
                }
            }
            catch { return null; }
        }

        // true if the "Lighthouse" task exists and is enabled
        static bool RunAtLogin()
        {
            string xml = Schtasks("/query /tn Lighthouse /xml");
            return xml != null && !Regex.IsMatch(xml, @"<Settings>[\s\S]*<Enabled>false</Enabled>[\s\S]*</Settings>");
        }

        void ToggleRunAtLogin() { SetRunAtLogin(!RunAtLogin()); }

        // at startup: migrate the old Run entry, and re-point the task if the exe it refers to no longer
        // exists (it was moved); a test copy launched from another folder leaves the task alone
        void SyncAutostart()
        {
            bool legacy;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) legacy = k != null && k.GetValue("Lighthouse") != null;
            string xml = Schtasks("/query /tn Lighthouse /xml");
            if (xml == null) { if (legacy) SetRunAtLogin(true); return; }
            var cm = Regex.Match(xml, "<Command>(.*?)</Command>");
            string cmd = cm.Success ? cm.Groups[1].Value.Replace("&amp;", "&").Trim('"') : "";
            if (legacy || !File.Exists(cmd)) SetRunAtLogin(true);
        }

        // Creates (or deletes) the scheduled task: at logon of the current user, normal priority (4),
        // no admin rights, no time limit, also on battery. The task is defined as XML because
        // schtasks' command-line switches cannot set all of these options.
        void SetRunAtLogin(bool enable)
        {
            // earlier versions used the Run key: remove it so Lighthouse does not start twice
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("Lighthouse", false);
            using (var k = Registry.CurrentUser.CreateSubKey(ApprovedKey)) k.DeleteValue("Lighthouse", false);
            if (!enable) { Schtasks("/delete /tn Lighthouse /f"); return; }

            string args = iniPath == Path.Combine(dir, "Lighthouse.ini") ? "" :
                "<Arguments>" + SecurityElement.Escape("\"" + iniPath + "\"") + "</Arguments>";
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "<RegistrationInfo><Description>Avvia Lighthouse all'accesso</Description></RegistrationInfo>\n" +
                "<Triggers><LogonTrigger><UserId>" + SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) + "</UserId></LogonTrigger></Triggers>\n" +
                "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
                "<Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<StartWhenAvailable>true</StartWhenAvailable><Priority>4</Priority></Settings>\n" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) + "</Command>" + args +
                "<WorkingDirectory>" + SecurityElement.Escape(dir.TrimEnd('\\')) + "</WorkingDirectory></Exec></Actions>\n" +
                "</Task>\n";
            string tmp = Path.Combine(Path.GetTempPath(), "Lighthouse-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            bool ok = Schtasks("/create /tn Lighthouse /xml \"" + tmp + "\" /f") != null;
            try { File.Delete(tmp); } catch { }
            if (!ok) MessageBox.Show("Non sono riuscito a creare l'attività pianificata \"Lighthouse\".", "Lighthouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // exits the tray; managed processes keep running and are adopted at the next start
        void Quit()
        {
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
