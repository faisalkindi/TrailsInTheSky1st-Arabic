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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrailsArabic
{
    // ---- payload manifest (v1.1.0, game v1.07) ----
    class ImgEntry { public string name; public string file; public long size; public string md5_vanilla; public string md5_mod; }
    class FullPac { public string name; public long size; public string md5; }
    class Manifest { public string mod_version; public string game_version; public List<FullPac> full_pacs; public string image_pac; public List<ImgEntry> image_entries; }

    static class Program
    {
        public const string Version = "1.1.0";
        const string AppId = "3375780";                       // Trails in the Sky 1st Chapter (Steam)
        const string InstallDirName = "Trails in the Sky 1st Chapter";

        [STAThread]
        static int Main(string[] args)
        {
            // headless: --detect | --status <root> | --install <root> | --uninstall <root>
            if (args.Length > 0)
            {
                try
                {
                    if (args[0] == "--detect") { Console.WriteLine(DetectGamePath() ?? "(not found)"); return 0; }
                    if (args[0] == "--status") { Console.WriteLine(Status(args[1])); return 0; }
                    if (args[0] == "--install") { Install(args[1], Console.WriteLine); return 0; }
                    if (args[0] == "--uninstall") { Uninstall(args[1], Console.WriteLine); return 0; }
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(true);
            Application.Run(new MainForm());
            return 0;
        }

        // ---- Steam detection ----
        public static string DetectGamePath()
        {
            try
            {
                string steam = GetSteamPath();
                if (steam == null) return null;
                var libs = new List<string> { steam };
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                foreach (string lib in libs)
                {
                    string acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                    string installdir = InstallDirName;
                    if (File.Exists(acf))
                    {
                        var im = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s*\"([^\"]+)\"");
                        if (im.Success) installdir = im.Groups[1].Value;
                    }
                    string game = Path.Combine(lib, "steamapps", "common", installdir);
                    if (IsValidGameFolder(game)) return game;
                }
            }
            catch { }
            return null;
        }

        static string GetSteamPath()
        {
            try { if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string s1 && Directory.Exists(s1)) return s1.Replace('/', '\\'); } catch { }
            try { if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string s2 && Directory.Exists(s2)) return s2; } catch { }
            return null;
        }

        public static string PacDir(string gameRoot) => Path.Combine(gameRoot, "pac", "steam");
        public static string BackupDir(string gameRoot) => Path.Combine(gameRoot, "_arabic_mod_backup");
        public static string ImagePac(string gameRoot) => Path.Combine(PacDir(gameRoot), "image.pac");

        // installer.py: find_game() accepts any folder with pac\steam
        public static bool IsValidGameFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            try { return Directory.Exists(PacDir(folder)); } catch { return false; }
        }

        // ---- FPAC index + md5 (installer.py pac_index / md5_at / md5_file) ----
        static Dictionary<string, (long loc, long sz)> PacIndex(string path)
        {
            using var f = File.OpenRead(path);
            byte[] head = new byte[64]; f.Read(head, 0, 64);
            uint cnt = BitConverter.ToUInt32(head, 4);
            uint hsz = BitConverter.ToUInt32(head, 8);
            f.Seek(0, SeekOrigin.Begin);
            int bufLen = (int)Math.Min(f.Length, (long)hsz + 400_000);
            byte[] b = new byte[bufLen]; int r = 0; while (r < bufLen) { int k = f.Read(b, r, bufLen - r); if (k <= 0) break; r += k; }
            var d = new Dictionary<string, (long, long)>();
            for (int i = 0; i < cnt; i++)
            {
                int bse = 16 + i * 32;
                long no = (long)BitConverter.ToUInt64(b, bse + 8);
                long sz = (long)BitConverter.ToUInt64(b, bse + 16);
                long loc = (long)BitConverter.ToUInt64(b, bse + 24);
                int e = (int)no; while (e < b.Length && b[e] != 0) e++;
                d[Encoding.UTF8.GetString(b, (int)no, e - (int)no)] = (loc, sz);
            }
            return d;
        }

        static byte[] ReadAt(string path, long loc, long sz)
        {
            using var f = File.OpenRead(path); f.Seek(loc, SeekOrigin.Begin);
            byte[] buf = new byte[sz]; int got = 0; while (got < sz) { int k = f.Read(buf, got, (int)sz - got); if (k <= 0) break; got += k; }
            return buf;
        }
        static string Md5(byte[] data) { using var md5 = MD5.Create(); return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant(); }
        static string Md5At(string path, long loc, long sz) => Md5(ReadAt(path, loc, sz));
        static string Md5File(string path) { using var md5 = MD5.Create(); using var f = File.OpenRead(path); return Convert.ToHexString(md5.ComputeHash(f)).ToLowerInvariant(); }

        static Manifest _man;
        static string _payloadTmp;
        static string Res(params string[] parts) { LoadManifest(); return Path.Combine(_payloadTmp, Path.Combine(parts)); }
        public static Manifest LoadManifest()
        {
            if (_man != null) return _man;
            _payloadTmp = Path.Combine(Path.GetTempPath(), "TrailsAr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_payloadTmp);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (s == null) throw new Exception("ملفات التعريب المضمّنة غير موجودة داخل المثبّت.");
                using var z = new ZipArchive(s, ZipArchiveMode.Read);
                z.ExtractToDirectory(_payloadTmp, true);
            }
            _man = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(_payloadTmp, "manifest.json")),
                new JsonSerializerOptions { IncludeFields = true });
            return _man;
        }

        // installer.py Installer.status(): vanilla | installed | partial | mismatch | missing
        public static string Status(string gameRoot)
        {
            string img = ImagePac(gameRoot);
            if (!File.Exists(img)) return "missing";
            var man = LoadManifest();
            var idx = PacIndex(img);
            int van = 0, mod = 0;
            foreach (var e in man.image_entries)
            {
                if (!idx.TryGetValue(e.name, out var v)) return "mismatch";
                if (v.sz != e.size) return "mismatch";
                string h = Md5At(img, v.loc, v.sz);
                if (h == e.md5_vanilla) van++;
                else if (h == e.md5_mod) mod++;
                else return "mismatch";
            }
            int n = man.image_entries.Count;
            if (mod == n) return "installed";
            if (van == n) return "vanilla";
            return "partial";
        }

        public static bool IsInstalled(string gameRoot)
        {
            try { return Status(gameRoot) == "installed"; } catch { return false; }
        }

        // installer.py Installer._backup()
        static void Backup(string gameRoot, Action<string> log)
        {
            var man = LoadManifest();
            string pac = PacDir(gameRoot), bak = BackupDir(gameRoot), img = ImagePac(gameRoot);
            Directory.CreateDirectory(Path.Combine(bak, "pacs"));
            Directory.CreateDirectory(Path.Combine(bak, "image_vanilla"));
            string stamp = Path.Combine(bak, "game_version.txt");
            string prev = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
            bool fresh = prev != man.game_version;

            foreach (var p in man.full_pacs)
            {
                string src = Path.Combine(pac, p.name), dst = Path.Combine(bak, "pacs", p.name);
                if (Md5File(src) == p.md5) continue;                       // already the modded file — never back that up
                if (fresh || !File.Exists(dst)) File.Copy(src, dst, true);
            }
            var idx = PacIndex(img);
            foreach (var e in man.image_entries)
            {
                string dst = Path.Combine(bak, "image_vanilla", e.file);
                var v = idx[e.name];
                byte[] data = ReadAt(img, v.loc, v.sz);
                if (Md5(data) != e.md5_vanilla) continue;                  // not vanilla right now; never overwrite a good backup with it
                if (fresh || !File.Exists(dst)) File.WriteAllBytes(dst, data);
            }
            File.WriteAllText(stamp, man.game_version);
            log("تم حفظ نسخة احتياطية من الملفات الأصلية");
        }

        // installer.py Installer._vanilla_bytes(): user's backup first, then the copy shipped in the installer
        static byte[] VanillaBytes(string gameRoot, ImgEntry e)
        {
            string p = Path.Combine(BackupDir(gameRoot), "image_vanilla", e.file);
            if (File.Exists(p)) { byte[] b = File.ReadAllBytes(p); if (Md5(b) == e.md5_vanilla) return b; }
            p = Res("vanilla_entries", e.file);
            if (File.Exists(p)) { byte[] b = File.ReadAllBytes(p); if (Md5(b) == e.md5_vanilla) return b; }
            return null;
        }

        // installer.py Installer._patch_image(): null bytes => entry left as is
        static void PatchImage(string img, Func<ImgEntry, byte[]> getBytes)
        {
            var man = LoadManifest();
            var idx = PacIndex(img);
            using var f = new FileStream(img, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            foreach (var e in man.image_entries)
            {
                var v = idx[e.name];
                byte[] data = getBytes(e);
                if (data == null) continue;
                if (data.Length != v.sz) throw new Exception("حجم غير متطابق: " + e.name);
                f.Seek(v.loc, SeekOrigin.Begin); f.Write(data, 0, data.Length);
            }
        }

        // installer.py Installer.install()
        public static void Install(string gameRoot, Action<string> progress)
        {
            if (!IsValidGameFolder(gameRoot)) throw new Exception("هذا المجلد لا يحتوي على pac\\steam. اختر مجلد اللعبة الصحيح.");
            EnsureGameClosed(gameRoot);
            var man = LoadManifest();
            progress("جارٍ فحص ملفات اللعبة…");
            string st = Status(gameRoot);
            if (st == "missing") throw new Exception("لم يُعثر على image.pac — هل هذا مجلد اللعبة الصحيح؟");
            if (st == "mismatch") throw new Exception("ملفات اللعبة لا تطابق الإصدار الأصلي v" + man.game_version +
                " (إصدار مختلف أو معدَّل بمود آخر).\nأوقفنا التثبيت حفاظًا على ملفاتك — شغّل «التحقق من سلامة ملفات اللعبة» في Steam ثم أعد المحاولة.");
            if (st == "installed") { progress("التعريب مُثبّت بالفعل ✔"); return; }
            if (st == "partial") progress("وُجد تثبيت جزئي (تحديث اللعبة أعاد بعض الملفات) — سيُعاد التثبيت");

            try
            {
                progress("جارٍ إنشاء نسخة احتياطية…");
                Backup(gameRoot, progress);
                foreach (var p in man.full_pacs)
                {
                    progress("جارٍ تثبيت: " + p.name + " …");
                    File.Copy(Res("pacs", p.name), Path.Combine(PacDir(gameRoot), p.name), true);
                }
                progress("جارٍ تحديث الصور داخل image.pac…");
                PatchImage(ImagePac(gameRoot), e => File.ReadAllBytes(Res("image_entries", e.file)));
            }
            catch (UnauthorizedAccessException) { RunElevated("--install", gameRoot); }
            progress("تم التثبيت بنجاح ✔");
        }

        // installer.py Installer.restore()
        public static void Uninstall(string gameRoot, Action<string> progress)
        {
            EnsureGameClosed(gameRoot);
            string bak = BackupDir(gameRoot);
            if (!Directory.Exists(bak)) throw new Exception("لا توجد نسخة احتياطية للاستعادة.\nاستخدم «التحقق من سلامة ملفات اللعبة» في Steam.");
            var man = LoadManifest();
            try
            {
                progress("جارٍ استعادة الملفات الأصلية…");
                foreach (var p in man.full_pacs)
                {
                    string src = Path.Combine(bak, "pacs", p.name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(PacDir(gameRoot), p.name), true);
                }
                progress("جارٍ استعادة الصور الأصلية…");
                PatchImage(ImagePac(gameRoot), e => VanillaBytes(gameRoot, e));
            }
            catch (UnauthorizedAccessException) { RunElevated("--uninstall", gameRoot); }
            progress("تمت الإزالة ✔");
        }

        // asInvoker manifest (house rule): on a protected game folder, re-run this exe elevated in headless mode.
        static void RunElevated(string verb, string gameRoot)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas", Arguments = verb + " \"" + gameRoot + "\"" };
            Process p;
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { throw new Exception("تعذّر الوصول إلى مجلد اللعبة. شغّل المثبّت كمسؤول (Run as administrator)."); }
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("فشلت العملية بصلاحيات المسؤول. شغّل المثبّت كمسؤول (Run as administrator) وأعد المحاولة.");
        }

        static void EnsureGameClosed(string gameRoot)
        {
            string t = Path.Combine(PacDir(gameRoot), "table_en.pac");
            if (!File.Exists(t)) return;
            try { using (new FileStream(t, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
            catch (IOException) { throw new Exception("يبدو أن اللعبة قيد التشغيل. أغلقها تمامًا ثم أعد المحاولة."); }
        }
    }

    // ===================== modern UI (same visual language as the other Kindiboy installers) =====================

    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(10, 16, 28);
        public static readonly Color Card = Color.FromArgb(22, 32, 50);
        public static readonly Color Sky = Color.FromArgb(96, 196, 240);     // logo blue
        public static readonly Color SkyHover = Color.FromArgb(140, 216, 250);
        public static readonly Color Gold = Color.FromArgb(232, 196, 96);
        public static readonly Color Red = Color.FromArgb(190, 54, 44);
        public static readonly Color Ink = Color.FromArgb(8, 14, 24);
        public static readonly Color Text = Color.FromArgb(228, 234, 244);
        public static readonly Color Muted = Color.FromArgb(132, 146, 168);

        static PrivateFontCollection _pfc;
        public static FontFamily Family;

        public static void LoadFont()
        {
            try
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui_font.ttf");
                byte[] data = new byte[s.Length]; s.Read(data, 0, data.Length);
                IntPtr ptr = Marshal.AllocCoTaskMem(data.Length); Marshal.Copy(data, 0, ptr, data.Length);
                _pfc = new PrivateFontCollection(); _pfc.AddMemoryFont(ptr, data.Length); Marshal.FreeCoTaskMem(ptr);
                Family = _pfc.Families[0];
            }
            catch { Family = new FontFamily("Tahoma"); }
        }

        public static Font F(float size, FontStyle style = FontStyle.Regular) => new Font(Family, size, style, GraphicsUnit.Point);

        static Image LoadRes(string name)
        {
            try
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                using var img = Image.FromStream(s);
                return new Bitmap(img);
            }
            catch { return null; }
        }
        public static Image LoadBackground() => LoadRes("ui_bg.jpg");
        public static Image LoadLogo() => LoadRes("ui_logo.png");

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2; var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    public class RoundButton : Button
    {
        public Color Base = Ui.Sky; public Color Hover = Ui.SkyHover; public Color Fg = Ui.Ink;
        public int Radius = 14; public Color Outline = Color.Empty; bool _hover;
        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = Color.Transparent; Cursor = Cursors.Hand;
            MouseEnter += (s, e) => { _hover = true; Invalidate(); }; MouseLeave += (s, e) => { _hover = false; Invalidate(); };
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = !Enabled ? Color.FromArgb(40, 50, 66) : (_hover ? Hover : Base);
            using (var path = Ui.Round(rect, Radius))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
                if (Outline != Color.Empty) using (var pen = new Pen(Outline, 1f)) g.DrawPath(pen, path);
            }
            var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(Enabled ? Fg : Color.FromArgb(120, 132, 148));
            g.DrawString(Text, Font, tb, rect, sf);
        }
    }

    public class RoundPanel : Panel
    {
        public Color Fill = Ui.Card; public Color Border = Color.Empty; public int Radius = 12;
        public RoundPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Ui.Round(r, Radius); using var b = new SolidBrush(Fill); g.FillPath(b, path);
            if (Border != Color.Empty) using (var pen = new Pen(Border, 1f)) g.DrawPath(pen, path);
        }
    }

    public class MainForm : Form
    {
        string gamePath; Label lblStatus, lblPath; RoundButton btnInstall, btnUninstall; LinkLabel btnBrowse; bool busy;

        public MainForm()
        {
            Ui.LoadFont();
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 780); BackColor = Ui.Bg;
            BackgroundImage = Ui.LoadBackground(); BackgroundImageLayout = ImageLayout.Stretch;
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; Font = Ui.F(11f);
            Text = "Trails in the Sky 1st Arabic Installer v" + Program.Version;
            MouseDown += DragStart;

            var close = new Label { Text = "✕", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Ui.Muted, AutoSize = false, Size = new Size(34, 30), Location = new Point(14, 14), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, BackColor = Color.Transparent };
            close.Click += (s, e) => Close(); close.MouseEnter += (s, e) => close.ForeColor = Ui.Red; close.MouseLeave += (s, e) => close.ForeColor = Ui.Muted; Controls.Add(close);

            var ver = new Label { Text = "v" + Program.Version, Font = new Font("Segoe UI", 9f), ForeColor = Ui.Muted, AutoSize = false, Size = new Size(80, 30), Location = new Point(ClientSize.Width - 92, 14), TextAlign = ContentAlignment.MiddleRight, RightToLeft = RightToLeft.No, BackColor = Color.Transparent };
            ver.MouseDown += DragStart; Controls.Add(ver);

            var logo = new PictureBox { Image = Ui.LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Size = new Size(460, 150), Location = new Point((ClientSize.Width - 460) / 2, 66) };
            logo.MouseDown += DragStart; Controls.Add(logo);

            var subtitle = new Label { Text = "التعريب الكامل", Font = Ui.F(18f, FontStyle.Bold), ForeColor = Ui.Sky, AutoSize = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 58), Location = new Point(0, 236), BackColor = Color.Transparent };
            subtitle.MouseDown += DragStart; Controls.Add(subtitle);

            var tagline = new Label { Text = "ترجمة كاملة لكل الحوارات والقوائم", Font = Ui.F(9f), ForeColor = Ui.Muted, AutoSize = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 30), Location = new Point(0, 294), BackColor = Color.Transparent };
            tagline.MouseDown += DragStart; Controls.Add(tagline);

            var card = new RoundPanel { Size = new Size(480, 86), Location = new Point((ClientSize.Width - 480) / 2, 334), Fill = Color.FromArgb(205, Ui.Card), Border = Color.FromArgb(60, Ui.Sky) };
            lblPath = new Label { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 4), ForeColor = Ui.Text, Font = Ui.F(8.5f), UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };
            card.Controls.Add(lblPath); Controls.Add(card);

            btnInstall = new RoundButton { Text = "تثبيت اللغة العربية", Font = Ui.F(15f, FontStyle.Bold), Size = new Size(480, 64), Location = new Point((ClientSize.Width - 480) / 2, 440), Base = Ui.Sky, Hover = Ui.SkyHover, Fg = Ui.Ink, Radius = 14 };
            btnInstall.Click += OnInstall; Controls.Add(btnInstall);

            btnUninstall = new RoundButton { Text = "استعادة الملفات الأصلية", Font = Ui.F(12f, FontStyle.Bold), Size = new Size(480, 52), Location = new Point((ClientSize.Width - 480) / 2, 514), Base = Color.FromArgb(24, 36, 54), Hover = Color.FromArgb(36, 52, 74), Fg = Ui.Sky, Outline = Color.FromArgb(150, Ui.Sky), Radius = 14 };
            btnUninstall.Click += OnUninstall; Controls.Add(btnUninstall);

            lblStatus = new Label { AutoSize = false, Font = Ui.F(10f), UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Muted, Size = new Size(ClientSize.Width, 32), Location = new Point(0, 578), BackColor = Color.Transparent };
            lblStatus.MouseDown += DragStart; Controls.Add(lblStatus);

            btnBrowse = new LinkLabel { Text = "تحديد مجلد اللعبة يدويًا", AutoSize = false, Font = Ui.F(9f), LinkColor = Ui.Muted, ActiveLinkColor = Ui.Sky, LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 28), Location = new Point(0, 610), BackColor = Color.Transparent };
            btnBrowse.Click += OnBrowse; Controls.Add(btnBrowse);

            var kofi = new RoundButton { Text = "أعجبك التعريب؟ ادعمني على Ko-fi", Font = Ui.F(10.5f, FontStyle.Bold), Size = new Size(440, 46), Location = new Point((ClientSize.Width - 440) / 2, 676), Base = Color.FromArgb(20, 30, 46), Hover = Color.FromArgb(32, 46, 66), Fg = Ui.Text, Outline = Color.FromArgb(120, Ui.Sky), Radius = 14 };
            kofi.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("https://ko-fi.com/kindiboy") { UseShellExecute = true }); } catch { } };
            Controls.Add(kofi);

            var footer = new Label { Text = "تعريب وإعداد:  Kindiboy", Font = Ui.F(9.5f, FontStyle.Bold), ForeColor = Ui.Sky, AutoSize = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 28), Location = new Point(0, 736), BackColor = Color.Transparent };
            footer.MouseDown += DragStart; Controls.Add(footer);

            try { gamePath = Program.DetectGamePath(); } catch { gamePath = null; }
            RefreshState();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Region = new Region(Ui.Round(new Rectangle(0, 0, Width, Height), (int)(20 * DeviceDpi / 96f)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(70, Ui.Sky), 1);
            using var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), (int)(20 * DeviceDpi / 96f));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.DrawPath(pen, path);
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int wp, int lp);
        void DragStart(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } }

        void RefreshState()
        {
            if (Program.IsValidGameFolder(gamePath))
            {
                string st; try { st = Program.Status(gamePath); } catch { st = "mismatch"; }
                bool installed = st == "installed";
                string ver = "‪" + Program.LoadManifest().game_version + "‬";
                lblPath.ForeColor = st == "mismatch" ? Ui.Red : Ui.Text;
                lblPath.Text = (st == "mismatch" ? "ملفات اللعبة لا تطابق الإصدار " + ver : "تم العثور على اللعبة — الإصدار " + ver) + Environment.NewLine + Trim(gamePath);
                btnInstall.Enabled = !busy && st != "mismatch" && st != "missing";
                btnUninstall.Enabled = !busy && Directory.Exists(Program.BackupDir(gamePath));
                if (installed) SetStatus("✔ اللغة العربية مُثبّتة حاليًا", Ui.Sky);
                else if (st == "partial") SetStatus("تثبيت جزئي — أعد التثبيت", Ui.Gold);
                else if (st == "mismatch") SetStatus("تحقّق من سلامة الملفات في Steam ثم أعد المحاولة", Ui.Red);
                else SetStatus("اللغة العربية غير مُثبّتة", Ui.Muted);
            }
            else
            {
                lblPath.ForeColor = Ui.Red; lblPath.Text = "لم يتم العثور على اللعبة" + Environment.NewLine + "الرجاء تحديد المجلد يدويًا";
                btnInstall.Enabled = false; btnUninstall.Enabled = false; SetStatus("في انتظار تحديد مجلد اللعبة", Ui.Muted);
            }
            btnInstall.Invalidate(); btnUninstall.Invalidate();
        }

        static string Trim(string p)
        {
            if (p != null && p.Length > 30) p = "…" + p.Substring(p.Length - 28);
            return p == null ? null : "\u202A" + p + "\u202C";
        }
        void SetStatus(string text, Color color) { lblStatus.Text = text; lblStatus.ForeColor = color; }
        void Progress(string text) { if (InvokeRequired) BeginInvoke(new Action(() => SetStatus(text, Ui.Sky))); else SetStatus(text, Ui.Sky); }
        void SetBusy(bool b) { busy = b; Cursor = b ? Cursors.WaitCursor : Cursors.Default; RefreshState(); }

        void OnBrowse(object sender, EventArgs e)
        {
            if (busy) return;
            using var dlg = new FolderBrowserDialog { Description = "اختر مجلد اللعبة (الذي يحتوي على pac\\steam)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                string chosen = dlg.SelectedPath;
                if (!Program.IsValidGameFolder(chosen))
                {
                    string sub = Path.Combine(chosen, "Trails in the Sky 1st Chapter");
                    if (Program.IsValidGameFolder(sub)) chosen = sub;
                }
                if (Program.IsValidGameFolder(chosen)) gamePath = chosen;
                else MessageBox.Show(this, "هذا المجلد لا يحتوي على ملفات اللعبة (pac\\steam).", "مجلد غير صالح", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshState();
            }
        }

        void OnInstall(object sender, EventArgs e)
        {
            if (busy) return;
            if (MessageBox.Show(this, "سيتم تعديل ملفات اللعبة لإضافة التعريب (مع نسخة احتياطية في _arabic_mod_backup يمكن استعادتها).\nتأكد من إغلاق اللعبة تمامًا.\n\nالمتابعة؟", "تثبيت", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetBusy(true); string root = gamePath;
            var t = new Thread(() =>
            {
                try { Program.Install(root, Progress); BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, "تم تثبيت اللغة العربية بنجاح!\n\nشغّل اللعبة الآن.", "تم التثبيت", MessageBoxButtons.OK, MessageBoxIcon.Information); })); }
                catch (Exception ex) { BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, ex.Message, "خطأ في التثبيت", MessageBoxButtons.OK, MessageBoxIcon.Error); })); }
            }) { IsBackground = true }; t.Start();
        }

        void OnUninstall(object sender, EventArgs e)
        {
            if (busy) return; SetBusy(true); string root = gamePath;
            var t = new Thread(() =>
            {
                try { Program.Uninstall(root, Progress); BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, "تمت استعادة الملفات الأصلية.", "تمت الإزالة", MessageBoxButtons.OK, MessageBoxIcon.Information); })); }
                catch (Exception ex) { BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, ex.Message, "خطأ في الإزالة", MessageBoxButtons.OK, MessageBoxIcon.Error); })); }
            }) { IsBackground = true }; t.Start();
        }
    }
}
