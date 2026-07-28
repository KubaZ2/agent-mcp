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

        globalJson = builtins.fromJSON (builtins.readFile ./global.json);
        version = builtins.splitVersion globalJson.sdk.version;

        major = builtins.elemAt version 0;
        minor = builtins.elemAt version 1;

        dotnet = pkgs.dotnetCorePackages."sdk_${major}_${minor}-bin";
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
          ] ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isDarwin [
            pkgs.swiftPackages.swift
            pkgs.darwin.ICU
          ];

          DOTNET_ROOT = dotnetRoot;

          NIX_LDFLAGS = pkgs.lib.optionalString pkgs.stdenv.hostPlatform.isDarwin "-L${pkgs.swiftPackages.swift-unwrapped.lib}/lib/swift/macosx";
        };
      }
    );
  };
}
