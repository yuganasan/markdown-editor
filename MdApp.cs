// MarkdownEditor.exe - a native Win32 markdown editor.
//
// Editor  : the system EDIT control (what Notepad uses)
// Preview : the system RichEdit control, fed RTF
// No browser, no web view, no runtime to install.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    public const int WM_SETREDRAW = 0x000B;
    public const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    public const int EM_LINESCROLL = 0x00B6;
    public const int EM_REPLACESEL = 0x00C2;
    public const int EM_SETEVENTMASK = 0x0445;   // WM_USER + 69
    public const int WM_VSCROLL = 0x0115;
    public const int SB_VERT = 1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessageStr(IntPtr h, int msg, IntPtr w, string l);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr h, string app, string id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    public static void DarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        // 20 on current Windows 10/11, 19 on older builds; try both, ignore failures
        if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref v, 4);
    }

    public static void DarkScrollbars(Control c, bool dark)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
        catch { }
    }
}

class Settings
{
    public bool Dark = true;
    public string Mode = "split";
    public double Split = 0.5;
    public int Width = 1180, Height = 780;
    public int FontPt = 11;
    public bool ShowSide = true;
    public List<string> Recent = new List<string>();

    static string Path_
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkdownEditor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.ini");
        }
    }

    public static Settings Load()
    {
        Settings s = new Settings();
        try
        {
            if (!File.Exists(Path_)) return s;
            foreach (string line in File.ReadAllLines(Path_))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k == "dark") s.Dark = v == "1";
                else if (k == "mode") s.Mode = v;
                else if (k == "split") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Split);
                else if (k == "width") int.TryParse(v, out s.Width);
                else if (k == "height") int.TryParse(v, out s.Height);
                else if (k == "fontpt") int.TryParse(v, out s.FontPt);
                else if (k == "side") s.ShowSide = v == "1";
                else if (k == "recent")
                {
                    foreach (string part in v.Split('|'))
                        if (part.Trim().Length > 0) s.Recent.Add(part.Trim());
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path_, string.Format(CultureInfo.InvariantCulture,
                "dark={0}\r\nmode={1}\r\nsplit={2:0.###}\r\nwidth={3}\r\nheight={4}\r\nfontpt={5}\r\n"
                + "side={6}\r\nrecent={7}\r\n",
                Dark ? 1 : 0, Mode, Split, Width, Height, FontPt,
                ShowSide ? 1 : 0, string.Join("|", Recent.ToArray())));
        }
        catch { }
    }
}

// One reversible edit. Only the region that actually changed is stored, so a
// thousand of these cost a few KB no matter how big the document is.
class EditStep
{
    public int Pos;
    public string Removed;
    public string Inserted;
}

class MainForm : Form
{
    TextBox editor;
    RichTextBox preview;
    SplitContainer split;
    Panel bar;
    Label status;
    Timer renderTimer;

    Settings cfg;
    Palette pal;
    string bodyFont, monoFont;
    List<LinkSpan> links = new List<LinkSpan>();

    string docPath = "";
    string savedText = "";
    bool dirty, hadBom, usesLf, suppress;
    string lastRendered = null;
    List<Button> toolButtons = new List<Button>();
    Button[] modeButtons;
    Button themeButton;
    // one shared tooltip: every ToolTip instance creates its own native window
    readonly ToolTip tips = new ToolTip();

    // undo / redo
    const int MaxUndo = 1000;
    List<EditStep> undoStack = new List<EditStep>();
    List<EditStep> redoStack = new List<EditStep>();
    string prevText = "";
    bool applyingHistory;
    int lastEditTick;

    // recent-files sidebar
    Panel side, sideList;
    Button sideToggle;
    List<string> recent = new List<string>();
    const int MaxRecent = 12;

    // timed backups
    Timer backupTimer;
    int focusedSeconds;
    bool windowActive = true;
    string lastBackupText;
    const int BackupAfterSeconds = 300;   // five minutes of focused time
    const int BackupsPerDoc = 3;
    const int BackupDocs = 5;

    const string Welcome =
        "# Markdown\r\n\r\n" +
        "A native editor. No browser engine, no web view - the editor is the same\r\n" +
        "control Notepad uses and the preview is Windows' own rich text control.\r\n\r\n" +
        "## Keys\r\n\r\n" +
        "- **Ctrl+S** save, **Ctrl+Shift+S** save as, **Ctrl+O** open, **Ctrl+N** new\r\n" +
        "- **Ctrl+B** bold, **Ctrl+I** italic, **Ctrl+K** link, **Ctrl+E** code\r\n" +
        "- **Tab** / **Shift+Tab** indent, **Enter** continues lists\r\n" +
        "- **Ctrl+1/2/3** edit, split, read - **Ctrl+D** theme\r\n" +
        "- **Ctrl+plus / Ctrl+minus** text size\r\n\r\n" +
        "## What it renders\r\n\r\n" +
        "- **bold**, *italic*, ~~strike~~, ==highlight==, `code`\r\n" +
        "- nested lists\r\n" +
        "  1. ordered\r\n" +
        "  2. and back\r\n" +
        "- [x] task lists\r\n" +
        "- [ ] that stay in sync\r\n\r\n" +
        "> Blockquotes nest whole blocks.\r\n\r\n" +
        "| Thing | Cost |\r\n" +
        "| --- | ---: |\r\n" +
        "| This editor | ~30 MB |\r\n" +
        "| A browser tab | ~300 MB |\r\n\r\n" +
        "```csharp\r\n" +
        "// fenced code keeps its own font\r\n" +
        "var blocks = Md.SplitBlocks(text);\r\n" +
        "```\r\n\r\n" +
        "Links like https://commonmark.org are clickable.\r\n\r\n" +
        "---\r\n\r\n" +
        "Drop a .md file on this window to open it.\r\n";

    public MainForm(string openPath)
    {
        cfg = Settings.Load();
        pal = cfg.Dark ? Palette.Dark() : Palette.Light();
        bodyFont = PickFont("Segoe UI Variable Text", "Segoe UI", "Tahoma");
        monoFont = PickFont("Cascadia Mono", "Consolas", "Courier New");

        Text = "Markdown";
        ClientSize = new Size(cfg.Width, cfg.Height);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 360);
        KeyPreview = true;
        AllowDrop = true;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { }

        BuildUi();

        renderTimer = new Timer();
        renderTimer.Interval = 60;
        renderTimer.Tick += delegate { RenderTick(); };

        recent = cfg.Recent;
        backupTimer = new Timer();
        backupTimer.Interval = 1000;
        backupTimer.Tick += BackupTick;
        backupTimer.Start();
        Activated += delegate { windowActive = true; };
        Deactivate += delegate { windowActive = false; };

        ApplyTheme();
        side.Visible = cfg.ShowSide;

        if (!string.IsNullOrEmpty(openPath)) LoadFile(openPath);
        else SetText(Welcome, "", false);

        SetMode(cfg.Mode);
        DragEnter += delegate(object s, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        };
        DragDrop += delegate(object s, DragEventArgs e)
        {
            string[] f = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (f != null && f.Length > 0 && ConfirmDiscard()) LoadFile(f[0]);
        };
        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            cfg.Dark = IsDark;
            cfg.Split = split.SplitterDistance / (double)Math.Max(1, split.Width);
            if (WindowState == FormWindowState.Normal)
            {
                cfg.Width = ClientSize.Width;
                cfg.Height = ClientSize.Height;
            }
            cfg.Recent = recent;
            cfg.Save();
        };
    }

    bool IsDark = true;

    // Probing names directly is far cheaper than enumerating FontFamily.Families,
    // which walks every installed font and costs real milliseconds at startup.
    static string PickFont(params string[] names)
    {
        foreach (string n in names)
        {
            try
            {
                using (FontFamily f = new FontFamily(n)) return f.Name;
            }
            catch { }
        }
        return names[names.Length - 1];
    }

    // ------------------------------------------------------------------- ui

    void BuildUi()
    {
        IsDark = cfg.Dark;

        bar = new Panel();
        bar.Dock = DockStyle.Top;
        bar.Height = 34;
        Controls.Add(bar);

        split = new SplitContainer();
        split.Dock = DockStyle.Fill;
        split.SplitterWidth = 6;

        editor = new TextBox();
        editor.Multiline = true;
        editor.WordWrap = true;
        editor.AcceptsTab = false;
        editor.ScrollBars = ScrollBars.Vertical;
        editor.BorderStyle = BorderStyle.None;
        editor.MaxLength = 0;
        editor.Font = new Font(monoFont, cfg.FontPt, FontStyle.Regular, GraphicsUnit.Point);
        editor.Dock = DockStyle.Fill;
        editor.TextChanged += delegate { OnEdited(); };
        editor.KeyDown += Editor_KeyDown;

        preview = new RichTextBox();
        preview.ReadOnly = true;
        preview.BorderStyle = BorderStyle.None;
        preview.Dock = DockStyle.Fill;
        preview.TabStop = false;
        preview.DetectUrls = false;
        preview.ScrollBars = RichTextBoxScrollBars.Vertical;
        preview.MouseMove += Preview_MouseMove;
        preview.MouseUp += Preview_MouseUp;

        Panel edPad = new Panel();
        edPad.Dock = DockStyle.Fill;
        edPad.Padding = new Padding(14, 12, 6, 12);
        edPad.Controls.Add(editor);

        Panel pvPad = new Panel();
        pvPad.Dock = DockStyle.Fill;
        pvPad.Padding = new Padding(18, 12, 8, 12);
        pvPad.Controls.Add(preview);

        split.Panel1.Controls.Add(edPad);
        split.Panel2.Controls.Add(pvPad);
        Controls.Add(split);
        split.BringToFront();

        // ---- recent files rail ----
        side = new Panel();
        side.Dock = DockStyle.Left;
        side.Width = 208;
        side.Padding = new Padding(0, 6, 0, 6);

        Label sideHead = new Label();
        sideHead.Text = "  RECENT";
        sideHead.Dock = DockStyle.Top;
        sideHead.Height = 24;
        sideHead.TextAlign = ContentAlignment.MiddleLeft;
        sideHead.Font = new Font(bodyFont, 7.5f, FontStyle.Bold);

        sideList = new Panel();
        sideList.Dock = DockStyle.Fill;
        sideList.AutoScroll = true;

        side.Controls.Add(sideList);
        side.Controls.Add(sideHead);
        Controls.Add(side);

        status = new Label();
        status.Dock = DockStyle.Bottom;
        status.Height = 22;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Padding = new Padding(12, 0, 12, 0);
        status.Font = new Font(bodyFont, 8.25f);
        Controls.Add(status);

        // docking is applied from the end of the collection inwards: the split
        // must sit at index 0 so it fills whatever the edges leave behind
        Controls.SetChildIndex(split, 0);
        Controls.SetChildIndex(side, 1);

        BuildToolbar();
    }

    // ------------------------------------------------------- recent files

    void PushRecent(string path)
    {
        recent.RemoveAll(delegate(string p)
        {
            return string.Equals(p, path, StringComparison.OrdinalIgnoreCase);
        });
        recent.Insert(0, path);
        while (recent.Count > MaxRecent) recent.RemoveAt(recent.Count - 1);
        BuildSideList();
    }

    void BuildSideList()
    {
        if (sideList == null) return;
        sideList.SuspendLayout();
        while (sideList.Controls.Count > 0)
        {
            Control c = sideList.Controls[0];
            sideList.Controls.Remove(c);
            c.Dispose();
        }

        // Dock.Top stacks last-added on top, so walk the list backwards to get
        // most-recent-first on screen
        for (int i = recent.Count - 1; i >= 0; i--)
        {
            string path = recent[i];
            bool current = string.Equals(path, docPath, StringComparison.OrdinalIgnoreCase);
            bool missing = !File.Exists(path);

            Button b = new Button();
            b.Text = "   " + Path.GetFileName(path);
            b.Tag = path;
            b.Dock = DockStyle.Top;
            b.Height = 30;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.TabStop = false;
            b.Font = new Font(bodyFont, 8.75f, current ? FontStyle.Bold : FontStyle.Regular);
            b.ForeColor = missing ? Color.FromArgb(120, 90, 90)
                        : current ? pal.Accent : pal.Muted;
            b.BackColor = current ? CurrentEntryBack() : SideBack();
            b.FlatAppearance.MouseOverBackColor = HoverBack();
            b.Click += RecentClicked;
            tips.SetToolTip(b, missing ? path + "  (missing)" : path);
            sideList.Controls.Add(b);
        }
        sideList.ResumeLayout();
    }

    Color SideBack() { return IsDark ? Color.FromArgb(0x13, 0x16, 0x1C) : Color.FromArgb(0xF6, 0xF7, 0xF9); }
    Color CurrentEntryBack() { return IsDark ? Color.FromArgb(0x1D, 0x22, 0x2C) : Color.FromArgb(0xE6, 0xEC, 0xF7); }
    Color HoverBack() { return IsDark ? Color.FromArgb(0x23, 0x29, 0x35) : Color.FromArgb(0xE2, 0xE6, 0xEC); }

    void RecentClicked(object sender, EventArgs e)
    {
        string path = (string)((Button)sender).Tag;
        if (string.Equals(path, docPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "That file is no longer there:\r\n\r\n" + path,
                "Markdown", MessageBoxButtons.OK, MessageBoxIcon.Information);
            recent.Remove(path);
            BuildSideList();
            return;
        }
        if (!ConfirmDiscard()) return;
        LoadFile(path);
    }

    void ToggleSide()
    {
        cfg.ShowSide = !cfg.ShowSide;
        side.Visible = cfg.ShowSide;
        if (sideToggle != null) sideToggle.ForeColor = cfg.ShowSide ? pal.Accent : pal.Muted;
    }

    Button MakeButton(string text, string tip, int width, EventHandler onClick)
    {
        Button b = new Button();
        b.Text = text;
        b.Width = width;
        b.Height = 24;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.TabStop = false;
        b.Font = new Font(bodyFont, 8.5f);
        b.Click += onClick;
        if (tip.Length > 0) tips.SetToolTip(b, tip);
        bar.Controls.Add(b);
        toolButtons.Add(b);
        return b;
    }

    void BuildToolbar()
    {
        int x = 6;
        Button b;
        sideToggle = MakeButton("Files", "Show or hide the recent files rail", 44,
                                delegate { ToggleSide(); });
        sideToggle.Left = x; x += sideToggle.Width + 12;
        b = MakeButton("New", "Ctrl+N", 42, delegate { NewDoc(); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("Open", "Ctrl+O", 48, delegate { OpenDoc(); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("Save", "Ctrl+S", 46, delegate { SaveDoc(false); }); b.Left = x; x += b.Width + 12;

        b = MakeButton("B", "Bold (Ctrl+B)", 28, delegate { Wrap("**", "**", "bold"); });
        b.Font = new Font(bodyFont, 8.5f, FontStyle.Bold); b.Left = x; x += b.Width + 2;
        b = MakeButton("I", "Italic (Ctrl+I)", 28, delegate { Wrap("*", "*", "italic"); });
        b.Font = new Font(bodyFont, 8.5f, FontStyle.Italic); b.Left = x; x += b.Width + 2;
        b = MakeButton("H", "Heading", 28, delegate { Heading(); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("Link", "Ctrl+K", 38, delegate { Link(); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("Code", "Ctrl+E", 42, delegate { Wrap("`", "`", "code"); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("Quote", "", 52, delegate { TogglePrefix("> "); }); b.Left = x; x += b.Width + 2;
        b = MakeButton("List", "", 44, delegate { TogglePrefix("- "); }); b.Left = x;

        modeButtons = new Button[3];
        modeButtons[0] = MakeButton("Edit", "Ctrl+1", 42, delegate { SetMode("edit"); });
        modeButtons[1] = MakeButton("Split", "Ctrl+2", 44, delegate { SetMode("split"); });
        modeButtons[2] = MakeButton("Read", "Ctrl+3", 46, delegate { SetMode("view"); });
        themeButton = MakeButton("Theme", "Ctrl+D", 50, delegate { ToggleTheme(); });

        bar.Resize += delegate { LayoutBarRight(); };
        foreach (Button btn in toolButtons) btn.Top = 5;
        LayoutBarRight();
    }

    void LayoutBarRight()
    {
        if (modeButtons == null || themeButton == null || bar.Width < 200) return;
        int rx = bar.Width - 10 - themeButton.Width;
        themeButton.Left = rx; rx -= modeButtons[2].Width + 2;
        modeButtons[2].Left = rx; rx -= modeButtons[1].Width + 2;
        modeButtons[1].Left = rx; rx -= modeButtons[0].Width + 2;
        modeButtons[0].Left = rx;
    }

    void ApplyTheme()
    {
        pal = IsDark ? Palette.Dark() : Palette.Light();
        Color bg = IsDark ? Color.FromArgb(0x0F, 0x11, 0x15) : Color.White;
        Color panel = IsDark ? Color.FromArgb(0x12, 0x14, 0x19) : Color.FromArgb(0xFA, 0xFA, 0xFB);
        Color barBg = IsDark ? Color.FromArgb(0x17, 0x1A, 0x21) : Color.FromArgb(0xF2, 0xF3, 0xF5);
        Color fg = pal.Text;

        BackColor = barBg;
        bar.BackColor = barBg;
        status.BackColor = barBg;
        status.ForeColor = pal.Muted;

        split.BackColor = IsDark ? Color.FromArgb(0x24, 0x29, 0x33) : Color.FromArgb(0xE2, 0xE5, 0xEA);
        split.Panel1.BackColor = bg;
        split.Panel2.BackColor = panel;
        foreach (Control c in split.Panel1.Controls) c.BackColor = bg;
        foreach (Control c in split.Panel2.Controls) c.BackColor = panel;

        editor.BackColor = bg;
        editor.ForeColor = fg;
        preview.BackColor = panel;
        preview.ForeColor = fg;

        side.BackColor = SideBack();
        foreach (Control c in side.Controls)
        {
            c.BackColor = SideBack();
            c.ForeColor = pal.Muted;
        }
        BuildSideList();

        foreach (Button b in toolButtons)
        {
            b.BackColor = barBg;
            b.ForeColor = pal.Text;
            b.FlatAppearance.MouseOverBackColor = IsDark
                ? Color.FromArgb(0x25, 0x2A, 0x34) : Color.FromArgb(0xE3, 0xE6, 0xEB);
            b.FlatAppearance.MouseDownBackColor = IsDark
                ? Color.FromArgb(0x2E, 0x35, 0x42) : Color.FromArgb(0xD5, 0xD9, 0xE0);
        }
        HighlightMode();
        if (sideToggle != null) sideToggle.ForeColor = cfg.ShowSide ? pal.Accent : pal.Muted;

        Native.DarkTitleBar(Handle, IsDark);
        Native.DarkScrollbars(editor, IsDark);
        Native.DarkScrollbars(preview, IsDark);

        lastRendered = null;
        RenderNow();
    }

    void ToggleTheme()
    {
        IsDark = !IsDark;
        cfg.Dark = IsDark;
        ApplyTheme();
    }

    void HighlightMode()
    {
        if (modeButtons == null) return;
        string[] names = { "edit", "split", "view" };
        for (int i = 0; i < 3; i++)
        {
            bool on = names[i] == cfg.Mode;
            modeButtons[i].ForeColor = on ? pal.Accent : pal.Muted;
            modeButtons[i].Font = new Font(bodyFont, 8.5f, on ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    void SetMode(string mode)
    {
        cfg.Mode = mode;
        bool wasHidden = split.Panel2Collapsed;
        split.Panel1Collapsed = (mode == "view");
        split.Panel2Collapsed = (mode == "edit");
        HighlightMode();
        if (wasHidden && !split.Panel2Collapsed) RenderNow();
        if (mode != "view") editor.Focus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if (!split.Panel1Collapsed && !split.Panel2Collapsed)
                split.SplitterDistance = (int)(split.Width * Math.Min(0.85, Math.Max(0.15, cfg.Split)));
        }
        catch { }
        Native.DarkTitleBar(Handle, IsDark);
        Native.DarkScrollbars(editor, IsDark);
        Native.DarkScrollbars(preview, IsDark);
        LayoutBarRight();
        editor.Focus();
    }

    // --------------------------------------------------------------- render

    // ------------------------------------------------------------ undo/redo

    // Locate the single changed region by scanning in from both ends. One pass,
    // no allocation beyond the two changed substrings.
    static EditStep Diff(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        int max = Math.Min(la, lb);
        int p = 0;
        while (p < max && a[p] == b[p]) p++;
        int s = 0;
        while (s < max - p && a[la - 1 - s] == b[lb - 1 - s]) s++;
        if (la - p - s <= 0 && lb - p - s <= 0) return null;
        EditStep e = new EditStep();
        e.Pos = p;
        e.Removed = a.Substring(p, la - p - s);
        e.Inserted = b.Substring(p, lb - p - s);
        return e;
    }

    void ResetHistory(string text)
    {
        undoStack.Clear();
        redoStack.Clear();
        prevText = text;
    }

    void ApplyStep(int pos, int takeLength, string put)
    {
        applyingHistory = true;
        editor.SelectionStart = pos;
        editor.SelectionLength = takeLength;
        Replace(put);
        editor.SelectionStart = pos + put.Length;
        editor.SelectionLength = 0;
        applyingHistory = false;
        prevText = editor.Text;
        editor.ScrollToCaret();
    }

    void Undo()
    {
        if (undoStack.Count == 0) return;
        EditStep s = undoStack[undoStack.Count - 1];
        undoStack.RemoveAt(undoStack.Count - 1);
        ApplyStep(s.Pos, s.Inserted.Length, s.Removed);
        redoStack.Add(s);
    }

    void Redo()
    {
        if (redoStack.Count == 0) return;
        EditStep s = redoStack[redoStack.Count - 1];
        redoStack.RemoveAt(redoStack.Count - 1);
        ApplyStep(s.Pos, s.Removed.Length, s.Inserted);
        undoStack.Add(s);
    }

    void OnEdited()
    {
        if (suppress) return;
        string now = editor.Text;
        if (!applyingHistory)
        {
            EditStep step = Diff(prevText, now);
            if (step != null)
            {
                undoStack.Add(step);
                if (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
                redoStack.Clear();
            }
        }
        prevText = now;
        SetDirty(true);
        // Just stamp the time. Stopping and restarting a WinForms timer costs
        // over 2ms a keystroke, so the timer is left ticking cheaply instead
        // and decides for itself when typing has paused.
        lastEditTick = Environment.TickCount;
        if (!renderTimer.Enabled) renderTimer.Start();
    }

    // Fires every 60ms while a typing burst is in progress; the repaint only
    // happens once the burst has actually stopped. Bigger files wait longer.
    void RenderTick()
    {
        int n = prevText.Length;
        int quiet = n > 200000 ? 700 : n > 60000 ? 350 : 140;
        if (Environment.TickCount - lastEditTick < quiet) return;
        RenderNow();
    }

    void RenderNow()
    {
        if (renderTimer != null) renderTimer.Stop();
        // in Edit mode the preview is not on screen; rebuilding it would be
        // pure waste, so it is deferred until the pane comes back
        if (split != null && split.Panel2Collapsed) { UpdateStatus(); return; }
        string text = prevText;          // kept in step with the control
        if (text == lastRendered) { UpdateStatus(); return; }
        lastRendered = text;

        IntPtr h = preview.Handle;
        int firstLine = Native.SendMessage(h, Native.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
        Native.SendMessage(h, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            preview.Rtf = Md.ToRtf(text, pal, bodyFont, monoFont, cfg.FontPt, out links);
        }
        catch
        {
            links = new List<LinkSpan>();
            preview.Text = text;
        }
        if (firstLine > 0)
            Native.SendMessage(h, Native.EM_LINESCROLL, IntPtr.Zero, (IntPtr)firstLine);
        Native.SendMessage(h, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        preview.Invalidate();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        string v = prevText;
        int words = 0, lines = 1;
        bool inWord = false;
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (c == '\n') { lines++; inWord = false; continue; }
            if (c == ' ' || c == '\t' || c == '\r') { inWord = false; continue; }
            if (!inWord) { inWord = true; words++; }
        }
        string name = docPath.Length > 0 ? Path.GetFileName(docPath) : "untitled.md";
        string line = string.Format("{0}{1}     {2} words   {3} chars   {4} lines   {5} min read",
            name, dirty ? " *" : "", words, v.Length, lines, Math.Max(1, (int)Math.Round(words / 220.0)));
        if (status.Text != line) status.Text = line;
        string title = name + (dirty ? " *" : "") + " - Markdown";
        if (Text != title) Text = title;   // setting a window title is not free
    }

    // ----------------------------------------------------------------- links

    LinkSpan HitLink(Point p)
    {
        if (links.Count == 0) return null;
        int idx = preview.GetCharIndexFromPosition(p);
        if (idx < 0) return null;
        Point at = preview.GetPositionFromCharIndex(idx);
        if (Math.Abs(at.Y - p.Y) > preview.Font.Height * 2) return null;
        if (p.X < at.X - 4) return null;
        foreach (LinkSpan s in links)
            if (idx >= s.Start && idx < s.Start + s.Length) return s;
        return null;
    }

    void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        preview.Cursor = HitLink(e.Location) != null ? Cursors.Hand : Cursors.IBeam;
    }

    void Preview_MouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        LinkSpan s = HitLink(e.Location);
        if (s == null) return;
        string url = s.Url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
        else
        {
            // relative link: open it here if it looks like a markdown file
            try
            {
                string baseDir = docPath.Length > 0 ? Path.GetDirectoryName(docPath) : Environment.CurrentDirectory;
                string full = Path.GetFullPath(Path.Combine(baseDir, url));
                if (File.Exists(full) && ConfirmDiscard()) LoadFile(full);
            }
            catch { }
        }
    }

    // --------------------------------------------------------------- backups

    static string BackupDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MarkdownEditor");
        }
    }

    // Counts only while this window has focus; a manual save or a backup puts
    // it back to zero.
    void BackupTick(object sender, EventArgs e)
    {
        if (!windowActive) return;
        focusedSeconds++;
        if (focusedSeconds < BackupAfterSeconds) return;
        focusedSeconds = 0;
        MakeBackup();
    }

    void MakeBackup()
    {
        string text = prevText;
        if (text.Length == 0) return;
        if (text == lastBackupText) return;        // nothing new to preserve
        try
        {
            string dir = BackupDir;
            Directory.CreateDirectory(dir);
            string baseName = docPath.Length > 0
                ? Path.GetFileNameWithoutExtension(docPath) : "untitled";
            string stamp = DateTime.Now.ToString("ddMMyyyyHHmmss", CultureInfo.InvariantCulture);
            string outPath = Path.Combine(dir, baseName + stamp + ".md");
            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                byte[] data = new UTF8Encoding(false).GetBytes(usesLf ? text.Replace("\r\n", "\n") : text);
                fs.Write(data, 0, data.Length);
            }
            lastBackupText = text;
            PruneBackups();
        }
        catch { /* a backup must never interrupt editing */ }
    }

    class BackupFile
    {
        public string Path;
        public DateTime When;
    }

    // Keep the newest three backups of each document, for the five documents
    // backed up most recently. Everything else is deleted.
    void PruneBackups()
    {
        try
        {
            string dir = BackupDir;
            if (!Directory.Exists(dir)) return;
            Dictionary<string, List<BackupFile>> groups = new Dictionary<string, List<BackupFile>>();
            foreach (string f in Directory.GetFiles(dir, "*.md"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.Length <= 14) continue;
                string stamp = n.Substring(n.Length - 14);
                DateTime when;
                if (!DateTime.TryParseExact(stamp, "ddMMyyyyHHmmss", CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out when)) continue;
                string key = n.Substring(0, n.Length - 14);
                if (!groups.ContainsKey(key)) groups[key] = new List<BackupFile>();
                BackupFile bf = new BackupFile();
                bf.Path = f;
                bf.When = when;
                groups[key].Add(bf);
            }

            List<string> keys = new List<string>(groups.Keys);
            keys.Sort(delegate(string a, string b)
            {
                return Newest(groups[b]).CompareTo(Newest(groups[a]));
            });

            for (int i = 0; i < keys.Count; i++)
            {
                List<BackupFile> list = groups[keys[i]];
                list.Sort(delegate(BackupFile a, BackupFile b) { return b.When.CompareTo(a.When); });
                int keep = i < BackupDocs ? BackupsPerDoc : 0;
                for (int j = keep; j < list.Count; j++)
                {
                    try { File.Delete(list[j].Path); }
                    catch { }
                }
            }
        }
        catch { }
    }

    static DateTime Newest(List<BackupFile> list)
    {
        DateTime best = DateTime.MinValue;
        foreach (BackupFile b in list) if (b.When > best) best = b.When;
        return best;
    }

    // ------------------------------------------------------------------ file

    void SetText(string text, string path, bool bom)
    {
        suppress = true;
        editor.Text = text;
        suppress = false;
        docPath = path;
        hadBom = bom;
        savedText = text;
        lastRendered = null;
        ResetHistory(text);
        lastBackupText = text;      // an untouched document never gets backed up
        focusedSeconds = 0;
        SetDirty(false);
        editor.SelectionStart = 0;
        editor.SelectionLength = 0;
        if (path.Length > 0) PushRecent(path);
        else BuildSideList();
        RenderNow();
    }

    void SetDirty(bool d)
    {
        if (dirty == d) return;
        dirty = d;
        UpdateStatus();
    }

    void NewDoc()
    {
        if (!ConfirmDiscard()) return;
        usesLf = false;          // a new file gets Windows line endings
        SetText("", "", false);
        editor.Focus();
    }

    void LoadFile(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            UTF8Encoding enc = new UTF8Encoding(false);
            string text = bom ? enc.GetString(raw, 3, raw.Length - 3) : enc.GetString(raw);
            usesLf = text.IndexOf("\r\n", StringComparison.Ordinal) < 0 && text.IndexOf('\n') >= 0;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            SetText(text, Path.GetFullPath(path), bom);
            editor.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the file.\r\n\r\n" + ex.Message,
                "Markdown", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OpenDoc()
    {
        if (!ConfirmDiscard()) return;
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = "Open markdown file";
            d.Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd"
                     + "|Text (*.txt)|*.txt|All files (*.*)|*.*";
            if (docPath.Length > 0)
            {
                try { d.InitialDirectory = Path.GetDirectoryName(docPath); }
                catch { }
            }
            if (d.ShowDialog(this) == DialogResult.OK) LoadFile(d.FileName);
        }
    }

    bool SaveDoc(bool saveAs)
    {
        string target = docPath;
        if (saveAs || target.Length == 0)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Save markdown file";
                d.Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd"
                         + "|Text (*.txt)|*.txt|All files (*.*)|*.*";
                d.DefaultExt = "md";
                d.AddExtension = true;
                d.FileName = target.Length > 0 ? Path.GetFileName(target) : "untitled.md";
                if (target.Length > 0)
                {
                    try { d.InitialDirectory = Path.GetDirectoryName(target); }
                    catch { }
                }
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                target = d.FileName;
            }
        }
        try
        {
            string text = editor.Text;
            if (usesLf) text = text.Replace("\r\n", "\n");
            byte[] data = new UTF8Encoding(false).GetBytes(text);
            string tmp = target + ".tmp~";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                if (hadBom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(data, 0, data.Length);
            }
            if (File.Exists(target)) File.Replace(tmp, target, null);
            else File.Move(tmp, target);
            docPath = Path.GetFullPath(target);
            savedText = prevText;
            focusedSeconds = 0;          // a real save restarts the backup clock
            SetDirty(false);
            PushRecent(docPath);
            UpdateStatus();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file.\r\n\r\n" + ex.Message,
                "Markdown", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    bool ConfirmDiscard()
    {
        if (!dirty) return true;
        DialogResult r = MessageBox.Show(this,
            "Save changes to " + (docPath.Length > 0 ? Path.GetFileName(docPath) : "untitled.md") + "?",
            "Markdown", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return SaveDoc(false);
        return true;
    }

    // ---------------------------------------------------------------- editing

    void Replace(string text)
    {
        // EM_REPLACESEL with wParam=1 keeps the control's own undo stack intact
        Native.SendMessageStr(editor.Handle, Native.EM_REPLACESEL, (IntPtr)1, text);
    }

    int LineStart(string v, int pos)
    {
        if (pos <= 0) return 0;
        int i = v.LastIndexOf('\n', pos - 1);
        return i < 0 ? 0 : i + 1;
    }

    int LineEnd(string v, int pos)
    {
        int i = v.IndexOf('\n', pos);
        if (i < 0) return v.Length;
        return (i > 0 && v[i - 1] == '\r') ? i - 1 : i;
    }

    void Wrap(string pre, string post, string placeholder)
    {
        string v = editor.Text;
        int s = editor.SelectionStart, len = editor.SelectionLength;
        string sel = v.Substring(s, len);
        // clicking bold twice on the same selection removes it again
        if (s >= pre.Length && s + len + post.Length <= v.Length
            && v.Substring(s - pre.Length, pre.Length) == pre
            && v.Substring(s + len, post.Length) == post)
        {
            editor.SelectionStart = s - pre.Length;
            editor.SelectionLength = len + pre.Length + post.Length;
            Replace(sel);
            editor.SelectionStart = s - pre.Length;
            editor.SelectionLength = sel.Length;
            return;
        }
        string body = len > 0 ? sel : placeholder;
        Replace(pre + body + post);
        editor.SelectionStart = s + pre.Length;
        editor.SelectionLength = body.Length;
    }

    void Link()
    {
        string v = editor.Text;
        int s = editor.SelectionStart, len = editor.SelectionLength;
        string sel = v.Substring(s, len);
        if (sel.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || sel.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Replace("[](" + sel + ")");
            editor.SelectionStart = s + 1;
            editor.SelectionLength = 0;
        }
        else
        {
            Replace("[" + sel + "](url)");
            editor.SelectionStart = s + sel.Length + 3;
            editor.SelectionLength = 3;
        }
    }

    // apply fn to every line the selection touches
    void MapLines(Func<string, string> fn)
    {
        string v = editor.Text;
        int selStart = editor.SelectionStart, selLen = editor.SelectionLength;
        bool collapsed = selLen == 0;
        int a = LineStart(v, selStart);
        int b = LineEnd(v, selStart + selLen);
        string before = v.Substring(a, b - a);
        string[] parts = before.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            string line = parts[i].TrimEnd('\r');
            bool cr = parts[i].EndsWith("\r");
            parts[i] = fn(line) + (cr ? "\r" : "");
        }
        string after = string.Join("\n", parts);
        if (after == before) return;
        editor.SelectionStart = a;
        editor.SelectionLength = b - a;
        Replace(after);
        if (collapsed)
        {
            int p = Math.Max(a, Math.Min(a + after.Length, selStart + after.Length - before.Length));
            editor.SelectionStart = p;
            editor.SelectionLength = 0;
        }
        else
        {
            editor.SelectionStart = a;
            editor.SelectionLength = after.Length;
        }
    }

    void Heading()
    {
        MapLines(delegate(string l)
        {
            int h = 0;
            while (h < l.Length && l[h] == '#') h++;
            if (h == 0 || h >= l.Length || l[h] != ' ') return "# " + l;
            return h >= 6 ? l.Substring(7) : "#" + l;
        });
    }

    void TogglePrefix(string prefix)
    {
        string trimmed = prefix.TrimEnd();
        MapLines(delegate(string l)
        {
            string t = l.TrimStart();
            int indent = l.Length - t.Length;
            if (t.StartsWith(prefix)) return l.Substring(0, indent) + t.Substring(prefix.Length);
            if (t == trimmed) return l.Substring(0, indent);
            return l.Substring(0, indent) + prefix + t;
        });
    }

    // Tab never reaches KeyDown - the framework claims it for focus navigation
    // first - so it is handled at the ProcessCmdKey stage instead.
    void HandleTab(bool shift)
    {
        string v = editor.Text;
        int a = LineStart(v, editor.SelectionStart);
        string firstLine = v.Substring(a, LineEnd(v, a) - a);
        bool onList = System.Text.RegularExpressions.Regex.IsMatch(
            firstLine, @"^[ \t]*(?:[-*+]|\d{1,9}[.)])([ \t]+|$)");
        if (shift)
            MapLines(delegate(string l)
            {
                if (l.StartsWith("  ")) return l.Substring(2);
                if (l.StartsWith(" ") || l.StartsWith("\t")) return l.Substring(1);
                return l;
            });
        else if (editor.SelectionLength == 0 && !onList)
            Replace("  ");
        else
            MapLines(delegate(string l) { return l.Trim().Length == 0 ? l : "  " + l; });
    }

    void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
        {
            if (editor.SelectionLength != 0) return;
            string v = editor.Text;
            int p = editor.SelectionStart;
            int a = LineStart(v, p);
            string line = v.Substring(a, p - a);
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                line, @"^([ \t]*)(?:([-*+])|(\d{1,9})([.)]))([ \t]+)(\[[ xX]\][ \t]+)?");
            if (!m.Success) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (line.Length == m.Length)
            {
                // empty item: clear the marker and end the list
                editor.SelectionStart = a;
                editor.SelectionLength = p - a;
                Replace("");
                return;
            }
            string marker = m.Groups[2].Success
                ? m.Groups[2].Value
                : (int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + 1) + m.Groups[4].Value;
            Replace("\r\n" + m.Groups[1].Value + marker + m.Groups[5].Value
                    + (m.Groups[6].Success ? "[ ] " : ""));
        }
    }

    // -------------------------------------------------------------- shortcuts

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool ctrl = (keyData & Keys.Control) == Keys.Control;
        bool alt = (keyData & Keys.Alt) == Keys.Alt;
        Keys key = keyData & Keys.KeyCode;

        if (key == Keys.Tab && !ctrl && !alt && editor.Focused)
        {
            HandleTab((keyData & Keys.Shift) == Keys.Shift);
            return true;
        }
        if (ctrl)
        {
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            switch (key)
            {
                case Keys.Z:
                    if (shift) Redo(); else Undo();
                    return true;
                case Keys.Y: Redo(); return true;
                case Keys.S: SaveDoc(shift); return true;
                case Keys.O: OpenDoc(); return true;
                case Keys.N: NewDoc(); return true;
                case Keys.B: Wrap("**", "**", "bold"); return true;
                case Keys.I: Wrap("*", "*", "italic"); return true;
                case Keys.E: Wrap("`", "`", "code"); return true;
                case Keys.K: Link(); return true;
                case Keys.D: ToggleTheme(); return true;
                case Keys.D1: SetMode("edit"); return true;
                case Keys.D2: SetMode("split"); return true;
                case Keys.D3: SetMode("view"); return true;
                case Keys.Oemplus:
                case Keys.Add: SetFontSize(cfg.FontPt + 1); return true;
                case Keys.OemMinus:
                case Keys.Subtract: SetFontSize(cfg.FontPt - 1); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SetFontSize(int pt)
    {
        cfg.FontPt = Math.Max(7, Math.Min(22, pt));
        editor.Font = new Font(monoFont, cfg.FontPt, FontStyle.Regular, GraphicsUnit.Point);
        lastRendered = null;
        RenderNow();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string path = args.Length > 0 ? args[0] : null;
        Application.Run(new MainForm(path));
        return 0;
    }
}
