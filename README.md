# NetDown Reporter

App de bandeja para **Windows 11** que vigila la conexión a internet, hace sonar una alarma cuando se corta y registra cada corte para armar un reporte con patrones (hora del día, día de la semana, causa). La idea es juntar evidencia para reclamarle al proveedor de internet.

- **Alarma que se repite** hasta que vuelve internet, además de la notificación: sirve también cuando estás en un juego en pantalla completa, donde Windows silencia las notificaciones.
- **Distingue la causa** de cada corte:
  - **Sin WiFi**: la PC perdió la conexión al WiFi.
  - **Router**: hay WiFi pero el router no responde.
  - **ISP**: el router responde pero no hay salida a internet.
- **Reconecta el WiFi sola** cuando Windows no lo hace hasta que abrís el panel de WiFi. Se puede desactivar desde el menú.
- **Reporte HTML** sin dependencias externas: abre sin internet, con filtros, mapa de calor y línea de tiempo. También tiene en cuenta en qué horarios estuvo prendida la PC, para que "todos los cortes son de noche" no sea solo "la PC solo está prendida de noche".
- **Liviana**: unos 13 MB de memoria y ~0,01% de CPU en segundo plano.

## Descarga

No se instala: es un solo `.exe`. En [Releases](../../releases) hay dos versiones:

| Archivo | Descarga | Requisito |
|---|---|---|
| `NetDownReporter-x.y.z-portable-win-x64.zip` | ~45 MB (el exe ocupa ~110 MB) | Ninguno. Trae .NET adentro. Descomprimilo y listo. |
| `NetDownReporter-x.y.z-net10-win-x64.exe` | ~250 KB | Tener instalado el [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). |

Las dos usan lo mismo de memoria. Si no sabés cuál bajar, usá la **portable**.

El exe no está firmado, así que la primera vez Windows SmartScreen puede mostrar "Windows protegió su PC": tocá **Más información → Ejecutar de todas formas**.

## Uso

1. Poné el `.exe` en una carpeta fija (por ejemplo `C:\Programas\NetDownReporter\`) y abrilo. Aparece un punto en la bandeja: verde con internet, rojo sin internet, gris en pausa.
2. Clic derecho en el ícono:
   - **Abrir reporte**: genera y abre el reporte HTML (también con doble clic).
   - **Silenciar alarma**: corta el sonido del corte en curso.
   - **Reconectar WiFi automáticamente**: activado por defecto.
   - **Iniciar con Windows**: guarda la ruta actual del exe; si lo movés de carpeta, volvé a activarlo.
3. Para desinstalar: desactivá "Iniciar con Windows", cerrá la app y borrá el exe y la carpeta de datos.

### Dónde guarda los datos

En `%LOCALAPPDATA%\NetDownReporter\`, en texto plano para poder abrirlos en Excel:

- `cortes.csv`: un renglón por corte (inicio, fin, duración, causa, nota, detalle del WiFi).
- `sesiones.csv`: cuándo estuvo corriendo el monitor.
- `reporte.html`: el último reporte generado.

Las opciones del menú se guardan en `HKCU\Software\NetDownReporter`.

## Compilar

Requiere el [SDK de .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build NetDownReporter.slnx
dotnet test NetDownReporter.slnx
dotnet publish src/NetDownReporter -c Release -o publish
```

Versión portable (un solo exe con el runtime adentro):

```bash
dotnet publish src/NetDownReporter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-portable
```

No se usa `EnableCompressionInSingleFile`: el exe queda a la mitad, pero al descomprimirse en memoria la app pasa de ~13 MB a ~50 MB de RAM.

Hay una sola instancia por usuario: para probar cambios, primero cerrá la que está corriendo.

Los releases los arma GitHub Actions ([release.yml](.github/workflows/release.yml)) al subir un tag `vX.Y.Z`.

## Contribuir

Se aceptan issues y pull requests. [CLAUDE.md](CLAUDE.md) explica la arquitectura y las decisiones de diseño, por ejemplo por qué no es un servicio de Windows o cómo se clasifican los cortes. Dos reglas:

- Que siga consumiendo lo mínimo en segundo plano.
- No agregar dependencias NuGet a la app sin necesidad real.

## Licencia

[MIT](LICENSE)
