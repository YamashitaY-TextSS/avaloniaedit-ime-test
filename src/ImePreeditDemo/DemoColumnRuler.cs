// Column ruler drawn above the editor: tick marks, the caret column marker and its number.
// ==========================================================================================
// Design notes (why it is built this way):
//   * AvaloniaEdit has no "top margin" concept (margins are left-only, and ColumnRulerRenderer only draws
//     a vertical line at a fixed column). So this is a plain Control placed outside the TextView that
//     draws itself in the TextView's coordinate system.
//   1. The origin comes from translating the TextView's top-left corner into this control's coordinates.
//      That way it follows the line-number margin appearing/disappearing and changing width automatically -
//      never add up margin widths by hand, that is where drift comes from.
//   2. One column is TextView.WideSpaceWidth (the measured width of 'x'), so it follows font family and
//      font size changes for free.
//   3. Horizontal scrolling subtracts TextView.HorizontalOffset, exactly like the text does.
//   4. The caret marker uses the measured X from Caret.CalculateCaretRectangle(). It is NOT computed as
//      column * character width, so it stays aligned with the text even with proportional fonts, tabs and
//      wrapped lines.
//
// Note on what "column" means here:
//   Measured on Windows with Consolas: the displayed column and the character/byte count do NOT agree.
//   Consolas has no Japanese glyphs, so CJK is drawn with a fallback font whose advance width is not
//   exactly two half-width cells (measured: a long Japanese line put the caret at displayed column 164
//   while the half-width equivalent was 175).
//   The number on this ruler is therefore defined as the DISPLAYED column - the grid you can see - so the
//   ticks, the marker and the number always agree with each other. "How many characters / bytes from the
//   start of the line" is a logical value and belongs on a status line instead.
// ==========================================================================================
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace ImePreeditDemo;

/// <summary>
/// Column ruler placed above an AvaloniaEdit editor: ticks, caret column marker and the caret column number.
/// </summary>
public sealed class DemoColumnRuler : Control
{
    private readonly TextEditor _editor;
    private readonly TextView _textView;

    // LayoutUpdated fires on every layout pass, so only redraw when one of the three values that actually
    //   change the output has moved. Calling InvalidateVisual unconditionally repaints continuously.
    private double _lastOriginX = double.NaN;
    private double _lastCharWidth = double.NaN;
    private double _lastHorizontalOffset = double.NaN;

    public DemoColumnRuler(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _textView = editor.TextArea.TextView;

        _textView.ScrollOffsetChanged += OnNeedRedraw;
        _textView.VisualLinesChanged += OnNeedRedraw;
        _editor.TextArea.Caret.PositionChanged += OnNeedRedraw;
        _editor.PropertyChanged += OnEditorPropertyChanged;
        _textView.LayoutUpdated += OnTextViewLayoutUpdated;
    }

    /// <summary>Label size, derived from the editor font size and clamped at both ends.</summary>
    private double LabelFontSize => Math.Clamp(_editor.FontSize - 3, 8, 13);

    /// <summary>
    /// Displayed column of the caret, 0-based. Computed as measured X / column width, so it always agrees
    /// with the ticks on this ruler. This is not the character or byte offset - see the note at the top of
    /// this file. Returns -1 while no visual line is built yet.
    /// </summary>
    public int CurrentVisualColumn
    {
        get
        {
            double cw = _textView.WideSpaceWidth;
            var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
            if (cw < 1 || caretRect == default) return -1;
            // 0-based: the start of a line is column 0, matching how hex addresses are counted.
            return (int)Math.Round(caretRect.X / cw);
        }
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Height = label + ticks + the baseline rule
        return new Size(availableSize.Width, Math.Ceiling(LabelFontSize + 9));
    }

    private void OnNeedRedraw(object? sender, EventArgs e) => InvalidateVisual();

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextEditor.FontSizeProperty || e.Property == TextEditor.FontFamilyProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (e.Property == TextEditor.ShowLineNumbersProperty || e.Property == TextEditor.WordWrapProperty)
        {
            InvalidateVisual();   // the origin moves when the line-number margin appears or disappears
        }
    }

    private void OnTextViewLayoutUpdated(object? sender, EventArgs e)
    {
        var origin = _textView.TranslatePoint(new Point(0, 0), this);
        double originX = origin?.X ?? double.NaN;
        if (Same(originX, _lastOriginX)
            && Same(_textView.WideSpaceWidth, _lastCharWidth)
            && Same(_textView.HorizontalOffset, _lastHorizontalOffset))
        {
            return;
        }
        InvalidateVisual();
    }

    private static bool Same(double a, double b)
        => (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) < 0.01;

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool dark = IsDark;
        var bgBrush = new ImmutableSolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF2, 0xF2, 0xF2));
        var tickBrush = new ImmutableSolidColorBrush(dark ? Color.FromRgb(0x9A, 0x9A, 0x9A) : Color.FromRgb(0x78, 0x78, 0x78));
        var labelBrush = new ImmutableSolidColorBrush(dark ? Color.FromRgb(0xD0, 0xD0, 0xD0) : Color.FromRgb(0x40, 0x40, 0x40));
        var accentBrush = new ImmutableSolidColorBrush(dark ? Color.FromRgb(0xFF, 0xB3, 0x00) : Color.FromRgb(0xC6, 0x28, 0x28));
        var tickPen = new Pen(tickBrush, 1);

        ctx.FillRectangle(bgBrush, bounds);
        double h = bounds.Height;
        ctx.DrawLine(tickPen, new Point(0, h - 0.5), new Point(bounds.Width, h - 0.5));

        double cw = _textView.WideSpaceWidth;
        var originPoint = _textView.TranslatePoint(new Point(0, 0), this);
        if (cw < 1 || originPoint is null) return;

        double x0 = originPoint.Value.X - _textView.HorizontalOffset;   // screen X of document column 0
        _lastOriginX = originPoint.Value.X;
        _lastCharWidth = cw;
        _lastHorizontalOffset = _textView.HorizontalOffset;

        var typeface = new Typeface(_editor.FontFamily);
        double labelSize = LabelFontSize;

        // Build the caret label first and remember the area it occupies. Drawing it after the tick labels
        //   lets e.g. "32" land on top of "30" and neither stays readable.
        var caret = BuildCaretLabel(bounds, x0, typeface, labelSize, accentBrush);
        Rect? caretLabelArea = caret is null ? null : caret.Value.LabelRect.Inflate(new Thickness(2, 0));

        // Stop the ticks at the right edge of the text area, not under the vertical scroll bar.
        double rightLimit = Math.Min(bounds.Width, originPoint.Value.X + _textView.Bounds.Width);

        int firstCol = Math.Max(0, (int)Math.Floor((0 - x0) / cw));
        int lastCol = firstCol + (int)Math.Ceiling(bounds.Width / cw) + 2;
        const int MaxTicks = 5000;   // guard: keeps the work bounded for a tiny font in a huge window

        for (int col = firstCol; col <= lastCol && col - firstCol < MaxTicks; col++)
        {
            double x = x0 + col * cw;
            if (x > rightLimit) break;
            if (x < -cw) continue;

            // 0-based, so a "0" is always drawn at the start of the line.
            bool isTen = col % 10 == 0;
            bool isFive = col % 5 == 0;
            if (!isFive && cw < 5) continue;        // when columns get too narrow, only draw every 5th tick

            double tickH = isTen ? h * 0.55 : isFive ? h * 0.38 : h * 0.22;
            double tx = Math.Round(x) + 0.5;
            ctx.DrawLine(tickPen, new Point(tx, h - tickH - 1), new Point(tx, h - 1));

            if (isTen && cw >= 3)
            {
                var ft = new FormattedText(col.ToString(CultureInfo.InvariantCulture),
                                           CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                           typeface, labelSize, labelBrush);
                double lx = x + cw / 2 - ft.Width / 2;   // centred over that column
                var area = new Rect(lx, 0, ft.Width, ft.Height);
                if (caretLabelArea?.Intersects(area) == true) continue;   // the caret label wins
                if (lx > -ft.Width && lx < bounds.Width) ctx.DrawText(ft, new Point(lx, 0));
            }
        }

        if (caret is not null) DrawCaret(ctx, bounds, x0, cw, caret.Value, accentBrush);
    }

    /// <summary>Build the caret label (displayed column) and the area it occupies. X comes from the caret rectangle.</summary>
    private CaretLabel? BuildCaretLabel(Rect bounds, double x0, Typeface typeface, double labelSize, IBrush accent)
    {
        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        if (caretRect == default) return null;   // no visual line yet

        // x0 is the screen X of document column 0 (origin minus horizontal scroll offset). caretRect.X is in
        //   document coordinates, so adding the two gives the screen X - the same formula the text uses.
        double cx = x0 + caretRect.X;
        if (cx < -4 || cx > bounds.Width + 4) return null;

        int column = CurrentVisualColumn;
        if (column < 0) return null;   // 0 is a valid column; only a negative value means "unavailable"

        var ft = new FormattedText(column.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
                                   FlowDirection.LeftToRight, typeface, labelSize, accent);
        double lx = Math.Clamp(cx - ft.Width / 2, 0, Math.Max(0, bounds.Width - ft.Width));
        return new CaretLabel(cx, caretRect.X, ft, new Rect(lx, 0, ft.Width, ft.Height));
    }

    /// <summary>Caret marker: a faint fill over the current column, a triangle, and the column number.</summary>
    private static void DrawCaret(DrawingContext ctx, Rect bounds, double x0, double cw, CaretLabel caret, ISolidColorBrush accent)
    {
        double h = bounds.Height;

        // Tint the current column cell; with a monospaced font this covers exactly one character.
        double cellLeft = x0 + Math.Round(caret.DocumentX / cw) * cw;
        ctx.FillRectangle(new ImmutableSolidColorBrush(accent.Color, 0.18), new Rect(cellLeft, 0, cw, h - 1));

        // Triangle pointing down at the text
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(caret.ScreenX, h - 1), true);
            g.LineTo(new Point(caret.ScreenX - 4, h - 7));
            g.LineTo(new Point(caret.ScreenX + 4, h - 7));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(accent, null, geo);

        ctx.DrawText(caret.Text, new Point(caret.LabelRect.X, 0));
    }

    private readonly record struct CaretLabel(double ScreenX, double DocumentX, FormattedText Text, Rect LabelRect);
}
