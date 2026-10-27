{
  description = ".NET env";

  inputs = {
    nixpkgs.url = "github:nixos/nixpkgs?ref=nixos-unstable";
    nuget-packageslock2nix = {
      url = "github:mdarocha/nuget-packageslock2nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs = { nixpkgs, nuget-packageslock2nix, ... }:
  let
    supportedArch = [
      "x86_64-linux"
      "aarch64-linux"
      "x86_64-darwin"
      "aarch64-darwin"
    ];

    globalJson = builtins.fromJSON (builtins.readFile ./global.json);
    version = builtins.splitVersion globalJson.sdk.version;

    major = builtins.elemAt version 0;
    minor = builtins.elemAt version 1;

    forAllArch = f: nixpkgs.lib.genAttrs supportedArch (arch:
      let
        pkgs = nixpkgs.legacyPackages.${arch};
        isDarwin = pkgs.stdenv.hostPlatform.isDarwin;
        stdenv = if isDarwin then pkgs.swiftPackages.stdenv else pkgs.stdenv;
        dotnet-sdk = pkgs.dotnetCorePackages."sdk_${major}_${minor}-bin";
        buildInputs = [
          pkgs.zlib
        ] ++ pkgs.lib.optionals isDarwin [
          pkgs.swiftPackages.swift
          pkgs.darwin.ICU
        ];
      in
      f { inherit pkgs stdenv dotnet-sdk buildInputs; }
    );
  in
  {
    devShells = forAllArch ({ pkgs, stdenv, dotnet-sdk, buildInputs }: {
      default = pkgs.mkShell.override {
        stdenv = stdenv;
      } {
        packages = [ dotnet-sdk ];
        buildInputs = buildInputs;

        DOTNET_ROOT = "${dotnet-sdk.unwrapped}/share/dotnet";
      };
    });

    packages = forAllArch ({ pkgs, stdenv, dotnet-sdk, buildInputs }:
      let
        buildDotnetModule = pkgs.buildDotnetModule.override {
          stdenvNoCC = stdenv;
        };
      in
      {
        default = buildDotnetModule {
          name = "agent-mcp";
          executables = [ "agent-mcp" ];
          projectFile = "AgentMcp/AgentMcp.csproj";
          dotnet-sdk = dotnet-sdk;
          nugetDeps = nuget-packageslock2nix.lib {
            system = stdenv.hostPlatform.system;
            name = "agent-mcp-deps";
            lockfiles = [ ./AgentMcp/packages.lock.json ];
            excludePackages = map (pkg: "${pkg.pname}-${pkg.version}") dotnet-sdk.packages;
          };
          selfContainedBuild = true;
          buildInputs = buildInputs;
          src = pkgs.lib.cleanSource ./.;
          dotnetFlags = [ "-p:MinVerVersionOverride=0.0.0" ];
          dotnetRestoreFlags = [ "-p:RestorePackagesWithLockFile=false" ];
          preConfigure = ''
            rm -f AgentMcp/packages.lock.json
          '';
        };
      }
    );
  };
}
