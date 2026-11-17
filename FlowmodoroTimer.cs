// フローモドーロタイマー（トレイ常駐）。build.bat でコンパイルする。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

enum St { Stopped, Working, Break }

class HotkeyWindow : NativeWindow
{
    public event Action<int> Pressed;
    public HotkeyWindow() { CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && Pressed != null) Pressed(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}

class App : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    const int HK_TOGGLE = 1, HK_PAUSE = 2, HK_CATEGORY = 3;

    readonly string dir, iniPath, csvPath;
    readonly Dictionary<string, string> cfg = new Dictionary<string, string>();
    double ratio = 5;
    readonly List<string> categories = new List<string>();
    string category;

    St state = St.Stopped;
    bool paused;
    DateTime phaseStart, pauseStart;
    TimeSpan pausedInPhase;
    double breakPlanSec;

    double catLoggedMin; string catKey = "";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly HotkeyWindow hk = new HotkeyWindow();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly Icon icoStop, icoWork, icoBreak, icoPause;
    ToolStripMenuItem miToggle, miPause, miStop;
    readonly List<ToolStripMenuItem> miCats = new List<ToolStripMenuItem>();

    public App()
    {
        dir = AppDomain.CurrentDomain.BaseDirectory;
        iniPath = Path.Combine(dir, "settings.ini");
        LoadConfig();
        ratio = double.Parse(Cfg("ratio", "5"), CultureInfo.InvariantCulture);
        if (ratio <= 0) ratio = 5;
        string csv = Cfg("csv", "log.csv");
        csvPath = Path.IsPathRooted(csv) ? csv : Path.Combine(dir, csv);
        foreach (var c in Cfg("categories", "勤務,趣味").Split(','))
            if (c.Trim().Length > 0 && !categories.Contains(c.Trim())) categories.Add(c.Trim());
        if (categories.Count == 0) categories.Add("勤務");
        category = categories[0];

        icoStop = MakeIcon(Color.Gray, false);
        icoWork = MakeIcon(Color.FromArgb(220, 60, 50), false);
        icoBreak = MakeIcon(Color.FromArgb(40, 150, 230), false);
        icoPause = MakeIcon(Color.FromArgb(240, 160, 30), true);

        BuildMenu();
        tray.Icon = icoStop;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };

        hk.Pressed += id =>
        {
            if (id == HK_TOGGLE) Toggle();
            else if (id == HK_PAUSE) TogglePause();
            else if (id == HK_CATEGORY) SetCategory(categories[(categories.IndexOf(category) + 1) % categories.Count], true);
        };
        var failed = new List<string>();
        Register(HK_TOGGLE, Cfg("hotkey", ""), failed);
        Register(HK_PAUSE, Cfg("pause_hotkey", ""), failed);
        Register(HK_CATEGORY, Cfg("category_hotkey", ""), failed);

        // PC のシャットダウン・サインアウト時も作業中の分を記録する
        Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => Stop();

        tick.Interval = 1000;
        tick.Tick += (s, e) => OnTick();
        tick.Start();
        Refresh();
        if (failed.Count > 0)
            Balloon("ショートカット登録失敗", string.Join(", ", failed.ToArray()) + " は他のアプリが使用中です。settings.ini で変更してください。", ToolTipIcon.Warning);
    }

    // ---------- 設定 ----------
    void LoadConfig()
    {
        if (!File.Exists(iniPath))
        {
            File.WriteAllText(iniPath,
                "; フローモドーロタイマー設定（変更後はアプリを再起動）\r\n" +
                "; ショートカットキー（空欄ならオフ）\r\n" +
                "; 書き方の例: Ctrl+Alt+F / Ctrl+Shift+F12 / Win+Alt+W\r\n" +
                "; 作業開始 → 休憩 → 次の作業 の切り替え\r\n" +
                "hotkey=\r\n" +
                "; 一時停止 / 再開\r\n" +
                "pause_hotkey=\r\n" +
                "; 区分の切り替え\r\n" +
                "category_hotkey=\r\n" +
                "; 休憩 = 作業時間 ÷ ratio\r\n" +
                "ratio=5\r\n" +
                "; 記録先（相対パスは exe と同じフォルダ）\r\n" +
                "csv=log.csv\r\n" +
                "; 区分（カンマ区切り。先頭が起動時の区分）\r\n" +
                "categories=勤務,趣味\r\n", new UTF8Encoding(true));
        }
        foreach (var raw in File.ReadAllLines(iniPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
            int i = line.IndexOf('=');
            if (i > 0) cfg[line.Substring(0, i).Trim().ToLowerInvariant()] = line.Substring(i + 1).Trim();
        }
    }

    string Cfg(string key, string def) { string v; return cfg.TryGetValue(key, out v) && v.Length > 0 ? v : def; }

    void Register(int id, string spec, List<string> failed)
    {
        if (spec.Length == 0) return; // 空欄 = オフ
        uint mods = 0x4000; // MOD_NOREPEAT
        Keys key = Keys.None;
        try
        {
            foreach (var p in spec.Split('+'))
            {
                var t = p.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") mods |= 2;
                else if (t == "alt") mods |= 1;
                else if (t == "shift") mods |= 4;
                else if (t == "win") mods |= 8;
                else key = (Keys)Enum.Parse(typeof(Keys), p.Trim(), true);
            }
        }
        catch { key = Keys.None; }
        if (key == Keys.None || !RegisterHotKey(hk.Handle, id, mods, (uint)key)) failed.Add(spec);
    }

    // ---------- 状態遷移 ----------
    TimeSpan Elapsed()
    {
        var now = paused ? pauseStart : DateTime.Now;
        return now - phaseStart - pausedInPhase;
    }

    void StartPhase(St s)
    {
        state = s; paused = false; pausedInPhase = TimeSpan.Zero; phaseStart = DateTime.Now;
    }

    void Toggle()
    {
        if (state == St.Working)
        {
            double workSec = Elapsed().TotalSeconds;
            breakPlanSec = workSec / ratio;
            WriteRow(workSec);
            StartPhase(St.Break);
            Balloon("休憩", string.Format("作業 {0}（{1}）→ 休憩 {2}", Fmt(workSec), category, Fmt(breakPlanSec)), ToolTipIcon.Info);
        }
        else
        {
            StartPhase(St.Working);
            Balloon("作業開始", "区分: " + category, ToolTipIcon.Info);
        }
        Refresh();
    }

    void TogglePause()
    {
        if (state == St.Stopped) return;
        if (!paused) { paused = true; pauseStart = DateTime.Now; }
        else { paused = false; pausedInPhase += DateTime.Now - pauseStart; }
        Refresh();
    }

    void Stop()
    {
        if (state == St.Working) WriteRow(Elapsed().TotalSeconds);
        StartPhase(St.Stopped);
        Refresh();
    }

    void SetCategory(string c, bool notify)
    {
        category = c;
        if (notify) Balloon("区分", "区分を「" + c + "」にしました", ToolTipIcon.Info);
        Refresh();
    }

    void OnTick()
    {
        if (state == St.Break && !paused && Elapsed().TotalSeconds >= breakPlanSec)
        {
            StartPhase(St.Stopped);
            SystemSounds.Asterisk.Play();
            Balloon("休憩終了", "アイコンをクリックすると次の作業を始めます", ToolTipIcon.Info);
            Refresh();
            return;
        }
        UpdateTooltip();
    }

    // ---------- 記録 ----------
    void WriteRow(double workSec)
    {
        var now = DateTime.Now;
        var pausedMin = (pausedInPhase + (paused ? now - pauseStart : TimeSpan.Zero)).TotalMinutes;
        bool isNew = !File.Exists(csvPath);
        string row = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd},{0:HH:mm:ss},{1:HH:mm:ss},{2:0.0},{3:0.0},{4:0.0},{5}\r\n",
            phaseStart, now, workSec / 60, pausedMin, workSec / ratio / 60, category);
        try
        {
            if (isNew) File.WriteAllText(csvPath, "日付,開始,終了,作業分,中断分,休憩分,区分\r\n", new UTF8Encoding(true));
            File.AppendAllText(csvPath, row, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Balloon("CSV書き込み失敗", ex.Message + "\n" + row, ToolTipIcon.Error);
        }
        catKey = ""; // 再集計させる
    }

    // 今日、今の区分で作業した分数（記録済み + 作業中の分）
    double TodayCategoryMinutes()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (catKey != today + "|" + category)
        {
            catLoggedMin = 0;
            try
            {
                if (File.Exists(csvPath))
                    foreach (var line in File.ReadAllLines(csvPath, Encoding.UTF8))
                    {
                        var c = line.Split(',');
                        double m;
                        if (c.Length >= 7 && c[0] == today && c[6].Trim() == category &&
                            double.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                            catLoggedMin += m;
                    }
            }
            catch { }
            catKey = today + "|" + category;
        }
        double cur = state == St.Working ? Elapsed().TotalMinutes : 0;
        return catLoggedMin + cur;
    }

    // ---------- 表示 ----------
    static string Fmt(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(sec)));
        return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                 : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }

    void Refresh()
    {
        tray.Icon = paused ? icoPause : state == St.Working ? icoWork : state == St.Break ? icoBreak : icoStop;
        miToggle.Text = state == St.Working ? "作業終了 → 休憩" : "作業開始";
        miPause.Text = paused ? "再開" : "一時停止";
        miPause.Enabled = state != St.Stopped;
        miStop.Enabled = state != St.Stopped;
        foreach (var mi in miCats) mi.Checked = (string)mi.Tag == category;
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        string s;
        if (state == St.Working) s = "作業中 " + Fmt(Elapsed().TotalSeconds);
        else if (state == St.Break) s = "休憩中 残り " + Fmt(breakPlanSec - Elapsed().TotalSeconds);
        else s = "停止";
        if (paused) s += "（一時停止）";
        s += " [" + category + "]";
        s += "\n今日の" + category + " " + Fmt(TodayCategoryMinutes() * 60);
        if (s.Length > 63) s = s.Substring(0, 63);
        tray.Text = s;
    }

    void Balloon(string title, string text, ToolTipIcon icon) { tray.ShowBalloonTip(3000, title, text, icon); }

    void BuildMenu()
    {
        var m = new ContextMenuStrip();
        miToggle = new ToolStripMenuItem("作業開始", null, (s, e) => Toggle());
        miPause = new ToolStripMenuItem("一時停止", null, (s, e) => TogglePause());
        miStop = new ToolStripMenuItem("停止（作業中なら記録）", null, (s, e) => Stop());
        m.Items.Add(miToggle);
        m.Items.Add(miPause);
        m.Items.Add(miStop);
        m.Items.Add(new ToolStripSeparator());
        foreach (var c in categories)
        {
            string name = c;
            var mi = new ToolStripMenuItem("区分: " + name, null, (s, e) => SetCategory(name, false));
            mi.Tag = name;
            miCats.Add(mi);
            m.Items.Add(mi);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("記録（CSV）を開く", null, (s, e) => { if (File.Exists(csvPath)) Process.Start(csvPath); });
        m.Items.Add("設定ファイルを開く", null, (s, e) => Process.Start("notepad.exe", "\"" + iniPath + "\""));
        m.Items.Add("終了", null, (s, e) => Quit());
        tray.ContextMenuStrip = m;
    }

    static Icon MakeIcon(Color c, bool pause)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
            if (pause)
            {
                g.FillRectangle(Brushes.White, 10, 8, 4, 16);
                g.FillRectangle(Brushes.White, 18, 8, 4, 16);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void Quit()
    {
        if (state == St.Working) WriteRow(Elapsed().TotalSeconds);
        tray.Visible = false;
        UnregisterHotKey(hk.Handle, HK_TOGGLE);
        UnregisterHotKey(hk.Handle, HK_PAUSE);
        UnregisterHotKey(hk.Handle, HK_CATEGORY);
        ExitThread();
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "FlowmodoroTimer.SingleInstance", out created))
        {
            if (!created) { MessageBox.Show("フローモドーロタイマーはすでに起動しています（タスクトレイを確認）。"); return; }
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
    }
}
