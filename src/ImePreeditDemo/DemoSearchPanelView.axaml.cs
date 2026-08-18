// Code-behind of the Ctrl+F search panel used by the demo.
//   This is a trimmed copy of the search panel TextSS uses. What matters for this demo is WHERE it lives:
//   it is placed in the Window's OverlayLayer, i.e. outside the TextArea. See DemoSearchPanelOverlayHost
//   and the note about input method client hijacking in the repository README.
//   Layout, dragging and clamping live in DemoSearchPanelOverlayHost; this class only exposes the named
//   parts so DemoSearchController can wire the search logic to them.
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ImePreeditDemo;

/// <summary>
/// Ctrl+F search panel UI. Hosted in the Window's OverlayLayer, so it is not a visual descendant of the
/// TextArea and therefore never routes TextInputMethodClientRequested through it.
/// </summary>
public partial class DemoSearchPanelView : UserControl
{
    public DemoSearchPanelView()
    {
        InitializeComponent();
    }

    /// <summary>Drag grip. DemoSearchPanelOverlayHost subscribes to its DragDelta.</summary>
    public Thumb DragThumb => PART_DragThumb;

    /// <summary>Search text input.</summary>
    public TextBox SearchBox => PART_SearchBox;

    /// <summary>"current / total" match counter.</summary>
    public TextBlock CountText => PART_SearchCount;

    /// <summary>Find previous.</summary>
    public Button PrevButton => PART_SearchPrev;

    /// <summary>Find next.</summary>
    public Button NextButton => PART_SearchNext;

    /// <summary>Match case toggle.</summary>
    public ToggleButton MatchCaseToggle => PART_SearchMatchCase;

    /// <summary>Whole word toggle.</summary>
    public ToggleButton WholeWordToggle => PART_SearchWholeWord;

    /// <summary>Regular expression toggle.</summary>
    public ToggleButton RegexToggle => PART_SearchRegex;

    /// <summary>Close button.</summary>
    public Button CloseButton => PART_SearchClose;
}
