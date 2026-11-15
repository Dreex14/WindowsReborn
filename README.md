# Windows Reborn

![Windows Reborn — Mis programas](docs/screenshot.png)

**Windows Reborn** es una aplicación portátil para Windows 10 y 11 que te ayuda a
**preparar el PC antes de formatear**, **recuperar tus archivos, programas y
preferencias después** y, en próximas versiones, aplicar ajustes de privacidad y
personalización **seguros, transparentes y reversibles**.

Guárdala en un disco externo junto a tus copias y úsala cada vez que reinstales Windows.

> Funciona completamente en local: sin telemetría, sin analítica, sin publicidad y sin
> cuentas. Solo se conecta a Internet cuando pulsas un botón que lo necesita (buscar o
> descargar programas).

## Qué hace

### Backup
- Categorías recomendadas (Escritorio, Documentos, Descargas, Imágenes, Vídeos, Música,
  Partidas guardadas) que siguen las redirecciones de OneDrive.
- Carpetas personalizadas y carpetas de **proyectos** (excluye `bin/`, `obj/`,
  `node_modules/`…).
- Configuración de aplicaciones conocidas mediante **listas blancas de archivos**
  (VS Code, Windows Terminal, Git, PowerShell, Notepad++, marcadores de Edge, Chrome y
  Brave). Nunca copia cookies, contraseñas, sesiones ni claves privadas.
- Formatos **ZIP**, **7z** (con cifrado AES-256 si tienes 7-Zip instalado) o **carpeta**.
- Inventario previo, comprobación de espacio, progreso real por bytes, cancelación
  segura, reintentos ante archivos bloqueados, **SHA-256 de cada archivo**, manifiesto,
  informe de omitidos y escritura en temporal: **una copia solo se da por buena si
  supera la verificación completa**.
- Cualquier carpeta del equipo, también escrita con variables (`%APPDATA%\.minecraft`):
  se guarda relativa a la carpeta conocida para adaptarse a otro usuario, con nombre,
  exclusiones propias, estado de selección y destino de restauración.
- La selección se guarda sola, perfiles reutilizables y lista de aplicaciones de WinGet.

### Restauración
- **Asistente por pasos** (se pueden omitir): abrir, verificar, revisar contenido y
  destinos, recuperar archivos, recuperar la configuración, reinstalar programas,
  volver a aplicar tus ajustes de Windows e informe final.
- Muestra fecha, equipo y Windows de origen, contenido e **integridad**.
- Restauración selectiva con **remapeo automático** de carpetas (otro nombre de usuario,
  otra unidad u otra versión de Windows).
- Si un archivo existe: omitir (por defecto), renombrar o sobrescribir (con confirmación).
- Cada archivo restaurado se verifica con su hash; ninguna ruta manipulada del backup
  puede escribir fuera del destino autorizado.
- Las configuraciones de aplicaciones solo se restauran con la aplicación cerrada.
- Accesos a las herramientas oficiales de recuperación de Windows (un backup no es una
  imagen del sistema).

### Mis programas
- Biblioteca permanente con nombre, página oficial, fuente preferida, Id de WinGet,
  arquitectura, tipo de instalador y preferencia de instalación.
- Identificación de la fuente oficial: **API del desarrollador → GitHub Releases →
  WinGet → página oficial**, con niveles de confianza (*verificada*, *elegida por ti*,
  *sin comprobar*). Tu fuente nunca se sustituye sin tu permiso.
- Descargas solo por HTTPS, con **hash publicado** y **firma Authenticode**
  (y firmante esperado). Sin *scraping* genérico.
- Instalación siempre tras confirmación: WinGet en una ventana visible (tú aceptas los
  acuerdos) o el instalador descargado.
- **Preparar para formatear**: guardar solo la lista, o también los instaladores
  verificados (en el backup o en una carpeta de recuperación).

### Configuración portable
- Carpetas seleccionadas, perfiles, biblioteca de programas, preferencias y ajustes de
  Windows elegidos **se incluyen siempre** en cada backup y también se pueden exportar e
  importar como archivo `.wrconfig` (formato versionado, con migración de perfiles
  antiguos).
- Tras formatear, tus carpetas vuelven a aparecer seleccionadas, adaptadas al usuario
  actual.

### Personalización y privacidad
- **Más de 60 ajustes reales** con su estado actual leído del sistema: Explorador
  (extensiones, ocultos, vista compacta, menú contextual clásico, «Este equipo»…), barra
  de tareas, Inicio, apariencia, búsqueda, privacidad (publicidad, diagnósticos,
  sugerencias, Copilot, Recall…), permisos de aplicaciones y opciones avanzadas
  (servicios no esenciales, inicio rápido, rutas largas, aceleración del ratón…).
- Cada ajuste indica su **nivel de riesgo** (seguro, moderado, avanzado), si necesita
  administrador o reiniciar, y la compatibilidad con tu compilación de Windows.
- Antes de cambiar nada se guarda el **valor original**; después se vuelve a leer para
  verificar y, si Windows lo rechaza, se deshace. **Revertir** devuelve el valor real
  que tenías, no un valor «por defecto» supuesto.
- Los cambios moderados y avanzados piden confirmación, muestran los valores exactos
  que se escriben y ofrecen crear un **punto de restauración**.
- **Aplicaciones de inicio** (mismo mecanismo que el Administrador de tareas) y
  **aplicaciones preinstaladas** de una lista cerrada, reinstalables desde la Store.
- No desactiva Defender, SmartScreen, UAC, Windows Update ni el firewall, y no promete
  eliminar toda la telemetría de Windows.

### Preparar para formatear
Comprueba que hay un backup verificado fuera de la unidad de Windows, que incluye tus
carpetas personalizadas, la configuración y la biblioteca de programas, y que se podrá
abrir desde otra instalación (puede copiar `WindowsReborn.exe` junto al backup). Enlaza
la descarga oficial de Windows 11 y [Rufus](https://rufus.ie/). Nunca formatea nada.

## Descarga y uso

1. Descarga `WindowsReborn.exe` de la sección *Releases*.
2. Ejecútalo: no necesita instalación, .NET ni Python.
3. Para el **modo portátil** (todos los datos junto al `.exe`), crea un archivo vacío
   llamado `portable.flag` en la misma carpeta.

Windows SmartScreen puede avisar porque el ejecutable no está firmado digitalmente.
Comprueba el SHA-256 publicado en la *release* antes de ejecutarlo.

La aplicación se muestra en español si la distribución del teclado es española y en
inglés en cualquier otro caso (se puede cambiar en Ajustes).

## Compilar desde el código

Requisitos: [SDK de .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet test
dotnet publish src/WindowsReborn.App -c Release -o dist
```

Las traducciones se generan desde `tools/translations.py` (`python tools/translations.py`).

### Estructura

| Proyecto | Responsabilidad |
|---|---|
| `WindowsReborn.Core` | Rutas seguras, carpetas conocidas, versión de Windows, registro local, configuración portable, idiomas |
| `WindowsReborn.Storage` | Formato del backup, manifiesto, SHA-256, ZIP/7z/carpeta, verificación |
| `WindowsReborn.Backup` | Inventario, exclusiones, perfiles, motor de copia |
| `WindowsReborn.Restore` | Remapeo, conflictos, restauración verificada, informes |
| `WindowsReborn.Programs` | Mis programas, WinGet, GitHub Releases, adaptadores oficiales, descargas verificadas |
| `WindowsReborn.Tweaks` | Catálogo de ajustes (`catalog.json`), motor con diario de valores originales, aplicaciones de inicio y preinstaladas, proceso elevado limitado al catálogo |
| `WindowsReborn.App` | Interfaz WPF (Fluent) |
| `WindowsReborn.Tests` | Pruebas automatizadas (xUnit) |

## Qué hacen las herramientas externas y qué añade Windows Reborn

- **WinGet** (de Microsoft) busca, descarga e instala paquetes y exporta la lista de
  aplicaciones. Windows Reborn lo invoca cuando tú lo pides y añade la biblioteca, la
  verificación de firmas y la integración con los backups.
- **7-Zip** (si ya está instalado) crea y lee archivos 7z cifrados. Windows Reborn no lo
  incluye ni lo descarga.
- Todo lo demás (copia, verificación, manifiesto, restauración, remapeo) es código propio.

## Licencia

Código propio bajo licencia [MIT](LICENSE) — Copyright (c) 2026 Dreex.

Las dependencias tienen sus propias licencias; consulta
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

**Exención de responsabilidad:** el autor no se hace responsable del uso que se haga del
programa ni de los programas o contenidos de terceros que se descarguen o instalen con él.
Haz siempre más de una copia de tus datos importantes.

---

Hecho por **[Dreex](https://github.com/Dreex14)**.
