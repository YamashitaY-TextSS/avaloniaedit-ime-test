// Current-line marker: a small triangle drawn immediately to the left of the text.
// ==========================================================================================
// Purpose : make the current LINE as easy to spot as the current COLUMN.
//           The column ruler at the top marks the caret column with a triangle; this control is its
//           counterpart for the row. The line itself is tinted by AvaloniaEdit's built-in
//           HighlightCurrentLine (enabled in MainWindow).
//
// Design notes (why it is built this way):
//   * A left margin is just a Control added to TextArea.LeftMargins; the fork's TextArea.xaml lays them
//     out with an ItemsControl.
//   * It is appended at the END of that list, so it is not affected when the line-number margin is
//     toggled (TextEditor.OnShowLineNumbersChanged only inserts/removes the first two entries), and it
//     ends up directly to the left of the text.
//   * The Y position comes from Caret.CalculateCaretRectangle() and is translated into this control's
//     coordinates - the same approach the column ruler uses for X. That keeps it correct when the line
//     height changes or the line is wrapped.
//   * The colour follows the theme and matches the ruler accent, so the row and column markers read as
//     a pair.
// ==========================================================================================
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace ImePreeditDemo;

/// <summary>Left margin that draws a triangle next to the current line, pairing with the column ruler.</summary>
public sealed class DemoCurrentRowMarker : Control
{
    private const double MarkerWidth = 13;

    private readonly TextEditor _editor;
    private readonly TextView _textView;
    private double _lastY = double.NaN;

    public DemoCurrentRowMarker(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _textView = editor.TextArea.TextView;

        _textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        _textView.VisualLinesChanged += (_, _) => InvalidateVisual();
        _editor.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
        _textView.LayoutUpdated += OnLayoutUpdated;
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    protected override Size MeasureOverride(Size availableSize) => new(MarkerWidth, 0);

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        double y = CurrentRowY();
        if (Math.Abs(y - _lastY) < 0.01) return;
        InvalidateVisual();
    }

    /// <summary>Instrumentation for --diag-ruler: top Y and height of the current row.</summary>
    public (double Y, double Height) DiagCurrentRow()
    {
        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        double h = caretRect.Height > 1 ? caretRect.Height : _textView.DefaultLineHeight;
        return (CurrentRowY(), h);
    }

    /// <summary>Top Y of the current row in this control's coordinate system, or NaN when unavailable.</summary>
    private double CurrentRowY()
    {
        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        if (caretRect == default) return double.NaN;
        var origin = _textView.TranslatePoint(new Point(0, 0), this);
        if (origin is null) return double.NaN;
        return origin.Value.Y - _textView.VerticalOffset + caretRect.Y;
    }

    public override void Render(DrawingContext ctx)
    {
        double y = CurrentRowY();
        _lastY = y;
        if (double.IsNaN(y)) return;

        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        double h = caretRect.Height > 1 ? caretRect.Height : _textView.DefaultLineHeight;
        if (y + h < 0 || y > Bounds.Height) return;   // off screen

        var accent = new ImmutableSolidColorBrush(IsDark ? Color.FromRgb(0xFF, 0xB3, 0x00) : Color.FromRgb(0xC6, 0x28, 0x28));

        // Triangle pointing at the text, same size and colour as the marker on the column ruler.
        double cy = y + h / 2;
        double right = Bounds.Width - 2;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(right, cy), true);
            g.LineTo(new Point(right - 6, cy - 4));
            g.LineTo(new Point(right - 6, cy + 4));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(accent, null, geo);
    }
}
