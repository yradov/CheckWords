# CheckWords

**English** | [Русский](docs/README.ru.md)

A Windows tray app that watches what you type and **beeps** when a word was typed in the wrong keyboard layout
(EN / RU / UK). A hotkey **retypes** the word in the right layout and switches the layout.

| Action | Default key |
|---|---|
| Fix the last word (pressing again cycles EN → RU → UK) | `Alt` + `\` (left or right Alt) |
| Fix the selected text | `Ctrl` + `Alt` + `\` |
| Turn on / off | double-click the tray icon |

Example: you type `ghbdtn ` and hear the beep. Press `Alt+\` and get `привет `; the layout is now RU.

`\` is the key above Enter (`|` with Shift). It types nothing with AltGr in any of the three layouts,
so capturing it takes no characters away. Details that are handled:

- **Right Alt in the Ukrainian layout is AltGr**: Windows adds a fake left Ctrl to it
  (scan code `0x21D`). The app tells it apart from a real Ctrl, so right Alt + `\` always
  means "word" and `Ctrl` + `Alt` + `\` means "selection". Typing `ґ` with AltGr still works.
- **Releasing Alt does not open the window menu**: while Alt is held, a "mask" key `VK 0xE8` is sent.
- **Nothing is typed while modifiers are held** (otherwise Backspace would become Alt+Backspace = "Undo").
  If they are not released within 2 seconds, the action is cancelled.

The app's interface (tray menu, dialogs) is in Russian. Menu items below are given in English
with the original label in parentheses.

## Running

```bash
dotnet run --project src/CheckWords
```

Build a single exe (requires the .NET 10 Desktop Runtime):

```bash
dotnet publish src/CheckWords -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

For an exe that runs without .NET installed, use `--self-contained true` (the file will be about 70 MB).

## Tests

```bash
dotnet run --project tests/CheckWords.Tests
```

The tests use the real Windows layouts, so EN, RU and UK must be installed.
The `-v` flag prints missed words with their scores. The `--autostart` flag tests the Task Scheduler integration:
it creates, reads and deletes a temporary task `CheckWords.SelfTest` without administrator rights.

## Autostart

The tray menu item **"Run at Windows sign-in"** («Запускать при входе в Windows») knows two ways to autostart
and shows which one is on:

| State | Item text | Click |
|---|---|---|
| nothing | "Run at Windows sign-in" | asks "with administrator rights?": **Yes** creates a Task Scheduler task (UAC prompt), **No** adds a registry entry |
| registry entry `HKCU\…\Run` | "… (without administrator rights)" | removes the entry |
| "CheckWords" task in Task Scheduler | "… (Task Scheduler, with administrator rights)" | after confirmation deletes the task (UAC prompt) |

Notes are appended to the item text: the task is disabled; the task runs a different exe (the path is in the tooltip);
there is also a registry entry next to the task.

The task runs with highest privileges so that the app also works in windows started as administrator.
It starts 10 seconds after sign-in, with no time limit (the default would be 72 hours),
also runs on battery, and uses normal priority.

Both methods must not be on at the same time: the registry entry starts earlier and launches a copy without
administrator rights, and the copy from the task then exits (only one instance runs at a time).
So on startup the app removes the extra registry entry itself when the task is enabled.

## How it works

1. **Capture.** A global `WH_KEYBOARD_LL` hook. The buffer stores *physical keys* (VK + Shift + CapsLock),
   not characters. So "convert to another layout" means reading the same keys through `ToUnicodeEx`
   with another layout. No `q↔й` tables are needed.
2. **The detector** (`src/CheckWords.Core/LayoutDetector.cs`) compares each word as it looks in every layout:
   - by **dictionary** from `Data/*.txt`: if the word is unknown in the current layout but known in another, it is a mistake;
   - by a **trigram model** trained on the same words: `ghbdtn` is implausible for English, `привет` is fine for Russian;
   - by **punctuation inside the word**: `e;by` (that is, «ужин») is suspicious right away.
   The check runs at the end of a word (space, punctuation) and, if enabled, mid-word starting from 3–4 letters.
3. **Fixing.** The app sends N × Backspace, then types the text via `SendInput` (Unicode)
   and switches the window's layout with `WM_INPUTLANGCHANGEREQUEST`.
4. **The buffer is reset** on a mouse click, window change, arrows, Tab, Enter and Ctrl/Alt/Win shortcuts,
   that is, whenever the caret position is no longer known.

Accuracy on held-out test phrases (words that are not in the training lists):

| Typed in | Meant | Caught |
|---|---|---|
| RU / UK | EN | 100% |
| EN | RU | 99% |
| EN | UK | 96% |
| RU | UK | 90% |
| UK | RU | 81% |

Zero false positives on 422 correctly typed words and on 95 technical terms and names
(`kubectl`, `useEffect`, `Kyiv`, etc.).

RU and UK differ in only a few keys (ы/і, э/є, ъ/ї, ё/'), so this pair is the hardest.
Larger dictionaries improve it (see below).

## Settings

Everything lives in `%APPDATA%\CheckWords\` (the tray menu has "Open settings folder" / «Открыть папку настроек»):

- `settings.json`: hotkeys (`"Alt+Oem5"`, `"Shift+Pause"`, `"Ctrl+Alt+Space"`: key names from
  `System.Windows.Forms.Keys` or a single character as in the EN layout, in JSON `"Alt+\\"`),
  sound (frequency and duration), and the list of excluded programs.
  After editing, click "Reload settings and dictionaries" («Перечитать настройки и словари»).
- `user-words.txt`: words to ignore. The menu item "Don't warn for "…"" («Не предупреждать для «…»») adds them here.
- `dict\en*.txt`, `dict\ru*.txt`, `dict\uk*.txt`: **extra large dictionaries**, one word per line,
  for example exported from Hunspell. They extend both the dictionary and the trigram model and noticeably improve RU↔UK.

By default the app is off in RDP (`mstsc.exe`) and in password managers.
The menu can turn it off for any current program.

## Prototype limitations

- Windows started **as administrator** are invisible to the hook (UIPI protection). To work in them, CheckWords
  must also run as administrator: the easiest way is autostart with administrator rights (see above).
- The hook is global: `Alt+\` and `Ctrl+Alt+\` no longer reach other programs. If a program needs them,
  change the hotkey or add the program to the exclusions.
- `Ctrl+Pause` won't work as a hotkey: Windows sends it as a different key (`Cancel`).
- Fixing selected text goes through the clipboard: only the clipboard *text* is saved and restored,
  images in it will be lost.
- Dead keys reset the current word buffer.
- Input generated by other programs (macros, on-screen keyboard) is ignored.
  The environment variable `CHECKWORDS_ACCEPT_INJECTED=1` enables it, but only for automated tests.
- The app writes nothing to disk and sends nothing anywhere. The buffer keeps at most the last 256 keys in memory.

## Structure

```
src/CheckWords.Core/   UI-free logic: layouts, dictionaries, detector, typing buffer, selection converter
  Data/*.txt           built-in EN / RU / UK dictionaries
src/CheckWords/        WinForms tray app: hooks, SendInput, menu, sound, settings
tests/CheckWords.Tests console test suite and accuracy report
```
