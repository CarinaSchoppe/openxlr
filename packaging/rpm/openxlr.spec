# The publish output is prebuilt .NET; its bundled native libraries must
# not be stripped, scanned for shared-library deps, or given debuginfo.
%global debug_package %{nil}
%global __strip /usr/bin/true
%global _build_id_links none

Name:           openxlr
Version:        0.1.5
Release:        1%{?dist}
Summary:        Control suite and PipeWire submixer for Elgato XLR interfaces
License:        GPL-3.0-only
URL:            https://github.com/emaspa/openxlr
Source0:        %{name}-%{version}.tar.gz
ExclusiveArch:  x86_64

BuildRequires:  dotnet-sdk-10.0
BuildRequires:  systemd-rpm-macros

# Prebuilt .NET assemblies; dependencies are declared by hand, matching
# the Debian and Arch packages.
AutoReqProv:    no
Requires:       aspnetcore-runtime-10.0
Requires:       pipewire
Requires:       pipewire-pulseaudio
Requires:       wireplumber
Requires:       pulseaudio-libs
Requires:       libusb1
Requires:       fontconfig
Requires:       libX11
Requires:       libICE
Requires:       libSM
Recommends:     alsa-utils
Recommends:     pulseaudio-utils
Recommends:     xdg-utils
Suggests:       ladspa-swh-plugins

%description
Native Linux control for Elgato XLR interfaces over reverse-engineered
USB protocols: gain, DSP, phantom power, output routing and the
hardware mixer. Includes a Wave Link style PipeWire submixer with
per-application channels, virtual microphones, multi-output monitoring
and a dedicated mix for a second computer on the USB Aux port, plus an
OpenDeck plugin for Stream Deck control.

Supported devices: Wave XLR Pro, XLR Dock (Stream Deck+ module),
Wave XLR and Wave XLR MK.2.

After installing, enable the per-user daemon with
"systemctl --user enable --now openxlr-daemon" and replug the
interface once so the udev rule applies.

%prep
%autosetup

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
       DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet publish src/OpenXLR.Daemon -c Release -r linux-x64 \
    --self-contained false -o out/daemon
dotnet publish src/OpenXLR.UI -c Release -r linux-x64 \
    --self-contained false -o out/ui

%install
install -dm755 %{buildroot}%{_prefix}/lib/openxlr
cp -r out/daemon %{buildroot}%{_prefix}/lib/openxlr/daemon
cp -r out/ui %{buildroot}%{_prefix}/lib/openxlr/ui
# dotnet publish marks assemblies executable; only the apphosts are.
find %{buildroot}%{_prefix}/lib/openxlr -type f -exec chmod 644 {} +
chmod 755 %{buildroot}%{_prefix}/lib/openxlr/daemon/OpenXLR.Daemon \
    %{buildroot}%{_prefix}/lib/openxlr/ui/OpenXLR.UI

install -dm755 %{buildroot}%{_bindir}
printf '#!/bin/sh\nexec %{_prefix}/lib/openxlr/daemon/OpenXLR.Daemon "$@"\n' \
    > %{buildroot}%{_bindir}/openxlr-daemon
printf '#!/bin/sh\nexec %{_prefix}/lib/openxlr/ui/OpenXLR.UI "$@"\n' \
    > %{buildroot}%{_bindir}/openxlr
chmod 755 %{buildroot}%{_bindir}/openxlr-daemon %{buildroot}%{_bindir}/openxlr

install -Dm644 packaging/70-openxlr.rules \
    %{buildroot}%{_udevrulesdir}/70-openxlr.rules
install -Dm644 packaging/50-xlr-dock-capture-hold.conf \
    %{buildroot}%{_datadir}/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf

# The reference unit points into a source checkout; the package runs
# the wrapper.
sed 's|^ExecStart=.*|ExecStart=%{_bindir}/openxlr-daemon|' \
    packaging/openxlr-daemon.service > openxlr-daemon.service
install -Dm644 openxlr-daemon.service \
    %{buildroot}%{_userunitdir}/openxlr-daemon.service

install -Dm644 packaging/openxlr.desktop \
    %{buildroot}%{_datadir}/applications/openxlr.desktop
for size in 16 32 48 64 128 256; do
    install -Dm644 src/OpenXLR.UI/Assets/icon-$size.png \
        %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps/openxlr.png
done
install -Dm644 src/OpenXLR.UI/Assets/icon.svg \
    %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/openxlr.svg

# OpenDeck loads plugins from the user's config dir; ship it for copying.
install -dm755 %{buildroot}%{_datadir}/openxlr
cp -r plugin/com.emaspa.openxlr.sdPlugin %{buildroot}%{_datadir}/openxlr/
find %{buildroot}%{_datadir}/openxlr -type f -exec chmod 644 {} +
find %{buildroot}%{_datadir}/openxlr -type d -exec chmod 755 {} +

%post
/usr/bin/udevadm control --reload 2>/dev/null || :
/usr/bin/udevadm trigger 2>/dev/null || :
cat <<'MSG'
OpenXLR: replug your interface once so the udev rule applies.
Start the daemon:  systemctl --user enable --now openxlr-daemon
Start the mixer:   openxlr   (or from your application menu)
Stream Deck via OpenDeck:
  cp -r /usr/share/openxlr/com.emaspa.openxlr.sdPlugin ~/.config/opendeck/plugins/
MSG

%files
%license LICENSE
%doc README.md
%{_prefix}/lib/openxlr/
%{_bindir}/openxlr
%{_bindir}/openxlr-daemon
%{_udevrulesdir}/70-openxlr.rules
%{_datadir}/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
%{_userunitdir}/openxlr-daemon.service
%{_datadir}/applications/openxlr.desktop
%{_datadir}/icons/hicolor/*/apps/openxlr.*
%{_datadir}/openxlr/

%changelog
* Sun Aug 30 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.5-1
- XLR Dock: 48V phantom power and headphone low impedance, reached over
  the original Wave XLR's protocol dialect (discovery credit: openwave
  PR #8) and verified on hardware.
- Wave XLR Pro: the firmware's ~13 s anti-thump mute around every 48V
  change is now shown as a settling hold with a live countdown on the
  mute button, released the moment the input goes live again.
- Mic filter nodes carry an explicit session priority so they can never
  win the default-device election.

* Sat Aug 29 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.0-1
- Initial Fedora packaging, mirroring the tested AUR and Debian
  packages: framework-dependent .NET publish of the daemon and UI into
  /usr/lib/openxlr with wrapper scripts in /usr/bin, udev rule,
  WirePlumber capture-hold config, per-user systemd unit, desktop
  entry, icons and the bundled OpenDeck plugin.
