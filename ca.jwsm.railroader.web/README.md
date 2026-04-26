# ca.jwsm.railroader.web

Browser-based map viewer client.

## Different runtime

This is **not a UMM mod** and **not Harmony-patched**. It's HTML/JS that runs in a browser and talks to `mods/webview` over WebSocket.

It's a top-level peer for **dev convenience only** — built and versioned alongside the game-side projects so changes stay coordinated. Architecturally it sits outside the api ecosystem entirely.

## Why WebSocket instead of bus events

The bus + event-stream model doesn't render smoothly in a browser for moving entities — polling-on-event produces jerky vehicle motion. Streaming over WebSocket gives sub-tick interpolation. This is the documented "narrow break" from the event/stream model, **scoped to the browser-process boundary**.

## Don't apply api conventions here

No `ILoggerFactory`, no `IServiceRegistry`, no `info.json`. This project follows its own conventions appropriate to a browser app. The game-side counterpart is `mods/webview`, which is where api conventions resume.

## Reference

See `..\ARCHITECTURE.md`.
