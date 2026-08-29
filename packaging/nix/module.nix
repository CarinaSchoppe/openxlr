{ config, lib, pkgs, ... }:

let
  cfg = config.services.openxlr;
in
{
  options.services.openxlr = {
    enable = lib.mkEnableOption
      "OpenXLR, the control suite and PipeWire submixer for Elgato XLR interfaces";

    package = lib.mkOption {
      type = lib.types.package;
      description = "The OpenXLR package to use.";
    };

    clipGuard = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Make the SWH LADSPA plugins visible to the daemon so the software
        ClipGuard limiter works on devices that need it (XLR Dock).
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];

    # Device access for regular users (uaccess tag).
    services.udev.packages = [ cfg.package ];

    # Keeps the XLR Dock's capture source always active; without it the
    # kernel starves capture when playback starts first and the mic
    # records silence.
    services.pipewire.wireplumber.configPackages = [ cfg.package ];

    systemd.user.services.openxlr-daemon = {
      description = "OpenXLR audio daemon";
      after = [ "pipewire-pulse.service" "wireplumber.service" ];
      wantedBy = [ "default.target" ];
      environment = {
        OPENXLR_BUILD_MIXER = "1";
      } // lib.optionalAttrs cfg.clipGuard {
        LADSPA_PATH = "${pkgs.ladspaPlugins}/lib/ladspa";
      };
      serviceConfig = {
        ExecStart = "${cfg.package}/bin/openxlr-daemon";
        TimeoutStopSec = 45;
        Restart = "on-failure";
        RestartSec = 3;
      };
    };
  };
}
