// Main window of the AvaloniaEdit IME preedit verification demo.
//
//   Subject under test : the AvaloniaEdit fork in ../AvaloniaEdit
//                        (master be976ea + the preedit part of PR #592 + the extra changes described in
//                        the repository README).
//
//   What the demo exercises, all on top of the same marker machinery TextSS uses (DemoMarkerSupport.cs):
//     * does the composition text appear at the caret, and does the candidate window follow it
//     * multiple clauses, caret in the middle of a line, after horizontal scrolling, different font sizes,
//       light and dark themes, word wrap on and off, line numbers on and off
//     * Escape cancels, Enter commits
//     * clicking elsewhere during composition commits at the OLD caret position (Issue #534)
//     * the search panel: composing inside the search box must show the preedit INSIDE the search box
//     * line-ending markers and the full-width space box are not disturbed by any of the above
//
//   Diagnostic switches (none of them change the normal appearance; they only run when passed):
//     --diag              which TextInputMethodClient is chosen as focus moves
//     --diag-preedit      measure the horizontal scroll margin and drive the preedit rendering path
//                         without an IME, then save a PNG. Add --dark for the dark theme and --commit to
//                         also commit the text and measure the scroll that follows.
//     --diag-clickmarker  replay what SelectionMouseHandler does on a click past the end of a line
//     --diag-dblclick     verify that a double click does not select a line-ending marker on its own
//     --diag-inputsource  count which routed-event paths a real Right arrow key press reaches
//     --diag-ruler        verify that the column ruler stays aligned with the text
using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ImePreeditDemo;

public partial class MainWindow : Avalonia.Controls.Window
{
    private DemoSearchController? _searchController;
    private int _colorIndex; // 0=green 1=red 2=blue

    // Column ruler above the editor. Created in code because it needs a reference to the TextEditor.
    private DemoColumnRuler? _ruler;

    // Current-line marker in the left margin.
    private DemoCurrentRowMarker? _rowMarker;

    private static readonly (string Name, string Hex)[] ColorCycle =
    {
        ("green", "#006400"),
        ("red", "#B22222"),
        ("blue", "#1E90FF"),
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    // Instrumentation for "typing in the search box shows the preedit somewhere else".
    //   This makes the answer measurable without typing a single character: Avalonia raises
    //   TextInputMethodClientRequestedEvent on every focus change and uses whichever e.Client survived to
    //   the end of the bubble, so a handledEventsToo bubble handler on the Window observes the final value.
    private readonly System.Collections.Generic.List<string> _imDiag = new();

    // Keeps the IM client the editor installed, so --diag-preedit can call SetPreeditText() directly and
    //   drive the PreeditLayer rendering path without an IME. SetPreeditText is public in Avalonia 12.1.1.
    private TextInputMethodClient? _editorImClient;

    private void OnImClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        var source = (e.Source as Avalonia.Controls.Control)?.Name ?? e.Source?.GetType().Name ?? "(null)";
        var client = e.Client?.GetType().Name ?? "(null)";

        // Only remember the editor's own client, never the search box's.
        if (e.Client is { } editorClient && client == "TextAreaTextInputMethodClient")
            _editorImClient = editorClient;

        var rect = e.Client is { } c ? c.CursorRectangle.ToString() : "-";
        var line = $"focus={source} -> client={client} rect={rect}";
        _imDiag.Add(line);
        Console.WriteLine("[IM-DIAG] " + line);   // also to stdout: the status line gets overwritten
        if (_imDiag.Count > 8) _imDiag.RemoveAt(0);
    }

    /// <summary>--diag: move focus editor -> search box and report which IM client was chosen.</summary>
    private async void RunImClientDiagnosticsAsync()
    {
        Editor.Focus();
        await System.Threading.Tasks.Task.Delay(400);
        _imDiag.Add("--- opening the search panel ---");

        _searchController?.OpenPanel();
        await System.Threading.Tasks.Task.Delay(400);
        var searchBox = _searchController?.Panel.SearchBox;
        searchBox?.Focus();
        await System.Threading.Tasks.Task.Delay(600);

        Console.WriteLine("[IM-DIAG] ===== summary =====");
        foreach (var d in _imDiag) Console.WriteLine("[IM-DIAG]   " + d);
        Console.WriteLine($"[IM-DIAG] search box found = {(searchBox != null)} / focused = {(FocusManager?.GetFocusedElement()?.GetType().Name ?? "(null)")}");
        Title = "IM diagnostics - see stdout";
    }



    // ================= --diag-inputsource =================
    //   Question: when the user presses the Right arrow key, which routed-event paths actually see it?
    //   This matters because the caret correction in DemoMarkerSupport decides "was this a key press or a
    //   click" from a tunnelling KeyDown handler on the TextArea, and a synthesised event (RaiseEvent)
    //   cannot prove that a REAL key press takes the same path.
    //   Start the demo in this mode and send {DOWN 5}{END}{RIGHT} from outside; the counters below say
    //   which paths fired. PhysicalKey is logged too: a synthesised event reports None, a real one reports
    //   ArrowRight, so the log proves the measurement was genuine.
    private System.IO.StreamWriter? _inputSrcLog;
    private int _tunnelRightTextArea, _tunnelRightEditor, _bubbleRightEditor, _tunnelRightWindow;
    private int _physicalRightSeen;
    /// <summary>Total KeyDown count, so "no key ever arrived" is not misread as "the path does not fire".</summary>
    private int _anyKeyDownSeen;

    private void InputSrcLog(string line)
    {
        Console.WriteLine("[INPUTSRC-DIAG] " + line);
        _inputSrcLog?.WriteLine(line);
    }

    /// <summary>Instrument four paths: Window tunnel, TextEditor tunnel, TextArea tunnel, TextEditor bubble.</summary>
    private void SetUpInputSourceDiagnostics()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ime-inputsource-diag.txt");
        _inputSrcLog = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
        InputSrcLog("log=" + path);

        // 1. the handler the production decision actually uses (inside DemoMarkerSupport)
        DemoMarkers.InputSourceDiagSink = s =>
        {
            if (s.StartsWith("TextArea Tunnel KeyDown", StringComparison.Ordinal) && s.Contains("Key=Right"))
            {
                _tunnelRightTextArea++;
                if (s.Contains("PhysicalKey=ArrowRight")) _physicalRightSeen++;
            }
            InputSrcLog(s);
        };

        // 2. the same phase but on the TextEditor rather than the TextArea
        Editor.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Right) _tunnelRightEditor++;
            InputSrcLog($"TextEditor Tunnel KeyDown: Key={e.Key} PhysicalKey={e.PhysicalKey} handled={e.Handled}");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 3. bubble, the phase KeyBindings run in, to see the ordering in the log
        Editor.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Right) _bubbleRightEditor++;
            InputSrcLog($"TextEditor Bubble KeyDown: Key={e.Key} handled={e.Handled} (handled=True means a KeyBinding took it)");
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // 4. the outermost tunnel, to see whether the key reached the root at all
        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            _anyKeyDownSeen++;
            if (e.Key == Key.Right) _tunnelRightWindow++;
            InputSrcLog($"Window Tunnel KeyDown: Key={e.Key} PhysicalKey={e.PhysicalKey} handled={e.Handled}");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Wait for real key presses, then report per-path counts and the final caret position.</summary>
    private async void RunInputSourceDiagnosticsAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1200);
            var doc = Editor.Document;

            var lineNo = 1;
            for (var i = 1; i <= doc.LineCount; i++)
            {
                var l = doc.GetLineByNumber(i);
                if (doc.GetText(l.Offset, l.Length).Contains("CRLF 行のマーカーは")) { lineNo = i; break; }
            }
            var line = doc.GetLineByNumber(lineNo);
            var lineEnd = line.EndOffset;                               // to the RIGHT of the marker
            var nextLineStart = doc.GetLineByNumber(lineNo + 1).Offset; // where the Right arrow should land
            InputSrcLog($"sample: line {lineNo} / left of marker={lineEnd - 1} right={lineEnd} / next line start={nextLineStart} / start caret={Editor.CaretOffset}");
            InputSrcLog("Send real keys {DOWN 5}{END}{RIGHT} from outside now (waiting 20 seconds)");
            Title = "measuring input source (--diag-inputsource)";

            await System.Threading.Tasks.Task.Delay(20000);

            var caret = Editor.CaretOffset;
            var endToEnd = caret == nextLineStart;
            InputSrcLog("===== summary =====");
            InputSrcLog($"Key.Right seen on: Window tunnel={_tunnelRightWindow} / TextEditor tunnel={_tunnelRightEditor} / " +
                        $"TextArea tunnel={_tunnelRightTextArea} / TextEditor bubble={_bubbleRightEditor}");
            InputSrcLog($"of which PhysicalKey=ArrowRight (a real key from the OS) = {_physicalRightSeen}");
            InputSrcLog($"final caret={caret} (expected {nextLineStart} = start of next line) -> end to end {(endToEnd ? "OK" : "FAILED")}");
            // If no key arrived at all, saying "the path does not fire" would turn silence into a green result.
            //   Injecting keys can fail silently, so this branch has to exist.
            if (_anyKeyDownSeen == 0)
            {
                InputSrcLog("verdict: MEASUREMENT INVALID - not a single KeyDown arrived (key injection failed). " +
                            "Do not conclude anything about the routed-event paths from this run.");
            }
            else
            {
                InputSrcLog($"verdict: a tunnelling KeyDown handler on the TextArea {(_tunnelRightTextArea > 0 ? "DOES" : "does NOT")} see the Right arrow key" +
                            $" - measured over {_anyKeyDownSeen} KeyDown events");
            }
            _inputSrcLog?.Flush();
            Close();
        }
        catch (Exception ex)
        {
            InputSrcLog("exception: " + ex);
            _inputSrcLog?.Flush();
        }
    }

    /// <summary>
    /// --diag-clickmarker: reproduce "clicking past the end of a line selects the line-ending marker"
    ///   without a real mouse, by replaying exactly what SelectionMouseHandler does:
    ///     1. mouse down : set Caret.Position to the end of the line, then ClearSelection()
    ///     2. mouse up   : old = Caret.Position, set Caret.Position to the end of the line again,
    ///                     then Selection.StartSelectionOrSetEndpoint(old, Caret.Position)
    ///   TextEditorOptions.ExtendSelectionOnMouseUp defaults to true and Caret.PositionChanged is raised
    ///   synchronously, so this replay has the same ordering as the real path.
    /// </summary>
    private async void RunClickMarkerDiagnosticsAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(800);
            var area = Editor.TextArea;
            var doc = Editor.Document;
            var text = Editor.Text ?? string.Empty;

            // sample = the line whose last character is the CRLF marker
            var lineNo = 1;
            for (var i = 1; i <= doc.LineCount; i++)
            {
                var l = doc.GetLineByNumber(i);
                if (doc.GetText(l.Offset, l.Length).Contains("CRLF 行のマーカーは")) { lineNo = i; break; }
            }
            var line = doc.GetLineByNumber(lineNo);
            var lineEnd = line.EndOffset;                 // just before the line break = right of the marker
            Console.WriteLine($"[CLICK-DIAG] line {lineNo}: EndOffset={lineEnd} / text[{lineEnd - 1}]='{text[lineEnd - 1]}' / text[{lineEnd}]=U+{(int)text[lineEnd]:X4}");

            var ok = 0; var ng = 0;

            void Click(string label)
            {
                // 1. mouse down
                area.Caret.Position = new AvaloniaEdit.TextViewPosition(doc.GetLocation(lineEnd)) { IsAtEndOfLine = true };
                var afterDown = Editor.CaretOffset;
                area.ClearSelection();

                // 2. mouse up (ExtendSelectionOnMouseUp defaults to true)
                var oldUp = area.Caret.Position;
                area.Caret.Position = new AvaloniaEdit.TextViewPosition(doc.GetLocation(lineEnd)) { IsAtEndOfLine = true };
                var afterUp = Editor.CaretOffset;
                area.Selection = area.Selection.StartSelectionOrSetEndpoint(oldUp, area.Caret.Position);

                var empty = area.Selection.IsEmpty;
                var sel = empty ? "(empty)" : $"{area.Selection.SurroundingSegment.Offset}+{area.Selection.SurroundingSegment.Length}";
                if (empty) ok++; else ng++;
                Console.WriteLine($"[CLICK-DIAG] {label}: after down caret={afterDown} / old={doc.GetOffset(oldUp.Location)} -> after up caret={afterUp} / selection={sel} {(empty ? "OK" : "FAILED (the marker got selected)")}");
                area.ClearSelection();
            }

            Editor.CaretOffset = lineEnd - 1;   // caret already left of the marker (a repeated click)
            Click("1. previous caret left of the marker");

            Editor.CaretOffset = 0;             // caret far away (a single click)
            Click("2. previous caret far away");

            // 3. regression: the Right arrow key must still jump PAST the marker to the next line.
            Editor.CaretOffset = lineEnd - 1;                       // left of the marker (end of line)
            var keyArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Right,
                KeyModifiers = KeyModifiers.None,
                Source = area,
            };
            area.RaiseEvent(keyArgs);
            var afterRight = Editor.CaretOffset;
            var nextLineStart = doc.GetLineByNumber(lineNo + 1).Offset;
            var rightOk = afterRight == nextLineStart;
            if (rightOk) ok++; else ng++;
            Console.WriteLine($"[CLICK-DIAG] 3. regression: Right arrow moved caret {lineEnd - 1} -> {afterRight} (expected {nextLineStart}) {(rightOk ? "OK" : "FAILED")}");
            Console.WriteLine($"[CLICK-DIAG] result = OK {ok} / failed {ng}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[CLICK-DIAG] exception: " + ex);
        }
    }

    /// <summary>
    /// --diag-dblclick: build the same selection a double click produces (one marker character), run it
    ///   through DemoMarkers.TrySuppressMarkerOnlySelection and report whether it was suppressed.
    ///   A non-marker single character and a three-character selection are also probed, to prove that
    ///   ordinary drag selections are left alone.
    /// </summary>
    private async void RunDoubleClickGuardDiagnosticsAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(800);
            var area = Editor.TextArea;
            var text = Editor.Text ?? string.Empty;
            var ok = 0; var ng = 0;

            void Probe(string label, int off, int len, bool expect)
            {
                if (off < 0 || off + len > text.Length) { Console.WriteLine($"[DBLCLICK-DIAG] {label}: out of range (off={off} len={len}) FAILED"); ng++; return; }
                area.Selection = AvaloniaEdit.Editing.Selection.Create(area, off, off + len);
                Editor.CaretOffset = off + len;
                var before = area.Selection.SurroundingSegment;
                var hit = DemoMarkers.TrySuppressMarkerOnlySelection(Editor);
                var after = area.Selection.IsEmpty
                    ? "(empty)"
                    : $"{area.Selection.SurroundingSegment.Offset}+{area.Selection.SurroundingSegment.Length}";
                var judge = hit == expect ? "OK" : "FAILED";
                if (hit == expect) ok++; else ng++;
                Console.WriteLine($"[DBLCLICK-DIAG] {label}: selection {before.Offset}+{before.Length} -> suppressed={hit} (expected {expect}) / after={after} caret={Editor.CaretOffset} {judge}");
            }

            var crlf = text.IndexOf(DemoMarkers.CrlfMarker);
            var lf = text.IndexOf(DemoMarkers.LfMarker);
            var cr = text.IndexOf(DemoMarkers.CrMarker);
            Console.WriteLine($"[DBLCLICK-DIAG] marker offsets: CRLF={crlf} LF={lf} CR={cr} / document length={text.Length}");

            Probe("1. CRLF marker alone (what a double click selects)", crlf, 1, true);
            Probe("2. lone LF marker", lf, 1, true);
            Probe("3. lone CR marker", cr, 1, true);
            Probe("4. the character before the marker (ordinary text)", crlf - 1, 1, false);
            Probe("5. several characters including the marker (ordinary drag selection)", crlf - 1, 3, false);

            Console.WriteLine($"[DBLCLICK-DIAG] result = OK {ok} / failed {ng}");
            area.ClearSelection();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DBLCLICK-DIAG] exception: " + ex);
        }
    }

    /// <summary>
    /// --diag-preedit: measure, without an IME,
    ///   1. that the horizontal scroll extent really carries the extra margin, and
    ///   2. that a long preedit wraps onto the following line instead of being clipped.
    ///   Normal startup is completely unaffected; this only runs with the switch.
    /// </summary>
    private async void RunPreeditWrapDiagnosticsAsync()
    {
        try
        {
        // --dark takes the shot in the dark theme, to check that the wrapped preedit's backing fill follows
        var dark = Environment.GetCommandLineArgs().Contains("--dark");
        if (dark)
        {
            ThemeToggle.IsChecked = true;   // go through the same path the real toggle uses
            await System.Threading.Tasks.Task.Delay(400);
        }

        Editor.Focus();
        await System.Threading.Tasks.Task.Delay(1200);

        // Move to the end of the long sample line, i.e. scrolled all the way to the right.
        var doc = Editor.Document;
        var target = 1;
        for (var i = 1; i <= doc.LineCount; i++)
        {
            var l = doc.GetLineByNumber(i);
            if (doc.GetText(l.Offset, l.Length).Contains("確認 5")) { target = i; break; }
        }

        var line = doc.GetLineByNumber(target);
        Editor.CaretOffset = line.EndOffset;
        Editor.TextArea.Caret.BringCaretToView();
        await System.Threading.Tasks.Task.Delay(500);

        var tv = Editor.TextArea.TextView;
        var extent = ((Avalonia.Controls.Primitives.IScrollable)tv).Extent;
        var theoretical = 3 + tv.WideSpaceWidth * 10;   // upstream's 3 px + 10 half-width characters
        var caretX = Editor.TextArea.Caret.CalculateCaretRectangle().Right - tv.HorizontalOffset;

        Console.WriteLine($"[PREEDIT-DIAG] moved to the end of line {target} (offset={Editor.CaretOffset})");
        Console.WriteLine($"[PREEDIT-DIAG] 1. WideSpaceWidth={tv.WideSpaceWidth:F2}px / expected margin={theoretical:F1}px");
        Console.WriteLine($"[PREEDIT-DIAG] 1. viewport={tv.Bounds.Width:F1} extent={extent.Width:F1} hOffset={tv.HorizontalOffset:F1}");
        Console.WriteLine($"[PREEDIT-DIAG] 1. room left past the end of the line = {(extent.Width - (tv.HorizontalOffset + tv.Bounds.Width)):F1}px (0 means the margin is not in effect)");
        Console.WriteLine($"[PREEDIT-DIAG] 2. caret X={caretX:F1}px / {(tv.Bounds.Width - caretX):F1}px to the right edge");

        // Same ancestor walk PreeditLayer.FindEditorBackground does, to see which brush it will pick.
        Avalonia.Visual? probe = Editor.TextArea;
        var depth = 0;
        while (probe != null && depth++ < 14)
        {
            var bg = (probe as Avalonia.Controls.Primitives.TemplatedControl)?.Background
                     ?? (probe as Avalonia.Controls.Border)?.Background
                     ?? (probe as Avalonia.Controls.Panel)?.Background;
            Console.WriteLine($"[PREEDIT-DIAG] bg-probe[{depth}]: {probe.GetType().Name} = {bg?.ToString() ?? "(null)"}");
            probe = probe.GetVisualParent();
        }

        // 2. drive the preedit rendering path without an IME (long enough to always overflow the right edge)
        const string pseudo = "ああああああああああああああああああああああああ";
        _editorImClient?.SetPreeditText(pseudo);
        Console.WriteLine($"[PREEDIT-DIAG] 2. pseudo preedit set = {pseudo.Length} characters / client={_editorImClient?.GetType().Name ?? "(null)"}");
        Title = "preedit wrap diagnostics (--diag-preedit)";

        // Save the rendered result ourselves.
        //   An external screen capture could not be used: activating another window scrolls the editor back,
        //   and losing focus clears the preedit. Both were measured. So the demo renders its own PNG.
        await SaveShotAsync(dark ? "-dark" : "");

        // --commit also commits the text, to measure whether the view scrolls to follow the caret.
        if (Environment.GetCommandLineArgs().Contains("--commit"))
        {
            _editorImClient?.SetPreeditText("");        // clear the preedit first, as a real commit does
            Editor.TextArea.PerformTextInput(pseudo);   // the same path OnTextInput takes
            await System.Threading.Tasks.Task.Delay(400);

            var afterCaret = Editor.TextArea.Caret.CalculateCaretRectangle();
            Console.WriteLine($"[PREEDIT-DIAG] after commit: caretOffset={Editor.CaretOffset} hOffset={tv.HorizontalOffset:F1} " +
                              $"caretX={(afterCaret.Right - tv.HorizontalOffset):F1} viewport={tv.Bounds.Width:F1} " +
                              $"extent={((Avalonia.Controls.Primitives.IScrollable)tv).Extent.Width:F1}");
            await SaveShotAsync(dark ? "-commit-dark" : "-commit");
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PREEDIT-DIAG] exception: " + ex);
        }
    }

    // --diag-ruler: prove that the column ruler stays aligned with the text, in numbers and in pictures.
    //   Measured: 1. column width (WideSpaceWidth)  2. the TextView origin in ruler coordinates
    //             3. the horizontal scroll offset   4. the caret X in document coordinates
    //             5. where the ruler puts its marker (= 2 - 3 + 4)
    //   If 5 equals the caret's screen X, the ruler follows the line-number margin, the font and scrolling.
    private async void RunRulerDiagnosticsAsync()
    {
        var tv = Editor.TextArea.TextView;

        async System.Threading.Tasks.Task MeasureAsync(string label, string shotSuffix)
        {
            await System.Threading.Tasks.Task.Delay(400);
            Editor.TextArea.Caret.BringCaretToView();
            UpdateStatus();                       // the status line is part of the screenshot
            await System.Threading.Tasks.Task.Delay(200);

            var caretRect = Editor.TextArea.Caret.CalculateCaretRectangle();
            var origin = tv.TranslatePoint(new Point(0, 0), (Avalonia.Visual)RulerHost.Children[0]);
            double originX = origin?.X ?? double.NaN;
            double markerX = originX - tv.HorizontalOffset + caretRect.X;      // where the ruler draws
            double caretScreenX = caretRect.X - tv.HorizontalOffset + originX; // where the caret is
            // Read the column from the ruler itself; recomputing it here would risk an off-by-one lie.
            int col = _ruler?.CurrentVisualColumn ?? -1;

            var row = _rowMarker?.DiagCurrentRow() ?? (double.NaN, double.NaN);
            Console.WriteLine($"[RULER-DIAG] {label}: cw={tv.WideSpaceWidth:F3} originX={originX:F2} hOff={tv.HorizontalOffset:F2} " +
                              $"caretDocX={caretRect.X:F2} markerX={markerX:F2} caretScreenX={caretScreenX:F2} " +
                              $"diff={(markerX - caretScreenX):F3} col={col} lineNum={Editor.ShowLineNumbers} fontSize={Editor.FontSize} " +
                              $"rowY={row.Item1:F2} rowH={row.Item2:F2} rowHighlight={Editor.Options.HighlightCurrentLine}");
            await SaveShotAsync(shotSuffix);
        }

        await System.Threading.Tasks.Task.Delay(900);

        // 1. default (line numbers on): 10 ASCII characters into line 1
        Editor.CaretOffset = Editor.Document.GetLineByNumber(1).Offset + 10;
        await MeasureAsync("1. default", "-ruler1");

        // 2. inside the Japanese part of line 1: does a full-width character count as two columns
        Editor.CaretOffset = Editor.Document.GetLineByNumber(1).Offset + 30;
        await MeasureAsync("2. full-width text", "-ruler2");

        // 3. line numbers off: the origin moves left, and the ruler must follow
        LineNumberCheck.IsChecked = false;
        await MeasureAsync("3. line numbers off", "-ruler3");

        // 4. larger font: the column width changes
        LineNumberCheck.IsChecked = true;
        Editor.FontSize = 20;
        await MeasureAsync("4. font size 20", "-ruler4");

        // 5. horizontally scrolled: near the right end of the longest line
        Editor.FontSize = 14;
        var longLine = Editor.Document.Lines.OrderByDescending(l => l.Length).First();   // never hard-code a line number
        Editor.CaretOffset = Math.Min(longLine.EndOffset, longLine.Offset + 150);
        await MeasureAsync("5. scrolled", "-ruler5");

        // 6. dark theme: do the ruler colours still balance with the text
        ThemeToggle.IsChecked = true;
        Editor.CaretOffset = Editor.Document.GetLineByNumber(1).Offset + 10;
        await MeasureAsync("6. dark theme", "-ruler6");
        ThemeToggle.IsChecked = false;

        // 7. word wrap on: current-line fill and the left marker must still line up
        WrapCheck.IsChecked = true;
        await System.Threading.Tasks.Task.Delay(300);
        var wrapped = Editor.Document.Lines.OrderByDescending(l => l.Length).First();
        Editor.CaretOffset = Math.Min(wrapped.EndOffset, wrapped.Offset + 120);
        await MeasureAsync("7. word wrap on", "-ruler7");
        WrapCheck.IsChecked = false;

        Console.WriteLine("[RULER-DIAG] done");
        await System.Threading.Tasks.Task.Delay(300);
        Close();
    }

    private async System.Threading.Tasks.Task SaveShotAsync(string suffix)
    {
        await System.Threading.Tasks.Task.Delay(500);
        try
        {
            var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(this);
            var shot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ime-preedit-diag{suffix}.png");
            rtb.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.WriteLine("[PREEDIT-DIAG] shot=" + shot);
        }
        catch (Exception shotEx)
        {
            Console.WriteLine("[PREEDIT-DIAG] shot failed: " + shotEx.Message);
        }
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        AddHandler(TextInputMethodClientRequestedEvent, OnImClientRequested,
                   RoutingStrategies.Bubble, handledEventsToo: true);
        // Monospaced font: name one font that actually exists on each platform.
        //   Linux deliberately uses Noto Sans Mono CJK JP rather than DejaVu Sans Mono, because DejaVu Sans
        //   Mono has no Japanese glyphs at all (fc-list "DejaVu Sans Mono" :lang=ja returns nothing). With
        //   DejaVu, ASCII would be monospaced while Japanese came from a fallback font, which is not the
        //   combination we want to measure preedit positions in.
        if (OperatingSystem.IsWindows()) Editor.FontFamily = new FontFamily("Consolas");
        else if (OperatingSystem.IsMacOS()) Editor.FontFamily = new FontFamily("Menlo");
        else Editor.FontFamily = new FontFamily("Noto Sans Mono CJK JP");

        // Markers: half-width space and tab come from AvaloniaEdit, the full-width space box from an
        // element generator, and the line-ending markers are inserted as real characters.
        DemoMarkers.ApplyMarkerOptions(Editor, showMarkers: true);
        DemoMarkers.AttachMarkerAwareKeyHandling(Editor);       // Delete/Backspace remove marker + line break
        DemoMarkers.AttachMarkerAwareCaretCorrection(Editor);   // the caret never stops right of a marker
        DemoMarkers.AttachMarkerAwareDoubleClickGuard(Editor);  // a double click does not select a marker

        Editor.Text = DemoMarkers.AnnotateNewlines(BuildSampleRawText());

        // Search: Attach() uninstalls the built-in SearchPanel (which AvaloniaEdit installs in
        //   OnApplyTemplate) and takes over Ctrl+F, showing a panel on the Window's OverlayLayer instead.
        //   Being outside the TextArea is what keeps the input method client from being hijacked.
        _searchController = new DemoSearchController(Editor);
        _searchController.Attach();

        // Column ruler. Created after the font is set, because its label size follows the editor font.
        _ruler = new DemoColumnRuler(Editor);
        RulerHost.Children.Add(_ruler);
        _ruler.IsVisible = RulerCheck.IsChecked == true;

        // Current line: a faint fill over the whole line (AvaloniaEdit's own HighlightCurrentLine, so it
        //   follows wrapping and line-height changes) plus a marker in the left margin.
        _rowMarker = new DemoCurrentRowMarker(Editor);
        Editor.TextArea.LeftMargins.Add(_rowMarker);   // appended last: unaffected by the line-number toggle
        ApplyCurrentRowHighlight();

        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        UpdateStatus();
        Editor.Focus();

        // The diagnostics below only run when the matching switch is passed.
        if (Environment.GetCommandLineArgs().Contains("--diag"))
        {
            // wire the fork's static hook so commit-on-click can be traced
            AvaloniaEdit.Editing.TextArea.ImeCommitDiagSink = s => Console.WriteLine("[IM-DIAG] " + s);
            RunImClientDiagnosticsAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--diag-clickmarker"))
        {
            RunClickMarkerDiagnosticsAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--diag-dblclick"))
        {
            DemoMarkers.MarkerSelectionDiagSink = s => Console.WriteLine("[DBLCLICK-DIAG] " + s);
            RunDoubleClickGuardDiagnosticsAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--diag-inputsource"))
        {
            SetUpInputSourceDiagnostics();
            RunInputSourceDiagnosticsAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--diag-ruler"))
        {
            RunRulerDiagnosticsAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--diag-preedit"))
        {
            Console.WriteLine("[PREEDIT-DIAG] start");
            AvaloniaEdit.Editing.TextArea.ImeCommitDiagSink = s => Console.WriteLine("[PREEDIT-DIAG] " + s);
            RunPreeditWrapDiagnosticsAsync();
        }
    }

    /// <summary>
    /// Sample text, without markers. Deliberately mixes CRLF, a lone LF, a lone CR, a full-width space and
    /// a tab. It is written in Japanese because that is the input this demo exists to test; the numbered
    /// lines correspond to checks 1-9 listed in the repository README.
    /// </summary>
    private static string BuildSampleRawText()
    {
        return
            "AvaloniaEdit IME preedit デモ — 日本語を入力して変換中の文字がキャレット位置に出るか確認します。\r\n" +
            "確認 1: この行末にカーソルを置いて「きょうはいいてんきですね」を変換 → 複数文節の preedit 表示 → \r\n" +
            "確認 2: 行の途中に preedit → ここ[]の間で変換してみる。\r\n" +
            "確認 3: Escape で preedit が消えるか / Enter 確定で本文に残るか。\r\n" +
            "確認 4: 下の各マーカーが preedit 表示中も壊れないか (色 = 緑・ボールド):\r\n" +
            "  CRLF 行のマーカーは ↵ (この行)\r\n" +
            "  孤立 LF 行のマーカーは ↓ (この行)\n" +
            "  孤立 CR 行のマーカーは ← (この行)\r" +
            "  全角スペース→　←□表示 / 半角スペース→ ←·表示 / タブ→\t←→表示\r\n" +
            "確認 5: 長い行のスクロール後も preedit 位置がずれないか → 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 0123456789 ここで変換\r\n" +
            "確認 6: Ctrl+F の検索パネル (TextSS6 と同じ自前パネル) 内でも日本語入力が正常か = 変換中の文字が検索ボックスの中に出るか。\r\n" +
            "確認 7: DEL/BackSpace で「マーカー+改行コード」が一括削除されるか (↵ の上で DEL)。\r\n" +
            "確認 8: 変換中に本文の別の場所をクリック → 未確定文字が「元の位置で確定」してからキャレットが移動するか (クリック時確定・#534 対策)。\r\n" +
            "確認 9: 本文で変換中に検索パネルの検索ボックスをクリック → 未確定文字がどうなるかを記録 (確定 or 破棄・場所)。\r\n";
    }

    private void UpdateStatus()
    {
        var caret = Editor.TextArea.Caret;
        var (chars, cells, utf8Bytes) = MeasureCaretColumn();
        int visualColumn = _ruler?.CurrentVisualColumn ?? -1;
        // The DISPLAYED column (what the ruler shows) and the logical character / byte counts are reported
        //   separately on purpose. They do not agree, and that is not a defect: Consolas has no Japanese
        //   glyphs, and the fallback font's advance width is not exactly two half-width cells.
        StatusText.Text =
            $"Ln {caret.Line}, Col {caret.Column} (AvaloniaEdit, 1-based)" +
            $" / ruler column = {(visualColumn >= 0 ? visualColumn.ToString() : "-")} (displayed, 0-based)" +
            $" / from start of line: {chars} chars, {cells} half-width cells, {utf8Bytes} UTF-8 bytes (0-based)" +
            $" / FontSize {Editor.FontSize} / Avalonia 12.1.1 + AvaloniaEdit fork be976ea+preedit";
        _ruler?.InvalidateVisual();
    }

    /// <summary>
    /// Count from the start of the line to the caret, in half-width cells and in UTF-8 bytes.
    /// All three values are 0-based, matching the ruler (a full-width character is two cells; a tab runs to
    /// the next tab stop).
    /// </summary>
    private (int Chars, int Cells, int Utf8Bytes) MeasureCaretColumn()
    {
        var doc = Editor.Document;
        if (doc == null) return (1, 1, 1);

        int offset = Math.Clamp(Editor.CaretOffset, 0, doc.TextLength);
        var line = doc.GetLineByOffset(offset);
        string head = doc.GetText(line.Offset, offset - line.Offset);

        int tabSize = Math.Max(1, Editor.Options.IndentationSize);
        int cells = 0;
        for (int i = 0; i < head.Length; i++)
        {
            char c = head[i];
            if (c == '\t') { cells += tabSize - (cells % tabSize); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < head.Length) { cells += 2; i++; continue; }
            cells += IsWideChar(c) ? 2 : 1;
        }
        return (head.Length, cells, System.Text.Encoding.UTF8.GetByteCount(head));
    }

    /// <summary>True for East Asian wide characters, which occupy two half-width cells.</summary>
    private static bool IsWideChar(char c)
        => (c >= 0x1100 && c <= 0x115F)     // Hangul Jamo
        || (c >= 0x2E80 && c <= 0xA4CF)     // CJK radicals through Han, Kana, Bopomofo
        || (c >= 0xAC00 && c <= 0xD7A3)     // Hangul syllables
        || (c >= 0xF900 && c <= 0xFAFF)     // CJK compatibility ideographs
        || (c >= 0xFE30 && c <= 0xFE4F)     // CJK compatibility forms
        || (c >= 0xFF00 && c <= 0xFF60)     // full-width forms
        || (c >= 0xFFE0 && c <= 0xFFE6);    // full-width currency signs

    private void OnThemeToggled(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app == null) return;
        app.RequestedThemeVariant = ThemeToggle.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        // The current-line colours are plain property values, so they have to be re-applied on a theme
        //   change. The ruler reads the theme while rendering and needs nothing here.
        if (_rowMarker != null) ApplyCurrentRowHighlight();
    }

    private void OnFontMinus(object? sender, RoutedEventArgs e)
    {
        if (Editor.FontSize > 8) Editor.FontSize -= 1;
        UpdateStatus();
    }

    private void OnFontPlus(object? sender, RoutedEventArgs e)
    {
        if (Editor.FontSize < 40) Editor.FontSize += 1;
        UpdateStatus();
    }

    private void OnWrapChanged(object? sender, RoutedEventArgs e)
    {
        Editor.WordWrap = WrapCheck.IsChecked == true;
    }

    private void OnLineNumberChanged(object? sender, RoutedEventArgs e)
    {
        // Toggling the line-number margin moves the origin; both the preedit and the ruler must follow.
        Editor.ShowLineNumbers = LineNumberCheck.IsChecked == true;
    }

    /// <summary>Show or hide the column ruler.</summary>
    private void OnRulerChanged(object? sender, RoutedEventArgs e)
    {
        if (_ruler == null) return;   // fires once before Loaded, when the ruler does not exist yet
        _ruler.IsVisible = RulerCheck.IsChecked == true;
    }

    /// <summary>Show or hide the current-line highlight (line fill and left marker together).</summary>
    private void OnCurrentRowChanged(object? sender, RoutedEventArgs e)
    {
        if (_rowMarker == null) return;   // fires once before Loaded
        ApplyCurrentRowHighlight();
    }

    /// <summary>
    /// Apply the current-line highlight for the active theme. The accent matches the column ruler, so the
    /// row marker and the column marker read as a pair. The fill is kept faint so it does not fight with
    /// the text, and a thin border shows the extent of the line.
    /// </summary>
    private void ApplyCurrentRowHighlight()
    {
        bool on = CurrentRowCheck.IsChecked == true;
        // Trust the toggle, not ActualThemeVariant: right after switching, the latter can still be stale.
        bool dark = ThemeToggle.IsChecked == true;
        var accent = dark ? Color.FromRgb(0xFF, 0xB3, 0x00) : Color.FromRgb(0xC6, 0x28, 0x28);

        Editor.Options.HighlightCurrentLine = on;
        Editor.TextArea.TextView.CurrentLineBackground =
            new Avalonia.Media.Immutable.ImmutableSolidColorBrush(accent, dark ? 0.10 : 0.07);
        Editor.TextArea.TextView.CurrentLineBorder =
            new Pen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(accent, dark ? 0.45 : 0.35), 1);

        if (_rowMarker != null) _rowMarker.IsVisible = on;
    }

    private void OnMarkerChanged(object? sender, RoutedEventArgs e)
    {
        bool show = MarkerCheck.IsChecked == true;
        DemoMarkers.ApplyMarkerOptions(Editor, show);
        // The line-ending markers are real characters, so toggling them rewrites the document.
        Editor.Text = show
            ? DemoMarkers.AnnotateNewlines(DemoMarkers.StripMarkers(Editor.Text ?? string.Empty))
            : DemoMarkers.StripMarkers(Editor.Text ?? string.Empty);
    }

    private void OnMarkerColorCycle(object? sender, RoutedEventArgs e)
    {
        _colorIndex = (_colorIndex + 1) % ColorCycle.Length;
        var (name, hex) = ColorCycle[_colorIndex];
        DemoMarkers.MarkerColor = Color.Parse(hex);
        MarkerColorButton.Content = $"Marker colour: {name}";
        DemoMarkers.RefreshMarkerColor(Editor); // the transformer reads the colour on every draw
    }

    private void OnReannotate(object? sender, RoutedEventArgs e)
    {
        // Re-insert the markers after line breaks that were typed by hand.
        Editor.Text = DemoMarkers.AnnotateNewlines(DemoMarkers.StripMarkers(Editor.Text ?? string.Empty));
    }

    private void OnOpenSearch(object? sender, RoutedEventArgs e)
    {
        _searchController?.OpenPanel();
    }
}
