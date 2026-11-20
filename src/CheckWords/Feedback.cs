using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Media;
using CheckWords.Core;

namespace CheckWords;

/// <summary>A short sine "blip" generated in memory (no sound files needed).</summary>
internal sealed class Beeper : IDisposable
{
    readonly MemoryStream _wav;
    readonly SoundPlayer _player;

    public Beeper(int frequency, int durationMs)
    {
        _wav = MakeWav(Math.Clamp(frequency, 200, 4000), Math.Clamp(durationMs, 20, 1000));
        _player = new SoundPlayer(_wav);
        _player.Load();
    }

    public void Play()
    {
        try { _player.Play(); } catch { /* no audio device */ }
    }

    static MemoryStream MakeWav(int frequency, int durationMs)
    {
        const int rate = 44100;
        int samples = rate * durationMs / 1000;
        int fade = rate * 8 / 1000;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double env = Math.Min(1.0, Math.Min(i, samples - i) / (double)fade);
            w.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * env * 9000));
        }
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _player.Dispose();
        _wav.Dispose();
    }
}

/// <summary>Tray icons drawn at runtime.</summary>
internal sealed class Icons : IDisposable
{
    public Icon Normal { get; } = Make(Color.FromArgb(37, 99, 235), "Aa");
    public Icon Warning { get; } = Make(Color.FromArgb(220, 38, 38), "!");
    public Icon Disabled { get; } = Make(Color.FromArgb(130, 130, 130), "Aa");

    static Icon Make(Color background, string text)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(size - 2 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(size - 2 - r * 2, size - 2 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, size - 2 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(background);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    public void Dispose()
    {
        Normal.Dispose();
        Warning.Dispose();
        Disabled.Dispose();
    }
}
