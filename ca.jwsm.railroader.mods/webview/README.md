# mods/webview

In-game WebSocket server. The companion to top-level `web` (the browser client).

## The narrow break from event/stream

This mod is the documented exception to the "everything is bus + streams" model. Vehicle motion in a browser doesn't render smoothly when fed by event ticks — it needs continuous interpolated streaming. So this mod publishes a WebSocket channel that the browser client consumes directly.

The break is **scoped to the browser-process boundary**. In-process consumers still use the bus and streams. Only `web` consumes the WebSocket.

## Mod roles

- **Consumer** — subscribes to physics streams + bus events.
- **Service provider** — implements `IWebChannel` (or similar) for any in-process mod that wants to push state out to the browser client.

## Depends on

- Physics streams (from `physics`).
- Bus events (from api).

## Companion project

`..\..\ca.jwsm.railroader.web\` — the browser-side counterpart that consumes this mod's WebSocket.
