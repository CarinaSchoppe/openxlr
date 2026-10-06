# Window localization

The desktop window ships English, German, Spanish and French catalogues.
Options, APPEARANCE, Language selects the next launch's language. System
language follows the process's UI culture, including regional variants such
as `de-AT`, `es-MX` and `fr-CA`. Unsupported languages fall back to English.
`OPENXLR_LANGUAGE=en` overrides the saved choice for that launch, so a user
can recover from an unfamiliar language without editing a file.

The `language` field in `~/.config/openxlr/ui.json` is `null` for the system
language, or `en`, `de`, `es`, `fr`. It is local window state, independent of
the daemon, audio profiles, layout and skins. Changing it preserves the
other window preferences. A failed save is reported and the picker returns
to its previous choice. Unreadable or malformed preferences are not replaced
by a language save. Closing and launching the app again applies the
choice; simply reopening a window or activating the existing tray instance
does not. Audio and open plugin editors are not rebuilt for a language
change.

This first pass translates the fixed text in desktop markup, including
tooltips, placeholders and accessibility names, the desktop key setup page,
tray menu, profile overwrite confirmation and common mixer and insert
statuses. Device, channel, mix, profile and plugin names remain their own
names. Plugin parameter names, plugin editors, daemon errors and diagnostics,
some dynamic setup messages, release notes, the terminal mixer, OpenDeck
inspectors and Omarchy remain in their original language. They are separate
follow-up work; the catalogues do not rewrite strings received over the API.

## Adding or changing text

`src/OpenXLR.UI/Localization/Strings.resx` is the neutral English catalogue.
The `.de.resx`, `.es.resx` and `.fr.resx` files carry matching keys. They are
ordinary .NET resources compiled into satellite assemblies, with no new
dependency, translation service or runtime file watcher. Packages include
them through the normal .NET publish output.

Use a stable descriptive key. A wording change keeps its key. Add every key
to all four files in the same change. Markup uses
`Text="{loc:Text Key=Close}"` with
`xmlns:loc="using:OpenXLR.UI.Localization"`. Code uses
`Localizer.Text("Close")`. Text with data uses numbered placeholders and
`Localizer.Format("InsertCount", count)`. Preserve placeholder indices in
each translation; arguments can move within the sentence. Never make a
user-supplied name a resource key, concatenate sentence fragments, or
translate an API command or stable layout id.

The localizer selects an explicit resource culture before any window is
built. It does not set `CurrentCulture` or `CurrentUICulture`. Machine
numbers and PipeWire helpers keep their existing conventions. Catalogue
lookup is cached by `ResourceManager`; there is no disk read or translation
allocation in repeated insert status lookups. A missing key is a programming
error. Missing language resources use .NET's neutral English fallback.

`LocalizationTests` checks all keys, usages, nonempty translations, format
arguments, packaged satellites, regional lookup, fallback and repeated
lookup allocations. `LanguageSettingsTests` checks persistence and failure.
Run the existing window layout acceptance separately for each language:

```sh
OPENXLR_LANGUAGE=de OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
```

Repeat with `en`, `es` and `fr`, and at 150% desktop scale using
`AVALONIA_GLOBAL_SCALE_FACTOR=1.5`. Set `OPENXLR_LAYOUT_ARTIFACTS` to save
previews. Translated controls use the same skin tokens and layout as the
English window. Xvfb checks do not establish Plasma or Wayland acceptance.
