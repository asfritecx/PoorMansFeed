using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GraphiteUi;

/// <summary>
/// Draws a 24-unit stroke icon (Lucide geometry) scaled to the control size in the inherited foreground,
/// so icons inside buttons follow the button's normal and disabled text colours.
/// </summary>
public sealed class LineIcon : Control
{
    private const double ViewBoxSize = 24;
    private const double StrokeThickness = 2;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<LineIcon>(16);
        HeightProperty.OverrideDefaultValue<LineIcon>(16);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width, Bounds.Height) / ViewBoxSize;
        if (Data is not { } data || Foreground is not { } brush || scale <= 0)
        {
            return;
        }

        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        double offsetX = (Bounds.Width - ViewBoxSize * scale) / 2;
        double offsetY = (Bounds.Height - ViewBoxSize * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(null, pen, data);
        }
    }
}
