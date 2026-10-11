using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DriveFlow.Core;
using DriveFlow.Core.Storage;

namespace DriveFlow.App;

/// <summary>Ícone PNG transparente pintado com a cor do tema, como o themed_icon da versão Python.</summary>
public sealed class TintedIcon : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(TintedIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(TintedIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source is null) return;
        var rect = new Rect(RenderSize);
        dc.PushOpacityMask(new ImageBrush(Source) { Stretch = Stretch.Uniform });
        dc.DrawRectangle(Fill, null, rect);
        dc.Pop();
    }

    public static ImageSource Asset(string name) =>
        new BitmapImage(new Uri($"pack://application:,,,/DriveFlow;component/Assets/{name}"));
}

/// <summary>
/// Barra do padrão Nexotool: trilho arredondado, azul→ciano enviando, verde-água concluída e o %
/// centralizado (branco sobre o preenchimento). Anima só até os bytes confirmados pelo Drive.
/// </summary>
public sealed class SmoothBar : FrameworkElement
{
    static DependencyProperty Prop(string name, Type type, object value) =>
        DependencyProperty.Register(name, type, typeof(SmoothBar), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = Register("Track", Color.FromRgb(0xE2, 0xE8, 0xF0));
    public static readonly DependencyProperty StartProperty = Register("Start", Color.FromRgb(0x25, 0x63, 0xEB));
    public static readonly DependencyProperty EndProperty = Register("End", Color.FromRgb(0x22, 0xD3, 0xEE));
    public static readonly DependencyProperty TextColorProperty = Register("TextColor", Color.FromRgb(0x0F, 0x17, 0x2A));
    public static readonly DependencyProperty DoneStartProperty = Register("DoneStart", Color.FromRgb(0x0D, 0x94, 0x88));
    public static readonly DependencyProperty DoneEndProperty = Register("DoneEnd", Color.FromRgb(0x2D, 0xD4, 0xBF));
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register("Display", typeof(double), typeof(SmoothBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(SmoothBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SmoothBar),
        new FrameworkPropertyMetadata(0.0, (d, e) => ((SmoothBar)d).Changed((double)e.OldValue, (double)e.NewValue)));
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(nameof(Animate), typeof(bool), typeof(SmoothBar),
        new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IdentityProperty = DependencyProperty.Register(nameof(Identity), typeof(string), typeof(SmoothBar),
        new FrameworkPropertyMetadata("", (d, _) => ((SmoothBar)d).Jump()));

    static DependencyProperty Register(string name, Color value) => Prop(name, typeof(Color), value);

    public SmoothBar()
    {
        Height = 20;
        SetResourceReference(TrackProperty, "DisabledFillColor");
        SetResourceReference(StartProperty, "AccentColor");
        SetResourceReference(EndProperty, "CyanColor");
        SetResourceReference(TextColorProperty, "TextColor");
        SetResourceReference(DoneStartProperty, "DoneStartColor");
        SetResourceReference(DoneEndProperty, "DoneEndColor");
    }

    /// <summary>Fração confirmada, de 0 a 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Animate { get => (bool)GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    /// <summary>Outro upload na mesma linha: pula direto, sem animar.</summary>
    public string Identity { get => (string)GetValue(IdentityProperty); set => SetValue(IdentityProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    void Jump()
    {
        BeginAnimation(DisplayProperty, null);
        SetValue(DisplayProperty, Math.Clamp(Value, 0, 1));
    }

    void Changed(double old, double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (!Animate || value < old)
        {
            Jump();
            return;
        }
        var from = (double)GetValue(DisplayProperty);
        BeginAnimation(DisplayProperty, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (rect.Width <= 0) return;
        var radius = rect.Height / 2;
        var track = new RectangleGeometry(rect, radius, radius);
        dc.DrawGeometry(new SolidColorBrush((Color)GetValue(TrackProperty)), null, track);
        var fraction = (double)GetValue(DisplayProperty);
        var fill = new Rect(0, 0, rect.Width * fraction, rect.Height);
        var done = fraction >= 1;
        if (fill.Width > 0)
        {
            var start = (Color)GetValue(done ? DoneStartProperty : StartProperty);
            var end = (Color)GetValue(done ? DoneEndProperty : EndProperty);
            dc.PushClip(track);
            dc.DrawRectangle(new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox }, null, fill);
            dc.Pop();
        }
        var percent = fraction < .995 || done ? $"{fraction * 100:0}%" : "99%";
        var text = $"{Caption} {percent}".Trim();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(Color color) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11, new SolidColorBrush(color), dpi);
        var dark = Label((Color)GetValue(TextColorProperty));
        var origin = new Point((rect.Width - dark.Width) / 2, (rect.Height - dark.Height) / 2);
        dc.DrawText(dark, origin);
        // Parte do texto sobre o preenchimento em branco.
        dc.PushClip(new RectangleGeometry(fill));
        dc.DrawText(Label(Colors.White), origin);
        dc.Pop();
    }
}

/// <summary>Gráfico de velocidade de um upload nos últimos 2 minutos (charts.py).</summary>
public sealed class UploadPlot : FrameworkElement
{
    readonly Queue<(double Time, double Speed)> samples = new();
    UploadItem? item;

    public UploadPlot()
    {
        MinWidth = 290;
        Height = 260;
        Margin = new Thickness(0, 0, 12, 12);
    }

    public void Sample(double stamp, UploadItem upload)
    {
        item = upload;
        samples.Enqueue((stamp, upload.Speed / 1048576));
        while (samples.Count > 241) samples.Dequeue();
        InvalidateVisual();
    }

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        if (item is null || samples.Count == 0) return;
        var (border, accent, muted, text) = (Res("Border"), Res("Accent"), Res("Subtle"), Res("Text"));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FormattedText Text(string value, Brush brush, bool bold = false, double size = 13) => new(value, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
        var (width, height) = (ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Res("Surface"), new Pen(border, 1), new Rect(0.5, 0.5, width - 1, height - 1), 4, 4);
        var title = Text(item.Name, text, bold: true);
        title.MaxTextWidth = Math.Max(10, width - 32);
        title.MaxLineCount = 1;
        title.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(title, new Point(16, 10));
        var status = item.Status.Length > 0 ? char.ToUpper(item.Status[0]) + item.Status[1..] : "";
        dc.DrawText(Text($"{Format.Size(item.Speed)}/s   ·   {status}", accent), new Point(16, 33));
        var eta = item.Speed > 0 ? Format.Duration((item.Size - item.Offset) / item.Speed) : "—";
        dc.DrawText(Text($"Decorrido {Format.Duration(item.Elapsed)}   ·   Restante {eta}", muted), new Point(16, 55));
        var plot = new Rect(62, 103, Math.Max(1, width - 82), Math.Max(1, height - 147));
        var now = samples.Last().Time;
        var visible = samples.Where(s => s.Time >= now - 120).ToList();
        var peak = Math.Max(0.1, visible.Max(s => s.Speed));
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var ceiling = Math.Ceiling(peak / magnitude) * magnitude;
        dc.DrawText(Text("MiB/s", muted), new Point(16, 78));
        var dotted = new Pen(border, 1) { DashStyle = DashStyles.Dot };
        for (var i = 0; i < 5; i++)
        {
            var y = plot.Bottom - i * plot.Height / 4;
            dc.DrawLine(dotted, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text((ceiling * i / 4).ToString("G2", CultureInfo.InvariantCulture), muted);
            dc.DrawText(label, new Point(55 - label.Width, y - label.Height / 2));
        }
        int[] seconds = [120, 90, 60, 30, 0];
        for (var i = 0; i < seconds.Length; i++)
        {
            var x = plot.Left + i * plot.Width / 4;
            var label = Text(seconds[i] > 0 ? $"-{seconds[i]}s" : "agora", muted);
            dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 9));
        }
        if (visible.Count == 0) return;
        Point At((double Time, double Speed) s) => new(plot.Right - (now - s.Time) * plot.Width / 120, plot.Bottom - s.Speed / ceiling * plot.Height);
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(visible[0]), false, false);
            foreach (var s in visible.Skip(1)) ctx.LineTo(At(s), true, true);
        }
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(At(visible[0]), true, true);
            foreach (var s in visible.Skip(1)) ctx.LineTo(At(s), true, false);
            ctx.LineTo(new Point(plot.Right, plot.Bottom), true, false);
            ctx.LineTo(new Point(plot.Right - (now - visible[0].Time) * plot.Width / 120, plot.Bottom), true, false);
        }
        var color = (accent as SolidColorBrush)?.Color ?? Colors.RoyalBlue;
        var gradient = new LinearGradientBrush(Color.FromArgb(65, color.R, color.G, color.B), Color.FromArgb(4, color.R, color.G, color.B),
            new Point(0, 0), new Point(0, 1)) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 2)));
        dc.DrawGeometry(gradient, null, area);
        dc.DrawGeometry(null, new Pen(accent, 2), line);
        dc.Pop();
    }
}

/// <summary>Ícones do Windows para arquivos, pastas e unidades, como o QFileIconProvider.</summary>
public static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    const uint Icon = 0x100, Small = 0x1, UseAttributes = 0x10, DirectoryAttribute = 0x10, NormalAttribute = 0x80;
    static readonly Dictionary<string, ImageSource?> cache = [];

    public static ImageSource? Folder() => Get("<folder>", "folder", DirectoryAttribute, UseAttributes);

    /// <summary>Pelo nome (extensão); não precisa existir no disco.</summary>
    public static ImageSource? ForName(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        // Executáveis e atalhos têm ícone próprio; o genérico basta na lista.
        return Get("ext:" + extension, "file" + extension, NormalAttribute, UseAttributes);
    }

    /// <summary>Ícone real do caminho (unidades, programas como o WinRAR).</summary>
    public static ImageSource? ForPath(string path) => Get("path:" + path.ToLowerInvariant(), path, 0, 0);

    static ImageSource? Get(string key, string path, uint attributes, uint flags)
    {
        if (cache.TryGetValue(key, out var found)) return found;
        ImageSource? image = null;
        try
        {
            var info = new ShFileInfo();
            if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), Icon | Small | flags) != IntPtr.Zero && info.Icon != IntPtr.Zero)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                DestroyIcon(info.Icon);
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
        }
        return cache[key] = image;
    }
}
