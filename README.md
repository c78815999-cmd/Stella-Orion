# Stella Orion

Stella Orion is a native Windows Chromium browser shell built with WebView2, not Electron.

## Why WebView2

WebView2 uses the Microsoft Edge Chromium runtime and avoids Electron's application shell. This gives Stella Orion a smaller native Windows surface and keeps the browser closer to a normal Chromium runtime. Google sign-in behavior is still controlled by Google's security policy, so no custom browser can honestly promise permanent compatibility, but this route avoids the Electron-specific browser identity problem.

## Features

- Chromium rendering through Microsoft Edge WebView2
- Multi-tab browser interface
- Private tabs with isolated temporary user-data folders
- Borderless dark native shell with a favicon sidebar
- Animated new-tab speed dial with favicon-only tiles
- User-managed speed dial sites
- HTTPS lock state in the address bar
- Custom accent colors
- Force-dark Chromium page rendering option
- DuckDuckGo address-bar search fallback
- HTTPS-first address resolution
- Tracker blocking with a local host ruleset
- Do Not Track and Global Privacy Control request headers
- Hardened permission defaults for location, camera, microphone, notifications, MIDI, and sensors
- Proxy tunnel profile for HTTP, HTTPS, SOCKS4, and SOCKS5 gateways
- Local SOCKS preset for `127.0.0.1:9050`
- Unpacked Chromium extension loading when the installed WebView2 runtime supports extension APIs
- Download notifications
- One-click browsing-data cleanup

## Build

```powershell
dotnet restore
dotnet build
dotnet run
```

## Tunnel Note

The built-in tunnel setting configures Chromium proxy routing. A real VPN requires a trusted VPN provider and usually a native OS tunnel driver. Stella Orion can route through a provider's HTTP/SOCKS endpoint or a local tunnel such as Tor/Clash/V2Ray when it is already running.
