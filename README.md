# Atlantis

A cross-platform webview app framework using .NET NativeAOT. Runs on macOS, Windows and
Linux (via self-implemented native webview hosts) and iOS (via a small Swift WKWebView
host).

## Requirements

- **.NET 11 SDK** (preview) with NativeAOT support
- **macOS** with Xcode (for iOS development)
- iOS Simulator (included with Xcode)

### iOS NativeAOT Setup

The .NET SDK doesn't officially support iOS NativeAOT yet. You need to patch the SDK:

1. Find your SDK path:
   ```bash
   dotnet --list-sdks
   ```

2. Edit `Microsoft.NETCoreSdk.BundledVersions.props` in the SDK directory and add iOS RIDs to the `ILCompilerRuntimeIdentifiers` for net11.0:
   ```xml
   <ILCompilerRuntimeIdentifiers>...(existing);ios-arm64;iossimulator-arm64;maccatalyst-arm64;maccatalyst-x64</ILCompilerRuntimeIdentifiers>
   ```

## Building

### Desktop
```bash
dotnet run --project samples/HelloWorld
```

### iOS Simulator
```bash
# Build and package
dotnet publish samples/HelloWorld -r iossimulator-arm64
./scripts/package-ios.sh

# Install and launch (requires booted simulator)
./scripts/package-ios.sh install
```

## Testing

### Unit Tests (.NET)
```bash
dotnet test tests/HelloWorld.Tests
```

### UI Tests (iOS Simulator)

XCUITest-based UI tests that verify the app launches and displays content correctly.

**Requirements:**
- Xcode with iOS Simulator
- App must be installed on the simulator first

```bash
# 1. Boot a simulator
open -a Simulator

# 2. Build and install the app
./scripts/package-ios.sh install

# 3. Run UI tests
./tests/Atlantis.iOS.UITests/run-uitests.sh
```

## Project Structure

```
atlantis/
├── src/
│   ├── Atlantis/             # The Atlantis framework (NuGet package "Atlantis.Framework")
│   │   ├── Bridge/           #   AOT-safe JS bridge core
│   │   ├── Host/             #   AtlantisApp webview hosts (macOS/Windows/Linux legs)
│   │   ├── Platforms/iOS/    #   iOS host (P/Invoke into Swift)
│   │   └── native/ios/       #   Swift WKWebView entry point
│   ├── Atlantis.Analyzers/   # Roslyn analyzers/code fixes (powers 'atl fix')
│   └── Atlantis.Cli/         # 'atl' CLI: scaffolding, bindgen, build
├── samples/
│   └── HelloWorld/           # Sample app (iOS + Desktop)
├── tests/
│   ├── HelloWorld.Tests/     # .NET unit tests
│   └── Atlantis.iOS.UITests/ # XCUITest UI tests (Swift/Xcode)
├── scripts/
│   └── package-ios.sh        # iOS app bundling script
└── artifacts/                # Build outputs
```

## Architecture

Apps reference a single `Atlantis.Framework` package and call `AtlantisApp.Run(...)`, which owns
the native webview so app code never touches a platform webview type. The concrete host
is selected at build time per `RuntimeIdentifier`:

- **macOS**: a `WKWebView` host implemented entirely in managed C#, driven through the
  Objective-C runtime (`libobjc`). Classes are resolved by name at runtime, so there is
  no link-time framework dependency and no native binary to ship.
- **Windows**: a Win32 + `WebView2` (Edge Chromium) host. WebView2 is reached through
  manual COM (vtable function pointers for calls in, hand-built vtables with
  `[UnmanagedCallersOnly]` thunks for callbacks out) since NativeAOT has no built-in COM
  marshaller. The Edge WebView2 runtime ships with Windows; the package only vendors the
  small `WebView2Loader.dll` shim under `runtimes/win-*/native`.
- **Linux**: a GTK 3 + WebKitGTK host over plain C P/Invoke. GTK/WebKitGTK are
  distro-provided system libraries, so no native binary is shipped; library sonames are
  resolved across distros (WebKitGTK 4.1 then 4.0) at runtime.
- **iOS**: a pure C# WKWebView host driven through a small Swift entry point.

The webview is an implementation detail, not part of the public API, and the `Atlantis`
package has **no external NuGet dependencies**; its only native payload is the Windows
`WebView2Loader.dll` shim. It is otherwise a single managed assembly:

- `lib/<tfm>/Atlantis.dll` — the desktop assembly (macOS, Windows and Linux hosts). Each
  host binds its native libraries lazily, so the single assembly links cleanly on any
  desktop OS and also doubles as the iOS NativeAOT fallback.
- `runtimes/win-{x64,arm64}/native/WebView2Loader.dll` — the WebView2 loader shim.
- `runtimes/ios*/lib/<tfm>/Atlantis.dll` — the iOS leg, packed only when built with
  `-p:IncludeiOSLeg=true` on a machine with the iOS NativeAOT toolchain.

The native webview wiring (the `window.external` bridge and message plumbing for all
three desktop hosts) is translated from
[Photino](https://github.com/tryphotino/photino.Native)'s MIT-licensed source.

> **Note:** the iOS implementation leg requires the patched iOS NativeAOT SDK (see above)
> and is not yet produced by CI; the published package currently validates the macOS leg.

## Documentation

- [JavaScript Bridge Architecture](docs/js-bridge.md) — How C#/JS interop works