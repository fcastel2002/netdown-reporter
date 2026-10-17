# NetDown Reporter

**English** | [Español](README.es.md)

A **Windows 11** tray app that watches your internet connection, sounds an alarm when it drops and logs every outage to build a report with patterns (time of day, day of the week, cause). The goal is to collect evidence for a complaint to your internet provider.

- **Repeating alarm** until the connection is back, on top of the notification. It also works while you're in a fullscreen game, where Windows silences notifications.
- **Tells the cause** of each outage apart:
  - **No WiFi**: the PC lost its WiFi connection.
  - **Router**: WiFi is up but the router doesn't respond.
  - **ISP**: the router responds but there's no way out to the internet.
- **Reconnects WiFi on its own** when Windows won't until you open the WiFi panel. It can be turned off from the menu.
- **HTML report** with no external dependencies: it opens offline, with filters, a heatmap and a timeline. It also accounts for when the PC was actually on, so "all outages happen at night" isn't just "the PC is only on at night".
- **Windows' own WiFi disconnect log** in the report: it reads the WLAN-AutoConfig event log when the report is built, so it also shows disconnects from before the app was installed or while it was closed, with the interval between them to reveal periodic drops (for example, every 4 hours).
- **Lightweight**: about 13 MB of memory and ~0.01% CPU in the background.

> The app's interface, the report and the data files are in Spanish.

## Download

There's nothing to install: it's a single `.exe`. [Releases](../../releases) has two versions:

| File | Download | Requirement |
|---|---|---|
| `NetDownReporter-x.y.z-portable-win-x64.zip` | ~45 MB (the exe is ~110 MB) | None. .NET is bundled inside. Unzip it and run it. |
| `NetDownReporter-x.y.z-net10-win-x64.exe` | ~250 KB | The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) installed. |

Both use the same amount of memory. If you're not sure which one to get, use the **portable** one.

The exe isn't signed, so the first time Windows SmartScreen may show "Windows protected your PC": click **More info → Run anyway**.

## Usage

1. Put the `.exe` in a permanent folder (for example `C:\Programs\NetDownReporter\`) and run it. A dot shows up in the tray: green when online, red when offline, gray when paused.
2. Right-click the icon:
   - **Abrir reporte** (open report): builds and opens the HTML report (double-clicking the icon does the same).
   - **Silenciar alarma** (silence alarm): stops the sound for the current outage.
   - **Reconectar WiFi automáticamente** (reconnect WiFi automatically): on by default.
   - **Iniciar con Windows** (start with Windows): saves the exe's current path. If you move it or download a new version, it updates itself the next time you open it by hand.
3. To uninstall: turn off "Iniciar con Windows", exit the app and delete the exe and the data folder.

### Where the data goes

In `%LOCALAPPDATA%\NetDownReporter\`, as plain text so it can be opened in Excel:

- `cortes.csv`: one row per outage (start, end, duration, cause, note, WiFi details).
- `sesiones.csv`: when the monitor was running.
- `reporte.html`: the last generated report.

The CSV files use `;` as the separator, so they open correctly in Excel with Spanish regional settings.

Menu options are stored in `HKCU\Software\NetDownReporter`.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build NetDownReporter.slnx
dotnet test NetDownReporter.slnx
dotnet publish src/NetDownReporter -c Release -o publish
```

Portable version (a single exe with the runtime inside):

```bash
dotnet publish src/NetDownReporter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-portable
```

`EnableCompressionInSingleFile` isn't used on purpose: it halves the exe, but since it's decompressed into memory the app goes from ~13 MB to ~50 MB of RAM.

Only one instance runs per user: to try changes, close the running one first.

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a `vX.Y.Z` tag is pushed.

## Contributing

Issues and pull requests are welcome. [CLAUDE.md](CLAUDE.md) (in Spanish) explains the architecture and design decisions, such as why it isn't a Windows service or how outages are classified. Two rules:

- Keep it as light as possible in the background.
- Don't add NuGet dependencies to the app unless they're really needed.

## License

[MIT](LICENSE)
