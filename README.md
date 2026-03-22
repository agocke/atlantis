# Atlantis

A cross-platform webview app using .NET NativeAOT. Runs on desktop (via Photino) and iOS (via pure C# Objective-C runtime bindings).

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
dotnet build src/Atlantis
dotnet run --project src/Atlantis
```

### iOS Simulator
```bash
# Build and package
dotnet publish src/Atlantis -r iossimulator-arm64
./scripts/package-ios.sh

# Install and launch (requires booted simulator)
./scripts/package-ios.sh install
```

## Testing

### Unit Tests (.NET)
```bash
dotnet test tests/Atlantis.Tests
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
│   └── Atlantis/              # Main app (iOS + Desktop)
├── tests/
│   ├── Atlantis.Tests/        # .NET unit tests
│   └── Atlantis.iOS.UITests/  # XCUITest UI tests (Swift/Xcode)
├── scripts/
│   └── package-ios.sh         # iOS app bundling script
└── artifacts/                 # Build outputs
```

## Architecture

The app uses conditional compilation (`#if IOS`) to share code between platforms:

- **Desktop**: Uses [Photino.NET](https://github.com/nicegui/photino.NET) for native webview
- **iOS**: Pure C# implementation using `objc_msgSend` P/Invoke calls to create UIWindow, UIViewController, and WKWebView directly through the Objective-C runtime

## Documentation

- [JavaScript Bridge Architecture](docs/js-bridge.md) — How C#/JS interop works