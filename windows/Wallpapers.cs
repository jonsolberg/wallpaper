/*
 * Wallpapers from jonsolberg.se – a tray app for Windows.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 *
 * Sits in the notification area, changes the desktop picture on a schedule and picks the picture size
 * that fits the screen. Click the icon for the menu; double-click it for the next wallpaper.
 * The first run installs it for the current user (Programs folder, Start menu, Settings › Apps); --portable skips that.
 * Build: see build.sh (Mono mcs) – the result is a single .exe for .NET Framework 4.x, which every Windows 10/11 has.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Wallpapers from jonsolberg.se")]
[assembly: AssemblyProduct("Wallpapers")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Jon Solberg – GNU GPL v3")]
[assembly: AssemblyVersion("1.11.0.0")]
[assembly: AssemblyFileVersion("1.11.0.0")]

namespace JonSolberg.Wallpapers
{
    /// <summary>Everything that is saved between runs.</summary>
    public class Settings
    {
        public string mode { get; set; }        // off, hours, daily, weekly, new
        public int hours { get; set; }
        public int hour { get; set; }
        public int weekday { get; set; }        // 0 = Monday … 6 = Sunday
        public string order { get; set; }       // random, order
        public bool newFirst { get; set; }
        public bool favOnly { get; set; }
        public string size { get; set; }        // auto or e.g. 2560x1600
        public bool notify { get; set; }
        public string current { get; set; }
        public long lastChange { get; set; }
        public long baseline { get; set; }      // wallpapers published after this are "new"
        public long followed { get; set; }      // newest one seen by "when a new one is published"
        public long notified { get; set; }      // newest one we told the user about
        public List<string> favs { get; set; }
        public List<string> hidden { get; set; }
        public List<string> done { get; set; }  // used in this round of shuffling
        public List<string> ever { get; set; }  // ever shown
        public List<string> history { get; set; }

        public Settings()
        {
            mode = "daily"; hours = 3; hour = 7; weekday = 0; order = "random"; newFirst = true; size = "auto"; notify = true;
            current = ""; favs = new List<string>(); hidden = new List<string>(); done = new List<string>();
            ever = new List<string>(); history = new List<string>();
        }
    }

    /// <summary>One wallpaper on the site, in all the sizes it comes in.</summary>
    public class Item
    {
        public string Slug, Title;
        public long Time;
        public Dictionary<string, string> Urls = new Dictionary<string, string>();

        public Item(string slug, long time)
        {
            Slug = slug; Time = time;
            string t = Regex.Replace(slug, "^(jonsolberg-)?(wallpaper-)?", "").Replace('-', ' ').Replace('_', ' ');
            Title = t.Length == 0 ? slug : char.ToUpper(t[0]) + t.Substring(1);
        }

        public string ThumbUrl
        {
            get
            {
                string u = Urls.ContainsKey("1920x1080") ? Urls["1920x1080"] : Urls.Values.First();
                string name = Uri.UnescapeDataString(u.Substring(u.LastIndexOf('/') + 1));
                return App.Base + "/Images/thumbs/" + Uri.EscapeDataString(Regex.Replace(name, @"\.[^.]+$", ".jpg"));
            }
        }
    }

    static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(int action, int param, string value, int flags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public const int SPI_SETDESKWALLPAPER = 20, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    }

    public class App : ApplicationContext
    {
        public const string Version = "1.11";
        public const string Base = "https://jonsolberg.se:8080";
        const string Kofi = "https://ko-fi.com/jonsolberg";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "jonsolberg-wallpapers";
        static readonly string[] Sizes = { "1920x1080", "2560x1600", "3440x1440", "3840x2160" };
        static readonly Dictionary<string, string> SizeNames = new Dictionary<string, string> {
            { "1920x1080", "1920×1080 (16:9, Full HD)" }, { "2560x1600", "2560×1600 (16:10, laptops)" },
            { "3440x1440", "3440×1440 (21:9, ultrawide)" }, { "3840x2160", "3840×2160 (16:9, 4K)" } };
        static readonly Regex DesktopRe = new Regex(@"^(.+?)-(" + string.Join("|", Sizes) + @")\.(jpe?g|png)$", RegexOptions.IgnoreCase);
        static readonly string[] Weekdays = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jonsolberg-wallpapers");
        static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        static readonly string ListPath = Path.Combine(Dir, "list.json");
        static readonly string PicDir = Path.Combine(Dir, "pictures");
        static readonly string ThumbDir = Path.Combine(Dir, "thumbs");

        readonly NotifyIcon tray;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer timer;
        Settings s;
        List<Item> items = new List<Item>();
        Item upcoming;
        bool busy;
        DateTime lastCheck = DateTime.MinValue;
        readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();
        readonly HashSet<string> loadingThumbs = new HashSet<string>();

        public App()
        {
            Directory.CreateDirectory(PicDir);
            Directory.CreateDirectory(ThumbDir);
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            s = LoadSettings();
            items = Parse(ReadText(ListPath));
            tray = new NotifyIcon {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Text = "Wallpapers from jonsolberg.se",
                Visible = true
            };
            // left click = next wallpaper, right click = menu
            tray.MouseUp += (o, e) => {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) Next();
            };
            Build();
            timer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            timer.Tick += (o, e) => Tick();
            timer.Start();
            First();
            // A newer copy being installed asks this one to quit, so the file can be replaced.
            var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
            ThreadPool.RegisterWaitForSingleObject(quitSignal, (o, t) => ui.Post(_ => Quit(), null), null, -1, true);
        }

        // ------------------------------------------------------------------ settings and files

        static string ReadText(string path)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); } catch { return null; }
        }

        static Settings LoadSettings()
        {
            try
            {
                string t = ReadText(SettingsPath);
                if (t != null) return new JavaScriptSerializer().Deserialize<Settings>(t) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        void Save()
        {
            try
            {
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(s), Encoding.UTF8);
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(tmp, SettingsPath);
            }
            catch (Exception e) { Log("save settings: " + e.Message); }
        }

        static void Log(string msg)
        {
            try
            {
                string f = Path.Combine(Dir, "log.txt");
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                if (new FileInfo(f).Length > 200000) File.WriteAllLines(f, File.ReadAllLines(f).Skip(1000).ToArray());
            }
            catch { }
        }

        static long Now { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; } }

        // ------------------------------------------------------------------ the list from the site

        static byte[] Fetch(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "jonsolberg-wallpapers/" + Version + " (Windows)";
                return wc.DownloadData(url);
            }
        }

        static List<Item> Parse(string raw)
        {
            var groups = new Dictionary<string, Item>();
            if (string.IsNullOrEmpty(raw)) return new List<Item>();
            try
            {
                var list = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(raw);
                foreach (var f in list)
                {
                    string name = Convert.ToString(f.ContainsKey("name") ? f["name"] : "");
                    var m = DesktopRe.Match(name);
                    if (!m.Success) continue;
                    if (f.ContainsKey("size") && Convert.ToInt64(f["size"]) == 0) continue;
                    string slug = Regex.Replace(m.Groups[1].Value, "^jonsolberg-", "");
                    long time = Convert.ToInt64(f["time"]);
                    Item it;
                    if (!groups.TryGetValue(slug, out it)) groups[slug] = it = new Item(slug, time);
                    it.Urls[m.Groups[2].Value] = Base + Convert.ToString(f["url"]);
                    it.Time = Math.Min(it.Time, time);   // published when its first size appeared
                }
            }
            catch (Exception e) { Log("parse: " + e.Message); }
            return groups.Values.OrderByDescending(i => i.Time).ToList();
        }

        List<Item> RefreshList()
        {
            byte[] raw = Fetch(Base + "/pictures.php");
            File.WriteAllBytes(ListPath, raw);
            return Parse(Encoding.UTF8.GetString(raw));
        }

        void GotList(List<Item> list)
        {
            if (list == null || list.Count == 0) return;
            items = list;
            if (s.baseline == 0) { s.baseline = items[0].Time; Save(); }
            Build();
        }

        Item Find(string slug) { return items.FirstOrDefault(i => i.Slug == slug); }

        /// <summary>Runs work in the background and continues on the UI thread.</summary>
        void Bg<T>(Func<T> work, Action<T> done, Action<Exception> err = null)
        {
            ThreadPool.QueueUserWorkItem(_ => {
                try
                {
                    T r = work();
                    ui.Post(__ => done(r), null);
                }
                catch (Exception e)
                {
                    Log("error: " + e.Message);
                    ui.Post(__ => { if (err != null) err(e); }, null);
                }
            });
        }

        void ShowError(Exception e)
        {
            Balloon("Wallpapers", "Couldn’t reach jonsolberg.se (" + e.Message + ")", ToolTipIcon.Warning);
        }

        void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            tray.ShowBalloonTip(4000, title, text, icon);
        }

        // ------------------------------------------------------------------ screen and size

        static Size ScreenPx()
        {
            Size best = new Size(1920, 1080);
            long area = 0;
            foreach (var sc in Screen.AllScreens)
            {
                long a = (long)sc.Bounds.Width * sc.Bounds.Height;
                if (a > area) { area = a; best = sc.Bounds.Size; }
            }
            return best;
        }

        /// <summary>The closest shape first, then the smallest one that is at least as wide as the screen.</summary>
        static string BestSize(IEnumerable<string> sizes, int sw, int sh)
        {
            var list = sizes.ToList();
            if (list.Count == 0) return "1920x1080";
            double want = Math.Log((double)Math.Max(sw, sh) / Math.Max(1, Math.Min(sw, sh)));
            var rows = list.Select(k => {
                var p = k.Split('x');
                int w = int.Parse(p[0]), h = int.Parse(p[1]);
                return new { d = Math.Abs(Math.Log((double)w / h) - want), w, k };
            }).ToList();
            double dmin = rows.Min(r => r.d);
            var same = rows.Where(r => r.d - dmin < .02).OrderBy(r => r.w).ToList();
            var fit = same.Where(r => r.w >= Math.Max(sw, sh)).ToList();
            return (fit.Count > 0 ? fit[0] : same[same.Count - 1]).k;
        }

        string SizeFor(Item it)
        {
            if (s.size != "auto" && it.Urls.ContainsKey(s.size)) return s.size;
            var sc = ScreenPx();
            return BestSize(it.Urls.Keys, sc.Width, sc.Height);
        }

        // ------------------------------------------------------------------ choosing and setting

        Item PickNext()
        {
            var pool = items.Where(i => !s.hidden.Contains(i.Slug)).ToList();
            if (s.favOnly && s.favs.Count > 0)
            {
                var f = pool.Where(i => s.favs.Contains(i.Slug)).ToList();
                if (f.Count > 0) pool = f;
            }
            if (pool.Count == 0) pool = items.ToList();
            if (pool.Count == 0) return null;
            if (s.newFirst && s.baseline > 0)
            {
                var n = pool.FirstOrDefault(i => i.Time > s.baseline && !s.ever.Contains(i.Slug));
                if (n != null) return n;
            }
            if (s.order == "order")
            {
                int at = pool.FindIndex(i => i.Slug == s.current);
                return pool[(at + 1) % pool.Count];
            }
            var left = pool.Where(i => !s.done.Contains(i.Slug) && i.Slug != s.current).ToList();
            if (left.Count == 0)
            {
                s.done.Clear();
                left = pool.Where(i => i.Slug != s.current).ToList();
                if (left.Count == 0) left = pool;
            }
            return left[new Random().Next(left.Count)];
        }

        Item Upcoming()
        {
            var u = upcoming == null ? null : Find(upcoming.Slug);
            if (u == null || u.Slug == s.current || s.hidden.Contains(u.Slug) || (s.favOnly && s.favs.Count > 0 && !s.favs.Contains(u.Slug)))
                u = PickNext();
            upcoming = u;
            return u;
        }

        Item TakeUpcoming()
        {
            var u = Upcoming();
            upcoming = null;
            return u;
        }

        static string Download(string url)
        {
            string name = Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1));
            string f = Path.Combine(PicDir, name);
            if (!File.Exists(f) || new FileInfo(f).Length == 0)
            {
                byte[] data = Fetch(url);
                File.WriteAllBytes(f + ".part", data);
                if (File.Exists(f)) File.Delete(f);
                File.Move(f + ".part", f);
            }
            File.SetLastWriteTime(f, DateTime.Now);
            foreach (var old in new DirectoryInfo(PicDir).GetFiles().OrderByDescending(x => x.LastWriteTime).Skip(40))
                try { old.Delete(); } catch { }
            return f;
        }

        static void SetWallpaper(string path)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
            {
                if (k != null)
                {
                    k.SetValue("WallpaperStyle", "10");   // Fill
                    k.SetValue("TileWallpaper", "0");
                }
            }
            if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
                throw new Exception("Windows didn’t accept the picture");
        }

        void Apply(Item it, bool pushHistory = true, Action<Item> then = null)
        {
            if (it == null || busy) return;
            busy = true;
            string key = SizeFor(it);
            string url = it.Urls.ContainsKey(key) ? it.Urls[key] : it.Urls.Values.First();
            Bg(() => { string p = Download(url); SetWallpaper(p); return p; }, p => {
                busy = false;
                if (pushHistory && !string.IsNullOrEmpty(s.current) && s.current != it.Slug)
                {
                    s.history.Remove(s.current);
                    s.history.Insert(0, s.current);
                    if (s.history.Count > 30) s.history.RemoveRange(30, s.history.Count - 30);
                }
                s.current = it.Slug;
                s.lastChange = Now;
                if (!s.done.Contains(it.Slug)) s.done.Add(it.Slug);
                if (!s.ever.Contains(it.Slug)) s.ever.Add(it.Slug);
                Save();
                Log("wallpaper: " + it.Slug + " (" + key + ")");
                Build();
                if (then != null) then(it);
            }, e => { busy = false; ShowError(e); });
        }

        // ------------------------------------------------------------------ actions

        void Next()
        {
            if (items.Count == 0)
            {
                Bg(RefreshList, r => { GotList(r); if (items.Count > 0) Next(); }, ShowError);
                return;
            }
            Apply(TakeUpcoming(), true, it => Balloon("Wallpapers", "Wallpaper: " + it.Title));
        }

        void Previous()
        {
            while (s.history.Count > 0)
            {
                var it = Find(s.history[0]);
                s.history.RemoveAt(0);
                if (it == null) continue;
                var cur = Find(s.current);
                Apply(it, false);
                upcoming = cur;   // "Next" goes back to the one you came from
                return;
            }
            Save();
        }

        void SetFav(Item it, bool on)
        {
            if (it == null) return;
            if (on && !s.favs.Contains(it.Slug)) s.favs.Add(it.Slug);
            if (!on) s.favs.Remove(it.Slug);
            Save();
            Build();
        }

        void Skip()
        {
            if (!string.IsNullOrEmpty(s.current) && !s.hidden.Contains(s.current)) s.hidden.Add(s.current);
            Save();
            Next();
        }

        void SaveCopy()
        {
            var it = Find(s.current);
            if (it == null) return;
            string url = it.Urls[SizeFor(it)];
            Bg(() => {
                string p = Download(url);
                string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string dst = Path.Combine(pics, Path.GetFileName(p));
                File.Copy(p, dst, true);
                return dst;
            }, dst => Balloon("Saved", dst), ShowError);
        }

        static bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            set { SetAutostart(value); }
        }

        static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch { }
        }

        void Set(Action change)
        {
            change();
            Save();
            upcoming = null;
            Build();
        }

        // ------------------------------------------------------------------ schedule

        bool IsDue()
        {
            if (s.mode == "off" || s.mode == "new") return false;
            DateTime last = s.lastChange > 0 ? new DateTime(1970, 1, 1).AddSeconds(s.lastChange).ToLocalTime() : DateTime.MinValue;
            DateTime now = DateTime.Now;
            if (s.mode == "hours") return now - last >= TimeSpan.FromHours(Math.Max(1, s.hours));
            DateTime at = now.Date.AddHours(s.hour);
            if (s.mode == "daily")
            {
                if (now < at) at = at.AddDays(-1);
                return last < at;
            }
            if (s.mode == "weekly")
            {
                int today = ((int)now.DayOfWeek + 6) % 7;   // Monday = 0
                at = at.AddDays(-((today - s.weekday + 7) % 7));
                if (at > now) at = at.AddDays(-7);
                return last < at;
            }
            return false;
        }

        void First()
        {
            Bg(RefreshList, list => {
                GotList(list);
                if (string.IsNullOrEmpty(s.current) || IsDue()) Apply(TakeUpcoming());
            }, e => { if (string.IsNullOrEmpty(s.current) && items.Count > 0) Apply(TakeUpcoming()); });
        }

        void Tick()
        {
            bool due = IsDue();
            if (!due && s.mode != "new" && !s.notify) return;
            if (!due && DateTime.Now - lastCheck < TimeSpan.FromMinutes(55)) return;
            lastCheck = DateTime.Now;
            Bg(RefreshList, list => {
                GotList(list);
                if (items.Count == 0) return;
                var newest = items[0];
                if (s.notify)
                {
                    if (s.notified == 0) { s.notified = newest.Time; Save(); }
                    else if (newest.Time > s.notified)
                    {
                        int n = items.Count(i => i.Time > s.notified);
                        Balloon("New wallpapers", n == 1 ? "New: " + newest.Title : n + " new wallpapers. Newest: " + newest.Title);
                        s.notified = newest.Time;
                        Save();
                    }
                }
                if (s.mode == "new")
                {
                    if (s.followed == 0) { s.followed = newest.Time; Save(); }
                    else if (newest.Time > s.followed) { s.followed = newest.Time; Save(); Apply(newest); }
                }
                else if (due) Apply(TakeUpcoming());
            });
        }

        // ------------------------------------------------------------------ the menu

        ContextMenuStrip menu;

        static bool DarkMode
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 1)) == 0;
                }
                catch { return false; }
            }
        }

        Image Thumb(Item it, Action<Image> ready)
        {
            Image img;
            if (thumbs.TryGetValue(it.Slug, out img)) return img;
            string f = Path.Combine(ThumbDir, it.Slug + ".jpg");
            if (File.Exists(f))
            {
                try
                {
                    using (var src = Image.FromFile(f)) img = Small(src);
                    thumbs[it.Slug] = img;
                    return img;
                }
                catch { }
            }
            if (loadingThumbs.Add(it.Slug))
            {
                string url = it.ThumbUrl;
                Bg(() => { File.WriteAllBytes(f, Fetch(url)); return f; }, p => {
                    loadingThumbs.Remove(it.Slug);
                    try
                    {
                        using (var src = Image.FromFile(p)) thumbs[it.Slug] = Small(src);
                        ready(thumbs[it.Slug]);
                    }
                    catch { }
                }, e => loadingThumbs.Remove(it.Slug));
            }
            return null;
        }

        static Image Small(Image src)
        {
            int h = 36, w = 64;
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return b;
        }

        ToolStripMenuItem Entry(string text, Action click = null, bool enabled = true)
        {
            var mi = new ToolStripMenuItem(text) { Enabled = enabled };
            if (click != null) mi.Click += (o, e) => click();
            return mi;
        }

        ToolStripMenuItem Check(string text, bool on, Action<bool> change)
        {
            var mi = new ToolStripMenuItem(text) { Checked = on, CheckOnClick = true };
            mi.CheckedChanged += (o, e) => change(mi.Checked);
            return mi;
        }

        ToolStripMenuItem Radio(string text, bool on, Action pick)
        {
            var mi = new ToolStripMenuItem(text) { Checked = on };
            mi.Click += (o, e) => pick();
            return mi;
        }

        ToolStripMenuItem Pic(string label, Item it, Action click)
        {
            var mi = Entry(label, click);
            mi.ImageScaling = ToolStripItemImageScaling.None;
            mi.Image = Thumb(it, img => { if (!mi.IsDisposed) mi.Image = img; });
            return mi;
        }

        void ScrollableList(ToolStripMenuItem parent, IEnumerable<Item> list, Func<Item, string> label)
        {
            var dd = (ToolStripDropDownMenu)parent.DropDown;
            dd.MaximumSize = new Size(0, Math.Max(300, Screen.PrimaryScreen.WorkingArea.Height * 7 / 10));
            dd.MouseWheel += (o, e) => ScrollMenu(dd, e.Delta);
            if (DarkMode) dd.Opened += (o, e) => DarkScrollButtons(dd);
            foreach (var it in list)
            {
                var x = it;
                parent.DropDownItems.Add(Pic(label(x), x, () => Apply(x)));
            }
        }

        // WinForms menus only scroll with their arrow buttons; this makes the mouse wheel work too.
        static readonly MethodInfo ScrollInternal = typeof(ToolStripDropDownMenu).GetMethod("ScrollInternal",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);

        /// <summary>The up and down arrows of a long menu are white by default; paint them like the rest of a dark menu.</summary>
        static void DarkScrollButtons(ToolStripDropDownMenu m)
        {
            foreach (var up in new[] { true, false })
            {
                object b = null;
                var t = typeof(ToolStripDropDownMenu);
                var prop = t.GetProperty(up ? "UpScrollButton" : "DownScrollButton", BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) b = prop.GetValue(m, null);
                if (b == null)
                {
                    var field = t.GetField(up ? "upScrollButton" : "downScrollButton", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null) b = field.GetValue(m);
                }
                var host = b as ToolStripControlHost;
                if (host == null) continue;
                host.BackColor = DarkColors.Bg;
                if (host.Control == null) continue;
                host.Control.BackColor = DarkColors.Bg;
                host.Control.ForeColor = Color.White;
                var label = host.Control as Label;
                if (label != null) label.Image = Arrow(up);
            }
        }

        static Image Arrow(bool up)
        {
            var b = new Bitmap(9, 5);
            using (var g = Graphics.FromImage(b))
            using (var br = new SolidBrush(Color.White))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPolygon(br, up ? new[] { new Point(0, 5), new Point(9, 5), new Point(4, 0) }
                                     : new[] { new Point(0, 0), new Point(9, 0), new Point(4, 5) });
            }
            return b;
        }

        static void ScrollMenu(ToolStripDropDownMenu m, int delta)
        {
            if (ScrollInternal == null || m.Items.Count == 0) return;
            int step = m.Items[0].Height * 3;
            try { ScrollInternal.Invoke(m, new object[] { delta > 0 ? -step : step }); } catch { }
        }

        void Build()
        {
            var m = new ContextMenuStrip { ShowImageMargin = true };
            if (DarkMode) { m.Renderer = new DarkRenderer(); m.ForeColor = Color.White; }
            var cur = Find(s.current);
            var up = Upcoming();

            if (cur != null) m.Items.Add(Pic("Now: " + cur.Title, cur, null));
            var next = Entry(up != null ? "Next wallpaper  (" + up.Title + ")" : "Next wallpaper", Next, true);
            next.Font = new Font(next.Font, FontStyle.Bold);
            m.Items.Add(next);
            m.Items.Add(Entry("Previous wallpaper", Previous, s.history.Count > 0));

            var choose = Entry("Choose a wallpaper");
            ScrollableList(choose, items.Where(i => !s.hidden.Contains(i.Slug)), i => (i.Slug == s.current ? "● " : "") + i.Title);
            if (items.Count == 0) choose.DropDownItems.Add(Entry("No wallpapers loaded yet", null, false));
            m.Items.Add(choose);

            var favMenu = Entry("Favourites");
            var favItems = items.Where(i => s.favs.Contains(i.Slug)).ToList();
            ScrollableList(favMenu, favItems, i => i.Title);
            if (favItems.Count == 0) favMenu.DropDownItems.Add(Entry("Mark the current wallpaper with ★ to add it here", null, false));
            m.Items.Add(favMenu);

            if (cur != null)
            {
                m.Items.Add(Check("★ Favourite", s.favs.Contains(cur.Slug), on => SetFav(cur, on)));
                m.Items.Add(Entry("Skip this one from now on", Skip));
                m.Items.Add(Entry("Save a copy to Pictures", SaveCopy));
            }
            var hiddenMenu = Entry("Skipped wallpapers");
            foreach (var slug in s.hidden.ToList())
            {
                var it = Find(slug);
                if (it == null) continue;
                hiddenMenu.DropDownItems.Add(Pic("Use again: " + it.Title, it, () => Set(() => s.hidden.Remove(slug))));
            }
            if (hiddenMenu.DropDownItems.Count > 0) m.Items.Add(hiddenMenu);
            m.Items.Add(new ToolStripSeparator());

            var change = Entry("Change automatically");
            change.DropDownItems.Add(Radio("Off", s.mode == "off", () => Set(() => s.mode = "off")));
            foreach (int h in new[] { 1, 3, 6, 12 })
            {
                int hh = h;
                change.DropDownItems.Add(Radio(h == 1 ? "Every hour" : "Every " + h + " hours", s.mode == "hours" && s.hours == h,
                    () => Set(() => { s.mode = "hours"; s.hours = hh; s.lastChange = Now; })));
            }
            change.DropDownItems.Add(Radio("Every day at " + s.hour.ToString("00") + ":00", s.mode == "daily", () => Set(() => s.mode = "daily")));
            change.DropDownItems.Add(Radio("Every " + Weekdays[s.weekday] + " at " + s.hour.ToString("00") + ":00", s.mode == "weekly", () => Set(() => s.mode = "weekly")));
            change.DropDownItems.Add(Radio("When a new wallpaper is published", s.mode == "new", () => Set(() => { s.mode = "new"; s.followed = items.Count > 0 ? items[0].Time : 0; })));
            change.DropDownItems.Add(new ToolStripSeparator());
            var time = Entry("Time of day");
            for (int h = 0; h < 24; h++)
            {
                int hh = h;
                time.DropDownItems.Add(Radio(h.ToString("00") + ":00", s.hour == h, () => Set(() => s.hour = hh)));
            }
            change.DropDownItems.Add(time);
            var day = Entry("Day of the week");
            for (int d = 0; d < 7; d++)
            {
                int dd = d;
                day.DropDownItems.Add(Radio(Weekdays[d], s.weekday == d, () => Set(() => s.weekday = dd)));
            }
            change.DropDownItems.Add(day);
            m.Items.Add(change);

            var order = Entry("Which one comes next");
            order.DropDownItems.Add(Radio("Shuffle", s.order != "order", () => Set(() => s.order = "random")));
            order.DropDownItems.Add(Radio("Newest to oldest", s.order == "order", () => Set(() => s.order = "order")));
            order.DropDownItems.Add(new ToolStripSeparator());
            order.DropDownItems.Add(Check("New wallpapers first", s.newFirst, v => Set(() => s.newFirst = v)));
            order.DropDownItems.Add(Check("Only favourites", s.favOnly, v => Set(() => s.favOnly = v)));
            m.Items.Add(order);

            var sz = Entry("Picture size");
            var sc = ScreenPx();
            sz.DropDownItems.Add(Radio("Automatic (this screen " + sc.Width + "×" + sc.Height + " gets " + BestSize(Sizes, sc.Width, sc.Height).Replace('x', '×') + ")",
                s.size == "auto", () => Set(() => s.size = "auto")));
            foreach (var k in Sizes)
            {
                string kk = k;
                sz.DropDownItems.Add(Radio(SizeNames[k], s.size == k, () => Set(() => s.size = kk)));
            }
            m.Items.Add(sz);

            m.Items.Add(Check("Tell me about new wallpapers", s.notify, v => Set(() => { s.notify = v; s.notified = 0; })));
            m.Items.Add(Check("Start when I log in", Autostart, v => { Autostart = v; Build(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Entry("Open the gallery on jonsolberg.se", () => Open(Base + "/Pictures/")));
            m.Items.Add(Entry("❤️ Support me on Ko-fi", () => Open(Kofi)));
            m.Items.Add(Entry("Version " + Version + " · jonsolberg.se", () => Open(Base + "/")));
            m.Items.Add(Entry("© 2026 Jon Solberg · source and help on GitHub", () => Open("https://github.com/jonsolberg/wallpaper")));
            m.Items.Add(Entry("Quit", Quit));

            var old = menu;
            menu = m;
            tray.ContextMenuStrip = m;
            if (old != null) old.Dispose();
            tray.Text = cur != null ? Trim("Wallpaper: " + cur.Title) : "Wallpapers from jonsolberg.se";
        }

        static string Trim(string t) { return t.Length > 63 ? t.Substring(0, 62) + "…" : t; }

        void Quit()
        {
            tray.Visible = false;
            timer.Stop();
            ExitThread();
        }

        /// <summary>Dark menus when Windows is in dark mode.</summary>
        class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(140, 140, 140);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Color.White;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                using (var b = new SolidBrush(Color.FromArgb(91, 63, 217))) e.Graphics.FillRectangle(b, r);
                using (var p = new Pen(Color.White, 2))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(p, new[] { new Point(r.Left + 4, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 5), new Point(r.Right - 4, r.Top + 4) });
                }
            }
        }

        class DarkColors : ProfessionalColorTable
        {
            public static readonly Color Bg = Color.FromArgb(32, 32, 32); static readonly Color Hover = Color.FromArgb(55, 55, 60), Line = Color.FromArgb(70, 70, 70);
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color CheckBackground { get { return Hover; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
        }

        // ------------------------------------------------------------------ start

        const string QuitEvent = "jonsolberg-wallpapers-quit";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\jonsolberg-wallpapers";
        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "jonsolberg-wallpapers");
        static readonly string InstalledExe = Path.Combine(InstallDir, "Wallpapers.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wallpapers from jonsolberg.se.lnk");

        static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Asks a running copy to quit and waits for it, so its file can be replaced.</summary>
        static void StopRunningCopy()
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(QuitEvent, out ev)) return;
            using (ev) ev.Set();
            for (int i = 0; i < 50; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting("jonsolberg-wallpapers", out m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
        }

        /// <summary>Copies the app to the user's Programs folder (no admin rights needed), with a Start menu
        /// shortcut, an entry in Settings › Apps for uninstalling, and start with Windows. Updates work the same way.</summary>
        static bool Install()
        {
            bool update = File.Exists(InstalledExe);
            try
            {
                StopRunningCopy();
                Directory.CreateDirectory(InstallDir);
                File.Copy(Application.ExecutablePath, InstalledExe, true);

                try
                {
                    var shellType = Type.GetTypeFromProgID("WScript.Shell");
                    object shell = Activator.CreateInstance(shellType);
                    object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartMenuLink });
                    var lt = link.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { InstalledExe });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { InstallDir });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Wallpapers from jonsolberg.se" });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { InstalledExe + ",0" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }
                catch (Exception e) { Log("shortcut: " + e.Message); }

                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Wallpapers from jonsolberg.se");
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "Jon Solberg");
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("URLInfoAbout", "https://github.com/jonsolberg/wallpaper");
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                if (!update || Autostart) SetAutostart(true);   // keeps the user's choice when updating

                System.Diagnostics.Process.Start(InstalledExe, update ? "--updated" : "--installed");
                return true;
            }
            catch (Exception e)
            {
                Log("install: " + e.Message);
                MessageBox.Show("Couldn’t install Wallpapers (" + e.Message + "). It will run from where it is instead.", "Wallpapers",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        static void Uninstall()
        {
            if (MessageBox.Show("Remove Wallpapers from jonsolberg.se from this computer?\n\nYour current desktop picture stays as it is.",
                    "Uninstall Wallpapers", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            StopRunningCopy();
            SetAutostart(false);
            try { File.Delete(StartMenuLink); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // The program can't delete itself while it runs, so a short-lived command does it a moment later.
            string cmd = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\" & rmdir /s /q \"" + Dir + "\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmd) {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
            MessageBox.Show("Wallpapers has been removed.", "Wallpapers", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + InstalledExeOrSelf + "\" --background");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        static string InstalledExeOrSelf { get { return File.Exists(InstalledExe) ? InstalledExe : Application.ExecutablePath; } }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) { Uninstall(); return; }
            if (!IsInstalledCopy && !args.Contains("--portable") && Install()) return;
            bool created;
            using (var single = new Mutex(true, "jonsolberg-wallpapers", out created))
            {
                if (!created) return;   // already running
                try { Native.SetProcessDPIAware(); } catch { }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
                var app = new App();
                if (args.Contains("--installed"))
                    app.Balloon("Wallpapers is installed", "Find it here in the notification area, or in the Start menu. It starts with Windows; you can change that in its menu.");
                if (args.Contains("--updated"))
                    app.Balloon("Wallpapers is updated", "Version " + Version);
                Application.Run(app);
            }
        }
    }
}
