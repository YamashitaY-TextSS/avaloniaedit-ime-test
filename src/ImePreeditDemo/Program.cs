// Entry point of the AvaloniaEdit IME preedit verification demo (Windows / macOS / Linux).
using System;
using Avalonia;

namespace ImePreeditDemo;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Linux only: guarantee a default font through FontManagerOptions.
    //   Problem : on Linux Mint 22.3 the demo aborted immediately at startup with
    //             `System.InvalidOperationException: Could not create glyphTypeface. Font family: $Default`
    //             (exit 134). The window never appeared.
    //   Cause   : without FontManagerOptions, resolving $Default on Linux depends on what fonts the machine
    //             happens to have. While MainWindow is being constructed, the AvaloniaEdit TextEditor
    //             constructor calls TextView.CalculateDefaultTextMetrics(), which runs BEFORE any font is
    //             applied to the editor - so setting Editor.FontFamily is not enough to avoid it.
    //   Fix     : on Linux, set an embedded font (NotoSansJP-VF.ttf, SIL OFL 1.1) as both DefaultFamilyName
    //             and FontFallbacks.
    //   Note    : do NOT set FontManagerOptions on the other platforms; Windows and macOS start fine without
    //             it and changing them would only risk a regression.
    //   Note    : this has nothing to do with the preedit patch. It is a property of the demo host itself and
    //             should not be counted against the quality of PR #592.
    private const string LinuxEmbeddedFont =
        "avares://ImePreeditDemo/Assets/Fonts/NotoSansJP-VF.ttf#Noto Sans JP";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsLinux())
        {
            builder = builder.With(new Avalonia.Media.FontManagerOptions
            {
                DefaultFamilyName = LinuxEmbeddedFont,
                FontFallbacks = new[]
                {
                    new Avalonia.Media.FontFallback
                    {
                        FontFamily = new Avalonia.Media.FontFamily(LinuxEmbeddedFont),
                    },
                },
            });
        }

        return builder;
    }
}
