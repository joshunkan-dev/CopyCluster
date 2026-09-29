using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CopyCluster;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly ClipboardMonitor _monitor;
    readonly KeyboardHook _keyboard;
    readonly ClusterManager _cluster;
    readonly HotkeyWindow _hotkeys;
    readonly ClusterViewer _viewer;
    GuideForm? _guide;

    public TrayContext()
    {
        _cluster = new ClusterManager();
        _cluster.Appended += count =>
            _tray.ShowBalloonTip(1000, "CopyCluster", $"Cluster now has {count} items", ToolTipIcon.Info);
        _viewer = new ClusterViewer(_cluster);

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show cluster", null, (_, _) => _viewer.Reveal())
            { ShortcutKeyDisplayString = "Ctrl+Alt+S" };
        var clearItem = new ToolStripMenuItem("Clear cluster", null, (_, _) => ClearCluster())
            { ShortcutKeyDisplayString = "Ctrl+Alt+C" };
        var pauseItem = new ToolStripMenuItem("Pause")
            { CheckOnClick = true, ShortcutKeyDisplayString = "Ctrl+Alt+P" };
        pauseItem.CheckedChanged += (_, _) =>
        {
            _cluster.Paused = pauseItem.Checked;
            if (pauseItem.Checked) _cluster.Reset();
            _tray.Text = pauseItem.Checked ? "CopyCluster (paused)" : "CopyCluster";
            _tray.ShowBalloonTip(1000, "CopyCluster", pauseItem.Checked ? "Paused" : "Resumed", ToolTipIcon.Info);
        };
        menu.Items.Add(showItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Guide && hotkeys", null, (_, _) => ShowGuide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CopyCluster",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowGuide();

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardChanged += _cluster.OnClipboardChanged;

        _keyboard = new KeyboardHook();
        _keyboard.PastePressed += _cluster.Reset;

        _hotkeys = new HotkeyWindow();
        var failed = new List<string>();
        if (!_hotkeys.Register(1, Keys.S, _viewer.Reveal)) failed.Add("Ctrl+Alt+S");
        if (!_hotkeys.Register(2, Keys.C, ClearCluster)) failed.Add("Ctrl+Alt+C");
        if (!_hotkeys.Register(3, Keys.P, () => pauseItem.Checked = !pauseItem.Checked)) failed.Add("Ctrl+Alt+P");
        if (failed.Count > 0)
            _tray.ShowBalloonTip(4000, "CopyCluster",
                $"Couldn't register: {string.Join(", ", failed)}. Another app may be using them.", ToolTipIcon.Warning);
    }

    void ClearCluster()
    {
        _cluster.Reset();
        _tray.ShowBalloonTip(1000, "CopyCluster", "Cluster cleared", ToolTipIcon.Info);
    }

    void ShowGuide()
    {
        if (_guide is null || _guide.IsDisposed) _guide = new GuideForm();
        _guide.Show();
        _guide.WindowState = FormWindowState.Normal;
        _guide.Activate();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _monitor.Dispose();
        _keyboard.Dispose();
        _hotkeys.Dispose();
        _viewer.Dispose();
        _guide?.Dispose();
        base.ExitThreadCore();
    }
}

sealed class ClusterManager
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    string? _cluster;       // the combined text so far (null = no active cluster)
    string? _lastWritten;   // what WE last put on the clipboard, to ignore our own writes
    int _count;
    DateTime _lastCopy = DateTime.MinValue;

    public string Separator { get; set; } = " ";   // change to "\n" for new lines
    public bool Paused { get; set; }
    public string? Current => _cluster;
    public int Count => _count;
    public event Action<int>? Appended;
    public event Action? Changed;

    public void Reset()
    {
        _cluster = null;
        _count = 0;
        Changed?.Invoke();
    }

    public void OnClipboardChanged()
    {
        if (Paused) return;

        string? text = ReadText();
        if (text is null) { Reset(); return; }   // not text, or flagged sensitive
        if (text == _lastWritten) return;        // this change was our own write

        var now = DateTime.UtcNow;
        if (_cluster is null || now - _lastCopy > Timeout)
        {
            // First copy: start a new cluster, leave the clipboard alone.
            _cluster = text;
            _count = 1;
            Changed?.Invoke();
        }
        else
        {
            // Second (or later) copy with no paste in between: append.
            _cluster += Separator + text;
            _count++;
            if (WriteText(_cluster))
            {
                _lastWritten = _cluster;
                Changed?.Invoke();
                Appended?.Invoke(_count);
            }
        }
        _lastCopy = now;
    }

    static string? ReadText()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Clipboard.GetDataObject()?.GetDataPresent(ExcludeFormat) == true) return null;
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException) { Thread.Sleep(30); }   // clipboard busy, retry
        }
        return null;
    }

    static bool WriteText(string s)
    {
        try { Clipboard.SetDataObject(s, true, 10, 50); return true; }
        catch (ExternalException) { return false; }
    }
}

sealed class ClusterViewer : Form
{
    readonly ClusterManager _cluster;
    readonly Label _header = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 8, 0, 0) };
    readonly TextBox _box = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BackColor = SystemColors.Window
    };

    public ClusterViewer(ClusterManager cluster)
    {
        _cluster = cluster;
        Text = "CopyCluster - current cluster";
        Size = new Size(480, 340);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6)
        };
        var close = new Button { Text = "Close", Width = 80 };
        close.Click += (_, _) => Hide();
        var clear = new Button { Text = "Clear cluster", Width = 100 };
        clear.Click += (_, _) => _cluster.Reset();
        buttons.Controls.Add(close);
        buttons.Controls.Add(clear);

        // Fill control must be added first so it docks last.
        Controls.Add(_box);
        Controls.Add(buttons);
        Controls.Add(_header);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        _cluster.Changed += () => { if (Visible) Reload(); };
    }

    void Reload()
    {
        string? text = _cluster.Current;
        if (text is null)
        {
            _header.Text = "Cluster is empty";
            _box.Text = "";
        }
        else
        {
            _header.Text = $"{_cluster.Count} item(s), {text.Length} characters";
            _box.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        _box.Select(0, 0);
    }

    public void Reveal()
    {
        Reload();
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }
}

sealed class GuideForm : Form
{
    public GuideForm()
    {
        Text = "CopyCluster - guide";
        Size = new Size(540, 500);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        string[] lines =
        {
            "HOW IT WORKS",
            "  1. Copy something with Ctrl+C, as normal.",
            "  2. Copy again WITHOUT pasting in between. The new text is",
            "     added to the previous text on your clipboard.",
            "  3. Paste once with Ctrl+V to get everything together.",
            "  4. Pasting resets the cluster, so the next copy starts fresh.",
            "",
            "HOTKEYS (work in any app)",
            "  Ctrl+Alt+S    Show the current cluster",
            "  Ctrl+Alt+C    Clear the cluster",
            "  Ctrl+Alt+P    Pause / resume CopyCluster",
            "",
            "TRAY ICON",
            "  Double-click   Open this guide",
            "  Right-click    Menu with all of the above, plus Exit",
            "",
            "GOOD TO KNOW",
            "  - Only text is combined; images and files are ignored.",
            "  - The cluster also resets after 60 seconds without a copy.",
            "  - If you paste using right-click > Paste, CopyCluster can't",
            "    tell. Press Ctrl+Alt+C afterwards to start fresh.",
            "  - Copies flagged as sensitive (like password managers) are skipped."
        };

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10), BackColor = SystemColors.Window,
            Text = string.Join("\r\n", lines)
        };
        Controls.Add(box);
        Shown += (_, _) => box.Select(0, 0);
    }
}

sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Dictionary<int, Action> _actions = new();

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    public bool Register(int id, Keys key, Action action)
    {
        if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key)) return false;
        _actions[id] = action;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue(m.WParam.ToInt32(), out var action)) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}

sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int VK_V = 0x56, VK_INSERT = 0x2D, VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    readonly LowLevelKeyboardProc _proc;   // stored in a field so it isn't garbage collected
    IntPtr _hook;

    public event Action? PastePressed;

    public KeyboardHook()
    {
        _proc = HookCallback;
        using var process = Process.GetCurrentProcess();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(process.MainModule!.ModuleName), 0);
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);   // first field of the key info struct
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if ((vk == VK_V && ctrl) || (vk == VK_INSERT && shift))
                PastePressed?.Invoke();
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);   // never block the keystroke
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}

sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action? ClipboardChanged;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) ClipboardChanged?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}