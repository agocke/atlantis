# JavaScript Bridge Architecture

## Overview

Atlantis provides a hybrid architecture where C# code can run in two places:

- **Native Host** (NativeAOT): Full OS access — file system, networking, hardware
- **WebView WASM** (NativeAOT-LLVM): Fast, synchronous calls from JavaScript

The framework automatically routes each method to the optimal execution path.

## The Core Idea

```csharp
[JSExport]  // = "JavaScript can call this"
```

That's it. Mark methods with `[JSExport]` to expose them to JavaScript. The framework analyzes each method and decides the best way to execute it:

| Method uses... | Runs in... | From JS... |
|----------------|------------|------------|
| Pure computation only | WASM (webview) | Sync |
| System.IO, networking, etc. | Native host (IPC) | Async |

## Example

```csharp
using System.Runtime.InteropServices.JavaScript;

public static partial class Api
{
    [JSExport]  // → WASM (pure, no OS deps)
    public static bool ValidateEmail(string email) 
        => email.Contains('@') && email.Contains('.');
    
    [JSExport]  // → IPC (uses System.IO)
    public static string[] ListFiles(string path) 
        => Directory.GetFiles(path);
    
    // No attribute → not callable from JS
    internal static void HelperMethod() { }
}
```

```javascript
// Sync — runs in WASM, ~nanoseconds
const valid = atlantis.Api.ValidateEmail("test@example.com");

// Async — goes through IPC to native host
const files = await atlantis.Api.ListFiles("/documents");
```

## Routing Decision Logic

```
┌─────────────────────────────────────────────────────────────┐
│  [JSExport] method                                          │
└─────────────────────────────┬───────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│  Does it use OS-dependent APIs?                             │
│  (System.IO, System.Net, System.Diagnostics.Process, etc.)  │
└──────────────┬──────────────────────────────┬───────────────┘
               │ No                           │ Yes
               ▼                              ▼
┌──────────────────────────┐    ┌──────────────────────────────┐
│  Compile to WASM         │    │  Generate IPC handler        │
│  • Sync calls from JS    │    │  • Async calls from JS       │
│  • Runs in webview       │    │  • Runs on native host       │
│  • ~nanosecond latency   │    │  • Full OS access            │
└──────────────────────────┘    └──────────────────────────────┘
```

### WASM-Compatible (runs in webview)

- Primitive operations, math, string manipulation
- LINQ, collection operations
- JSON serialization (System.Text.Json)
- Regular expressions
- Custom pure logic

### Requires IPC (runs on host)

- `System.IO` — File, Directory, Path operations
- `System.Net` — HttpClient, sockets
- `System.Diagnostics` — Process, debugging
- `System.Environment` — Environment variables
- Platform-specific APIs — Clipboard, notifications, window management

## Overriding the Default

```csharp
[JSExport(ForceIPC = true)]   // Always use IPC, even if WASM-compatible
public static decimal Calculate(string data) => ...;

[JSExport(ForceWasm = true)]  // Must run in WASM (build error if incompatible)
public static int FastHash(string input) => ...;
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Native Host Process (NativeAOT)                                            │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  • Window management (Photino / WKWebView)                            │  │
│  │  • IPC message dispatcher                                             │  │
│  │  • [JSExport] methods that use OS APIs                                │  │
│  └─────────────────────────────────────┬─────────────────────────────────┘  │
└────────────────────────────────────────┼────────────────────────────────────┘
                                         │ IPC: MessagePack over postMessage
┌────────────────────────────────────────▼────────────────────────────────────┐
│  WebView                                                                     │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  JavaScript                                                           │  │
│  │  • Auto-generated bridge (atlantis.*)                                 │  │
│  │  • Routes sync calls → WASM, async calls → IPC                        │  │
│  └─────────────────────────────────────┬─────────────────────────────────┘  │
│                                        │ JSImport/JSExport                  │
│  ┌─────────────────────────────────────▼─────────────────────────────────┐  │
│  │  WASM Module (NativeAOT-LLVM)                                         │  │
│  │  • [JSExport] methods that are pure / WASM-compatible                 │  │
│  │  • Same C# code, compiled to WebAssembly                              │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Serialization

Communication between WASM and the native host uses [MessagePack](https://msgpack.org/) via Serde.MsgPack for efficient binary serialization:

- ~50% smaller than JSON
- 2-4x faster to serialize/deserialize
- NativeAOT-compatible (source-generated)

## TypeScript Support

The build generates TypeScript definitions for all exported methods:

```typescript
// Generated: atlantis.d.ts
declare namespace atlantis {
  namespace Api {
    function ValidateEmail(email: string): boolean;           // sync
    function ListFiles(path: string): Promise<string[]>;      // async
  }
}
```

## Implementation Status

- [ ] Phase 1: IPC bridge with MessagePack
- [ ] Phase 2: WASM compilation and routing
- [ ] Phase 3: Analyzer for routing decisions
- [ ] Phase 4: SDK for single-project experience
