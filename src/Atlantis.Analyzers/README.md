# Atlantis.Analyzers

Roslyn analyzers and code fixes for [Atlantis](https://github.com/atlantis-framework/atlantis) projects.

## Installation

```xml
<PackageReference Include="Atlantis.Analyzers" Version="0.1.0" PrivateAssets="all" />
```

## Rules

| ID | Severity | Description |
|----|----------|-------------|
| ATL001 | Warning | `[JSExport]` is deprecated. Use `[AtlExport]` instead. |
| ATL002 | Error | `[AtlExport]` requires the `AtlExportAttribute` class to be defined. |
| ATL003 | Info | `System.Runtime.InteropServices.JavaScript` using is not needed for Atlantis. |

## Usage

Once installed, the analyzers will automatically run during build and in your IDE. Use the lightbulb/quick fix menu to apply suggested fixes.
