using System.Text;
using System.Text.RegularExpressions;
using CheckWords.Core;

// Self-checking harness: unit checks for the session/converter plus an accuracy report of the
// detector on held-out sentences typed in the right and in the wrong layout.
// Needs the EN, RU and UK layouts installed (it uses the real Windows layouts).

Console.OutputEncoding = Encoding.UTF8;
bool verbose = args.Contains("-v");

var layouts = KeyboardLayouts.Detect();
Console.WriteLine("Layouts: " + string.Join(", ", layouts.Layouts));
if (!new[] { Lang.En, Lang.Ru, Lang.Uk }.All(l => layouts.Langs.Contains(l)))
{
    Console.WriteLine("EN, RU and UK layouts must be installed to run the tests.");
    return 2;
}

var models = LanguageData.Build(layouts.Langs);
foreach (var m in models.Values) Console.WriteLine($"  {m.Lang.Code()}: {m.WordCount} words");
var detector = new LayoutDetector(layouts, models);
int failures = 0;

void Check(bool ok, string name, string details = "")
{
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {name}{(ok || details == "" ? "" : " — " + details)}");
    if (!ok) failures++;
}

List<KeyStroke> Type(string text, Lang lang) =>
    text.Select(c => layouts.FromChar(c, lang) ?? throw new ArgumentException($"'{c}' is not on the {lang} layout")).ToList();

// ---------------------------------------------------------------- unit checks
Console.WriteLine("\nUnit checks");
{
    var s = detector.Analyze(Type("привет", Lang.Ru), Lang.En, complete: true);
    Check(s?.To == Lang.Ru && s.ToText == "привет", "ghbdtn → привет", $"{s}");

    s = detector.Analyze(Type("hello", Lang.En), Lang.Ru, complete: true);
    Check(s?.To == Lang.En && s.ToText == "hello", "руддщ → hello", $"{s}");

    s = detector.Analyze(Type("привіт", Lang.Uk), Lang.En, complete: true);
    Check(s?.To == Lang.Uk, "ghbdsn → привіт (UK)", $"{s}");

    s = detector.Analyze(Type("він", Lang.Uk), Lang.Ru, complete: true);
    Check(s?.To == Lang.Uk && s.ToText == "він", "вын (RU) → він (UK)", $"{s}");

    s = detector.Analyze(Type("мы", Lang.Ru), Lang.Uk, complete: true);
    Check(s?.To == Lang.Ru && s.ToText == "мы", "мі (UK) → мы (RU)", $"{s}");

    Check(detector.Analyze(Type("hello", Lang.En), Lang.En, true) is null, "hello in EN: no warning");
    Check(detector.Analyze(Type("readme.md", Lang.En), Lang.En, true) is null, "readme.md in EN: no warning");
    Check(detector.Analyze(Type("word,", Lang.En), Lang.En, true) is null, "'word,' in EN: no warning");

    s = detector.Analyze(Type("хорошо", Lang.Ru), Lang.En, complete: false);
    Check(s?.To == Lang.Ru, "mid-word: [jhjij → хорошо", $"{s}");

    // Session: type "ghbdtn " in EN, expect a warning, then Pause → "привет ", Pause again → back.
    var session = new TypingSession(layouts, detector);
    Suggestion? warned = null;
    session.Warning += w => warned ??= w;
    foreach (var k in Type("ghbdtn ", Lang.En)) session.OnKey(k, Lang.En);
    Check(warned?.To == Lang.Ru, "session warns on 'ghbdtn '", $"{warned}");
    var plan = session.PlanWordConversion();
    Check(plan is { To: Lang.Ru, Insert: "привет ", DeleteCount: 7 }, "plan: delete 7, insert 'привет '", $"{plan}");
    session.Apply(plan!);
    var plan2 = session.PlanWordConversion();
    Check(plan2 is { To: Lang.En, Insert: "ghbdtn " }, "second press cycles back to EN", $"{plan2}");

    // Session: shift is kept → "Ghbdtn" → "Привет"
    session.Reset();
    foreach (var k in Type("Ghbdtn", Lang.En)) session.OnKey(k, Lang.En);
    plan = session.PlanWordConversion();
    Check(plan?.Insert == "Привет", "case is kept: Ghbdtn → Привет", $"{plan}");

    // Session: backspace edits the buffer
    session.Reset();
    foreach (var k in Type("ghbdtnn", Lang.En)) session.OnKey(k, Lang.En);
    session.OnKey(new KeyStroke(Vk.Back, 0, false, false), Lang.En);
    plan = session.PlanWordConversion();
    Check(plan?.Insert == "привет" && plan.DeleteCount == 6, "backspace is tracked", $"{plan}");

    // Session: a mouse click / arrow resets
    session.OnKey(new KeyStroke(0x25, 0, false, false), Lang.En); // left arrow
    Check(session.PlanWordConversion() is null, "arrow key resets the buffer");

    // Selection converter
    var conv = new TextConverter(layouts, detector);
    var c1 = conv.Convert("Ghbdtn? rfr ltkf&");
    Check(c1?.Text == "Привет, как дела?" && c1.To == Lang.Ru, "selection: 'Ghbdtn? rfr ltkf&' → 'Привет, как дела?'", $"{c1}");
    var c2 = conv.Convert("Ghbdsn? zr cghfdb&");
    Check(c2?.Text == "Привіт, як справи?" && c2.To == Lang.Uk, "selection: → 'Привіт, як справи?'", $"{c2}");
    var c3 = conv.Convert("руддщ цщкдв");
    Check(c3?.Text == "hello world" && c3.To == Lang.En, "selection: 'руддщ цщкдв' → 'hello world'", $"{c3}");
}

// ---------------------------------------------------------------- autostart (opt-in: touches Task Scheduler)
if (args.Contains("--autostart"))
{
    Console.WriteLine("\nAutostart checks");
    var xmlInfo = Autostart.ParseTaskXml(Autostart.BuildTaskXml(@"C:\Apps\CheckWords.exe", elevated: true, @"PC\user"), enabled: true);
    Check(xmlInfo == new ScheduledTaskInfo(true, true, @"C:\Apps\CheckWords.exe"), "task XML round trip", $"{xmlInfo}");
    Check(Autostart.QueryTask("CheckWords.NoSuchTask") is null, "missing task → null");

    var real = Autostart.QueryTask();
    Console.WriteLine($"  real task 'CheckWords': {(real is null ? "none" : real.ToString())}");

    // A temporary non-elevated task: creating it needs no UAC, and Windows validates our XML.
    const string testTask = "CheckWords.SelfTest";
    var exe = Path.Combine(AppContext.BaseDirectory, "CheckWords.Tests.exe");
    Check(Autostart.CreateTask(exe, elevated: false, asAdmin: false, testTask), "schtasks accepts the generated XML");
    var created = Autostart.QueryTask(testTask);
    Check(created is { Enabled: true, Elevated: false } && Autostart.SamePath(created.Command, exe),
        "created task reads back (enabled, not elevated, same exe)", $"{created}");
    Check(Autostart.DeleteTask(asAdmin: false, testTask), "test task deleted");
    Check(Autostart.QueryTask(testTask) is null, "test task is gone");
}

// ---------------------------------------------------------------- accuracy on held-out text
var corpora = new Dictionary<Lang, string>
{
    [Lang.En] = """
        I was going to send you the report yesterday but the build failed again. Could you please check the logs
        and tell me what happened with the deployment? We should probably schedule a meeting tomorrow morning to
        discuss the new features and the remaining bugs. The weather is nice today, so maybe we can walk to the
        office together. My brother bought a new car last week and he is very happy about it. Please remember to
        update the documentation before the release. Thanks for your help, I really appreciate it. The database
        migration takes too long because the table contains millions of records. Let me know if anything changes.
        Something strange happened when I opened the configuration file: every value was replaced by default
        settings. Honestly, I think we need better monitoring and automated tests. She answered quickly, although
        the question was complicated. Children were playing outside while their parents prepared dinner.
        The quarterly budget review was postponed because several managers were travelling abroad.
        """,
    [Lang.Ru] = """
        Вчера я хотел отправить тебе отчёт, но сборка снова упала. Посмотри, пожалуйста, логи и скажи, что
        случилось с развёртыванием. Наверное, стоит назначить встречу завтра утром, чтобы обсудить новые функции
        и оставшиеся ошибки. Сегодня хорошая погода, поэтому можно прогуляться до офиса вместе. Мой брат купил
        новую машину на прошлой неделе и очень доволен. Не забудь обновить документацию перед выпуском. Спасибо за
        помощь, я очень это ценю. Миграция базы данных занимает слишком много времени, потому что таблица содержит
        миллионы записей. Сообщи мне, если что-нибудь изменится. Когда я открыл файл конфигурации, произошло
        что-то странное: все значения были заменены настройками по умолчанию. Честно говоря, нам нужен более
        качественный мониторинг и автоматические тесты. Она быстро ответила, хотя вопрос был сложным. Дети играли
        на улице, пока родители готовили ужин. Объявление висело на двери подъезда. Квартальный бюджет пересмотрели
        позже, потому что несколько руководителей уехали в командировку.
        """,
    [Lang.Uk] = """
        Вчора я хотів надіслати тобі звіт, але збірка знову впала. Подивись, будь ласка, журнали і скажи, що
        сталося з розгортанням. Мабуть, варто призначити зустріч завтра вранці, щоб обговорити нові функції та
        помилки, які залишилися. Сьогодні гарна погода, тому можна прогулятися до офісу разом. Мій брат купив нову
        машину минулого тижня і дуже задоволений. Не забудь оновити документацію перед випуском. Дякую за
        допомогу, я це дуже ціную. Міграція бази даних триває надто довго, бо таблиця містить мільйони записів.
        Повідом мені, якщо щось зміниться. Коли я відкрив файл конфігурації, сталося щось дивне: усі значення
        було замінено типовими налаштуваннями. Чесно кажучи, нам потрібен якісніший моніторинг і автоматичні
        тести. Вона швидко відповіла, хоча питання було складним. Діти гралися надворі, поки батьки готували
        вечерю. Оголошення висіло на дверях під'їзду. Ґанок біля хати був старий. Квартальний бюджет переглянули
        пізніше, бо кілька керівників поїхали у відрядження.
        """,
};

Console.WriteLine("\nAccuracy on held-out text (complete words; mid = warned before the word ended)");
Console.WriteLine($"  {"typed as",-10}{"meant",-7}{"words",6}{"caught",8}{"wrong",7}{"missed",8}{"mid",6}");
var missedSamples = new List<string>();
var fpSamples = new List<string>();
int totalFp = 0, totalFpMid = 0, totalNeg = 0;

foreach (var (meant, text) in corpora)
{
    var words = Regex.Matches(text, @"[\p{L}']+").Select(m => m.Value.Trim('\'')).Where(w => w.Length >= 2).ToList();

    // False positives: the right layout must stay silent, at the end and in the middle of the word.
    foreach (var w in words)
    {
        var keys = Type(w, meant);
        totalNeg++;
        var s = detector.Analyze(keys, meant, complete: true);
        if (s is not null) { totalFp++; fpSamples.Add($"{w} ({meant.Code()}) → {s.ToText} ({s.To.Code()})"); }
        for (int p = 3; p < keys.Count; p++)
        {
            var sm = detector.Analyze(keys.Take(p).ToList(), meant, complete: false);
            if (sm is null) continue;
            totalFpMid++;
            fpSamples.Add($"mid: {w[..p]}… ({meant.Code()}) → {sm.ToText} ({sm.To.Code()})");
            break;
        }
    }

    // True positives: the same words typed in every other layout.
    foreach (var typed in layouts.Langs.Where(l => l != meant))
    {
        int n = 0, caught = 0, wrong = 0, missed = 0, mid = 0;
        foreach (var w in words)
        {
            var keys = Type(w, meant);
            var typedText = detector.TextFor(keys, typed);
            if (typedText is null || typedText == w) continue; // identical in both layouts: nothing to fix
            n++;
            var s = detector.Analyze(keys, typed, complete: true);
            bool early = false;
            for (int p = 3; p < keys.Count && !early; p++)
                early = detector.Analyze(keys.Take(p).ToList(), typed, complete: false)?.To == meant;
            if (early) mid++;
            // Same resulting text from RU and UK counts as caught: only the tie-break layout differs.
            if (s?.To == meant || s?.ToText == w || early) caught++;
            else if (s is not null) { wrong++; missedSamples.Add($"WRONG {typedText} ({typed.Code()}) ← {w} ({meant.Code()}), suggested {s.ToText} ({s.To.Code()})"); }
            else
            {
                missed++;
                var curCore = LanguageModel.Normalize(LayoutDetector.Core(typedText, out _));
                var altCore = LanguageModel.Normalize(LayoutDetector.Core(w, out _));
                missedSamples.Add($"{typedText} ({typed.Code()}) ← {w} ({meant.Code()})  " +
                    $"cur {models[typed].Score(curCore, true):F2} alt {models[meant].Score(altCore, true):F2}");
            }
        }
        Console.WriteLine($"  {typed.Code(),-10}{meant.Code(),-7}{n,6}{caught,8}{wrong,7}{missed,8}{mid,6}   {(n == 0 ? 0 : 100.0 * caught / n),5:F1}%");
    }
}
Console.WriteLine($"\n  False warnings on correctly typed words: {totalFp} of {totalNeg} at word end, {totalFpMid} mid-word");

// Technical tokens that are not in the dictionary: typed correctly in EN, must stay silent.
var techTokens = """
    getUserById ToUnicodeEx localhost nginx PostgreSQL stdout stderr kwargs argv chmod chown sudo grep awk sed
    qwerty lorem ipsum OAuth JWT tsconfig vscode repo repos linter eslint prettier useEffect useState jsx tsx dbo
    utf ascii regexp md5 sha256 webhook webhooks cron crontab kubectl minikube terraform ansible jenkins grafana
    prometheus redis mongodb mysql sqlite dockerfile makefile cmake gcc clang llvm wasm rustc cargo crate crates
    pytest jest vitest mocha chai junit xunit nunit moq sln csproj nupkg winforms wpf xaml blazor razor signalr
    Kyiv Lviv Kharkiv Odesa Dnipro London Berlin Microsoft Google Amazon Netflix Spotify Telegram Viber Slack Zoom
    """;
int techFp = 0, techN = 0;
foreach (var w in Regex.Matches(techTokens, @"[\p{L}\d]+").Select(m => m.Value))
{
    if (w.Any(char.IsDigit)) continue; // digits end words anyway
    techN++;
    var keys = Type(w, Lang.En);
    var s = detector.Analyze(keys, Lang.En, complete: true);
    string? mid = null;
    for (int p = 3; p < keys.Count && mid is null; p++)
        mid = detector.Analyze(keys.Take(p).ToList(), Lang.En, complete: false)?.ToText;
    if (s is null && mid is null) continue;
    techFp++;
    fpSamples.Add($"tech: {w} → {s?.ToText ?? "mid " + mid}");
}
Console.WriteLine($"  False warnings on {techN} technical/proper-noun tokens (EN): {techFp}");

if (verbose || fpSamples.Count > 0)
{
    Console.WriteLine("\n  False warnings:");
    foreach (var f in fpSamples.Take(verbose ? 200 : 25)) Console.WriteLine("    " + f);
}
if (verbose)
{
    Console.WriteLine("\n  Missed:");
    foreach (var m in missedSamples) Console.WriteLine("    " + m);
}

Console.WriteLine(failures == 0 ? "\nAll unit checks passed." : $"\n{failures} unit check(s) failed.");
return failures == 0 ? 0 : 1;
