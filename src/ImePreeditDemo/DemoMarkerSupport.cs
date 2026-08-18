// Marker machinery copied from TextSS, so the preedit can be verified alongside it.
//   Why this is in the demo at all: TextSS shows line endings and full-width spaces as visible characters,
//   and the whole point of the exercise was to check that the IME preedit and these markers coexist.
//   Four layers are involved:
//     1. line-ending markers are inserted as REAL characters into the document (AnnotateNewlines)
//     2. a VisualLineElementGenerator turns U+3000 into a box glyph
//     3. a DocumentColorizingTransformer colours the markers
//     4. helpers make Delete/Backspace remove "marker + line break" in one go, and keep the caret from
//        stopping to the right of a marker
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace ImePreeditDemo;

/// <summary>Marker constants, annotation and editor options, matching what TextSS does.</summary>
public static class DemoMarkers
{
    /// <summary>Marker shown for CRLF (U+21B5).</summary>
    public const char CrlfMarker = '↵';
    /// <summary>Marker shown for a lone LF.</summary>
    public const char LfMarker = '↓';
    /// <summary>Marker shown for a lone CR.</summary>
    public const char CrMarker = '←';

    /// <summary>Marker colour. The demo cycles through a few values with a button.</summary>
    public static Color MarkerColor { get; set; } = Color.Parse("#006400");

    /// <summary>Whether to render U+3000 (full-width space) as a visible box.</summary>
    public static bool ShowFullWidthSpaces { get; set; } = true;

    /// <summary>
    /// Insert a marker character in front of every line break, keeping the line break itself.
    /// gets U+21B5, a lone LF gets U+2193, a lone CR gets U+2190.
    /// </summary>
    public static string AnnotateNewlines(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length + 16);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
            {
                sb.Append(CrlfMarker).Append('\r').Append('\n');
                i++;
            }
            else if (c == '\n')
            {
                sb.Append(LfMarker).Append('\n');
            }
            else if (c == '\r')
            {
                sb.Append(CrMarker).Append('\r');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip the markers inserted by AnnotateNewlines and return the raw text.
    /// Only a marker character IMMEDIATELY followed by CR or LF is removed, so a marker character the user
    /// typed as ordinary text survives unless a line break happens to follow it.
    /// </summary>
    public static string StripMarkers(string annotated)
    {
        if (string.IsNullOrEmpty(annotated)) return string.Empty;
        var sb = new StringBuilder(annotated.Length);
        for (int i = 0; i < annotated.Length; i++)
        {
            char c = annotated[i];
            bool isMarker = c == CrlfMarker || c == LfMarker || c == CrMarker;
            if (isMarker && i + 1 < annotated.Length && (annotated[i + 1] == '\r' || annotated[i + 1] == '\n'))
            {
                continue; // drop the marker, keep the line break
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Half-width spaces and tabs use AvaloniaEdit's own rendering; line breaks and full-width spaces are
    /// handled by the code above. ShowEndOfLine is forced off - see the note below.
    /// </summary>
    public static void ApplyMarkerOptions(TextEditor editor, bool showMarkers)
    {
        editor.Options.ShowSpaces = showMarkers;
        editor.Options.ShowTabs = showMarkers;
        editor.Options.ShowEndOfLine = false; // with true, AvaloniaEdit prints a literal "/n" instead
        var generator = DemoMarkerElementGenerator.GetOrAdd(editor);
        generator.ShowFullWidthSpaces = showMarkers && ShowFullWidthSpaces;
        AddColoringIfMissing(editor);
        RefreshMarkerColor(editor);
    }

    /// <summary>Re-apply the current marker colour to NonPrintableCharacterBrush (spaces and tabs).</summary>
    public static void RefreshMarkerColor(TextEditor editor)
    {
        var tv = editor.TextArea?.TextView;
        if (tv == null) return;
        tv.NonPrintableCharacterBrush = new ImmutableSolidColorBrush(MarkerColor);
        tv.Redraw();
    }

    private static void AddColoringIfMissing(TextEditor editor)
    {
        var transformers = editor.TextArea?.TextView?.LineTransformers;
        if (transformers == null) return;
        if (transformers.OfType<DemoMarkerColoringTransformer>().Any()) return;
        transformers.Add(new DemoMarkerColoringTransformer());
    }

    // ================= Delete/Backspace as one unit, and caret correction =================
    // Because the markers are real characters in the document, "marker + CR + LF" has to be deleted as a
    // single unit, and the caret must not be able to sit between the marker and the line break.

    private static readonly ConditionalWeakTable<TextEditor, object> _keyHandlerAttached = new();
    private static readonly ConditionalWeakTable<TextEditor, object> _caretCorrectionAttached = new();
    private static readonly ConditionalWeakTable<TextEditor, object> _dblClickGuardAttached = new();

    /// <summary>Diagnostic sink for the double-click suppression (null = disabled).</summary>
    public static Action<string>? MarkerSelectionDiagSink { get; set; }

    /// <summary>
    /// Diagnostic sink for the "where did this caret move come from" decision, so it can be traced with
    /// real key presses rather than reasoned about. When the sink is null nothing is emitted at all, so
    /// normal runs are unaffected.
    /// (Measured result: arrow keys DO arrive at a tunnelling KeyDown handler on the TextArea.)
    /// </summary>
    public static Action<string>? InputSourceDiagSink { get; set; }

    private sealed class CaretTracker
    {
        public int LastCaret;
        public bool Suppress;
        /// <summary>Was the previous input the Right arrow key? Used to tell it apart from a click, End, or an insertion.</summary>
        public bool LastWasRightArrow;
    }

    public static void AttachMarkerAwareKeyHandling(TextEditor editor)
    {
        if (_keyHandlerAttached.TryGetValue(editor, out _)) return;
        _keyHandlerAttached.Add(editor, new object());
        // Must be Tunnel: on Bubble, AvaloniaEdit has already deleted a single character.
        editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextEditor editor) return;
        if (editor.Document == null) return;
        if (editor.SelectionLength > 0) return;

        var doc = editor.Document;
        var caret = editor.CaretOffset;
        var text = doc.Text;

        if (e.Key == Key.Delete)
        {
            if (editor.IsReadOnly) return;
            if (caret >= text.Length) return;
            char c = text[caret];
            if (c == CrlfMarker && caret + 2 < text.Length && text[caret + 1] == '\r' && text[caret + 2] == '\n')
            { doc.Remove(caret, 3); e.Handled = true; return; }
            if (c == LfMarker && caret + 1 < text.Length && text[caret + 1] == '\n')
            { doc.Remove(caret, 2); e.Handled = true; return; }
            if (c == CrMarker && caret + 1 < text.Length && text[caret + 1] == '\r')
            { doc.Remove(caret, 2); e.Handled = true; return; }
        }
        else if (e.Key == Key.Back)
        {
            if (editor.IsReadOnly) return;
            if (caret == 0) return;
            char prev = text[caret - 1];
            if (prev == '\n')
            {
                if (caret >= 3 && text[caret - 2] == '\r' && text[caret - 3] == CrlfMarker)
                { doc.Remove(caret - 3, 3); e.Handled = true; return; }
                if (caret >= 2 && text[caret - 2] == LfMarker)
                { doc.Remove(caret - 2, 2); e.Handled = true; return; }
            }
            else if (prev == '\r')
            {
                if (caret >= 2 && text[caret - 2] == CrMarker)
                { doc.Remove(caret - 2, 2); e.Handled = true; return; }
            }
        }
    }

    public static void AttachMarkerAwareCaretCorrection(TextEditor editor)
    {
        if (_caretCorrectionAttached.TryGetValue(editor, out _)) return;
        _caretCorrectionAttached.Add(editor, new object());

        var tracker = new CaretTracker { LastCaret = editor.CaretOffset };
        if (editor.TextArea?.Caret == null) return;

        // Remember WHERE a caret move came from (the Right arrow key, or a click / End / an insertion).
        //   Problem : clicking past the end of a line selected the line-ending marker.
        //   Cause   : wentForward below only looked at whether the caret moved +1..+3, which cannot tell the
        //             Right arrow key apart from a click. A click past the end of a line was therefore
        //             treated as "moving forward", the caret was pushed to the start of the next line, and
        //             between SelectionMouseHandler's mouse-down (which reads Caret.Position, then our
        //             correction) and its mouse-up (ExtendSelectionToMouse; ExtendSelectionOnMouseUp
        //             defaults to true) the caret oscillated between the two offsets. The resulting
        //             StartSelectionOrSetEndpoint(old, Caret.Position) selected marker + CR + LF.
        //   Fix     : only treat it as forward movement when the previous input really was the Right arrow
        //             key. A click, End, or an insertion always snaps to the left of the marker.
        //   Note    : SelectionMouseHandler subscribes on Bubble, so this flag is set on Tunnel, earlier.
        var area = editor.TextArea;
        area.AddHandler(InputElement.KeyDownEvent, (s, e) =>
        {
            tracker.LastWasRightArrow = e.Key == Key.Right;
            // One line of instrumentation to confirm this tunnelling handler really fires for a real key
            //   press. PhysicalKey is included on purpose: a synthesised event (RaiseEvent) reports None,
            //   while a real key from the OS reports ArrowRight - which proves the measurement is genuine.
            InputSourceDiagSink?.Invoke(
                $"TextArea Tunnel KeyDown: Key={e.Key} PhysicalKey={e.PhysicalKey} handled={e.Handled} " +
                $"source={e.Source?.GetType().Name} → LastWasRightArrow={tracker.LastWasRightArrow}");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            tracker.LastWasRightArrow = false;
            InputSourceDiagSink?.Invoke("TextArea Tunnel PointerPressed → LastWasRightArrow=False");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            tracker.LastWasRightArrow = false;
            InputSourceDiagSink?.Invoke("TextArea Tunnel PointerReleased → LastWasRightArrow=False");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.TextInputEvent, (s, e) =>
        {
            tracker.LastWasRightArrow = false;
            InputSourceDiagSink?.Invoke($"TextArea Tunnel TextInput: '{e.Text}' → LastWasRightArrow=False");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        editor.TextArea.Caret.PositionChanged += (s, e) =>
        {
            if (tracker.Suppress) return;
            var caret = editor.CaretOffset;
            var text = editor.Text ?? string.Empty;
            if (caret < 1 || caret > text.Length) { tracker.LastCaret = caret; return; }

            char prev = text[caret - 1];
            bool isMarker = prev == CrlfMarker || prev == LfMarker || prev == CrMarker;
            bool isLineCodeNext = caret < text.Length && (text[caret] == '\r' || text[caret] == '\n');

            if (isMarker && isLineCodeNext)
            {
                int diff = caret - tracker.LastCaret;
                // This used to be `diff > 0 && diff <= 3`, i.e. the Right arrow key was INFERRED from how far
                //   the caret moved. Distance cannot distinguish a key press from a click; see the note above.
                bool wentForward = tracker.LastWasRightArrow && diff > 0;
                InputSourceDiagSink?.Invoke(
                    $"Caret.PositionChanged: caret={caret} last={tracker.LastCaret} diff={diff} " +
                    $"LastWasRightArrow={tracker.LastWasRightArrow} → wentForward={wentForward} " +
                    $"(planned correction = {(wentForward ? "start of next line" : "left of the marker")})");
                // The old separate "moved backward" branch is folded into the else below.
                tracker.Suppress = true;
                try
                {
                    if (wentForward)
                    {
                        int skip = prev == CrlfMarker ? 2 : 1;
                        editor.CaretOffset = Math.Min(caret + skip, text.Length);
                    }
                    else
                    {
                        // Backward moves, clicks, End and large jumps all snap to the left of the marker.
                        //   Problem : the caret could stop to the RIGHT of a line-ending marker, and text
                        //             typed there ended up after the marker.
                        //   Cause   : the correction only looked at moves of up to 3 offsets (one arrow key
                        //             press), so End, a click, or a long insertion sailed straight past it.
                        //   Fix     : anything that is not a Right arrow key press puts the caret to the left
                        //             of the marker, i.e. at the end of the line. Typing then always inserts
                        //             before the marker, which is what the display implies.
                        editor.CaretOffset = caret - 1;
                    }
                }
                catch { /* ignore a failed CaretOffset assignment */ }
                finally { tracker.Suppress = false; }
            }
            tracker.LastCaret = editor.CaretOffset;
        };
    }

    /// <summary>
    /// Stop a double click from selecting a line-ending marker as if it were a word.
    ///   Problem : double clicking on a line-ending marker selected that single character.
    ///   Cause   : the markers are REAL characters in the document, and their Unicode categories are So / Sm,
    ///             which AvaloniaEdit maps to CharacterClass.Other (TextUtilities.GetCharacterClass, default
    ///             branch). Word selection treats a symbol as a one-character word, so
    ///             SelectionMouseHandler (WholeWord -> GetWordAtMousePosition -> Selection.Create) selects
    ///             exactly that one character.
    ///   Note    : this is upstream word-selection behaviour, not a side effect of the caret correction above.
    ///   Fix     : right after a double click, if the selection is exactly one marker character, drop the
    ///             selection and put the caret to the left of the marker (end of line).
    ///   Note    : this is deliberately done OUTSIDE the fork, so it survives going back to the NuGet build.
    ///   Note    : SelectionMouseHandler sets e.Handled = true, hence handledEventsToo: true here.
    ///             Ordinary drag selections spanning several characters are left alone.
    /// </summary>
    public static void AttachMarkerAwareDoubleClickGuard(TextEditor editor)
    {
        if (_dblClickGuardAttached.TryGetValue(editor, out _)) return;
        _dblClickGuardAttached.Add(editor, new object());

        var area = editor.TextArea;
        if (area == null) return;

        area.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (e.ClickCount < 2) return;
            var suppressed = TrySuppressMarkerOnlySelection(editor);
            MarkerSelectionDiagSink?.Invoke(
                $"dblclick: clickCount={e.ClickCount} caret={editor.CaretOffset} suppressed={suppressed}");
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// If the selection is exactly one line-ending marker, drop it and put the caret to the left of the
    /// marker (end of line). Returns whether it suppressed anything. Public so --diag-dblclick can measure it.
    /// </summary>
    public static bool TrySuppressMarkerOnlySelection(TextEditor editor)
    {
        var area = editor?.TextArea;
        if (area?.Selection == null || area.Selection.IsEmpty) return false;

        var seg = area.Selection.SurroundingSegment;
        if (seg == null || seg.Length != 1) return false;          // leave ordinary selections alone

        var text = editor!.Text ?? string.Empty;
        if (seg.Offset < 0 || seg.Offset >= text.Length) return false;

        char c = text[seg.Offset];
        if (c != CrlfMarker && c != LfMarker && c != CrMarker) return false;

        area.ClearSelection();
        editor.CaretOffset = seg.Offset;                            // left of the marker = end of line
        return true;
    }
}

/// <summary>Renders U+3000 (full-width space) as a box glyph. Line breaks are NOT handled here.</summary>
public sealed class DemoMarkerElementGenerator : VisualLineElementGenerator
{
    public bool ShowFullWidthSpaces { get; set; }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        // Returning the line-break position (line.EndOffset) from a VisualLineElementGenerator crashes
        //   AvaloniaEdit internally. Line-ending markers are inserted as real characters by
        //   AnnotateNewlines instead; this generator only deals with full-width spaces.
        var line = CurrentContext.VisualLine.LastDocumentLine;
        var endOffset = line.EndOffset;
        if (ShowFullWidthSpaces && endOffset > startOffset)
        {
            var relevantText = CurrentContext.GetText(startOffset, endOffset - startOffset);
            for (int i = 0; i < relevantText.Count; i++)
            {
                if (relevantText.Text[relevantText.Offset + i] == '　')
                    return startOffset + i;
            }
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var doc = CurrentContext.Document;
        var line = CurrentContext.VisualLine.LastDocumentLine;
        if (ShowFullWidthSpaces && offset < line.EndOffset)
        {
            if (doc.GetCharAt(offset) == '　')
                return new FormattedTextElement("□", 1);
        }
        return null;
    }

    public static DemoMarkerElementGenerator GetOrAdd(TextEditor editor)
    {
        var generators = editor.TextArea.TextView.ElementGenerators;
        var existing = generators.OfType<DemoMarkerElementGenerator>().FirstOrDefault();
        if (existing != null) return existing;
        var fresh = new DemoMarkerElementGenerator();
        generators.Add(fresh);
        return fresh;
    }
}

/// <summary>Colours the line-ending markers and the full-width space box with MarkerColor, in bold.</summary>
public sealed class DemoMarkerColoringTransformer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line == null || CurrentContext == null) return;
        var brush = (IBrush)new ImmutableSolidColorBrush(DemoMarkers.MarkerColor);
        var startOffset = line.Offset;
        var lineText = CurrentContext.Document.GetText(line);
        for (int i = 0; i < lineText.Length; i++)
        {
            char c = lineText[i];
            if (c == DemoMarkers.CrlfMarker || c == DemoMarkers.LfMarker || c == DemoMarkers.CrMarker || c == '□')
            {
                int offset = startOffset + i;
                ChangeLinePart(offset, offset + 1, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    var t = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, t.Style, FontWeight.Bold, t.Stretch));
                });
            }
        }
    }
}
