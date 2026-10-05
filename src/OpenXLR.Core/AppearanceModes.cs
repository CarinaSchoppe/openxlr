#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Saved Material appearance choices, shared by profiles and both clients.</summary>
public static class AppearanceModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static bool IsValid(string? value) => value is System or Light or Dark;

    /// <summary>An absent or unknown local preference follows the desktop.</summary>
    public static string Normalize(string? value) => IsValid(value) ? value! : System;
}
