# JavaScript Bridge Architecture

## Overview

Atlantis provides a bridge between JavaScript (in the webview) and C# (native host). Methods marked with `[JSExport]` become callable from JavaScript.

## Phase 1: IPC Bridge (Current Focus)

All `[JSExport]` methods run on the native host with full .NET API access. Calls from JavaScript are asynchronous.

```csharp
using System.Runtime.InteropServices.JavaScript;

public static partial class Api
{
    [JSExport]
    public static bool ValidateEmail(string email) 
        => email.Contains('@') && email.Contains('.');
    
    [JSExport]
    public static string[] ListFiles(string path) 
        => Directory.GetFiles(path);
}
```

```javascript
// All calls are async (IPC to native host)
const valid = await atlantis.Api.ValidateEmail("test@example.com");
const files = await atlantis.Api.ListFiles("/documents");
```

### Benefits
- **Simple** — One execution model, predictable behavior
- **Full API access** — System.IO, networking, everything works
- **Easy debugging** — All code runs in one place

## Phase 2: WASM Head (Opt-in, for Performance)

When you hit performance bottlenecks (e.g., validation called on every keystroke), add a separate WASM project for latency-sensitive code.

```
src/
├── MyApp/                  # Host (full .NET APIs, IPC)
│   └── Api.cs
│
└── MyApp.Wasm/             # WASM head (browser-wasm target)
    └── FastApi.cs          # Runs in webview, sync from JS
```

```csharp
// MyApp.Wasm/FastApi.cs
// Only WASM-compatible APIs available — compiler enforces this
[JSExport]
public static bool ValidateEmail(string email) 
    => email.Contains('@') && email.Contains('.');
```

```javascript
// Explicit choice: sync (WASM) vs async (IPC)
const valid = atlantis.FastApi.ValidateEmail(input);      // sync, ~ns
const files = await atlantis.Api.ListFiles('/docs');       // async, ~ms
```

### When to Add WASM

| Symptom | Solution |
|---------|----------|
| Input validation feels laggy | Move to WASM |
| Formatting called in render loop | Move to WASM |
| Pure computation on every frame | Move to WASM |
| Need file/network access | Keep on host |

## Architecture

### Phase 1: IPC Only

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Native Host Process (NativeAOT)                                            │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  • Window management (Photino / WKWebView)                            │  │
│  │  • IPC message dispatcher                                             │  │
│  │  • All [JSExport] methods                                             │  │
│  │  • Full .NET API access                                               │  │
│  └─────────────────────────────────────┬─────────────────────────────────┘  │
└────────────────────────────────────────┼────────────────────────────────────┘
                                         │ IPC: MessagePack over postMessage
┌────────────────────────────────────────▼────────────────────────────────────┐
│  WebView                                                                     │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  JavaScript                                                           │  │
│  │  • Auto-generated bridge (atlantis.*)                                 │  │
│  │  • All calls async                                                    │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Phase 2: With WASM Head (opt-in)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Native Host Process (NativeAOT)                                            │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  • [JSExport] methods from MyApp (IPC)                                │  │
│  │  • Full .NET API access                                               │  │
│  └─────────────────────────────────────┬─────────────────────────────────┘  │
└────────────────────────────────────────┼────────────────────────────────────┘
                                         │ IPC (async)
┌────────────────────────────────────────▼────────────────────────────────────┐
│  WebView                                                                     │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  JavaScript                                                           │  │
│  │  • atlantis.Api.* → IPC (async)                                       │  │
│  │  • atlantis.FastApi.* → WASM (sync)                                   │  │
│  └─────────────────────────────────────┬─────────────────────────────────┘  │
│                                        │ JSExport (sync)                    │
│  ┌─────────────────────────────────────▼─────────────────────────────────┐  │
│  │  WASM Module (NativeAOT-LLVM)                                         │  │
│  │  • [JSExport] methods from MyApp.Wasm                                 │  │
│  │  • WASM-compatible APIs only                                          │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Serialization

Communication uses [MessagePack](https://msgpack.org/) via Serde.MsgPack:

- ~50% smaller than JSON
- 2-4x faster to serialize/deserialize
- NativeAOT-compatible (source-generated)

## TypeScript Support

The build generates TypeScript definitions:

```typescript
// Generated: atlantis.d.ts
declare namespace atlantis {
  namespace Api {
    function ValidateEmail(email: string): Promise<boolean>;
    function ListFiles(path: string): Promise<string[]>;
  }
  
  // If WASM head is added:
  namespace FastApi {
    function ValidateEmail(email: string): boolean;  // sync!
  }
}
```

## Implementation Status

- [ ] Phase 1: IPC bridge — `[JSExport]` methods callable via async IPC
- [ ] Phase 2: WASM head — Opt-in separate project for sync calls
- [ ] Phase 3: Shared types — Common DTOs between host and WASM
