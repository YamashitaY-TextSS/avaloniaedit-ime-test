# Third-party notices

This repository redistributes the following third-party components.

## AvaloniaEdit

* Location: `src/AvaloniaEdit/`
* Upstream: <https://github.com/AvaloniaUI/AvaloniaEdit>
* Licence: MIT — Copyright (c) 2017 Eli Arbel. The full text is kept verbatim at
  `src/AvaloniaEdit/LICENSE`.
* Modifications: this is a fork of upstream master `be976ea`, with the preedit part of the unmerged
  [PR #592](https://github.com/AvaloniaUI/AvaloniaEdit/pull/592) applied, plus five further changes
  made while verifying it. Exactly four source files differ from upstream; every added block is
  marked `TEXTSS-ADD`. See the README for the list.
* AvaloniaEdit is itself a port of AvalonEdit, originally from the SharpDevelop team.

## Noto Sans JP

* Location: `src/ImePreeditDemo/Assets/Fonts/NotoSansJP-VF.ttf`
* Upstream: <https://github.com/notofonts/noto-cjk> / <https://fonts.google.com/noto>
* Vendor: Adobe (<http://www.adobe.com/type/>)
* Licence: SIL Open Font License, Version 1.1.
  Copyright (c) 2014-2021 Adobe (<http://www.adobe.com/>), with Reserved Font Name 'Source'.
  Full text: <https://openfontlicense.org/>
* Why it is here: on some Linux systems Avalonia cannot resolve its framework default font
  `$Default`, and AvaloniaEdit then crashes during construction with
  "Could not create glyphTypeface. Font family: $Default". The demo sets this embedded font as the
  default family on Linux so it starts. It is not applied on Windows or macOS.

## Avalonia

Referenced from NuGet, not redistributed in this repository.

* Upstream: <https://github.com/AvaloniaUI/Avalonia>
* Licence: MIT
