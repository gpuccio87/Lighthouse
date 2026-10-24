// Lighthouse - first-run setup and import of the existing Windows startup programs:
// shortcuts in the Startup folders, the registry Run key, and rclone mounts that are already running.
// For each one it works out the real command to manage (e.g. the rclone line inside a .bat, or the
// interpreter that is executing an .ahk script), lets the user choose, and writes the ini sections.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Lighthouse
{
    // A startup program found by the scan, shown as one row of the import window.
    class Candidate
    {
        public string Name, Source, Exe, Args = "", WorkDir, Note = "";   // Note: why it may not work as is
        public bool IsRclone, Checked;
        public string Drive = "";
        public string LnkPath, RunValue;   // to be removed from Windows startup after the import
    }

    // one running process, as reported by WMI
    class ProcInfo { public int Pid, Parent; public string Path, Cmd; }

    partial class TrayApp
    {
        // where a candidate comes from (shown in the "Origine" column)
        const string SrcStartup = "Esecuzione automatica", SrcCommon = "Avvio di tutti gli utenti",
            SrcRun = "Registro (Run)", SrcRclone = "rclone in esecuzione";

        // ini written on first run; {rclone} is replaced with the rclone.exe found on this PC
        const string IniTemplate =
@"; Lighthouse - configurazione
; Ogni sezione [Nome] è una voce del menu. Dopo una modifica: menu > ""Ricarica configurazione"".
; Le voci si possono aggiungere anche da menu > ""Importa programmi di avvio..."".
;
; [general] rclone = eseguibile di rclone; rcloneconfig = il suo rclone.conf (lo rileva l'importazione)
;
; Mount rclone (senza ""exe""):
;   args      = argomenti di rclone (senza ""rclone.exe""); Lighthouse aggiunge da sé --rc e --log-file
;   drive     = lettera di unità (se omessa la ricava da args)
;   port      = porta rc locale usata per controllarlo (una diversa per ogni mount)
;
; Altri programmi (con ""exe""): l'exe va indicato direttamente, non un collegamento o un .bat
;   exe       = percorso completo dell'eseguibile
;   args      = argomenti (facoltativo)
;   workdir   = cartella di lavoro (facoltativo, default: cartella dell'exe)
;
; Per tutti:
;   autostart = 1 per avviarlo all'apertura di Lighthouse, 0 per lasciarlo fermo

[general]
rclone={rclone}
; riavvia da solo un programma che si chiude (fino a 5 tentativi, attesa crescente)
autorestart=1
";

        // ---------- first run ----------

        // import the startup programs, then offer to start Lighthouse at every logon
        void FirstRun()
        {
            RunImport();
            if (!RunAtLogin() && Ask("Avviare Lighthouse automaticamente a ogni accesso a Windows?"))
                SetRunAtLogin(true);
        }

        // rclone.exe: the one already running, else C:\rclone, else the first one on PATH
        static string FindRclone()
        {
            foreach (var p in Processes())
                if (p.Path != null && Path.GetFileName(p.Path).Equals("rclone.exe", StringComparison.OrdinalIgnoreCase)) return p.Path;
            if (File.Exists(@"C:\rclone\rclone.exe")) return @"C:\rclone\rclone.exe";
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string f = Path.Combine(d.Trim(), "rclone.exe"); if (File.Exists(f)) return f; } catch { }
            }
            return "rclone.exe";
        }

        // ---------- scan ----------

        // all running processes with path, command line and parent (Process.GetProcesses has no command line)
        static List<ProcInfo> Processes()
        {
            var r = new List<ProcInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                    foreach (ManagementObject o in s.Get())
                        r.Add(new ProcInfo
                        {
                            Pid = Convert.ToInt32(o["ProcessId"]),
                            Parent = Convert.ToInt32(o["ParentProcessId"]),
                            Path = o["ExecutablePath"] as string,
                            Cmd = o["CommandLine"] as string
                        });
            }
            catch { }
            return r;
        }

        // reads a .lnk through the WScript.Shell COM object (late bound, so no interop assembly is needed)
        static bool ReadShortcut(string lnk, out string target, out string args, out string workDir)
        {
            target = args = workDir = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var st = sc.GetType();
                target = (string)st.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                args = (string)st.InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null);
                workDir = (string)st.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, sc, null);
                if (string.IsNullOrEmpty(workDir)) workDir = null;
                return !string.IsNullOrEmpty(target);
            }
            catch { return false; }
        }

        // splits a command line into executable and arguments, also for unquoted paths that contain spaces
        // (common in the Run key): an unquoted path ends at ".exe"
        static void SplitCommand(string cmd, out string exe, out string args)
        {
            cmd = (cmd ?? "").Trim();
            if (cmd.StartsWith("\""))
            {
                int e = cmd.IndexOf('"', 1);
                if (e < 0) e = cmd.Length;
                exe = cmd.Substring(1, e - 1);
                args = e + 1 < cmd.Length ? cmd.Substring(e + 1).Trim() : "";
                return;
            }
            int x = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int cut = x >= 0 ? x + 4 : cmd.IndexOf(' ');
            if (cut < 0 || cut > cmd.Length) cut = cmd.Length;
            exe = cmd.Substring(0, cut);
            args = cmd.Substring(cut).Trim();
        }

        static string ArgsOf(string cmd)
        {
            string exe, args;
            SplitCommand(cmd, out exe, out args);
            return args;
        }

        // removes the rclone options that Lighthouse adds by itself (rc, log file, console)
        static string StripRcloneFlags(string a)
        {
            a = " " + a;
            a = Regex.Replace(a, @"\s--no-console\b", "");
            a = Regex.Replace(a, @"\s--rc-(addr|user|pass)(=|\s+)\S+", "");
            a = Regex.Replace(a, @"\s--rc-no-auth\b", "");
            a = Regex.Replace(a, @"\s--rc\b(?!-)", "");
            a = Regex.Replace(a, @"\s--log-file(=|\s+)(""[^""]*""|\S+)", "");
            return Regex.Replace(a, @"\s+", " ").Trim();
        }

        // a name usable both as an ini section and as a folder name
        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { '[', ']', '=' })) s = s.Replace(c, '_');
            return s.Trim(' ', '_', '.');
        }

        // an rclone candidate from "mount <remote>: <X>: ..." (named after the remote); null if not a mount
        static Candidate RcloneFromArgs(string args, string exe, string fallbackName)
        {
            var mm = Regex.Match(args, @"^mount\s+(""[^""]+""|\S+)\s+""?([A-Za-z]):");
            if (!mm.Success) return null;
            string name = SafeName(mm.Groups[1].Value.Trim('"').TrimEnd(':'));
            return new Candidate
            {
                Name = name.Length > 0 ? name : fallbackName,
                IsRclone = true,
                Exe = exe,
                Args = args,
                Drive = mm.Groups[2].Value.ToUpperInvariant()
            };
        }

        // From the target of a shortcut, works out the command(s) to manage:
        //   .bat/.cmd -> every "rclone mount" line inside it, otherwise cmd.exe /c script
        //   .exe      -> the exe itself
        //   document  -> the process that currently has it open
        static List<Candidate> FromTarget(string fileName, string target, string args, string workDir, List<ProcInfo> procs)
        {
            var r = new List<Candidate>();
            string ext = Path.GetExtension(target).ToLowerInvariant();
            string baseName = fileName;
            while (Path.HasExtension(baseName)) baseName = Path.GetFileNameWithoutExtension(baseName);
            baseName = SafeName(baseName);

            if (ext == ".bat" || ext == ".cmd")
            {
                string[] lines;
                try { lines = File.ReadAllLines(target); } catch { lines = new string[0]; }
                foreach (var line in lines)
                {
                    var mm = Regex.Match(line, @"(?i)(?:""(?<exe>[^""]*rclone(?:\.exe)?)""|(?<exe>[^\s""]*rclone(?:\.exe)?))\s+(?<args>mount\s.*)$");
                    if (!mm.Success) continue;
                    string exe = mm.Groups["exe"].Value;
                    if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                    if (!Path.IsPathRooted(exe))
                    {
                        string local = Path.Combine(Path.GetDirectoryName(target), exe);
                        if (File.Exists(local)) exe = local;
                    }
                    var c = RcloneFromArgs(StripRcloneFlags(mm.Groups["args"].Value), exe, baseName);
                    if (c != null) r.Add(c);
                }
                if (r.Count == 0)
                    r.Add(new Candidate
                    {
                        Name = baseName, Exe = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        Args = "/c \"" + target + "\"", WorkDir = Path.GetDirectoryName(target),
                        Note = "script .bat: Lighthouse vede solo cmd.exe"
                    });
                return r;
            }
            if (ext == ".exe")
            {
                r.Add(new Candidate { Name = baseName, Exe = target, Args = args ?? "", WorkDir = workDir });
                return r;
            }
            // document opened by its associated program (.ahk, .ffs_real...): the real command comes from
            // the process that is running it, the innermost one if a launcher started it
            string key = Norm(target);
            var matches = procs.Where(p => p.Cmd != null && p.Path != null && Norm(p.Cmd).Contains(key)).ToList();
            var leaf = matches.FirstOrDefault(p => !matches.Any(q => q.Parent == p.Pid));
            if (leaf != null)
                r.Add(new Candidate { Name = baseName, Exe = leaf.Path, Args = ArgsOf(leaf.Cmd), WorkDir = workDir ?? Path.GetDirectoryName(target) });
            else
                r.Add(new Candidate
                {
                    Name = baseName, Exe = target, Args = args ?? "", WorkDir = workDir,
                    Note = "non è in esecuzione: avvialo e ripeti l'importazione"
                });
            return r;
        }

        // scan, leaving out what is already in the configuration
        List<Candidate> ScanStartup()
        {
            return ScanStartup(c =>
            {
                lock (sync)
                    return mounts.Any(m => c.IsRclone
                        ? m.IsRclone && c.Drive.Length > 0 && m.Drive == c.Drive
                        : !m.IsRclone && string.Equals(m.Exe, c.Exe, StringComparison.OrdinalIgnoreCase) && Norm(m.Args) == Norm(c.Args));
            }, null);
        }

        // diagnostics: Lighthouse.exe --scan result.txt [folder with more shortcuts]
        // writes one tab-separated line per candidate
        public static void ScanToFile(string outFile, string extraFolder)
        {
            var sb = new StringBuilder();
            foreach (var c in ScanStartup(c => false, extraFolder))
                sb.AppendLine(string.Join("\t", new[] { c.Checked ? "[x]" : "[ ]", c.Name, c.Source, c.IsRclone ? "rclone " + c.Drive + ":" : "programma",
                    c.Exe, c.Args, c.WorkDir ?? "", c.Note }));
            File.WriteAllText(outFile, sb.ToString());
        }

        // diagnostics: Lighthouse.exe --import-test test.ini [folder]: imports the preselected entries into the
        // given ini, copying the files next to that ini, without touching Windows startup
        public static void ImportTest(string ini, string extraFolder)
        {
            if (!File.Exists(ini)) File.WriteAllText(ini, IniTemplate.Replace("{rclone}", FindRclone()));
            var warnings = new List<string>();
            WriteCandidates(ini, ScanStartup(c => false, extraFolder).Where(c => c.Checked).ToList(), true, warnings);
            File.AppendAllText(ini, string.Concat(warnings.Select(w => "; AVVISO: " + w + Environment.NewLine)));
        }

        // The actual scan. known() filters out entries that are already configured; extraFolder is an
        // optional additional folder of shortcuts (for testing).
        static List<Candidate> ScanStartup(Func<Candidate, bool> known, string extraFolder)
        {
            var procs = Processes();
            var list = new List<Candidate>();

            // 1) shortcuts and files in the user's and the all-users Startup folders
            foreach (var folder in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), extraFolder })
            {
                if (folder == null) continue;
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.GetFiles(folder))
                {
                    if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = f, args = "", wd = null;
                    if (Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ReadShortcut(f, out target, out args, out wd)) continue;
                    foreach (var c in FromTarget(Path.GetFileName(f), target, args, wd, procs))
                    {
                        c.Source = folder == Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) ? SrcCommon : SrcStartup;
                        c.LnkPath = f;
                        list.Add(c);
                    }
                }
            }

            // 2) the current user's Run key, skipping entries disabled in Task Manager's Startup tab
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            using (var ap = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                    {
                        if (n == "Lighthouse") continue;
                        var flag = ap == null ? null : ap.GetValue(n) as byte[];
                        if (flag != null && flag.Length > 0 && (flag[0] & 1) == 1) continue;   // disabled
                        string exe, a;
                        SplitCommand(Environment.ExpandEnvironmentVariables(k.GetValue(n) as string ?? ""), out exe, out a);
                        if (exe.Length > 0) list.Add(new Candidate { Name = SafeName(n), Source = SrcRun, Exe = exe, Args = a, RunValue = n });
                    }

            // 3) rclone mounts that are running now, unless a startup entry already covers their drive letter
            foreach (var p in procs)
            {
                if (p.Path == null || p.Cmd == null || !Path.GetFileName(p.Path).Equals("rclone.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var c = RcloneFromArgs(StripRcloneFlags(ArgsOf(p.Cmd)), p.Path, "rclone");
                if (c != null && !list.Any(x => x.IsRclone && x.Drive == c.Drive)) { c.Source = SrcRclone; list.Add(c); }
            }

            // no duplicates of the current configuration, and not Lighthouse itself
            list.RemoveAll(c => known(c));
            list.RemoveAll(c => string.Equals(Path.GetFileName(c.Exe), "Lighthouse.exe", StringComparison.OrdinalIgnoreCase));
            // preselected: the user's shortcuts and the mounts; the Run key and the all-users folder are
            // usually installed apps that manage their own startup
            foreach (var c in list) c.Checked = (c.Source == SrcStartup || c.Source == SrcRclone) && c.Note.Length == 0;
            return list;
        }

        // ---------- selection window ----------

        // a file picked by hand (exe, bat, script, shortcut)
        static List<Candidate> CandidatesFromFile(string file)
        {
            string target = file, args = "", wd = null;
            if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ReadShortcut(file, out target, out args, out wd))
                return new List<Candidate>();
            var r = FromTarget(Path.GetFileName(file), target, args, wd, Processes());
            foreach (var c in r) { c.Source = "Scelto a mano"; c.Checked = c.Note.Length == 0; }
            return r;
        }

        // Shows the import window (a checkable list plus the two options) and returns the checked
        // candidates; an empty list if the user cancels. Sizes are scaled by the screen DPI.
        static List<Candidate> ChooseCandidates(List<Candidate> list, out bool disableSources, out bool copyFiles)
        {
            disableSources = copyFiles = false;
            float k;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
            Func<int, int> S = v => (int)(v * k);

            using (var f = new Form
            {
                Text = "Lighthouse - importa programmi di avvio",
                Size = new Size(S(960), S(520)),
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                ShowIcon = false,
                MinimizeBox = false,
                Font = SystemFonts.MessageBoxFont
            })
            {
                var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
                lv.Columns.Add("Nome", S(150));
                lv.Columns.Add("Origine", S(150));
                lv.Columns.Add("Comando", S(420));
                lv.Columns.Add("Nota", S(200));
                foreach (var c in list)
                    lv.Items.Add(new ListViewItem(new[] {
                        c.Name, c.Source, (c.IsRclone ? "rclone " : "\"" + c.Exe + "\" ") + c.Args, c.Note })
                    { Checked = c.Checked, Tag = c });

                var info = new Label
                {
                    Dock = DockStyle.Top, Height = S(56), Padding = new Padding(S(10), S(10), S(10), 0),
                    Text = "Spunta i programmi che Lighthouse deve avviare e sorvegliare al posto di Windows. " +
                           "Registro e avvio di tutti gli utenti sono deselezionati: di solito sono app che gestiscono da sole il proprio avvio."
                };
                var chk = new CheckBox
                {
                    Dock = DockStyle.Bottom, Height = S(30), Padding = new Padding(S(10), 0, 0, 0), Checked = true,
                    Text = "Togli i programmi importati dall'avvio di Windows (i collegamenti vengono spostati in \"backup-avvio\")"
                };
                var copy = new CheckBox
                {
                    Dock = DockStyle.Bottom, Height = S(30), Padding = new Padding(S(10), 0, 0, 0), Checked = true,
                    Text = "Copia programmi e script in \"programs\" nella cartella di Lighthouse (gli originali restano dove sono)"
                };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(46), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(6)) };
                var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
                var ok = new Button { Text = "Importa", DialogResult = DialogResult.OK, AutoSize = true };
                var add = new Button { Text = "Aggiungi file...", AutoSize = true };
                add.Click += (s, e) =>
                {
                    using (var d = new OpenFileDialog
                    {
                        Title = "Scegli programmi o script da far gestire a Lighthouse",
                        Filter = "Programmi e script|*.exe;*.bat;*.cmd;*.ahk;*.ffs_real;*.lnk|Tutti i file|*.*",
                        Multiselect = true
                    })
                        if (d.ShowDialog(f) == DialogResult.OK)
                            foreach (var file in d.FileNames)
                                foreach (var c in CandidatesFromFile(file))
                                    lv.Items.Add(new ListViewItem(new[] {
                                        c.Name, c.Source, (c.IsRclone ? "rclone " : "\"" + c.Exe + "\" ") + c.Args, c.Note })
                                    { Checked = c.Checked, Tag = c });
                };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(add);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.Add(lv);
                f.Controls.Add(info);
                f.Controls.Add(copy);
                f.Controls.Add(chk);
                f.Controls.Add(buttons);

                if (f.ShowDialog() != DialogResult.OK) return new List<Candidate>();
                disableSources = chk.Checked;
                copyFiles = copy.Checked;
                return lv.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (Candidate)i.Tag).ToList();
            }
        }

        // ---------- import ----------

        // The whole import flow: scan, let the user choose, write the ini, optionally remove the entries
        // from Windows startup, then reload and offer to take over mounts that are already running.
        void RunImport()
        {
            List<Candidate> found;
            Cursor.Current = Cursors.WaitCursor;
            try { found = ScanStartup(); } finally { Cursor.Current = Cursors.Default; }
            // the window opens even with no results: files can be added by hand
            bool disable, copy;
            var chosen = ChooseCandidates(found, out disable, out copy);
            if (chosen.Count == 0) return;

            var failed = new List<string>();
            var names = WriteCandidates(iniPath, chosen, copy, failed);
            if (disable) failed.AddRange(DisableSources(chosen));
            Bg(() =>
            {
                Reload();
                Mount[] external;
                lock (sync) external = mounts.Where(m => names.Contains(m.Name) && m.State == State.External).ToArray();
                if (external.Length > 0 &&
                    Ask("Questi mount sono già attivi, avviati fuori da Lighthouse: " +
                        string.Join(", ", external.Select(m => m.Drive + ":")) + ".\n" +
                        "Li riavvio adesso sotto Lighthouse? (Altrimenti ci penserà al prossimo accesso.)"))
                    foreach (var m in external)
                    {
                        Stop(m);
                        lock (sync) Start(m);
                    }
                Check();
                if (failed.Count > 0)
                    ui.Invoke(new Action(() =>
                    {
                        using (var f = new Form { TopMost = true })
                            MessageBox.Show(f, "Importazione fatta, con qualche avviso:\n\n" + string.Join("\n\n", failed),
                                "Lighthouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
            });
        }

        // Appends the entries to the ini and returns the names of the sections it created. With copy, exes and
        // scripts are copied into programs\<name>\ (rclone into programs\rclone\) and the ini points to the
        // copies, remembering the original command (origin=...) so the user can switch back to it.
        // Files under the Windows folder are never copied. Each new rclone mount gets the next free rc port.
        static HashSet<string> WriteCandidates(string iniPath, List<Candidate> chosen, bool copy, List<string> warnings)
        {
            string baseDir = Path.GetDirectoryName(iniPath);
            string progs = Path.Combine(baseDir, "programs");
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
            Func<string, bool> ours = path => path.StartsWith(baseDir + "\\", StringComparison.OrdinalIgnoreCase);
            Func<string, bool> copiable = path => copy && Path.IsPathRooted(path) && File.Exists(path) && !ours(path) &&
                !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);

            var ini = ReadIni(iniPath);
            var names = new HashSet<string>(ini.Keys, StringComparer.OrdinalIgnoreCase);
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int port = 5580, p;
            foreach (var s in ini.Values) { string v; if (s.TryGetValue("port", out v) && int.TryParse(v, out p) && p > port) port = p; }

            Dictionary<string, string> g;
            string current = ini.TryGetValue("general", out g) ? Get(g, "rclone", "") : "";
            var rc = chosen.FirstOrDefault(c => c.IsRclone && File.Exists(c.Exe));
            if (rc != null)
            {
                // the rclone.conf used by the original rclone (often next to its exe): it must be written
                // to the ini before switching to a copy, which would otherwise look for it elsewhere
                string conf = g != null ? Get(g, "rcloneconfig", "") : "";
                if (conf.Length == 0)
                {
                    conf = RcloneConfigFile(rc.Exe);
                    if (conf != null) SetIniValue(iniPath, "general", "rcloneconfig", conf);
                    else if (copy) warnings.Add("Non ho trovato il file di configurazione di rclone: se i mount non partono, " +
                        "indica il percorso del tuo rclone.conf in [general] rcloneconfig= nell'ini.");
                }
                string exe = rc.Exe;
                if (copiable(exe)) exe = CopyInto(exe, Path.Combine(progs, "rclone"), warnings) ?? exe;
                if (!File.Exists(current) || (copy && !ours(current))) SetIniValue(iniPath, "general", "rclone", exe);
            }

            var sb = new StringBuilder();
            foreach (var c in chosen)
            {
                // unique section name: "Name", "Name 2", "Name 3"...
                string name = c.Name;
                for (int i = 2; names.Contains(name); i++) name = c.Name + " " + i;
                names.Add(name);
                created.Add(name);
                sb.AppendLine().AppendLine("[" + name + "]");
                if (c.IsRclone)
                {
                    sb.AppendLine("args=" + c.Args);
                    sb.AppendLine("port=" + (++port));
                }
                else
                {
                    string dest = Path.Combine(progs, SafeName(name));
                    string exe = copiable(c.Exe) ? CopyInto(c.Exe, dest, warnings) ?? c.Exe : c.Exe;
                    // also copy the files passed as arguments (the .ahk script, the .ffs_real, the .bat...)
                    string args = Regex.Replace(c.Args, @"""([^""]+)""|(\S+)", mm =>
                    {
                        string f = mm.Groups[1].Success ? mm.Groups[1].Value : mm.Groups[2].Value;
                        if (!copiable(f)) return mm.Value;
                        string d = CopyInto(f, dest, warnings);
                        if (d == null) return mm.Value;
                        if (HasRelativeIncludes(f))
                            warnings.Add("\"" + name + "\": lo script " + Path.GetFileName(f) + " usa #Include o percorsi relativi. " +
                                "Se nella copia non funziona, nel menu della voce c'è \"Usa l'eseguibile originale\".");
                        return "\"" + d + "\"";
                    });
                    bool copied = exe != c.Exe || args != c.Args;
                    sb.AppendLine("exe=" + exe);
                    if (args.Length > 0) sb.AppendLine("args=" + args);
                    if (copied) sb.AppendLine("workdir=" + dest);
                    else if (c.WorkDir != null) sb.AppendLine("workdir=" + c.WorkDir);
                    if (copied)
                    {
                        sb.AppendLine("origin=" + c.Exe);
                        if (c.Args.Length > 0) sb.AppendLine("originargs=" + c.Args);
                        if (c.WorkDir != null) sb.AppendLine("originworkdir=" + c.WorkDir);
                    }
                }
                sb.AppendLine("autostart=1");
            }
            File.AppendAllText(iniPath, sb.ToString());
            return created;
        }

        // asks the given rclone which rclone.conf it uses ("rclone config file"); null if that file does not exist
        static string RcloneConfigFile(string rcloneExe)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(rcloneExe, "config file")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    string last = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    return last != null && File.Exists(last.Trim()) ? last.Trim() : null;
                }
            }
            catch { return null; }
        }

        // copies a file into a folder (reusing an identical copy already there); null if the copy fails
        static string CopyInto(string src, string dir, List<string> warnings)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, Path.GetFileName(src));
                var a = new FileInfo(src);
                var b = new FileInfo(dest);
                if (!b.Exists || b.Length != a.Length || b.LastWriteTimeUtc != a.LastWriteTimeUtc) File.Copy(src, dest, true);
                return dest;
            }
            catch (Exception ex)
            {
                warnings.Add("Non sono riuscito a copiare " + src + " (" + ex.Message + "): userò l'originale.");
                return null;
            }
        }

        // true for AutoHotkey scripts that use #Include with a relative path or A_ScriptDir:
        // a copy in programs\ may not find those files
        static bool HasRelativeIncludes(string file)
        {
            if (!file.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string text = File.ReadAllText(file);
                return Regex.IsMatch(text, @"(?im)^\s*#Include(Again)?\s+(?![A-Za-z]:\\|<)") || text.IndexOf("A_ScriptDir", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // Removes the imported entries from Windows startup, so they do not start twice: shortcuts are
        // moved to backup-avvio\, and removed Run values are saved in backup-avvio\registro-run.txt.
        // Returns the messages for what could not be removed.
        List<string> DisableSources(List<Candidate> chosen)
        {
            var failed = new List<string>();
            string bk = Path.Combine(Path.GetDirectoryName(iniPath), "backup-avvio");
            foreach (var lnk in chosen.Where(c => c.LnkPath != null).Select(c => c.LnkPath).Distinct())
            {
                try
                {
                    Directory.CreateDirectory(bk);
                    string dest = Path.Combine(bk, Path.GetFileName(lnk));
                    for (int i = 2; File.Exists(dest); i++)
                        dest = Path.Combine(bk, Path.GetFileNameWithoutExtension(lnk) + " " + i + Path.GetExtension(lnk));
                    File.Move(lnk, dest);
                }
                catch { failed.Add("Non sono riuscito a togliere dall'avvio di Windows " + lnk + " (servono i diritti di amministratore?): rimuovilo a mano, altrimenti all'accesso partirà due volte."); }
            }
            foreach (var c in chosen.Where(c => c.RunValue != null))
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        Directory.CreateDirectory(bk);
                        File.AppendAllText(Path.Combine(bk, "registro-run.txt"),
                            c.RunValue + "=" + k.GetValue(c.RunValue) + Environment.NewLine);
                        k.DeleteValue(c.RunValue, false);
                    }
                    using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                        if (k != null) k.DeleteValue(c.RunValue, false);
                }
                catch { failed.Add("Non sono riuscito a togliere dal registro la voce di avvio \"" + c.RunValue + "\": rimuovila a mano."); }
            }
            return failed;
        }
    }
}
