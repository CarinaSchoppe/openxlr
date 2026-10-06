using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

internal static class LocalizationRenderingTests
{
    // Called inside the existing desktop fixture after its platform starts.
    // The test job installs Noto core and CJK fonts so missing glyphs really
    // fail rather than passing with tofu rectangles on non-Latin scripts.
    internal static void Check()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var host = new Window { Width = 760, Height = 300, Content = text };
        try
        {
            host.Show();
            Assert.Equal(TextAlignment.DetectFromContent, text.TextAlignment);
            var english = Localizer.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;
            var labels = english.Cast<DictionaryEntry>().Select(entry => Localizer.Text((string)entry.Key))
                .Concat(Localizer.Languages.Select(language => language.Label));
            foreach (string label in labels)
            {
                text.Text = label;
                host.UpdateLayout();
                var runs = text.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().ToArray();
                Assert.NotEmpty(runs);
                foreach (var run in runs)
                    Assert.All(run.GlyphRun.GlyphInfos, glyph => Assert.NotEqual((ushort)0, glyph.GlyphIndex));
            }
            text.Text = Localizer.Text("Language");
            host.UpdateLayout();
            bool rtl = CultureInfo.GetCultureInfo(Localizer.Language).TextInfo.IsRightToLeft;
            var bounds = text.TextLayout.TextLines[0].GetTextBounds(0, text.Text.Length);
            Assert.Contains(bounds, b => b.FlowDirection == (rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight));
            // English fallback text stays readable in the same translated UI.
            text.Text = Localizer.Text("SkippedBundlesAfterFailedScan");
            host.UpdateLayout();
            Assert.All(text.TextLayout.TextLines[0].GetTextBounds(0, text.Text.Length),
                b => Assert.Equal(FlowDirection.LeftToRight, b.FlowDirection));
            Assert.Equal(FlowDirection.LeftToRight, host.FlowDirection);
        }
        finally { host.Close(); }
    }
}
