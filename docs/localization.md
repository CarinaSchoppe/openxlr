# Window localization

The desktop window ships fourteen languages. Chinese has separate simplified
and traditional catalogues, giving fifteen choices plus System language.
Options, APPEARANCE, Language changes the running window's language immediately. System
language follows the process's UI culture, including regional variants such
as `de-AT`, `es-MX` and `fr-CA`. Unsupported languages fall back to English.
On Linux the .NET runtime derives that culture from the locale environment
inherited at login: `LC_ALL`, then `LC_MESSAGES`, then `LANG`. OpenXLR does
not change the desktop's language or its numeric locale.
`OPENXLR_LANGUAGE=en` overrides the saved choice for that launch, so a user
can recover from an unfamiliar language without editing a file.
Options reports when this launch override is active. A successful language
selection replaces it for this session; the environment override applies again
on the next launch if it is still set.

The `language` field in `~/.config/openxlr/ui.json` is `null` for the system
language, or one of the catalogue ids below. It is local window state,
independent of the daemon, audio profiles, layout and skins. Changing it preserves the
other window preferences, including fields owned by newer window features.
Other window preference writes retain those unfamiliar fields too. A failed
save is reported and the picker returns
to its previous choice after the selection binding finishes. The same choice
can then be retried once the file is writable. A pending rejected selection
does not undo a later successful selection. Unreadable or malformed preferences are not replaced
by a language save. Successful selection updates translated resources in
existing windows and the tray menu. A failed save leaves the current language
unchanged. Audio and open plugin editors are not rebuilt for a language change.

An existing preferences file must contain an object. A root `null`, scalar
or array is refused by a language save rather than overwritten. An explicit
`null` collapsed-section list is recovered as an empty list, so window
restoration can still enumerate it. Unfamiliar nested values remain data and
are preserved by the standard JSON extension-data mechanism.

This first pass translates the fixed text in desktop markup, including
tooltips, placeholders and accessibility names, the desktop key setup page,
tray menu, profile overwrite confirmation and common mixer and insert
statuses. Device, channel, mix, profile and plugin names remain their own
names. Plugin parameter names, plugin editors, daemon errors and diagnostics,
some dynamic setup messages, release notes, the terminal mixer, OpenDeck
inspectors and Omarchy remain in their original language. They are separate
follow-up work; the catalogues do not rewrite strings received over the API.

## Shipped languages

| Language | Catalogue id | Picker label |
|---|---|---|
| English | `en` | English |
| German | `de` | Deutsch |
| Spanish | `es` | Español |
| French | `fr` | Français |
| Chinese, simplified | `zh-Hans` | 简体中文 |
| Chinese, traditional | `zh-Hant` | 繁體中文 |
| Hindi | `hi` | हिन्दी |
| Modern Standard Arabic | `ar` | العربية |
| Bengali | `bn` | বাংলা |
| Portuguese | `pt` | Português |
| Indonesian | `id` | Bahasa Indonesia |
| Urdu | `ur` | اردو |
| Russian | `ru` | Русский |
| Japanese | `ja` | 日本語 |
| Nigerian Pidgin | `pcm` | Naijíriá Píjin |

The ten additional languages follow the highest total-speaker entries not
already shipped in the [Ethnologue 2026 table reproduced here](https://en.wikipedia.org/wiki/List_of_languages_by_total_number_of_speakers#Ethnologue_(2026)).
Total speakers includes first- and second-language users. Written Arabic
uses Modern Standard Arabic rather than separate dialect catalogues; both
Chinese scripts belong to one language choice family. The new catalogues
translate 232 of the 364 window entries each: controls, menus, statuses,
profiles and language setup. The remaining longer help text uses the original
English fallback. Native-speaker wording review remains useful before release.

Regional locales select their primary language, including `pt-BR` and `pt-PT`,
`ar-EG`, `ur-PK` and the three-letter `pcm-NG`. Chinese keeps its script:
`zh-CN` and `zh-SG` select `zh-Hans`; `zh-TW`, `zh-HK` and `zh-MO` select
`zh-Hant`. Explicit `Hans` or `Hant` wins over a region; bare `zh` uses Hans.
The legacy .NET names `zh-CHS` and `zh-CHT` select Hans and Hant respectively,
using the same mapping for saved choices, temporary overrides and system
cultures. [Microsoft documents these aliases](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.parent).
They do not add picker entries or satellite assemblies.

Text uses Avalonia's content-based alignment and bidirectional shaping, so
Arabic and Urdu read right to left while English fallback text and paths keep
their own direction. Physical left/right audio controls, the signal graph
and the saved channel order retain their existing orientation. Input toggles
have a 44-pixel minimum width so short translations remain usable. Skins keep
supplying colours, fonts and control appearances; no script-specific palette
or font is forced. The desktop must have fonts covering the selected script.
CI installs Noto core and CJK fonts and checks shaped glyphs for every entry.

## Adding or changing text

`src/OpenXLR.UI/Localization/Strings.resx` is the neutral English catalogue.
The `Strings.<catalogue-id>.resx` files carry translated keys. They are
ordinary .NET resources compiled into satellite assemblies, with no new
dependency, translation service or runtime file watcher. `Localizer.Languages`
is the single list for system selection, the picker and save validation.
Packages include them through the normal .NET publish output.

Use a stable descriptive key. A wording change keeps its key. Always add the
original English wording to the neutral file. Add translations when available;
a key absent from a translated catalogue displays that English wording,
even when the rest of the window is translated. Do not add empty entries or
placeholder translations. The plugin scan count is one such English-only
dynamic message in this first pass. Markup reads a live `Ox.Text.*` application resource using
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
lookup uses `ResourceManager` and caches resolved strings by culture and key,
including neutral fallback results. Selecting a language updates the bounded
English catalogue's application resources and refreshes bound status text once.
It adds no timer or work to meter updates. There is no disk read or translation
allocation in repeated insert status lookups or untranslated text lookups.
A missing key is a programming error when absent from English too.
Missing language resources and individual
untranslated keys use .NET's neutral English fallback. Text not yet using the
localizer retains its existing English wording.

`LocalizationTests` checks all keys, usages, nonempty translations, format
arguments, packaged satellites, regional lookup, per-key fallback and repeated
lookup allocations. `LanguageSettingsTests` checks persistence and failure.
Resource-catalogue probes join the window configuration collection so the
allocation measurement does not share a resource cache with parallel
view-model tests looking up a different language.
Run the existing window layout acceptance separately for each language:

```sh
OPENXLR_LANGUAGE=de OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
```

Repeat with every id in the table, and at 150% desktop scale using
`AVALONIA_GLOBAL_SCALE_FACTOR=1.5`. Set `OPENXLR_LAYOUT_ARTIFACTS` to save
previews. Translated controls use the same skin tokens and layout as the
English window. Xvfb checks do not establish Plasma or Wayland acceptance.

The same acceptance fixture can seed a private saved language before the
application initializes. The expected language is checked after initialization,
and the settings picker must reflect the saved choice rather than a launch
override. These Linux examples check system selection, a saved override and
the precedence of the temporary launch override:

```sh
env -u OPENXLR_LANGUAGE LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 OPENXLR_TEST_LANGUAGE_PREFERENCE=system OPENXLR_TEST_EXPECT_LANGUAGE=de OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
env -u OPENXLR_LANGUAGE LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 OPENXLR_TEST_LANGUAGE_PREFERENCE=fr OPENXLR_TEST_EXPECT_LANGUAGE=fr OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 OPENXLR_LANGUAGE=en OPENXLR_TEST_LANGUAGE_PREFERENCE=fr OPENXLR_TEST_EXPECT_LANGUAGE=en OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
```

Also run system selection with `es_MX.UTF-8`, `fr_CA.UTF-8` and an unsupported
locale such as `it_IT.UTF-8`, expecting `es`, `fr` and `en`. No real desktop
preferences or daemon state are written by this fixture.
CI runs these startup cases, saved choices for all shipped catalogues, temporary overrides,
an explicit system override and an empty launch override, plus message-locale
and base-locale selection when `LC_ALL` is unset. Each case uses a
separate process, so the application initializes once with those inputs.

Startup acceptance also covers saved regional and legacy Chinese choices,
legacy temporary overrides and an unsupported saved value. The picker shows
the corresponding shipped language, without rewriting the original preference
at initialization. Unsupported choices display the English fallback.

When integrating another window feature, register its new captions in the
English catalogue and reuse existing keys for unchanged text. Translated
catalogues may omit new keys and use English until translations are supplied.
Remove superseded keys from every catalogue. The catalogue test checks that
all keys are used and that untranslated new captions retain their English value.
