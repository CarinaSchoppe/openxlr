{
  description = "Control suite and PipeWire submixer for Elgato XLR interfaces";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system:
        f nixpkgs.legacyPackages.${system});
    in {
      packages = forAllSystems (pkgs: rec {
        openxlr = pkgs.callPackage ./packaging/nix/package.nix { };
        default = openxlr;
      });

      nixosModules.openxlr = { pkgs, lib, ... }: {
        imports = [ ./packaging/nix/module.nix ];
        services.openxlr.package = lib.mkDefault
          self.packages.${pkgs.stdenv.hostPlatform.system}.openxlr;
      };
      nixosModules.default = self.nixosModules.openxlr;
    };
}
