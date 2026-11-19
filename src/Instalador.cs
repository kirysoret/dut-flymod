// Instalador de FlyMod para Drive Up Together (WinForms, .NET Framework 4.x).
// Comprueba version.txt en GitHub y muestra "NEW VERSION" si hay una version nueva.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("FlyMod Installer")]
[assembly: System.Reflection.AssemblyVersion("0.2.3.0")]

namespace FlyModInstaller
{
    static class App
    {
        // API de GitHub (sin cache de CDN); la pagina raw muestra el mismo archivo
        public const string UpdateUrl = "https://api.github.com/repos/kirysoret/dut-flymod/contents/version.txt?ref=main";
        public const string UpdateUrlRaw = "https://raw.githubusercontent.com/kirysoret/dut-flymod/main/version.txt";
        public const string ExeName = "Drive Up Together.exe";
        public const string ProcName = "Drive Up Together";
        public const string Canal = "Beta";

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try { var old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    class MainForm : Form
    {
        readonly string _here = AppDomain.CurrentDomain.BaseDirectory;
        string Payload => Path.Combine(_here, "archivos");
        string LocalVersionFile => Path.Combine(Payload, "version.txt");

        Panel _banner; Label _bannerText; Button _bannerBtn;
        TextBox _path, _log; Label _ver;
        Button _install, _uninstall, _repair, _browse;
        Dictionary<string, string> _remote;

        public MainForm(string[] args)
        {
            Text = "FlyMod " + App.Canal + " - Drive Up Together";
            ClientSize = new Size(520, 400);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _banner = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(255, 200, 0), Visible = false };
            _bannerText = new Label { Text = "NEW VERSION", Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = false, Location = new Point(12, 0), Size = new Size(370, 44), TextAlign = ContentAlignment.MiddleLeft };
            _bannerBtn = new Button { Text = "Actualizar", Location = new Point(400, 8), Size = new Size(105, 28) };
            _bannerBtn.Click += async (s, e) => await Run(DoUpdate);
            _banner.Controls.Add(_bannerText); _banner.Controls.Add(_bannerBtn);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            var title = new Label { Text = "FlyMod", Font = new Font("Segoe UI", 16f, FontStyle.Bold), Location = new Point(12, 8), AutoSize = true };
            _ver = new Label { Text = "Version: " + LocalVersion() + " " + App.Canal, Location = new Point(14, 44), AutoSize = true, ForeColor = Color.DimGray };

            var lblPath = new Label { Text = "Carpeta del juego:", Location = new Point(14, 72), AutoSize = true };
            _path = new TextBox { Location = new Point(14, 92), Size = new Size(400, 23), Text = FindGameDir() ?? "" };
            _browse = new Button { Text = "Buscar...", Location = new Point(420, 91), Size = new Size(85, 25) };
            _browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Carpeta donde esta '" + App.ExeName + "'" })
                    if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
            };

            _install = new Button { Text = "Instalar", Location = new Point(14, 128), Size = new Size(160, 34), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            _uninstall = new Button { Text = "Desinstalar", Location = new Point(180, 128), Size = new Size(160, 34) };
            _repair = new Button { Text = "Reparar", Location = new Point(346, 128), Size = new Size(159, 34) };
            _install.Click += async (s, e) => await Run(DoInstall);
            _uninstall.Click += async (s, e) => await Run(DoUninstall);
            _repair.Click += async (s, e) => await Run(DoRepair);

            _log = new TextBox { Location = new Point(14, 174), Size = new Size(491, 166), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

            body.Controls.AddRange(new Control[] { title, _ver, lblPath, _path, _browse, _install, _uninstall, _repair, _log });
            Controls.Add(body);
            Controls.Add(_banner);

            Log("Controles: 8 = menu, F8 = fly, G = voltereta, H = truco aereo, B = boost, N = espectar (se cambian en el menu, pestana Teclas).");
            if (string.IsNullOrEmpty(_path.Text)) Log("No encontre el juego automaticamente. Usa 'Buscar...'.");
            Shown += async (s, e) => await CheckUpdate();
        }

        // ------------------------------------------------------------ util
        void Log(string t)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(t))); return; }
            _log.AppendText(t + Environment.NewLine);
        }

        async Task Run(Action a)
        {
            foreach (var b in new[] { _install, _uninstall, _repair, _browse, _bannerBtn }) b.Enabled = false;
            try { await Task.Run(a); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally { foreach (var b in new[] { _install, _uninstall, _repair, _browse, _bannerBtn }) b.Enabled = true; }
        }

        static Dictionary<string, string> ParseKv(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$");
                if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return d;
        }

        string LocalVersion()
        {
            try { if (File.Exists(LocalVersionFile)) { string v; if (ParseKv(File.ReadAllText(LocalVersionFile)).TryGetValue("version", out v)) return v; } } catch { }
            return "0.0.0";
        }

        static string Sha256(string file)
        {
            using (var s = SHA256.Create()) using (var f = File.OpenRead(file))
                return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "");
        }

        static WebClient Web()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            wc.Headers[HttpRequestHeader.UserAgent] = "FlyModInstaller";
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github.raw";
            return wc;
        }

        string GameDir()
        {
            string d = null;
            Invoke(new Action(() => d = _path.Text.Trim().Trim('"')));
            if (string.IsNullOrEmpty(d) || !File.Exists(Path.Combine(d, App.ExeName)))
                throw new Exception("En la carpeta indicada no esta '" + App.ExeName + "'.");
            return d;
        }

        void WaitGameClosed()
        {
            while (Process.GetProcessesByName(App.ProcName).Length > 0)
            {
                DialogResult r = DialogResult.Cancel;
                Invoke(new Action(() => r = MessageBox.Show(this, "Cierra Drive Up Together y pulsa Aceptar.", "Juego abierto", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)));
                if (r != DialogResult.OK) throw new Exception("Cancelado: el juego sigue abierto.");
            }
        }

        // Si no hay permiso de escritura, reabre el instalador como administrador
        bool EnsureWritable(string dir)
        {
            try { var p = Path.Combine(dir, ".flymod_probe"); File.WriteAllText(p, "x"); File.Delete(p); return true; }
            catch (UnauthorizedAccessException)
            {
                Log("Hace falta permiso de administrador, reabriendo...");
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                Invoke(new Action(Close));
                return false;
            }
        }

        static void CopyDir(string src, string dst)
        {
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }

        // ------------------------------------------------------------ Steam
        static string FindGameDir()
        {
            var libs = new List<string>();
            foreach (var (root, key, val) in new[] {
                (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") })
            {
                try { using (var k = root.OpenSubKey(key)) { var v = k?.GetValue(val) as string; if (!string.IsNullOrEmpty(v)) libs.Add(v.Replace('/', '\\')); } } catch { }
            }
            foreach (var steam in libs.ToArray())
            {
                var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            foreach (var l in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var d = Path.Combine(l, @"steamapps\common\Drive UP Together");
                if (File.Exists(Path.Combine(d, App.ExeName))) return d;
            }
            return null;
        }

        // ------------------------------------------------------------ acciones
        async Task CheckUpdate()
        {
            Log("Buscando actualizaciones...");
            try
            {
                string txt = await Task.Run(() =>
                {
                    try { return Web().DownloadString(App.UpdateUrl); }          // sin cache, limite 60/h por IP
                    catch { return Web().DownloadString(App.UpdateUrlRaw); }    // respaldo (cache de hasta 5 min)
                });
                _remote = ParseKv(txt);
                string rv;
                if (!_remote.TryGetValue("version", out rv)) { Log("version.txt remoto invalido."); return; }
                if (new Version(rv) > new Version(LocalVersion()))
                {
                    string notas; _remote.TryGetValue("notas", out notas);
                    _bannerText.Text = "NEW VERSION " + rv + " " + App.Canal + " disponible" + (string.IsNullOrEmpty(notas) ? "" : "  -  " + notas);
                    _banner.Visible = true;
                    Log("Hay una version nueva: " + rv + " (tienes " + LocalVersion() + ").");
                }
                else Log("Estas al dia (" + LocalVersion() + ").");
            }
            catch (Exception ex) { Log("No se pudo comprobar actualizaciones: " + ex.Message); }
        }

        void DoUpdate()
        {
            if (_remote == null) throw new Exception("No hay datos de actualizacion.");
            string tmp = Path.Combine(Path.GetTempPath(), "flymod_upd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var wc = Web();
            string v; _remote.TryGetValue("version", out v);

            // 1) mod
            string url, sha;
            if (!_remote.TryGetValue("mod", out url)) throw new Exception("version.txt no tiene 'mod='.");
            Log("Descargando FlyMod " + v + "...");
            var dll = Path.Combine(tmp, "FlyMod.dll");
            wc.DownloadFile(url, dll);
            if (_remote.TryGetValue("mod_sha256", out sha) && !string.Equals(Sha256(dll), sha, StringComparison.OrdinalIgnoreCase))
                throw new Exception("El FlyMod.dll descargado no coincide con el hash publicado.");
            var payloadDll = Path.Combine(Payload, @"juego\BepInEx\plugins\FlyMod.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadDll));
            File.Copy(dll, payloadDll, true);

            // si el mod ya esta instalado en el juego, actualizarlo tambien
            try
            {
                var gameDll = Path.Combine(GameDir(), @"BepInEx\plugins\FlyMod.dll");
                if (File.Exists(gameDll)) { WaitGameClosed(); File.Copy(dll, gameDll, true); Log("Mod actualizado en el juego."); }
            }
            catch (Exception ex) { Log("(No se actualizo en el juego: " + ex.Message + ". Pulsa Instalar.)"); }

            File.WriteAllText(LocalVersionFile, "version=" + v);

            // 2) el propio instalador
            if (_remote.TryGetValue("instalador", out url))
            {
                string sha2; _remote.TryGetValue("instalador_sha256", out sha2);
                if (string.IsNullOrEmpty(sha2) || !string.Equals(sha2, Sha256(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                {
                    Log("Actualizando el instalador...");
                    var exe = Path.Combine(tmp, "FlyModInstaller.exe");
                    wc.DownloadFile(url, exe);
                    if (!string.IsNullOrEmpty(sha2) && !string.Equals(Sha256(exe), sha2, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("El instalador descargado no coincide con el hash publicado.");
                    // Windows deja renombrar un exe en uso: se aparta el viejo y se pone el nuevo en su lugar
                    var me = Application.ExecutablePath;
                    var old = me + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(me, old);
                    File.Move(exe, me);
                    Invoke(new Action(() => { MessageBox.Show(this, "Actualizado a " + v + ". El instalador se reiniciara.", "FlyMod"); Process.Start(me); Close(); }));
                    return;
                }
            }

            Invoke(new Action(() => { _banner.Visible = false; _ver.Text = "Version: " + v + " " + App.Canal; }));
            Log("Actualizado a " + v + ".");
        }

        void DoInstall()
        {
            var game = GameDir();
            if (!EnsureWritable(game)) return;
            WaitGameClosed();
            Log("Copiando BepInEx + FlyMod en " + game + " ...");
            CopyDir(Path.Combine(Payload, "juego"), game);
            Log("Instalado. Abre el juego desde Steam y pulsa 8 para el menu. Nuevo mapa: Infinito (Mod) en la lista de mapas.");
            Log("Al usar cualquier truco se bloquean leaderboard, logros y recompensas hasta cerrar el juego.");
        }

        void DoUninstall()
        {
            var game = GameDir();
            if (!EnsureWritable(game)) return;
            WaitGameClosed();
            foreach (var n in new[] { "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", "changelog.txt", ".doorstop_version" })
            {
                var p = Path.Combine(game, n);
                if (Directory.Exists(p)) { Directory.Delete(p, true); Log("Borrado " + n); }
                else if (File.Exists(p)) { File.Delete(p); Log("Borrado " + n); }
            }
            Log("Desinstalado. El juego queda como original.");
        }

        void DoRepair()
        {
            var game = GameDir();
            if (!EnsureWritable(game)) return;
            WaitGameClosed();
            var interop = Path.Combine(game, @"BepInEx\interop");
            if (!Directory.Exists(interop)) throw new Exception("No hay BepInEx\\interop. Instala y abre el juego una vez.");
            Log("Reparando librerias generadas por BepInEx...");
            var psi = new ProcessStartInfo(Path.Combine(Payload, @"herramientas\Reparar.exe"), "\"" + interop + "\" --write")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                string o; while ((o = p.StandardOutput.ReadLine()) != null) Log("  " + o);
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("Reparar.exe fallo.");
            }
            Log("Listo. Vuelve a abrir el juego.");
        }
    }
}
