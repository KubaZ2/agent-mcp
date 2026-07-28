{
  description = ".NET env";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs?ref=nixos-unstable";
  };

  outputs = { nixpkgs, ... }:
  let
    supportedArch = [
      "x86_64-linux"
      "aarch64-linux"
      "x86_64-darwin"
      "aarch64-darwin"
    ];

    forAllArch = nixpkgs.lib.genAttrs supportedArch;
  in
  {
    devShells = forAllArch (arch:
      let
        pkgs = nixpkgs.legacyPackages.${arch};
        lib = pkgs.lib;

        dotnet = pkgs.dotnetCorePackages.sdk_10_0-bin;
        dotnetRoot = "${dotnet.unwrapped}/share/dotnet";
      in
      {
        default = pkgs.mkShell.override {
          stdenv = if pkgs.stdenv.hostPlatform.isDarwin then pkgs.swiftPackages.stdenv else pkgs.stdenv;
        } {
          packages = [
            dotnet
            pkgs.nodejs_26
          ];

          buildInputs = [
            pkgs.zlib
          ] ++ lib.optionals pkgs.stdenv.hostPlatform.isDarwin [
            pkgs.swiftPackages.swift
            pkgs.darwin.ICU
          ];

          DOTNET_ROOT = dotnetRoot;
        };
      }
    );
  };
}
