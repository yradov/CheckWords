using System.Diagnostics;
using CheckWords.Core;

namespace CheckWords;

/// <summary>The tray application: hooks → <see cref="TypingSession"/> → beep / conversion.</summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly Control _ui = new();
    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly InputHooks _hooks = new();
    readonly Icons _icons = new();
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 1500 };
    readonly HashSet<int> _swallowedKeys = new();
    readonly ModifierTracker _modifiers = new();
    readonly Dictionary<uint, string> _processNames = new();
    readonly string _ownProcess = Process.GetCurrentProcess().ProcessName + ".exe";

    AppSettings _settings;
    KeyboardLayouts _layouts = null!;
    LayoutDetector _detector = null!;
    TypingSession _session = null!;
    TextConverter _converter = null!;
    Beeper? _beeper;
    Hotkey? _wordHotkey;
    Hotkey? _selectionHotkey;

    bool _capsOn;
    bool _capsDown;
    bool _busy;
    IntPtr _lastWindow;
    string _lastProcess = "";
    string _lastExternalProcess = "";
    Suggestion? _lastWarning;

    ToolStripMenuItem _miInfo = null!, _miEnabled = null!, _miSound = null!, _miMidWord = null!,
        _miIgnoreWord = null!, _miExclude = null!, _miAutostart = null!;

    public TrayApp()
    {
        _ = _ui.Handle; // create the handle so BeginInvoke works
        _settings = AppSettings.Load();
        LoadEngine();
        BuildMenu();

        _tray.ContextMenuStrip = _menu;
        _tray.DoubleClick += (_, _) => ToggleEnabled();
        _tray.Visible = true;
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); UpdateTray(); };

        _capsOn = (Native.GetKeyState(Vk.Capital) & 1) != 0;
        _hooks.KeyDown = OnHookKeyDown;
        _hooks.KeyUp = OnHookKeyUp;
        _hooks.MouseDown = () => _ui.BeginInvoke(new Action(() => _session.Reset()));
        _hooks.Install();
        UpdateTray();

        if (_layouts.Langs.Count < 2)
            _tray.ShowBalloonTip(5000, "CheckWords",
                "Нужно минимум две раскладки из EN / RU / UK. Найдено: " + string.Join(", ", _layouts.Layouts),
                ToolTipIcon.Warning);
    }

    // ------------------------------------------------------------------ setup

    void LoadEngine()
    {
        _layouts = KeyboardLayouts.Detect();
        var models = LanguageData.Build(_layouts.Langs, UserWords.Load(), AppSettings.DictionaryDir);
        var preferred = _detector?.PreferredCyrillic;
        _detector = new LayoutDetector(_layouts, models);
        if (preferred is { } p && _detector.Langs.Contains(p)) _detector.PreferredCyrillic = p;
        _session = new TypingSession(_layouts, _detector) { WarnMidWord = _settings.WarnMidWord };
        _session.Warning += OnWarning;
        _converter = new TextConverter(_layouts, _detector);

        _beeper?.Dispose();
        _beeper = new Beeper(_settings.BeepFrequency, _settings.BeepDurationMs);
        _wordHotkey = Hotkey.Parse(_settings.ConvertWordHotkey) ?? Hotkey.Parse(AppSettings.DefaultWordHotkey);
        _selectionHotkey = Hotkey.Parse(_settings.ConvertSelectionHotkey) ?? Hotkey.Parse(AppSettings.DefaultSelectionHotkey);
    }

    void BuildMenu()
    {
        _miInfo = new ToolStripMenuItem { Enabled = false };
        _miEnabled = new ToolStripMenuItem("Включено", null, (_, _) => ToggleEnabled());
        _miSound = new ToolStripMenuItem("Звуковой сигнал", null, (_, _) =>
        {
            _settings.Sound = !_settings.Sound;
            _settings.Save();
            if (_settings.Sound) _beeper?.Play();
        });
        _miMidWord = new ToolStripMenuItem("Предупреждать, не дожидаясь конца слова", null, (_, _) =>
        {
            _settings.WarnMidWord = !_settings.WarnMidWord;
            _session.WarnMidWord = _settings.WarnMidWord;
            _settings.Save();
        });
        _miIgnoreWord = new ToolStripMenuItem("", null, (_, _) => IgnoreLastWarning());
        _miExclude = new ToolStripMenuItem("", null, (_, _) => ToggleExcludeLastProcess());
        _miAutostart = new ToolStripMenuItem("Запускать при входе в Windows", null, (_, _) => Autostart.Set(!Autostart.IsEnabled));

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _miInfo,
            new ToolStripSeparator(),
            _miEnabled, _miSound, _miMidWord,
            new ToolStripSeparator(),
            _miIgnoreWord, _miExclude,
            new ToolStripSeparator(),
            _miAutostart,
            new ToolStripMenuItem("Открыть папку настроек", null, (_, _) =>
            {
                _settings.Save();
                Process.Start("explorer.exe", AppSettings.Dir);
            }),
            new ToolStripMenuItem("Перечитать настройки и словари", null, (_, _) =>
            {
                _settings = AppSettings.Load();
                LoadEngine();
                UpdateTray();
            }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => ExitThread()),
        });
        _menu.Opening += (_, _) => RefreshMenu();
    }

    void RefreshMenu()
    {
        _miInfo.Text = $"{_wordHotkey} — исправить слово,  {_selectionHotkey} — выделенное";
        _miEnabled.Checked = _settings.Enabled;
        _miSound.Checked = _settings.Sound;
        _miMidWord.Checked = _settings.WarnMidWord;
        _miAutostart.Checked = Autostart.IsEnabled;

        _miIgnoreWord.Visible = _lastWarning is not null;
        if (_lastWarning is not null)
            _miIgnoreWord.Text = $"Не предупреждать для «{LayoutDetector.Core(_lastWarning.FromText, out _)}»";

        _miExclude.Visible = _lastExternalProcess.Length > 0;
        _miExclude.Text = _settings.IsExcluded(_lastExternalProcess)
            ? $"Включить снова в {_lastExternalProcess}"
            : $"Не работать в {_lastExternalProcess}";
    }

    // ------------------------------------------------------------------ hook callbacks (keep fast)

    bool OnHookKeyDown(KeyInfo info)
    {
        int vk = info.Vk;
        _modifiers.OnKeyDown(info);
        if (vk == Vk.Capital)
        {
            if (!_capsDown) _capsOn = !_capsOn;
            _capsDown = true;
            return false;
        }
        if (!_settings.Enabled) return false;

        var mods = _modifiers.Current();
        Action? action = _wordHotkey?.Matches(vk, mods) == true ? ConvertWord
            : _selectionHotkey?.Matches(vk, mods) == true ? ConvertSelection
            : null;
        if (action is not null)
        {
            if (_swallowedKeys.Add(vk)) // first press, not auto-repeat
            {
                if (mods.Alt) InputSender.MaskAltRelease(); // must happen now, while Alt is still down
                _ui.BeginInvoke(action);
            }
            return true;
        }

        var key = new KeyStroke(vk, info.Scan, mods.Shift, _capsOn, mods.AltGr && !mods.Ctrl);
        _ui.BeginInvoke(new Action(() => ProcessKey(key, mods)));
        return false;
    }

    bool OnHookKeyUp(KeyInfo info)
    {
        _modifiers.OnKeyUp(info);
        if (info.Vk == Vk.Capital) _capsDown = false;
        return _swallowedKeys.Remove(info.Vk);
    }

    // ------------------------------------------------------------------ typing

    void ProcessKey(KeyStroke key, Modifiers mods)
    {
        if (!_settings.Enabled) return;
        var hwnd = Native.GetForegroundWindow();
        uint thread = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (hwnd != _lastWindow)
        {
            _session.Reset();
            _lastWindow = hwnd;
            _lastProcess = ProcessName(pid);
            if (!string.Equals(_lastProcess, _ownProcess, StringComparison.OrdinalIgnoreCase))
                _lastExternalProcess = _lastProcess;
        }
        if (_settings.IsExcluded(_lastProcess)) return;

        var lang = _layouts.LangOf(Native.GetKeyboardLayout(thread));
        if (mods.IsShortcut && !Vk.IsModifier(key.Vk)) { _session.Reset(); return; }
        _session.OnKey(key, lang);
    }

    void OnWarning(Suggestion s)
    {
        _lastWarning = s;
        if (_settings.Sound) _beeper?.Play();
        _tray.Icon = _icons.Warning;
        SetTooltip($"Не та раскладка? «{s.FromText}» → «{s.ToText}» ({s.To.Code()}). {_wordHotkey} — исправить");
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    async void ConvertWord()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!await InputSender.WaitForModifiersReleased()) return;
            var hwnd = Native.GetForegroundWindow();
            if (hwnd != _lastWindow) return;
            var plan = _session.PlanWordConversion();
            if (plan is null) return;

            InputSender.Replace(plan.DeleteCount, plan.Insert);
            _session.Apply(plan);
            SwitchLayout(hwnd, plan.To);
            _flashTimer.Stop();
            UpdateTray();
        }
        finally
        {
            _busy = false;
        }
    }

    async void ConvertSelection()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!await InputSender.WaitForModifiersReleased()) return;
            var hwnd = Native.GetForegroundWindow();

            string? backup = TryClipboard(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            uint seq = Native.GetClipboardSequenceNumber();
            InputSender.CtrlC();
            for (int i = 0; i < 40 && Native.GetClipboardSequenceNumber() == seq; i++) await Task.Delay(15);
            if (Native.GetClipboardSequenceNumber() == seq) return; // nothing selected / copy not supported

            await Task.Delay(30);
            var text = TryClipboard(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            var conversion = string.IsNullOrEmpty(text) ? null : _converter.Convert(text);
            if (conversion is not null)
            {
                InputSender.Replace(0, conversion.Text); // typing replaces the selection
                SwitchLayout(hwnd, conversion.To);
            }
            _session.Reset();

            await Task.Delay(150);
            TryClipboard(() =>
            {
                if (backup is not null) Clipboard.SetText(backup);
                else Clipboard.Clear();
                return (string?)null;
            });
        }
        finally
        {
            _busy = false;
        }
    }

    void SwitchLayout(IntPtr hwnd, Lang lang)
    {
        if (_layouts.Get(lang) is { } layout)
            Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout.Hkl);
    }

    static string? TryClipboard(Func<string?> action)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return action(); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(20); } // clipboard busy
        }
        return null;
    }

    // ------------------------------------------------------------------ menu actions

    void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _settings.Save();
        _session.Reset();
        UpdateTray();
    }

    void IgnoreLastWarning()
    {
        if (_lastWarning is null) return;
        var word = LanguageModel.Normalize(LayoutDetector.Core(_lastWarning.FromText, out _));
        if (word.Length > 0) UserWords.Add(word);
        _lastWarning = null;
        LoadEngine();
    }

    void ToggleExcludeLastProcess()
    {
        if (_lastExternalProcess.Length == 0) return;
        if (_settings.IsExcluded(_lastExternalProcess))
            _settings.ExcludedProcesses.RemoveAll(p => string.Equals(p, _lastExternalProcess, StringComparison.OrdinalIgnoreCase));
        else
            _settings.ExcludedProcesses.Add(_lastExternalProcess);
        _settings.Save();
        _session.Reset();
    }

    void UpdateTray()
    {
        _tray.Icon = _settings.Enabled ? _icons.Normal : _icons.Disabled;
        var langs = string.Join("/", _layouts.Langs.Select(l => l.Code()));
        SetTooltip(_settings.Enabled
            ? $"CheckWords — слежу за раскладкой ({langs}). {_wordHotkey} — исправить слово"
            : "CheckWords — выключен (двойной клик — включить)");
    }

    void SetTooltip(string text) => _tray.Text = text.Length <= 127 ? text : text[..124] + "...";

    string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
        catch { name = ""; }
        _processNames[pid] = name;
        return name;
    }

    protected override void ExitThreadCore()
    {
        _hooks.Dispose();
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hooks.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _flashTimer.Dispose();
            _beeper?.Dispose();
            _icons.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
