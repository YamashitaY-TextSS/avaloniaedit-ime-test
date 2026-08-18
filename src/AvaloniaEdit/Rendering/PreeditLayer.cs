// ============================================================================================
// This file does not exist in upstream AvaloniaEdit. It is added by the unmerged PR #592
// (https://github.com/AvaloniaUI/AvaloniaEdit/pull/592) by @Timskt.
//
// Two things in this file are TEXTSS-ADD, i.e. added on top of PR #592 while verifying it with
// Japanese IME (search for "TEXTSS-ADD"):
//   1. wrap the preedit onto the following line when it runs past the right edge
//   2. align the preedit to the text baseline of the caret line
// ============================================================================================
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Utils;

namespace AvaloniaEdit.Rendering
{
    /// <summary>
    /// Renders IME preedit (composition) text at the caret position.
    /// The text is rendered with an underline to indicate it is being composed.
    /// </summary>
    internal sealed class PreeditLayer : Layer
    {
        /// <summary>
        /// TEXTSS-ADD: upper bound on wrapped lines, purely as a runaway guard. A preedit is normally
        /// a few dozen characters at most, so this is never reached in practice.
        /// </summary>
        private const int MaxWrappedLines = 64;

        /// <summary>TEXTSS-ADD: below this width (px) nothing more fits on the line, so move to the next one.</summary>
        private const double MinimumUsableWidth = 1.0;

        private readonly TextArea _textArea;
        private string _preeditText;
        private Rect _caretRect;
        private IBrush _foreground;
        private int? _cursorOffset;

        public PreeditLayer(TextArea textArea) : base(textArea.TextView, KnownLayer.Caret)
        {
            _textArea = textArea;
            IsHitTestVisible = false;
        }

        /// <summary>TEXTSS-ADD: emit measured values to the diagnostic sink once, on the next render (demo --diag only).</summary>
        private bool _diagPending;

        public void SetPreedit(string text, Rect caretRect, IBrush foreground, int? cursorOffset = null)
        {
            _preeditText = text;
            _caretRect = caretRect;
            _foreground = foreground;
            _cursorOffset = cursorOffset;
            _diagPending = true;
            InvalidateVisual();
        }

        public void Clear()
        {
            if (_preeditText == null)
                return;
            _preeditText = null;
            _cursorOffset = null;
            InvalidateVisual();
        }

        public override void Render(DrawingContext drawingContext)
        {
            base.Render(drawingContext);

            if (string.IsNullOrEmpty(_preeditText))
                return;

            var textView = TextView;
            if (textView?.Document == null)
                return;

            // Position preedit text at the right edge of the caret, adjusted for scroll offset
            var x = _caretRect.Right - textView.HorizontalOffset;
            var y = _caretRect.Y - textView.VerticalOffset;

            // TEXTSS-ADD: align the preedit to the baseline of the caret line.
            //   Problem : the composing text was drawn a few pixels above the committed text and dropped
            //             into place the moment it was committed (measured: 3 px).
            //   Cause   : the origin used here was the TOP of the line (_caretRect.Y = VisualYPosition.LineTop),
            //             while body text is drawn from the baseline inside the line
            //             (VisualLine.GetTextLineVisualYPosition with VisualYPosition.Baseline). As soon as a
            //             line mixes scripts and picks up a fallback typeface, the line height and baseline
            //             change and the two no longer agree.
            //   Fix     : take the baseline of the caret line and place the preedit at
            //             (baseline - baseline of its own layout).
            //   Note    : if the baseline cannot be obtained (no VisualLine yet), fall back to the top of the
            //             line. Drawing slightly off is better than not drawing at all.
            var baselineY = TryGetCaretLineBaselineY(textView);

            var typeface = textView.CreateTypeface();
            var fontRenderingEmSize = textView.GetValue(TemplatedControl.FontSizeProperty);
            var foreground = _foreground
                ?? textView.GetValue(TemplatedControl.ForegroundProperty) as IBrush
                ?? Brushes.White;

            // TEXTSS-ADD: when the composing text runs past the right edge of the text area, wrap it onto
            // the following line instead of letting it be clipped.
            //   Problem : everything past the right edge was clipped, so the user could not see what they
            //             were typing.
            //   Cause   : the preedit is never inserted into the document, so it gets none of the automatic
            //             wrapping or horizontal scrolling that body text gets. It was drawn as a single
            //             TextWrapping.NoWrap line starting at the caret.
            //   Fix     : draw it in chunks with a varying width: the first line runs from the caret to the
            //             right edge, every following line runs from x = 0 to the right edge.
            //   Note    : a wrapped line always overlaps the body text of the line below it, so the
            //             background is filled before the glyphs are drawn. Without that fill the two texts
            //             overlap and neither is readable.
            //   Note    : nothing is pushed down; the document is not modified at all. Once the text is
            //             committed it becomes body text and is drawn normally.
            var viewportWidth = Bounds.Width;
            var lineHeight = textView.DefaultLineHeight;
            var background = FindEditorBackground();
            var underlinePen = new ImmutablePen(foreground.ToImmutable(), 1);
            var cursorPen = new ImmutablePen(foreground.ToImmutable(), 2);

            // Draw cursor within preedit text: use cursorOffset if specified, otherwise at end
            var effectiveCursorOffset = Math.Max(0, Math.Min(_cursorOffset ?? _preeditText.Length, _preeditText.Length));

            var diag = _diagPending;
            _diagPending = false;
            if (diag)
            {
                TextArea.ImeCommitDiagSink?.Invoke(
                    $"preedit-render: bounds={Bounds.Width:F1} x={x:F1} caretRight={_caretRect.Right:F1} " +
                    $"hOff={textView.HorizontalOffset:F1} avail={(viewportWidth - x):F1} len={_preeditText.Length} bg={(background?.ToString() ?? "(null)")} " +
                    $"lineTopY={y:F1} baselineY={(baselineY?.ToString("F1") ?? "(null)")}");
            }

            var chunkStart = 0;
            var originX = x;
            var originY = y;                 // top of the current line (origin when no baseline is available)
            var originBaselineY = baselineY; // baseline of the current line (preferred origin)
            var wrappedLines = 0;

            while (chunkStart < _preeditText.Length && wrappedLines <= MaxWrappedLines)
            {
                var available = viewportWidth - originX;
                var rest = _preeditText.Substring(chunkStart);

                int take;
                if (available < MinimumUsableWidth)
                {
                    take = 0;
                }
                else
                {
                    // Let Avalonia's text formatter decide how much fits: CJK breaks per character,
                    // Latin text breaks per word.
                    var measured = new TextLayout(
                        rest,
                        typeface,
                        fontRenderingEmSize,
                        foreground,
                        textWrapping: TextWrapping.Wrap,
                        maxWidth: available);
                    take = measured.TextLines.Count > 0 ? measured.TextLines[0].Length : rest.Length;

                    if (diag)
                    {
                        diag = false;
                        var probe = new TextLayout(rest, typeface, fontRenderingEmSize, foreground, textWrapping: TextWrapping.NoWrap);
                        TextArea.ImeCommitDiagSink?.Invoke(
                            $"preedit-wrap: avail={available:F1} rest={rest.Length} wrapTake={take} " +
                            $"lines={measured.TextLines.Count} noWrapWidth={probe.WidthIncludingTrailingWhitespace:F1}");
                    }
                }

                if (take <= 0)
                {
                    // Not even one character fits from the start of a line: the viewport is extremely narrow.
                    // Stop here rather than loop forever.
                    if (originX <= 0)
                        break;

                    originX = 0;
                    originY += lineHeight;
                    if (originBaselineY.HasValue) originBaselineY += lineHeight;
                    wrappedLines++;
                    continue;
                }

                var chunk = _preeditText.Substring(chunkStart, take);
                var chunkLayout = new TextLayout(
                    chunk,
                    typeface,
                    fontRenderingEmSize,
                    foreground,
                    textWrapping: TextWrapping.NoWrap);

                // TEXTSS-ADD: draw from the body baseline when it is available, otherwise from the line top.
                var origin = new Point(originX,
                    originBaselineY.HasValue ? originBaselineY.Value - chunkLayout.Baseline : originY);
                var width = chunkLayout.WidthIncludingTrailingWhitespace;
                var height = chunkLayout.Height;

                if (background != null)
                    drawingContext.FillRectangle(background, new Rect(origin.X, origin.Y, width, height));

                chunkLayout.Draw(drawingContext, origin);

                // Draw underline to indicate composition text
                drawingContext.DrawLine(underlinePen,
                    new Point(origin.X, origin.Y + height - 1),
                    new Point(origin.X + width, origin.Y + height - 1));

                var chunkEnd = chunkStart + take;
                var isLastChunk = chunkEnd >= _preeditText.Length;

                // Use a strict comparison for every chunk but the last one, so a cursor sitting exactly on a
                // chunk boundary is not drawn twice (once at the end of one line, once at the start of the next).
                if (effectiveCursorOffset >= chunkStart &&
                    (isLastChunk ? effectiveCursorOffset <= chunkEnd : effectiveCursorOffset < chunkEnd))
                {
                    var prefixLayout = new TextLayout(
                        _preeditText.Substring(chunkStart, effectiveCursorOffset - chunkStart),
                        typeface,
                        fontRenderingEmSize,
                        foreground,
                        textWrapping: TextWrapping.NoWrap);
                    var cursorX = origin.X + prefixLayout.WidthIncludingTrailingWhitespace;
                    drawingContext.DrawLine(cursorPen,
                        new Point(cursorX, origin.Y),
                        new Point(cursorX, origin.Y + height));
                }

                chunkStart = chunkEnd;
                originX = 0;
                originY += lineHeight;
                if (originBaselineY.HasValue) originBaselineY += lineHeight;
                wrappedLines++;
            }
        }

        /// <summary>
        /// TEXTSS-ADD: view coordinate of the <b>body baseline</b> of the caret line, or null if unavailable.
        /// </summary>
        /// <remarks>
        /// Body text is drawn from <see cref="VisualLine.GetTextLineVisualYPosition"/> with
        /// <see cref="VisualYPosition.Baseline"/> (line top + (line height - text height) / 2 + TextLine.Baseline).
        /// The preedit has to use the same reference, otherwise it floats a few pixels above the text on any
        /// line that picked up a fallback typeface (measured: 3 px on a line containing Japanese).
        /// During wrapping or right after a re-layout there may be no VisualLine yet; null is returned then and
        /// the caller falls back to the top of the line. Never let this stop the drawing - a missing preedit is
        /// far worse than a slightly misplaced one.
        /// </remarks>
        private double? TryGetCaretLineBaselineY(TextView textView)
        {
            try
            {
                var caret = _textArea?.Caret;
                if (caret == null) return null;
                var visualLine = textView.GetVisualLine(caret.Line);
                if (visualLine == null) return null;
                var textLine = visualLine.GetTextLine(caret.Position.VisualColumn, caret.Position.IsAtEndOfLine);
                if (textLine == null) return null;
                return visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.Baseline) - textView.VerticalOffset;
            }
            catch
            {
                return null;   // alignment is cosmetic; keep rendering even if it fails
            }
        }

        /// <summary>
        /// TEXTSS-ADD: find the background brush to paint under a wrapped preedit.
        /// TextView derives from <see cref="Control"/> and has no Background, so walk TextArea -> ancestors
        /// (TextEditor / Border / Window) and take the first brush that is actually visible. Returns null if
        /// there is none, in which case nothing is filled and the preedit is drawn straight over the text.
        ///   Problem : even after adding the fill, body text still showed through the wrapped preedit.
        ///   Cause   : an ancestor ScrollViewer returns Transparent and the search stopped there. On the
        ///             editor side TextArea / TextEditor / Border all have Background = null, and the colour
        ///             the user actually sees is the Window background showing through.
        ///   Fix     : treat a fully transparent brush as "not found" and keep walking up, which reaches the
        ///             Window background.
        ///   Note    : this also follows theme changes, because Window.Background changes with the theme.
        /// </summary>
        private IBrush FindEditorBackground()
        {
            Visual visual = _textArea;

            while (visual != null)
            {
                var brush = (visual as TemplatedControl)?.Background
                            ?? (visual as Border)?.Background
                            ?? (visual as Panel)?.Background;

                if (brush != null && !IsFullyTransparent(brush))
                    return brush;

                visual = visual.GetVisualParent();
            }

            return null;
        }

        /// <summary>TEXTSS-ADD: is this a solid brush that would paint nothing (alpha 0)?</summary>
        private static bool IsFullyTransparent(IBrush brush)
            => brush is ISolidColorBrush solid && solid.Color.A == 0;
    }
}
