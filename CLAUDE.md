# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Qué es

App de bandeja para **Windows 11** (C# / .NET 10, WinForms) que vigila la conexión a internet de esta PC, avisa con una notificación y una alarma que se repite cuando se corta, y registra cada corte para generar un reporte HTML con patrones (hora del día, día de la semana, causa). El reporte es evidencia para un reclamo al ISP. El usuario juega en pantalla completa, donde Windows suele silenciar las notificaciones: por eso la alarma sonora es lo principal.

Tiene que consumir lo mínimo en segundo plano (hoy: ~13 MB de memoria privada, ~0,01% de CPU). No agregar dependencias NuGet a la app sin necesidad real.

## Comandos

```bash
dotnet build NetDownReporter.slnx
dotnet test NetDownReporter.slnx
dotnet test NetDownReporter.slnx --filter "FullyQualifiedName~OutageTrackerTests.InterruptClosesOpenOutageWithNote"   # un test
dotnet publish src/NetDownReporter -c Release -o publish    # exe estable (publish/ está en .gitignore)
```

Los releases los arma `.github/workflows/release.yml` al subir un tag `vX.Y.Z`: un zip portable (self-contained, single-file) y un exe chico que necesita el .NET 10 Desktop Runtime. El portable va **sin** `EnableCompressionInSingleFile`: comprimido, la app usa ~50 MB de memoria privada en vez de ~13 MB.

Para probar cambios en la app, primero hay que cerrar la instancia que está corriendo (`Stop-Process -Name NetDownReporter`), porque un mutex deja una sola instancia por usuario. "Iniciar con Windows" guarda la ruta del exe actual en `HKCU\...\Run`, así que hay que activarlo desde `publish\` y no desde `bin\Debug` (al arrancar solo se reapunta si el exe registrado ya no existe). Al activarlo también borra el valor de `StartupApproved\Run` (lo deja el Administrador de tareas al desactivarlo) y el stream `Zone.Identifier` del exe: con la marca de la web, Windows no arranca el exe al iniciar sesión aunque se haya aceptado SmartScreen.

## Arquitectura

- **No es un servicio de Windows a propósito.** Un servicio corre en la sesión 0 y no puede mostrar notificaciones ni reproducir sonido en el escritorio. Es un `ApplicationContext` con `NotifyIcon` (`TrayApp`) que arranca con el usuario.
- **Todo corre en el hilo de UI.** El loop de chequeo (`TrayApp.RunAsync`, `PeriodicTimer` cada 5 s) usa `await`, así que no hay locks. Los eventos de `SystemEvents` llegan en otro hilo y se pasan al hilo de UI con `ui.Send`. El contador `epoch` descarta un chequeo que estaba en vuelo cuando la PC se suspendió.
- **Clasificación por capas** (`NetworkProbe.CheckAsync`): con internet hace un solo ping (1.1.1.1, y 8.8.8.8 si el primero falla). Solo si los dos fallan baja de capa: sin gateway = `Wifi` (la PC perdió el WiFi), el gateway no responde = `Router`, el gateway responde = `Isp`. La causa se guarda junto con todas las causas vistas durante el corte, porque cuando el router se reinicia también se cae el WiFi.
- **`OutageTracker` es lógica pura** (sin E/S), y es donde están los tests importantes. Un corte se confirma recién con 2 fallos seguidos, pero su inicio es la hora del primer fallo; termina con el primer chequeo exitoso. Después de arrancar o de volver de suspensión hay 30 s de gracia en los que no se chequea.
- **Persistencia en texto plano** (`OutageLog`, en `%LOCALAPPDATA%\NetDownReporter\`) para poder abrirla en Excel como evidencia: `cortes.csv` y `sesiones.csv` usan `;` como separador (Excel en español) y están en UTF-8 con BOM. `estado.json` se reescribe cada minuto y cuando empieza un corte. Si existe al arrancar, la app se cerró de golpe: `RecoverFromCrash` cierra la sesión y el corte pendiente con la última hora en que la app estaba viva.
- **`sesiones.csv` registra cuándo estuvo prendido el monitor.** El reporte lo usa para mostrar la cobertura por franja horaria: sin eso, "todos los cortes son a las 21 h" no se distingue de "la PC solo está prendida de noche". Suspender o apagar la PC cierra la sesión y el corte abierto, con una nota.
- **Reporte**: `report.html` es un recurso embebido y autocontenido (JS puro + SVG, sin CDN, porque tiene que abrir sin internet). `Report.Write` reemplaza `/*__DATA__*/null` por el JSON y escribe `reporte.html` en la carpeta de datos. Toda la agregación (filtros, horas, mapa de calor, línea de tiempo) se hace en el JS. Los textos que vienen de los datos (SSID, detalle de netsh) se insertan solo con `textContent`.
- **Reconexión automática del WiFi** (`WifiReconnector` + `Wlan`): Windows a veces no reconecta solo hasta que se abre el panel de WiFi, así que en un corte `Wifi` se repite lo que hace el panel: `WlanScan`, se esperan 4 s y, si sigue sin WiFi, `WlanConnect` con el último perfil conectado. `Wlan` usa la Native WiFi API (`wlanapi.dll`, P/Invoke) en lugar de netsh: no lanza procesos y el nombre del perfil (que puede ser distinto del SSID) viene en UTF-16. El perfil se lee como mucho una vez por minuto mientras hay internet y se guarda en `HKCU\Software\NetDownReporter` (`WifiProfile`), junto con la opción del menú (`AutoReconnect`, activada por defecto). `WifiReconnector` es lógica pura: primer intento al confirmarse el corte, después a los 10 s, 30 s y cada 60 s mientras el chequeo siga dando `Wifi`; deja en la nota del corte si el WiFi volvió tras N intentos o no. La alarma suena igual desde el inicio del corte hasta que vuelve internet. `OutageTracker.Interrupt` y `RecoverFromCrash` agregan su nota a la existente en vez de pisarla.
- **`netsh wlan show interfaces`** se ejecuta una sola vez por corte para guardar SSID, señal y estado como evidencia. Su salida viene traducida y en codificación OEM, así que `ParseWlan` solo reconoce las claves que son iguales en español e inglés o que son ASCII, y la señal por terminar en `%`.
