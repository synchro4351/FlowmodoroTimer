// フローモドーロタイマー（トレイ常駐）。build.bat でコンパイルする。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
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

    const int HK_MENU = 1, HK_CATEGORY = 3;

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
    ToolStripMenuItem miWork, miBreak, miPause, miResume, miStop;
    readonly List<ToolStripItem> extras = new List<ToolStripItem>(); // 右クリック時だけ出す項目
    bool quickMenu; // true = 左クリック / ショートカットで開いた短いメニュー
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

        icoWork = MakeIcon(Color.FromArgb(220, 60, 50), "\U0001F58A");  // 🖊
        icoBreak = MakeIcon(Color.FromArgb(40, 150, 230), "\u2615");    // ☕
        icoStop = MakeIcon(Color.Gray, "stop");                         // ⏹
        icoPause = MakeIcon(Color.FromArgb(240, 160, 30), "pause");     // ⏸

        BuildMenu();
        tray.Icon = icoStop;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowQuickMenu(false); };

        hk.Pressed += id =>
        {
            if (id == HK_MENU) ShowQuickMenu(true);
            else if (id == HK_CATEGORY) SetCategory(categories[(categories.IndexOf(category) + 1) % categories.Count], true);
        };
        var failed = new List<string>();
        Register(HK_MENU, Cfg("hotkey", ""), failed);
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
                "; 操作メニュー（作業 / 休憩 / 一時停止 / 停止）を開く\r\n" +
                "hotkey=\r\n" +
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

    void StartWork()
    {
        StartPhase(St.Working);
        Balloon("\U0001F58A\uFE0F 作業開始", "区分: " + category, ToolTipIcon.None);
        Refresh();
    }

    void StartBreak()
    {
        double workSec = Elapsed().TotalSeconds;
        breakPlanSec = workSec / ratio;
        WriteRow(workSec);
        StartPhase(St.Break);
        Balloon("\u2615 休憩しましょう", string.Format("今回の作業時間は{0}です。推奨休憩時間は{1}です。\n{2:HH:mm}まで休憩を取ってください。",
            FmtMin(workSec), FmtMin(breakPlanSec), DateTime.Now.AddSeconds(breakPlanSec)), ToolTipIcon.None);
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
        if (notify) Balloon("\U0001F3F7\uFE0F 区分", "区分を「" + c + "」にしました", ToolTipIcon.None);
        Refresh();
    }

    void OnTick()
    {
        if (state == St.Break && !paused && Elapsed().TotalSeconds >= breakPlanSec)
        {
            StartPhase(St.Stopped);
            SystemSounds.Asterisk.Play();
            Balloon("\u23F0 休憩終了", "アイコンをクリックして「作業」を選ぶと、次の作業を始めます", ToolTipIcon.None);
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

    // 通知用: 1分以上なら「〇分」、それ未満なら「〇秒」
    static string FmtMin(double sec)
    {
        return sec >= 60 ? Math.Round(sec / 60) + "分" : Math.Round(sec) + "秒";
    }

    void Refresh()
    {
        tray.Icon = paused ? icoPause : state == St.Working ? icoWork : state == St.Break ? icoBreak : icoStop;
        foreach (var mi in miCats) mi.Checked = (string)mi.Tag == category;
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        string s;
        if (paused) s = "\u23F8\uFE0F 一時停止中";
        else if (state == St.Working) s = "\U0001F58A\uFE0F 作業中";
        else if (state == St.Break) s = "\u2615 休憩中 残り";
        else s = "\u23F9\uFE0F 停止";
        if (state == St.Working) s += " " + Fmt(Elapsed().TotalSeconds);
        else if (state == St.Break) s += " " + Fmt(breakPlanSec - Elapsed().TotalSeconds);
        s += " [" + category + "]";
        s += "\n今日の" + category + " " + Fmt(TodayCategoryMinutes() * 60);
        if (s.Length > 63) s = s.Substring(0, 63);
        tray.Text = s;
    }

    void Balloon(string title, string text, ToolTipIcon icon) { tray.ShowBalloonTip(3000, title, text, icon); }

    void BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Font = new Font("Yu Gothic UI", 11F); // 押し間違えにくい大きさ
        miWork = new ToolStripMenuItem("\U0001F58A\uFE0F 作業", null, (s, e) => StartWork());
        miBreak = new ToolStripMenuItem("\u2615 休憩", null, (s, e) => StartBreak());
        miPause = new ToolStripMenuItem("\u23F8\uFE0F 一時停止", null, (s, e) => TogglePause());
        miResume = new ToolStripMenuItem("\u25B6\uFE0F 再開", null, (s, e) => TogglePause());
        miStop = new ToolStripMenuItem("\u23F9\uFE0F 停止", null, (s, e) => Stop());
        foreach (var mi in new[] { miWork, miBreak, miPause, miResume, miStop }) m.Items.Add(mi);
        extras.Add(new ToolStripSeparator());
        foreach (var c in categories)
        {
            string name = c;
            var mi = new ToolStripMenuItem("区分: " + name, null, (s, e) => SetCategory(name, false));
            mi.Tag = name;
            miCats.Add(mi);
            extras.Add(mi);
        }
        extras.Add(new ToolStripSeparator());
        extras.Add(new ToolStripMenuItem("\U0001F4C4 記録（CSV）を開く", null, (s, e) => { if (File.Exists(csvPath)) Process.Start(csvPath); }));
        extras.Add(new ToolStripMenuItem("\u2699\uFE0F 設定ファイルを開く", null, (s, e) => Process.Start("notepad.exe", "\"" + iniPath + "\"")));
        extras.Add(new ToolStripMenuItem("\u274C 終了", null, (s, e) => Quit()));
        foreach (var x in extras) m.Items.Add(x);
        // 開くたびに、今の状態で選べる操作だけを見せる
        m.Opening += (s, e) =>
        {
            bool running = state != St.Stopped;
            miWork.Visible = !paused && state != St.Working;
            miBreak.Visible = !paused && state == St.Working;
            miPause.Visible = !paused && running;
            miResume.Visible = paused;
            miStop.Visible = !paused && running;
            foreach (var x in extras) x.Visible = !quickMenu;
        };
        m.Closed += (s, e) => quickMenu = false;
        tray.ContextMenuStrip = m;
    }

    // 左クリック・ショートカットで、操作だけの短いメニューをマウスの位置に出す。
    // NotifyIcon の非公開メソッドを使うと、右クリック時と同じく前面に出て、ほかをクリックすると閉じる。
    // ショートカットから開いたときは先頭の項目を選んでおき、Enter で決定・矢印キーで選び直し・Esc で閉じる。
    void ShowQuickMenu(bool fromKeyboard)
    {
        quickMenu = true;
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tray, null);
        if (!fromKeyboard) return;
        foreach (ToolStripItem it in tray.ContextMenuStrip.Items)
            if (it.Available) { it.Select(); break; }
    }

    // 色付きの丸に白い絵文字を重ねたアイコン。GDI+ はカラー絵文字を描けないので、形は白抜きで描く。
    static Icon MakeIcon(Color c, string glyph)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 0, 0, 32, 32);
            if (glyph == "stop") g.FillRectangle(Brushes.White, 10, 10, 12, 12);
            else if (glyph == "pause")
            {
                g.FillRectangle(Brushes.White, 10, 9, 4, 14);
                g.FillRectangle(Brushes.White, 18, 9, 4, 14);
            }
            else
                using (var f = new Font("Segoe UI Emoji", 17, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(glyph, f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void Quit()
    {
        if (state == St.Working) WriteRow(Elapsed().TotalSeconds);
        tray.Visible = false;
        UnregisterHotKey(hk.Handle, HK_MENU);
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
