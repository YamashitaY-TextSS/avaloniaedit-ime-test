// Ctrl+F search, modelled on the one TextSS uses.
//   How    : AvaloniaEdit's built-in SearchPanel is uninstalled so this class can take over Ctrl+F, and a
//            custom panel (DemoSearchPanelView) is shown on the Window's OverlayLayer
//            (DemoSearchPanelOverlayHost).
//   Why it is here at all: the built-in SearchPanel lives INSIDE the TextArea, and its search box has its
//            input method client taken over by the TextArea class handler. The preedit then goes to the
//            editor (drawn at its top-left corner) while the committed text goes to the search box. That is
//            an upstream issue independent of the preedit patch - PR #591 by the same author fixes it - and
//            this demo simply avoids it structurally by keeping the panel outside the TextArea.
//   The match highlight colours are hard-coded here because this demo has no theme resources.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace ImePreeditDemo;

/// <summary>A matched range of text (start offset and length).</summary>
public readonly record struct TextMatchRange(int Start, int Length);

/// <summary>
/// Draggable Ctrl+F search. Disables the built-in SearchPanel and shows a custom panel on the
/// OverlayLayer. Call Attach() once the Window is loaded.
/// </summary>
public sealed class DemoSearchController
{
    private readonly TextEditor _editor;
    private readonly DemoSearchPanelView _panel;
    private readonly DemoSearchPanelOverlayHost _host;
    private readonly DemoSearchRenderer _renderer;

    private IReadOnlyList<TextMatchRange> _matches = Array.Empty<TextMatchRange>();
    private int _currentMatch = -1;
    private int _generation;                  // generation counter, so stale async results are dropped
    private DispatcherTimer? _debounce;        // 150 ms debounce while typing
    private bool _open;

    public DemoSearchController(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _panel = new DemoSearchPanelView();
        _host = new DemoSearchPanelOverlayHost(editor, _panel);
        _renderer = new DemoSearchRenderer
        {
            // Same colours TextSS uses for match / current-match highlighting.
            MatchBrush = new SolidColorBrush(Color.Parse("#66FFD54F")),
            CurrentMatchBrush = new SolidColorBrush(Color.Parse("#99FF8F00")),
        };
    }

    /// <summary>The hosted search panel (used by --diag to verify which control has focus).</summary>
    public DemoSearchPanelView Panel => _panel;

    /// <summary>Uninstall the built-in SearchPanel, take over Ctrl+F, and install the highlight renderer.</summary>
    public void Attach()
    {
        // The built-in SearchPanel is installed automatically in OnApplyTemplate; uninstall it and take Ctrl+F.
        try { _editor.SearchPanel?.Uninstall(); } catch { /* not installed, or a version difference */ }

        _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        _editor.AddHandler(InputElement.KeyDownEvent, OnTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _editor.DetachedFromVisualTree += OnEditorDetached;

        _panel.SearchBox.TextChanged += OnSearchBoxTextChanged;
        _panel.SearchBox.KeyDown += OnSearchBoxKeyDown;
        _panel.PrevButton.Click += OnPrevClick;
        _panel.NextButton.Click += OnNextClick;
        _panel.CloseButton.Click += OnCloseClick;
        _panel.MatchCaseToggle.IsCheckedChanged += OnOptionChanged;
        _panel.WholeWordToggle.IsCheckedChanged += OnOptionChanged;
        _panel.RegexToggle.IsCheckedChanged += OnOptionChanged;
    }

    /// <summary>Open the search panel from outside (a button, or --diag).</summary>
    public void OpenPanel() => Open();

    private void OnEditorDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_open) Close();
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Open();
            e.Handled = true;
            return;
        }
        if (!_open) return;
        if (e.Key == Key.F3)
        {
            Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>Keys inside the search box: Enter = next, Shift+Enter = previous, F3 / Shift+F3 the same, Esc closes.</summary>
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnSearchBoxTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => ScheduleSearch();
    private void OnOptionChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RunSearch();
    private void OnPrevClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(-1);
    private void OnNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(1);
    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Ctrl+F: show the panel on the OverlayLayer and focus the search box, pre-filled with a single-line selection.</summary>
    private void Open()
    {
        _host.Open();
        if (!_host.IsOpen) return;
        _open = true;

        var sel = _editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && sel.IndexOf('\n') < 0 && sel.IndexOf('\r') < 0)
            _panel.SearchBox.Text = sel;

        _panel.SearchBox.Focus();
        _panel.SearchBox.SelectAll();
        RunSearch();
    }

    /// <summary>Esc, the close button, or the editor being hidden: remove the panel, clear the highlights, focus the editor.</summary>
    private void Close()
    {
        _open = false;
        _matches = Array.Empty<TextMatchRange>();
        _currentMatch = -1;
        _renderer.Clear();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _host.Close();
        _editor.Focus();
    }

    private void ScheduleSearch()
    {
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounce.Tick += (_, _) => { _debounce!.Stop(); RunSearch(); };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Compute the matches on a background thread so a large document does not freeze the UI.</summary>
    private void RunSearch()
    {
        string query = _panel.SearchBox.Text ?? string.Empty;
        string body = _editor.Text ?? string.Empty;
        bool matchCase = _panel.MatchCaseToggle.IsChecked == true;
        bool wholeWord = _panel.WholeWordToggle.IsChecked == true;
        bool useRegex = _panel.RegexToggle.IsChecked == true;

        int gen = ++_generation;

        if (string.IsNullOrEmpty(query))
        {
            ApplyMatches(Array.Empty<TextMatchRange>(), patternError: false, gen);
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            var found = FindAll(body, query, matchCase, wholeWord, useRegex, out bool err);
            Dispatcher.UIThread.Post(() => ApplyMatches(found, err, gen));
        });
    }

    private void ApplyMatches(IReadOnlyList<TextMatchRange> matches, bool patternError, int gen)
    {
        if (gen != _generation) return; // a newer search has started; drop this result
        _matches = matches;
        _currentMatch = matches.Count > 0 ? 0 : -1;
        _renderer.SetMatches(matches, _currentMatch);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        UpdateCountLabel(patternError);
        if (_currentMatch >= 0) ScrollToMatch(_currentMatch);
    }

    private void Navigate(int delta)
    {
        if (_matches.Count == 0) return;
        _currentMatch = ((_currentMatch + delta) % _matches.Count + _matches.Count) % _matches.Count;
        _renderer.SetMatches(_matches, _currentMatch);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        UpdateCountLabel(false);
        ScrollToMatch(_currentMatch);
    }

    /// <summary>Scroll the current match into view. The match is shown by the highlight, not by selecting it.</summary>
    private void ScrollToMatch(int index)
    {
        if (index < 0 || index >= _matches.Count || _editor.Document is null) return;
        try
        {
            var loc = _editor.Document.GetLocation(_matches[index].Start);
            _editor.ScrollTo(loc.Line, loc.Column);
        }
        catch { /* transient out-of-range while the document is being edited */ }
    }

    private void UpdateCountLabel(bool patternError)
    {
        var countText = _panel.CountText;
        string query = _panel.SearchBox.Text ?? string.Empty;

        // Status text shown in the panel.
        if (patternError)
        {
            countText.Text = "Invalid pattern";
        }
        else if (string.IsNullOrEmpty(query))
        {
            countText.Text = string.Empty;
        }
        else if (_matches.Count == 0)
        {
            countText.Text = "No results";
        }
        else
        {
            countText.Text = string.Format("{0} / {1}", _currentMatch + 1, _matches.Count);
        }
    }

    // ---- Match computation: literal or regex, optional case sensitivity and whole-word matching,
    //      zero-length matches excluded, 2 second regex timeout. ----

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static IReadOnlyList<TextMatchRange> FindAll(
        string? text, string? query,
        bool matchCase, bool wholeWord, bool useRegex,
        out bool patternError)
    {
        patternError = false;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return Array.Empty<TextMatchRange>();

        string pattern = useRegex ? query! : Regex.Escape(query!);
        if (wholeWord)
            pattern = $"\\b(?:{pattern})\\b";

        var options = RegexOptions.CultureInvariant;
        if (!matchCase) options |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException)
        {
            patternError = true;
            return Array.Empty<TextMatchRange>();
        }

        var results = new List<TextMatchRange>();
        try
        {
            foreach (Match m in regex.Matches(text!))
            {
                if (m.Length > 0)
                    results.Add(new TextMatchRange(m.Index, m.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            patternError = true;
            return Array.Empty<TextMatchRange>();
        }
        return results;
    }
}

/// <summary>
/// IBackgroundRenderer that paints a translucent highlight behind each match.
/// </summary>
public sealed class DemoSearchRenderer : IBackgroundRenderer
{
    private IReadOnlyList<TextMatchRange> _matches = Array.Empty<TextMatchRange>();
    private int _currentIndex = -1;

    /// <summary>Fill used for ordinary matches.</summary>
    public IBrush? MatchBrush { get; set; }

    /// <summary>Fill used for the current match.</summary>
    public IBrush? CurrentMatchBrush { get; set; }

    // Drawn behind the glyphs, on the same layer the built-in search renderer uses.
    public KnownLayer Layer => KnownLayer.Background;

    public void SetMatches(IReadOnlyList<TextMatchRange> matches, int currentIndex)
    {
        _matches = matches ?? Array.Empty<TextMatchRange>();
        _currentIndex = currentIndex;
    }

    public void Clear()
    {
        _matches = Array.Empty<TextMatchRange>();
        _currentIndex = -1;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        // Work out the visible offset range and skip matches outside it.
        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        var lastVisual = textView.VisualLines[textView.VisualLines.Count - 1];
        int viewEnd = lastVisual.LastDocumentLine.EndOffset;

        for (int i = 0; i < _matches.Count; i++)
        {
            var m = _matches[i];
            if (m.Length <= 0) continue;
            if (m.Start + m.Length <= viewStart || m.Start >= viewEnd) continue; // off screen

            var brush = (i == _currentIndex) ? CurrentMatchBrush : MatchBrush;
            if (brush is null) continue;

            var segment = new TextSegment { StartOffset = m.Start, Length = m.Length };
            var geoBuilder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 0 };
            geoBuilder.AddSegment(textView, segment);
            var geometry = geoBuilder.CreateGeometry();
            if (geometry != null)
                drawingContext.DrawGeometry(brush, null, geometry);
        }
    }
}
