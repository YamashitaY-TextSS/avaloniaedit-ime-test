# AvaloniaEdit IME preedit test

A small application for verifying IME (Input Method Editor) preedit behaviour in
[AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit), together with the fork it was verified
against.

Japanese version of this document: [README.ja.md](README.ja.md)

![The demo application](docs/demo-overview.png)

## Why

AvaloniaEdit does not render IME composition text inside the editor
([#524](https://github.com/AvaloniaUI/AvaloniaEdit/issues/524)). Until the text is committed there is
nothing on screen, which makes the editor hard to use for Japanese, Chinese and Korean input.
Avalonia's own `TextBox` does not have this problem.

The root cause is small and specific. Avalonia's Win32 IME implementation has
`ShowCompositionWindow => false`, so the OS never draws the composition itself, and the only way the
text reaches the application is `Client.SetPreeditText(...)` — which is skipped when
`Client.SupportsPreedit` is false. AvaloniaEdit's `TextArea` had
`public override bool SupportsPreedit => false;` and an empty `SetPreeditText`, so the composition
text was simply dropped. Nothing outside that pair can work around it.

[PR #592](https://github.com/AvaloniaUI/AvaloniaEdit/pull/592) by [@Timskt](https://github.com/Timskt)
implements exactly that pair: it turns `SupportsPreedit` on and adds a `PreeditLayer` that draws the
composition at the caret. The pull request is titled for Chinese IME, but the implementation is not
specific to any language, and it was still open with no review comments when this repository was
written.

This repository is the result of taking that pull request and using it in earnest with Japanese IME
on three platforms.

## What was verified

Environment: .NET 10, Avalonia 12.1.1, AvaloniaEdit master `be976ea` plus the preedit part of PR #592.

| Platform | IME |
|---|---|
| Windows 11 | Microsoft IME |
| macOS 26 | Japanese - Romaji, both with and without Live Conversion |
| Linux Mint 22.3 Xfce | fcitx5 5.1.7 + fcitx5-mozc 2.28.4715.102 |

### Works with PR #592 as it is

| # | Behaviour |
|:-:|---|
| 1 | The composition text is drawn inline at the caret, underlined, with a cursor inside it |
| 2 | The IME candidate window follows directly below the composition |
| 3 | The composition is not inserted into the document; committing inserts it once |
| 4 | Escape cancels the composition and it disappears |
| 5 | The composition is cleared on focus loss and when the document is replaced |
| 6 | The composition follows the caret when the caret moves |
| 7 | Multi-clause conversion, mid-line conversion, after horizontal scrolling, different font sizes, light and dark themes, word wrap on/off, line numbers on/off — all render in the right place |

Point 3 was checked numerically rather than by eye: while composing 9 characters at the end of a
58-character line, the status readout stayed at `Ln 2, Col 59`. Had the composition been inserted
into the document it would have read `Col 68`.

### Needed more work for everyday Japanese input

None of these are defects in PR #592. Three of them are about behaviour outside its scope, and two
only show up with the kind of long conversions Japanese input produces. They are listed here because
anyone adopting the pull request will meet them.

| # | What happens | Why | What this fork does |
|:-:|---|---|---|
| 1 | Clicking elsewhere while composing makes the uncommitted text follow the caret | [#534](https://github.com/AvaloniaUI/AvaloniaEdit/issues/534) — Avalonia only resets the IME on `ResetRequested` and on a client change, never on a caret move, so the composition stays alive and only its rendering moves | Commit at the old caret position during the tunnel phase of the click, before the caret moves |
| 2 | Composition text past the right edge is clipped and cannot be read | The composition is never inserted into the document, so it gets none of the automatic wrapping or horizontal scrolling that body text gets; it was drawn as a single `NoWrap` line | Wrap onto the following line, filling the background first so the text underneath does not show through |
| 3 | At the end of a long line there is no room to draw the composition at all | The horizontal scroll extent only carried upstream's 3 px safety margin | Add room for about 5 full-width characters, both to the scroll extent and to the rectangle `BringCaretToView` asks for |
| 4 | After committing a long conversion the caret is left off screen | `PerformTextInput` calls `BringCaretToView` before the scroll extent has been recomputed, so it clips against the old maximum | Bring the caret into view again after the layout pass |
| 5 | The composition sits a few pixels above the committed text | The composition was drawn from the top of the line, while body text is drawn from the baseline inside the line; the two differ as soon as the line picks up a fallback typeface | Draw the composition from the baseline of the caret line |

Measured for 4: committing 24 characters moved the horizontal offset only 384.4 → 386.9 px while the
caret sat at x = 1204.2 in a viewport 981.3 px wide. The extent after the commit had grown to 1670.6.
Measured for 5: 3 px.

![Composition wrapping at the right edge](docs/preedit-wrap-light.png)

The same in the dark theme — the backing fill follows the theme:

![Composition wrapping, dark theme](docs/preedit-wrap-dark.png)

### Still open

* **Per-clause underlines.** Japanese IMEs distinguish the clause being converted from the rest.
  Avalonia's `SetPreeditText(string text, int? cursorOffset)` carries the text and one cursor offset
  and nothing else, so the whole composition is drawn with a single underline.
* **Input method client hijacking by child controls.** `TextArea`'s class handler for
  `TextInputMethodClientRequestedEvent` sets `e.Client` unconditionally. Because the event bubbles,
  a `TextBox` that is a visual descendant of the `TextArea` — such as the built-in `SearchPanel`'s
  search box — has its own client taken over: the composition is drawn in the editor at its top-left
  corner while the committed text goes to the search box.
  This is independent of the preedit patch; it existed before, and turning `SupportsPreedit` on only
  made it visible. [PR #591](https://github.com/AvaloniaUI/AvaloniaEdit/pull/591) by the same author
  fixes it. The demo in this repository avoids it structurally by hosting its search panel in the
  Window's `OverlayLayer`, outside the `TextArea`, which is measurable with `--diag`:

  ```
  focus=PART_SearchBox -> client=TextBoxTextInputMethodClient rect=11, 7
  ```

## Layout

```
src/AvaloniaEdit/       AvaloniaEdit master be976ea + PR #592 preedit + the 5 changes above
src/ImePreeditDemo/     the demo application
docs/                   screenshots
run-demo.sh             build, measure, then run
```

Only four files under `src/AvaloniaEdit/` differ from upstream. Every added block is marked
`TEXTSS-ADD`:

```
Editing/TextArea.cs        PR #592 preedit wiring + commit on click + re-scroll after commit
Rendering/PreeditLayer.cs  added by PR #592 + right-edge wrapping + baseline alignment
Rendering/TextView.cs      horizontal scroll margin + cap the space below the last line
Editing/Caret.cs           room to the right of the caret in BringCaretToView
```

To see them all:

```
grep -rn "TEXTSS-ADD" src/AvaloniaEdit/
```

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/ImePreeditDemo
```

or, to build, run the automated measurement and then open the window:

```
bash run-demo.sh
```

Type Japanese (or Chinese, or Korean) into the editor and watch the composition text. The plain
`TextBox` at the bottom is there for comparison: preedit works in it out of the box, so it shows what
the editor is expected to match. The sample text in the editor lists nine numbered checks.

The editor is deliberately set up the way [TextSS](https://textss.sakura.ne.jp/en/) sets it up: line
endings, full-width spaces and tabs are shown as visible marker characters, because that is the
context in which these problems were found, and markers and composition text have to coexist.

### Diagnostics

None of these change the normal appearance; they only run when the switch is passed.

| Switch | What it does |
|---|---|
| `--diag` | Report which `TextInputMethodClient` is chosen as focus moves |
| `--diag-preedit` | Measure the horizontal scroll margin, drive the preedit rendering path without an IME, and save a PNG. `--dark` for the dark theme, `--commit` to also commit and measure the scroll that follows |
| `--diag-clickmarker` | Replay what `SelectionMouseHandler` does when clicking past the end of a line |
| `--diag-dblclick` | Check that a double click does not select a line-ending marker on its own |
| `--diag-inputsource` | Count which routed-event paths a real arrow key press reaches |
| `--diag-ruler` | Check that the column ruler stays aligned with the text |

`--diag-preedit` renders its own PNG rather than relying on an external screen capture, because
activating another window scrolls the editor back and losing focus clears the composition — neither
of which can be worked around from outside the process.

## Related

* Issue [#524](https://github.com/AvaloniaUI/AvaloniaEdit/issues/524) — Support IME Preedit
* Issue [#534](https://github.com/AvaloniaUI/AvaloniaEdit/issues/534) — Composition should be committed
* PR [#532](https://github.com/AvaloniaUI/AvaloniaEdit/pull/532) — Support PreeditText
* PR [#591](https://github.com/AvaloniaUI/AvaloniaEdit/pull/591) — Fix IME client incorrectly handling child control events
* PR [#592](https://github.com/AvaloniaUI/AvaloniaEdit/pull/592) — Support Chinese IME preedit and fix related bugs

The work is written up in English in the TextSS development log:
<https://textss.sakura.ne.jp/en/devlog11-avaloniaedit.html>

## Licence

The demo application is MIT — see [LICENSE](LICENSE).

`src/AvaloniaEdit/` is a fork of AvaloniaEdit, which is MIT licensed; its own licence file is kept at
`src/AvaloniaEdit/LICENSE`. Third-party notices are collected in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
