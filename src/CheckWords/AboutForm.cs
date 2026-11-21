using System.Diagnostics;
using System.Reflection;

namespace CheckWords;

/// <summary>The "About" window: version and links to the GitHub repository and its issues.</summary>
internal sealed class AboutForm : Form
{
    public const string RepoUrl = "https://github.com/yradov/CheckWords";
    public const string IssuesUrl = RepoUrl + "/issues";

    /// <summary>Version from the project file, without the "+commit" suffix the SDK appends.</summary>
    public static string Version =>
        (typeof(AboutForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "?").Split('+')[0];

    public AboutForm(Icon icon)
    {
        Text = "О программе CheckWords";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var picture = new PictureBox
        {
            Image = new Icon(icon, 32, 32).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(32, 32),
            Margin = new Padding(0, 4, 12, 0),
        };
        layout.Controls.Add(picture, 0, 0);
        layout.SetRowSpan(picture, 5);

        layout.Controls.Add(new Label
        {
            Text = "CheckWords",
            Font = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        }, 1, 0);
        layout.Controls.Add(Caption("Версия " + Version, new Padding(0, 0, 0, 10)), 1, 1);
        layout.Controls.Add(Caption(
            "Пищит, если слово набрано не в той раскладке (EN / RU / UK),\n" +
            "и перепечатывает его в нужной раскладке по хоткею.",
            new Padding(0, 0, 0, 10)), 1, 2);
        layout.Controls.Add(Link("github.com/yradov/CheckWords", RepoUrl), 1, 3);
        layout.Controls.Add(Link("Сообщить о проблеме", IssuesUrl), 1, 4);

        var close = new Button
        {
            Text = "Закрыть",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 14, 0, 0),
        };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close, 0, 5);
        layout.SetColumnSpan(close, 2);

        Controls.Add(layout);
        AcceptButton = CancelButton = close;
        ActiveControl = close; // not the first link, which would get a focus frame
    }

    static Label Caption(string text, Padding margin) => new() { Text = text, AutoSize = true, Margin = margin };

    static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        link.LinkClicked += (_, _) => Open(url);
        return link;
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось открыть ссылку:\n" + url + "\n\n" + e.Message, "CheckWords",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
