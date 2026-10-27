{
  description = "Linux Wave Link replacement for Elgato XLR interfaces and Wave microphones";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system:
        f nixpkgs.legacyPackages.${system});
    in {
      packages = forAllSystems (pkgs: rec {
        openxlr = pkgs.callPackage ./packaging/nix/package.nix {
          sourceCommit = self.rev or (if self ? dirtyRev then builtins.substring 0 40 self.dirtyRev else null);
          dirtySource = !(self ? rev);
        };
        # DeepFilterNet's static library alone, which the helper links.
        openxlr-deepfilter = openxlr.passthru.deepfilter;
        default = openxlr;
      } // pkgs.lib.optionalAttrs pkgs.stdenv.hostPlatform.isx86_64 {
        openxlr-yabridge = pkgs.callPackage ./packaging/nix/yabridge.nix { };
      });

      nixosModules.openxlr = { pkgs, lib, ... }: {
        imports = [ ./packaging/nix/module.nix ];
        services.openxlr.package = lib.mkDefault
          self.packages.${pkgs.stdenv.hostPlatform.system}.openxlr;
      };
      nixosModules.default = self.nixosModules.openxlr;
    };
}
