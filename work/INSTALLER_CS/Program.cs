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
    // ---- payload manifest ----
    class ImgEntry { public string name; public string file; public long size; public string md5_vanilla; public string md5_mod; }
    class FullPac { public string name; public long size; public string md5; }
    class Manifest { public string game_version; public List<FullPac> full_pacs; public string image_pac; public List<ImgEntry> image_entries; }

    static class Program
    {
        const string AppId = "3375780";                       // Trails in the Sky 1st Chapter (Steam)
        const string InstallDirName = "Trails in the Sky 1st Chapter";

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--status")
            {
                string outp = Path.Combine(Path.GetTempPath(), "trailsar_status.txt");
                try
                {
                    string g = DetectGamePath();
                    var man = LoadManifest();
                    File.WriteAllText(outp, "game=" + (g ?? "NULL") + "|status=" + (g != null ? Status(g) : "nogame") +
                        "|entries=" + (man?.image_entries?.Count ?? -1) + "|pacs=" + (man?.full_pacs?.Count ?? -1));
                }
                catch (Exception e) { File.WriteAllText(outp, "ERR: " + e); }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(true);
            Application.Run(new MainForm());
        }

        // ---- Steam detection (same approach as the FF7 Rebirth installer) ----
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

        public static string PakDir(string gameRoot) => Path.Combine(gameRoot, "pac", "steam");
        public static string BackupDir(string gameRoot) => Path.Combine(gameRoot, "_arabic_mod_backup");
        public static string ImagePac(string gameRoot) => Path.Combine(PakDir(gameRoot), "image.pac");

        public static bool IsValidGameFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            try { return File.Exists(ImagePac(folder)) && File.Exists(Path.Combine(PakDir(folder), "table_en.pac")); }
            catch { return false; }
        }

        // ---- FPAC index + md5 ----
        static Dictionary<string, (long loc, long sz)> PacIndex(string path)
        {
            using var f = File.OpenRead(path);
            byte[] head = new byte[64]; f.Read(head, 0, 64);
            uint cnt = BitConverter.ToUInt32(head, 4);
            uint hsz = BitConverter.ToUInt32(head, 8);
            f.Seek(0, SeekOrigin.Begin);
            int bufLen = (int)Math.Min(f.Length, (long)hsz + 1_000_000);
            byte[] b = new byte[bufLen]; int r = 0; while (r < bufLen) { int k = f.Read(b, r, bufLen - r); if (k <= 0) break; r += k; }
            var d = new Dictionary<string, (long, long)>();
            for (int i = 0; i < cnt; i++)
            {
                int bse = 16 + i * 32;
                long no = (long)BitConverter.ToUInt64(b, bse + 8);
                long sz = (long)BitConverter.ToUInt64(b, bse + 16);
                long loc = (long)BitConverter.ToUInt64(b, bse + 24);
                int e = (int)no; while (e < b.Length && b[e] != 0) e++;
                string name = Encoding.UTF8.GetString(b, (int)no, e - (int)no);
                d[name] = (loc, sz);
            }
            return d;
        }

        static string Md5At(string path, long loc, long sz)
        {
            using var f = File.OpenRead(path); f.Seek(loc, SeekOrigin.Begin);
            byte[] buf = new byte[sz]; int got = 0; while (got < sz) { int k = f.Read(buf, got, (int)sz - got); if (k <= 0) break; got += k; }
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(buf)).ToLowerInvariant();
        }

        static Manifest _man;
        static string _payloadTmp;
        static Manifest LoadManifest()
        {
            if (_man != null) return _man;
            _payloadTmp = Path.Combine(Path.GetTempPath(), "TrailsAr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_payloadTmp);
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("payload.zip"))
            {
                if (s == null) throw new Exception("ملفات التعريب المضمّنة غير موجودة داخل المثبّت.");
                using var z = new ZipArchive(s, ZipArchiveMode.Read);
                z.ExtractToDirectory(_payloadTmp, true);
            }
            _man = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(_payloadTmp, "manifest.json")),
                new JsonSerializerOptions { IncludeFields = true });
            return _man;
        }

        // "ready" | "installed" | "badversion" | "missing"
        // Version check is by ENTRY SIZE (same texture slots = compatible v1.06.2). We do NOT require the
        // bytes to match a recorded "vanilla" hash — a clean install can differ slightly from our reference,
        // and the backup captures whatever is actually there, so install works on any compatible base.
        public static string Status(string gameRoot)
        {
            string img = ImagePac(gameRoot);
            if (!File.Exists(img)) return "missing";
            var man = LoadManifest();
            var idx = PacIndex(img);
            int mod = 0;
            foreach (var e in man.image_entries)
            {
                if (!idx.TryGetValue(e.name, out var v)) return "missing";
                if (v.sz != e.size) return "badversion";
                if (Md5At(img, v.loc, v.sz) == e.md5_mod) mod++;
            }
            return mod == man.image_entries.Count ? "installed" : "ready";
        }

        public static bool IsInstalled(string gameRoot)
        {
            try { return Status(gameRoot) == "installed"; } catch { return false; }
        }

        public static void Install(string gameRoot, Action<string> progress)
        {
            EnsureGameClosed(gameRoot);
            progress("جارٍ فحص ملفات اللعبة…");
            string st = Status(gameRoot);
            if (st == "missing") throw new Exception("لم يُعثر على ملفات اللعبة (pac\\steam\\image.pac). تأكّد من اختيار مجلد اللعبة الصحيح.");
            if (st == "badversion") throw new Exception("إصدار اللعبة لا يطابق هذا التعريب (مبني على الإصدار v1.06.2).\nحدّث اللعبة إلى الإصدار الصحيح ثم أعد المحاولة.");
            if (st == "installed") { progress("التعريب مُثبّت بالفعل ✔"); return; }
            // st == "ready": back up the current state, then apply the mod

            var man = LoadManifest();
            string pak = PakDir(gameRoot), bak = BackupDir(gameRoot), img = ImagePac(gameRoot);

            // 1) backup originals (only if not already backed up) — 4 pacs + the 22 vanilla image entries
            progress("جارٍ إنشاء نسخة احتياطية…");
            Directory.CreateDirectory(Path.Combine(bak, "pacs"));
            Directory.CreateDirectory(Path.Combine(bak, "image_vanilla"));
            foreach (var p in man.full_pacs)
            {
                string dst = Path.Combine(bak, "pacs", p.name);
                if (!File.Exists(dst)) File.Copy(Path.Combine(pak, p.name), dst, false);
            }
            var idx = PacIndex(img);
            foreach (var e in man.image_entries)
            {
                string dst = Path.Combine(bak, "image_vanilla", e.file);
                if (!File.Exists(dst))
                {
                    var v = idx[e.name];
                    using var fi = File.OpenRead(img); fi.Seek(v.loc, SeekOrigin.Begin);
                    byte[] buf = new byte[v.sz]; int got = 0; while (got < v.sz) { int k = fi.Read(buf, got, (int)v.sz - got); if (k <= 0) break; got += k; }
                    File.WriteAllBytes(dst, buf);
                }
            }

            // 2) drop in the 4 modded pacs
            foreach (var p in man.full_pacs)
            {
                progress("جارٍ تثبيت: " + p.name + " …");
                File.Copy(Path.Combine(_payloadTmp, "pacs", p.name), Path.Combine(pak, p.name), true);
            }

            // 3) patch the 22 image.pac entries in place
            progress("جارٍ تحديث الصور داخل image.pac…");
            PatchImage(img, e => File.ReadAllBytes(Path.Combine(_payloadTmp, "image_entries", e.file)));
            progress("تم التثبيت بنجاح ✔");
        }

        public static void Uninstall(string gameRoot, Action<string> progress)
        {
            EnsureGameClosed(gameRoot);
            string bak = BackupDir(gameRoot), pak = PakDir(gameRoot), img = ImagePac(gameRoot);
            if (!Directory.Exists(bak)) throw new Exception("لا توجد نسخة احتياطية للاستعادة.\nاستخدم «التحقق من سلامة ملفات اللعبة» في Steam.");
            var man = LoadManifest();
            progress("جارٍ استعادة الملفات الأصلية…");
            foreach (var p in man.full_pacs)
            {
                string src = Path.Combine(bak, "pacs", p.name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(pak, p.name), true);
            }
            progress("جارٍ استعادة الصور الأصلية…");
            PatchImage(img, e => File.ReadAllBytes(Path.Combine(bak, "image_vanilla", e.file)));
            progress("تمت الإزالة ✔");
        }

        static void PatchImage(string img, Func<ImgEntry, byte[]> getBytes)
        {
            var man = LoadManifest();
            var idx = PacIndex(img);
            using var f = new FileStream(img, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            foreach (var e in man.image_entries)
            {
                var v = idx[e.name];
                byte[] data = getBytes(e);
                if (data.Length != v.sz) throw new Exception("حجم غير متطابق: " + e.name);
                f.Seek(v.loc, SeekOrigin.Begin); f.Write(data, 0, data.Length);
            }
        }

        static void EnsureGameClosed(string gameRoot)
        {
            string t = Path.Combine(PakDir(gameRoot), "table_en.pac");
            try { using (new FileStream(t, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException) { throw new Exception("يبدو أن اللعبة قيد التشغيل. أغلقها تمامًا ثم أعد المحاولة."); }
            catch (UnauthorizedAccessException) { throw new Exception("تعذّر الوصول لملفات اللعبة. شغّل المثبّت كمسؤول (Run as administrator)."); }
        }
    }

    // ===================== modern UI (reused from FF7 Rebirth installer) =====================
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(18, 22, 34);
        public static readonly Color Card = Color.FromArgb(30, 36, 52);
        public static readonly Color Gold = Color.FromArgb(96, 196, 232);
        public static readonly Color GoldHover = Color.FromArgb(132, 216, 246);
        public static readonly Color Red = Color.FromArgb(168, 70, 58);
        public static readonly Color RedHover = Color.FromArgb(192, 86, 72);
        public static readonly Color Ink = Color.FromArgb(10, 16, 26);
        public static readonly Color Text = Color.FromArgb(228, 234, 244);
        public static readonly Color Muted = Color.FromArgb(140, 150, 168);
        static PrivateFontCollection _pfc; public static FontFamily Family;
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
        public static Font Latin(float size, FontStyle style = FontStyle.Bold) => new Font("Segoe UI", size, style, GraphicsUnit.Point);
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
        public Color Base = Ui.Gold; public Color Hover = Ui.GoldHover; public Color Fg = Ui.Ink; public int Radius = 14; bool _hover;
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
            Color fill = !Enabled ? Color.FromArgb(60, 66, 80) : (_hover ? Hover : Base);
            using (var path = Ui.Round(rect, Radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(Enabled ? Fg : Color.FromArgb(130, 138, 150));
            g.DrawString(Text, Font, tb, rect, sf);
        }
    }

    public class RoundPanel : Panel
    {
        public Color Fill = Ui.Card; public int Radius = 12;
        public RoundPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Ui.Round(r, Radius); using var b = new SolidBrush(Fill); g.FillPath(b, path);
        }
    }

    public class MainForm : Form
    {
        string gamePath; Label lblStatus, lblPath; RoundButton btnInstall, btnUninstall; LinkLabel btnBrowse; bool busy;
        public MainForm()
        {
            Ui.LoadFont();
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 700); BackColor = Ui.Bg; RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            Font = Ui.F(11f); Region = new Region(Ui.Round(new Rectangle(0, 0, Width, Height), 18)); MouseDown += DragStart;

            var close = new Label { Text = "✕", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Ui.Muted, AutoSize = false, Size = new Size(34, 30), Location = new Point(12, 12), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            close.Click += (s, e) => Close(); close.MouseEnter += (s, e) => close.ForeColor = Ui.Red; close.MouseLeave += (s, e) => close.ForeColor = Ui.Muted; Controls.Add(close);

            var logo = new PictureBox { Image = LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Ui.Bg, Size = new Size(540, 166), Location = new Point((ClientSize.Width - 540) / 2, 54) };
            logo.MouseDown += DragStart; Controls.Add(logo);
            var subtitle = new Label { Text = "التعريب العربي الكامل", Font = Ui.F(15f, FontStyle.Bold), ForeColor = Ui.Gold, AutoSize = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 38), Location = new Point(0, 232) };
            subtitle.MouseDown += DragStart; Controls.Add(subtitle);

            var card = new RoundPanel { Size = new Size(524, 92), Location = new Point((ClientSize.Width - 524) / 2, 286), Fill = Ui.Card };
            lblPath = new Label { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 10), ForeColor = Ui.Text, Font = Ui.F(10.5f), UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };
            card.Controls.Add(lblPath); Controls.Add(card);

            btnInstall = new RoundButton { Text = "تثبيت اللغة العربية", Font = Ui.F(15f, FontStyle.Bold), Size = new Size(524, 70), Location = new Point((ClientSize.Width - 524) / 2, 400), Base = Ui.Gold, Hover = Ui.GoldHover, Fg = Ui.Ink };
            btnInstall.Click += OnInstall; Controls.Add(btnInstall);
            btnUninstall = new RoundButton { Text = "إزالة اللغة العربية", Font = Ui.F(13.5f, FontStyle.Bold), Size = new Size(524, 56), Location = new Point((ClientSize.Width - 524) / 2, 484), Base = Ui.Red, Hover = Ui.RedHover, Fg = Ui.Text };
            btnUninstall.Click += OnUninstall; Controls.Add(btnUninstall);

            lblStatus = new Label { AutoSize = false, Font = Ui.F(11f), UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Muted, Size = new Size(ClientSize.Width, 32), Location = new Point(0, 558) };
            lblStatus.MouseDown += DragStart; Controls.Add(lblStatus);
            btnBrowse = new LinkLabel { Text = "تحديد مجلد اللعبة يدويًا", AutoSize = false, Font = Ui.F(10f), LinkColor = Ui.Muted, ActiveLinkColor = Ui.Gold, LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 26), Location = new Point(0, 600), BackColor = Color.Transparent };
            btnBrowse.Click += OnBrowse; Controls.Add(btnBrowse);
            var footer = new Label { Text = "تعريب وإعداد:  Kindiboy", Font = Ui.F(10f, FontStyle.Bold), ForeColor = Color.FromArgb(120, 160, 190), AutoSize = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(ClientSize.Width, 24), Location = new Point(0, 662) };
            footer.MouseDown += DragStart; Controls.Add(footer);

            try { gamePath = Program.DetectGamePath(); } catch { gamePath = null; }
            RefreshState();
        }

        static Image LoadLogo()
        {
            try
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;   // kept alive by the returned Image
                return Image.FromStream(ms);
            }
            catch { return null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(70, Ui.Gold), 1);
            using var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), 18);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.DrawPath(pen, path);
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int wp, int lp);
        void DragStart(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } }

        void RefreshState()
        {
            if (Program.IsValidGameFolder(gamePath))
            {
                bool installed = Program.IsInstalled(gamePath);
                lblPath.ForeColor = Ui.Text; lblPath.Text = "تم العثور على اللعبة" + Environment.NewLine + Trim(gamePath);
                btnInstall.Enabled = !busy; btnUninstall.Enabled = !busy && installed;
                if (installed) SetStatus("✔ اللغة العربية مُثبّتة حاليًا", Ui.Gold); else SetStatus("اللغة العربية غير مُثبّتة", Ui.Muted);
            }
            else
            {
                lblPath.ForeColor = Ui.Red; lblPath.Text = "لم يتم العثور على اللعبة" + Environment.NewLine + "الرجاء تحديد المجلد يدويًا";
                btnInstall.Enabled = false; btnUninstall.Enabled = false; SetStatus("في انتظار تحديد مجلد اللعبة", Ui.Muted);
            }
            btnInstall.Invalidate(); btnUninstall.Invalidate();
        }
        static string Trim(string p) { if (p != null && p.Length > 64) return "…" + p.Substring(p.Length - 62); return p; }
        void SetStatus(string text, Color color) { lblStatus.Text = text; lblStatus.ForeColor = color; }
        void Progress(string text) { if (InvokeRequired) BeginInvoke(new Action(() => SetStatus(text, Color.FromArgb(120, 190, 230)))); else SetStatus(text, Color.FromArgb(120, 190, 230)); }
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
            if (MessageBox.Show(this, "سيتم تعديل ملفات اللعبة لإضافة التعريب (مع نسخة احتياطية يمكن استعادتها).\nتأكد من إغلاق اللعبة تمامًا.\n\nالمتابعة؟", "تثبيت", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetBusy(true); string root = gamePath;
            var t = new Thread(() =>
            {
                try { Program.Install(root, Progress); BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, "تم تثبيت اللغة العربية بنجاح!\n\nشغّل اللعبة، واضبط لغة النصوص على «English» لتظهر بالعربية.", "تم التثبيت", MessageBoxButtons.OK, MessageBoxIcon.Information); })); }
                catch (Exception ex) { BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, ex.Message, "خطأ في التثبيت", MessageBoxButtons.OK, MessageBoxIcon.Error); })); }
            }) { IsBackground = true }; t.Start();
        }

        void OnUninstall(object sender, EventArgs e)
        {
            if (busy) return; SetBusy(true); string root = gamePath;
            var t = new Thread(() =>
            {
                try { Program.Uninstall(root, Progress); BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, "تمت إزالة التعريب واستعادة الملفات الأصلية.", "تمت الإزالة", MessageBoxButtons.OK, MessageBoxIcon.Information); })); }
                catch (Exception ex) { BeginInvoke(new Action(() => { SetBusy(false); MessageBox.Show(this, ex.Message, "خطأ في الإزالة", MessageBoxButtons.OK, MessageBoxIcon.Error); })); }
            }) { IsBackground = true }; t.Start();
        }
    }
}
