# Windows Reborn — Documento de diseño

Versión del documento: 0.1 (noviembre de 2026). Documento histórico de la versión 0.2; la 1.0 rediseñó la interfaz (ver README).
Estado: fases 0, 1 y 1b implementadas (copia de seguridad, restauración, configuración portable y
«Mis programas»), con 54 pruebas automatizadas. Pendiente de validar en máquina virtual:
formato 7z/cifrado (no había 7-Zip en el equipo de desarrollo), instalaciones reales y
restauración tras un formateo real. Fases 2-5 sin empezar.

Este documento recoge, en el orden pedido, la evaluación técnica, el análisis de
referencias, la lista de funciones seguras, la arquitectura, la propuesta visual y
el plan por fases. Es un documento vivo: cada ajuste nuevo de Windows debe
añadirse primero aquí (tabla 3) antes de implementarse.

---

## 1. Evaluación técnica y elección del lenguaje

### Criterios

| Criterio | Peso |
|---|---|
| Integración nativa con Windows (API Win32, Registro, Shell, Known Folders, VSS, UAC) | Alto |
| Fiabilidad de E/S de archivos (rutas largas, Unicode, bloqueos, ZIP64, hashes) | Alto |
| Ejecutable portátil sin dependencias en el equipo de destino | Alto |
| Apariencia Fluent / Windows 11 y tema oscuro | Medio-alto |
| Mantenimiento a largo plazo y pruebas automatizadas | Medio-alto |
| Tamaño del ejecutable | Bajo |

### Opciones evaluadas

| Opción | A favor | En contra | Veredicto |
|---|---|---|---|
| **Python + PySide6** | Desarrollo rápido; Qt es maduro. | Empaquetado (PyInstaller/Nuitka) frágil y con muchos falsos positivos de antivirus; acceso a Win32 vía `ctypes`/`pywin32`; LGPL de Qt obliga a cuidar el enlazado; aspecto Fluent hecho a mano. | Descartada. |
| **C# + WinUI 3 (Windows App SDK)** | El aspecto más nativo de Windows 11. | El modo *unpackaged self-contained* sigue siendo delicado (bootstrapper, runtime del App SDK), no hay *single-file* real, más fricción en pruebas de UI y peor soporte en Windows 10 antiguos. | Descartada por ahora. |
| **C# + WPF (.NET 10 LTS) + WPF-UI** | API Win32 y .NET completas; `System.IO.Compression` con ZIP64; `SHA256` nativo; publicación *self-contained single-file* en un único `.exe` portátil; WPF es estable desde hace 18 años y Microsoft lo sigue manteniendo; la biblioteca **WPF-UI** (MIT) aporta controles Fluent, Mica y navegación lateral; funciona igual en Windows 10 y 11. | Ejecutable de ~70-90 MB (aceptable para una herramienta en disco externo). | **Elegida.** |
| Electron / Tauri | UI web flexible. | Peso, superficie de ataque y acceso a Win32 indirecto; nada que ganar aquí. | Descartada. |

### Decisión

**C# 14 sobre .NET 10 (LTS, soporte hasta noviembre de 2028), interfaz WPF con
[WPF-UI](https://github.com/lepoco/wpfui) (MIT), patrón MVVM con
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT).**
Se publica como `WindowsReborn.exe` *self-contained single-file* x64 (y opcionalmente
ARM64): no requiere tener .NET ni Python instalados.

Notas:

- El tema Fluent integrado de WPF (`ThemeMode`, .NET 9+) sigue marcado por Microsoft
  como «en progreso» en .NET 10, por lo que se usa WPF-UI, que es estable.
- La lógica no depende de WPF: si en el futuro WinUI 3 madura para portables, solo
  cambiaría el proyecto de interfaz.

---

## 2. Análisis de referencias

| Referencia | Licencia | Qué aporta | Qué tomamos (ideas, no código) | Qué evitamos |
|---|---|---|---|---|
| [Chris Titus Tech WinUtil](https://github.com/ChrisTitusTech/winutil) | MIT | Catálogo de tweaks en JSON (clave, valor original, valor nuevo), instalación de apps con WinGet/Chocolatey, botón *Undo*. | Ajustes declarativos con valor de origen y destino; agrupación por categorías; integración con WinGet. | Se lanza con `irm … \| iex` (script remoto); el *undo* devuelve valores **por defecto supuestos**, no los del equipo; incluye ajustes agresivos (desactivar servicios, telemetría vía tareas programadas, eliminar Edge). |
| [FluentTweaker (builtbybel)](https://github.com/builtbybel/FluentTweaker) | **Sin licencia declarada** en GitHub (todos los derechos reservados) | Plugins por ajuste con *check / do / undo* separados; interfaz con aspecto Windows 11. | El modelo «detectar → aplicar → revertir» por ajuste independiente. | No se puede reutilizar su código; varios plugins usan claves no documentadas. |
| [O&O ShutUp10++](https://www.oo-software.com/en/shutup10) | Freeware propietario | Clasificación de cada ajuste en «recomendado / limitado / no recomendado»; punto de restauración antes de aplicar; exportar/importar configuración. | Semáforo de riesgo por ajuste; exportar perfiles; ofrecer punto de restauración. | Muchas opciones son directivas de grupo HKLM que muestran «administrado por la organización» y no son evidentes de revertir. |
| [Winaero Tweaker](https://winaerotweaker.com/) | Freeware propietario | Cientos de ajustes con «restaurar valores predeterminados» y exportar/importar. | Búsqueda de ajustes; exportación de perfil. | Gran parte son claves no documentadas o efectos visuales sin respaldo oficial. |
| [Windows Package Manager](https://learn.microsoft.com/windows/package-manager/) | MIT (cliente) | `winget export --include-versions` y `winget import`. Solo exporta apps que logra emparejar con una fuente configurada. | Exportar/importar listado de apps; mostrar las no emparejadas como «instalación manual». | Aceptar acuerdos de origen o de paquete sin que el usuario lo vea. |
| [Robocopy](https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy) | Componente de Windows | `/B` (modo copia de seguridad con privilegios), reintentos `/R /W`, `/XJ` (excluir uniones), códigos de salida por bits. | Reintentos acotados ante bloqueos; excluir uniones/puntos de reanálisis para no entrar en bucles; códigos de resultado granulares. | No se invoca: la copia se hace en código propio para tener progreso real, hashes y manifiesto. |
| [7-Zip](https://www.7-zip.org/) | LGPL 2.1+ (+ BSD y restricción unRAR en `7z.dll`) | Formato 7z y cifrado AES-256 consolidado. | Usar `7z.exe` **si ya está instalado** (o el usuario indica su ruta) para el formato 7z y el cifrado. | No se incluye en el ejecutable, para no arrastrar obligaciones LGPL; nunca se descarga automáticamente. |

**Conclusiones del análisis:**

1. El mayor defecto común es revertir a «valores por defecto» en lugar de a los
   valores reales previos. Windows Reborn guarda el valor original (o su ausencia)
   en un diario antes de cada cambio.
2. Ninguna herramienta trata copia de seguridad + restauración con verificación por
   hash; es el hueco que cubre este proyecto.
3. Los ajustes se definen como datos, pero la **acción** se implementa con
   operaciones tipadas (Registro, API), nunca con scripts arbitrarios.

---

## 3. Funcionalidades seguras (tabla inicial)

Leyenda de método: **API** = API oficial de Windows; **Reg-doc** = valor de Registro
que coincide con un interruptor visible de Configuración y está documentado o
ampliamente verificado; **Reg-exp** = valor no documentado (experimental, desactivado
por defecto); **Enlace** = solo abre la página oficial `ms-settings:`.
W10 = Windows 10 22H2; W11 = Windows 11 23H2/24H2/25H2.

### 3.1 Copia de seguridad y restauración

| Función | Método | Riesgos conocidos | Compat. | Admin |
|---|---|---|---|---|
| Carpetas conocidas (Escritorio, Documentos, Descargas, Imágenes, Vídeos, Música, Partidas guardadas) | `SHGetKnownFolderPath` (sigue redirecciones de OneDrive) | Archivos «solo en la nube» de OneDrive: se detectan por atributo `RECALL_ON_DATA_ACCESS` y se omiten para no descargar GB sin aviso. | W10, W11 | No |
| Carpetas personalizadas y proyectos de programación | Selección libre + exclusiones (`node_modules`, `bin`, `obj`, `.venv`, `target`…) | Repos grandes; se avisa del tamaño. | W10, W11 | No |
| Partidas guardadas | `FOLDERID_SavedGames`, `Documents\My Games` | Juegos con guardado en la nube o en rutas propias: se listan como no cubiertos. | W10, W11 | No |
| Configuración de apps (VS Code, Windows Terminal, Git, PowerShell, Notepad++…) | Adaptadores con lista blanca de archivos | Rutas de paquetes MSIX cambian; `.gitconfig` puede tener *helpers* de credenciales (se avisa). Nunca `.ssh` por defecto. | W10, W11 | No |
| Marcadores de navegadores | Chromium (Edge, Chrome, Brave): archivo `Bookmarks` (JSON sin secretos). Firefox: copias `bookmarkbackups/*.jsonlz4`. | Nunca `Cookies`, `Login Data`, `Local State`, `Web Data`. Restaurar con el navegador cerrado. | W10, W11 | No |
| Listado de aplicaciones | `winget export` + inventario de *Uninstall* (HKLM/HKCU, solo lectura) | WinGet no empareja todo; licencias y configuraciones no viajan. | W10 1809+, W11 | No |
| Compresión ZIP | `System.IO.Compression` (ZIP64, Deflate) | — | Todas | No |
| Compresión 7z y cifrado | `7z.exe` instalado (AES-256, cabeceras cifradas `-mhe=on`) | Depende de 7-Zip presente; contraseña olvidada = datos perdidos. | Todas | No |
| Verificación | SHA-256 por archivo en `manifest.json`; relectura completa tras escribir | — | Todas | No |
| Archivos bloqueados | Reintentos acotados; omitir y registrar | VSS (instantáneas) requiere admin: fase posterior y opcional. | Todas | No/Sí |
| Rutas largas y Unicode | .NET 10 + `longPathAware` en el manifiesto | — | W10 1607+, W11 | No |
| Restauración selectiva con remapeo de usuario/unidad | Tokens de carpeta conocida en el manifiesto (`{Documents}`…) | — | Todas | No |
| Protección *zip slip* | Normalizar y comprobar que la ruta final queda dentro del destino autorizado | — | Todas | No |
| Punto de restauración | WMI `SystemRestore.CreateRestorePoint` | Requiere admin y Protección del sistema activa; Windows limita a uno cada 24 h. | W10, W11 | Sí |
| Herramientas oficiales de recuperación | Enlaces a `ms-settings:recovery`, `ms-settings:backup`, `rstrui.exe` | — | W10, W11 | No |

### 3.2 Privacidad (fase 3)

| Ajuste | Método | Riesgos / límites | Compat. | Admin |
|---|---|---|---|---|
| ID de publicidad | Reg-doc `HKCU\…\AdvertisingInfo\Enabled=0` (= interruptor de Configuración) | Ninguno relevante. | W10, W11 | No |
| Experiencias personalizadas con datos de diagnóstico | Reg-doc `HKCU\…\Privacy\TailoredExperiencesWithDiagnosticDataEnabled=0` | — | W10, W11 | No |
| Sitios web con acceso a mi lista de idiomas | Reg-doc `HKCU\Control Panel\International\User Profile\HttpAcceptLanguageOptOut=1` | — | W10, W11 | No |
| Seguimiento de apps iniciadas (Inicio/búsqueda) | Reg-doc `Explorer\Advanced\Start_TrackProgs=0` | Reduce «más usadas» en Inicio. | W10, W11 | No |
| Sugerencias en Inicio / consejos / contenido promocional | Reg-doc `ContentDeliveryManager\SubscribedContent-338388Enabled`, `-338389Enabled`, `SystemPaneSuggestionsEnabled` | Microsoft puede cambiar los IDs: se verifican por compilación. | W10, W11 | No |
| Recomendaciones en Inicio | Reg-doc `Start_IrisRecommendations=0` | Solo compilaciones verificadas de W11. | W11 22H2+ | No |
| Datos de diagnóstico opcionales | **Enlace** a `ms-settings:privacy-feedback` (la directiva HKLM marca el equipo como «administrado») | No se promete eliminar toda la telemetría. | W10, W11 | No |
| Permisos de apps (ubicación, cámara, micrófono…) | **Enlace** a cada página `ms-settings:privacy-*` + lectura del estado | — | W10, W11 | No |
| Aplicaciones de inicio | `StartupApproved\Run` (mismo mecanismo que el Administrador de tareas) | Reversible; no borra entradas. | W10, W11 | No (HKCU) |
| Apps preinstaladas de consumo | Lista blanca curada; `winget uninstall` por usuario | Reversible solo reinstalando desde la Store. Nunca paquetes de sistema/marco. | W10, W11 | No |

### 3.3 Personalización (fase 4)

| Ajuste | Método | Riesgos / límites | Compat. | Admin | Reinicio |
|---|---|---|---|---|---|
| Mostrar extensiones | Reg-doc `Explorer\Advanced\HideFileExt` | — | W10, W11 | No | Notificación al Explorador |
| Mostrar archivos ocultos | Reg-doc `Explorer\Advanced\Hidden` (1/2) | — | W10, W11 | No | Notificación |
| Vista compacta | Reg-doc `Explorer\Advanced\UseCompactMode` | — | W11 | No | Notificación |
| Carpeta inicial del Explorador | Reg-doc `Explorer\Advanced\LaunchTo` | Valores distintos entre W10 y W11 (Inicio/Este equipo). | W10, W11 | No | No |
| Alineación de la barra de tareas | Reg-doc `TaskbarAl` | — | W11 | No | Notificación |
| Botón de búsqueda / Vista de tareas | Reg-doc `Search\SearchboxTaskbarMode`, `ShowTaskViewButton` | — | W10, W11 | No | Notificación |
| Widgets | `TaskbarDa` | **Protegido por el controlador UCPD** en compilaciones recientes de W11: escritura denegada. Se marca «no disponible» y se ofrece el enlace oficial. | W11 | — | — |
| Tema claro/oscuro | Reg-doc `Themes\Personalize\AppsUseLightTheme`, `SystemUsesLightTheme` + `WM_SETTINGCHANGE` | — | W10, W11 | No | No |
| Menú contextual clásico | Reg-exp `HKCU\Software\Classes\CLSID\{86ca1aa0-…}\InprocServer32` | No documentado; puede dejar de funcionar. **Experimental, desactivado por defecto.** | W11 (verificado por compilación) | No | Reiniciar Explorador |

### Fuera de alcance (por principio)

Desactivar Defender, SmartScreen, Windows Update, UAC, el firewall o servicios
críticos; eliminar Edge, la Store o paquetes de marco; «tweaks de rendimiento» sin
evidencia (prioridades de CPU, `SvcHostSplitThresholdInKB`, *timers*, etc.);
modificar archivos `hosts` para bloquear telemetría.

### 3.4 Mis programas (ampliación)

| Función | Método | Riesgos / límites | Admin |
|---|---|---|---|
| Biblioteca permanente de programas | `programs.json` en la carpeta de configuración (nombre, web oficial, fuente preferida, Id de WinGet, arquitectura, tipo de instalador, preferencia de instalación, notas) | — | No |
| Identificar la fuente oficial al añadir | `winget search --source winget` (Id) + manifiesto público del repositorio `microsoft/winget-pkgs` (`PublisherUrl`, `PackageUrl`, `InstallerUrl`, `InstallerSha256`) | Solo se consulta al pulsar el botón; se envía el nombre buscado, nunca rutas ni datos del equipo. | No |
| Última versión: API oficial del desarrollador | Adaptadores específicos (p. ej. Mozilla `product-details.mozilla.org` + firma *Mozilla Corporation*) | Un adaptador por desarrollador; si la API cambia, el adaptador falla con aviso, no adivina. | No |
| Última versión: GitHub Releases | API `repos/{owner}/{repo}/releases/latest` (excluye *prerelease* y *draft*); el recurso se elige por un patrón que define el usuario **y** se verifica con el `digest` SHA-256 publicado por GitHub y la firma Authenticode | Límite de 60 peticiones/h sin autenticación. | No |
| Última versión: WinGet | Manifiesto del Id aprobado; hash `InstallerSha256` obligatorio | El manifiesto lo mantiene la comunidad; se muestra como «fuente WinGet». | No |
| Página oficial de descargas | Solo mediante adaptador específico verificable; **no hay scraping genérico** | Si no hay adaptador: aviso + botón «Abrir página oficial». | No |
| Verificación de descargas | Solo HTTPS; descarga a `.partial`; SHA-256 contra el esperado; `WinVerifyTrust` + editor esperado | Instaladores sin firma: se avisa y se pide confirmación expresa. | No |
| Instalación | WinGet en una consola visible (el usuario ve y acepta los acuerdos) o el instalador descargado, siempre tras confirmación | Puede pedir UAC. | Según instalador |
| Detección de instalados | `winget list --id <Id> --exact` (código de salida) y claves *Uninstall* (solo lectura) | Coincidencia por nombre aproximada: se indica como «probablemente instalado». | No |
| Preparar para formatear | A) solo la biblioteca; B) biblioteca + instaladores en el copia de seguridad (`installers/`) o en una carpeta elegida | Los instaladores archivados no se actualizan solos (aviso). | No |

**Niveles de confianza de una fuente** (nunca se cambia la fuente elegida sin
autorización):

- **Verificada**: el dominio de descarga coincide con la web oficial que figura en el
  manifiesto de WinGet, o es un adaptador oficial del desarrollador, o es el
  repositorio de GitHub enlazado desde el manifiesto.
- **Elegida por el usuario**: URL o repositorio introducido a mano y no contrastado.
- **No comprobada**: no se pudo contrastar (sin red, sin manifiesto, dominio distinto).

### 3.5 Configuración portable de Windows Reborn

- Paquete de configuración: `settings.json` (preferencias), `programs.json`
  (biblioteca), `profiles/*.json` (perfiles de copia de seguridad) y `tweaks.json` (ajustes de
  privacidad/personalización seleccionados).
- **Se incluye siempre** en los copias de seguridad completos (carpeta `reborn/` del archivo) y se
  puede exportar/importar por separado como `.wrconfig` (ZIP con `reborn-config.json`).
- En el primer arranque sin configuración, la página de inicio ofrece «Importar copia
  o configuración».
- Nunca contiene contraseñas, tokens ni cookies (el modelo no tiene campos para ello y
  la contraseña de cifrado no se guarda).
- Compatibilidad: `formatVersion` en el manifiesto y en `reborn-config.json`. Los
  lectores aceptan versiones iguales o anteriores, ignoran campos desconocidos y
  tratan como opcionales las secciones añadidas después (`reborn/`, `installers/`).

---

## 4. Arquitectura modular

```
WindowsReborn.sln
├─ src/
│  ├─ WindowsReborn.Core        Modelos comunes, registro local, detección de versión/compatibilidad,
│  │                            carpetas conocidas, utilidades de rutas seguras, localización (es/en)
│  ├─ WindowsReborn.Storage     Formato de la copia: manifiesto, hashes, escritores/lectores
│  │                            (carpeta, ZIP, 7z), finalización atómica, verificación
│  ├─ WindowsReborn.Backup      Inventario, categorías, adaptadores de apps, exclusiones,
│  │                            perfiles reutilizables, motor de copia con progreso y cancelación
│  ├─ WindowsReborn.Restore     Lectura de la copia, remapeo de rutas, conflictos, restauración
│  │                            verificada, informes
│  ├─ WindowsReborn.Programs    «Mis programas» + WinGet: biblioteca, adaptadores de fuentes
│  │                            (API oficial, GitHub Releases, manifiestos WinGet), descargas
│  │                            verificadas, firma Authenticode, detección de instalados,
│  │                            instalación confirmada, export/import de WinGet
│  ├─ WindowsReborn.Tweaks      Definiciones de ajustes, motor detectar/aplicar/revertir,
│  │                            diario de cambios, punto de restauración  (fase 3-4)
│  └─ WindowsReborn.App         WPF + WPF-UI, MVVM; solo presentación
└─ tests/
   └─ WindowsReborn.Tests       xUnit: almacenamiento, copia de seguridad, restauración, seguridad de rutas,
                                WinGet simulado, ajustes contra un Registro simulado
```

Dependencias: `App → {Backup, Restore, Programs, Tweaks} → Storage → Core`.
La configuración portable (`ConfigBundle`) vive en Core; Copia de seguridad la añade siempre a
`reborn/` y Restore la recupera. Ningún
módulo de lógica referencia WPF; todos los procesos largos son `async`, aceptan
`CancellationToken` e informan mediante `IProgress<T>`.

### 4.1 Formato del copia de seguridad

```
<Nombre>.wrbk.zip              (o carpeta <Nombre>/ o <Nombre>.wrbk.7z)
├─ manifest.json               metadatos + lista de archivos con SHA-256
├─ report.json                 omitidos y errores durante la creación
├─ reborn/                     configuración de Windows Reborn (siempre en copias completas)
├─ installers/<id>/…           opcional: instaladores verificados + installers/index.json
├─ apps/winget-export.json     opcional
└─ data/<categoría>/<ruta relativa>
```

`manifest.json` (esquema versionado):

- `formatVersion`, `appVersion`, `createdUtc`, `machineName`, `userName`
  (opcionalmente anonimizables), `windows` (producto, versión, compilación).
- `categories[]`: id, nombre, **raíz simbólica** (`{Documents}`, `{Desktop}`,
  `{AppData}`, `{Custom:Proyectos}`…) y ruta original informativa.
- `files[]`: ruta dentro del archivo, raíz simbólica, ruta relativa, tamaño,
  fecha de modificación, atributos y SHA-256.
- `emptyDirectories[]` para reproducir carpetas vacías.

La raíz simbólica es lo que permite restaurar en otro nombre de usuario, otra unidad
o con OneDrive redirigido.

### 4.2 Flujo del motor de copia de seguridad

1. **Inventario** (solo lectura): recorre las fuentes, aplica exclusiones, no sigue
   uniones ni enlaces simbólicos, detecta archivos solo en la nube, cuenta tamaño.
2. **Validación del destino**: no puede estar dentro de ninguna carpeta de origen;
   aviso si está en la unidad del sistema (se borrará al formatear); comprobación de
   espacio libre (tamaño original + margen, sin prometer compresión).
3. **Escritura** en `<nombre>.partial` con progreso real por bytes, SHA-256 calculado
   al vuelo y reintentos acotados en archivos bloqueados.
4. **Cierre y verificación**: se reabre el resultado y se recalcula el hash de cada
   entrada contra el manifiesto.
5. **Finalización atómica**: solo si la verificación pasa, se renombra a su nombre
   definitivo. Si se cancela o falla, se elimina el temporal y el informe lo indica.
   El estado final es *Correcto*, *Correcto con omisiones* (con lista) o *Fallido*.

### 4.3 Flujo de restauración

1. Abrir y validar el manifiesto (versión de formato, esquema).
2. Diagnóstico de integridad (opcional, recomendado): hashes de todas las entradas.
3. Selección de categorías/archivos y mapeo de raíces simbólicas a destinos.
4. Previsualización de conflictos: por archivo existente, *omitir* (por defecto),
   *renombrar* o *sobrescribir* (requiere confirmación explícita).
5. Extracción a temporal junto al destino, verificación del hash y movimiento final.
6. Informe de restauración (JSON + vista en pantalla).

Toda ruta de destino pasa por `SafePath.Combine(root, relative)`, que rechaza rutas
absolutas, `..`, nombres de dispositivo reservados (`CON`, `NUL`…), flujos
alternativos (`:`) y cualquier resultado fuera de la raíz autorizada.

### 4.4 Modelo de un ajuste de Windows (fase 3)

```csharp
public sealed record TweakDefinition
{
    string Id;                        // "explorer.show-file-extensions"
    LocalizedText Name, Description, Risks;
    TweakKind Kind;                   // Native, Documented, Experimental
    WindowsRange Compatible, Tested;  // por compilación
    bool RequiresAdmin;
    RestartScope Restart;             // None, ExplorerNotify, ExplorerRestart, SignOut, Reboot
    ITweakOperation Operation;        // Detect() / Apply() / Revert(snapshot)
    Reversibility Reversibility;      // Exact, ByReinstall, NotReliable
}
```

`ITweakOperation` solo puede ser de tipos cerrados (valor de Registro HKCU/HKLM,
`StartupApproved`, desinstalación WinGet de la lista blanca, enlace `ms-settings:`).
Antes de aplicar, el motor guarda una instantánea (valor, tipo, o «no existía») en
el diario `%LOCALAPPDATA%\WindowsReborn\journal\`. «Restaurar valores anteriores»
reproduce esa instantánea, no un valor por defecto. Si la detección no puede
verificarse en la compilación actual, el ajuste aparece como «No disponible».

### 4.5 Registro local

Archivo `%LOCALAPPDATA%\WindowsReborn\logs\AAAA-MM-DD.log` (o `.\Data\logs` en modo
portátil si existe `portable.flag` junto al ejecutable). Se registran operaciones,
recuentos y errores; las rutas personales se sustituyen por raíces simbólicas
(`{Documents}\…`). Sin telemetría ni conexiones de red, salvo WinGet cuando el usuario
lo invoca y la comprobación manual de actualizaciones (desactivada por defecto).

---

## 5. Propuesta visual

- **Ventana**: WPF-UI `FluentWindow` con fondo Mica (Windows 11) o acrílico/sólido
  (Windows 10), barra de título integrada, tamaño mínimo 960×640, adaptable.
- **Tema**: oscuro por defecto, sigue al sistema si el usuario lo prefiere; color de
  acento del sistema.
- **Navegación lateral** (`NavigationView`): Inicio · **Copia de seguridad** · **Restauración** ·
  **Mis programas** · **Privacidad** · **Personalización** · (pie) Ajustes.
- **Mis programas**: barra superior con *Agregar*, *Importar lista*, *Exportar lista*,
  *Buscar nuevas versiones*, *Descargar seleccionados*, *Preparar instaladores para
  formatear*; debajo, una fila por programa con icono, nombre, fuente y su nivel de
  confianza, versión instalada/disponible, estado y botones *Descargar*, *Instalar*
  (solo si hay método fiable), *Editar* y *Eliminar*.
  Las secciones aún no implementadas muestran «Próximamente» y no tienen botones de
  acción.
- **Copia de seguridad**: asistente de tres pasos en la misma página — *Qué copiar* (tarjetas por
  categoría con tamaño real tras el inventario + carpetas personalizadas), *Dónde y
  cómo* (destino, formato, exclusiones, cifrado), *Revisar y crear* (resumen con
  espacio necesario/libre, avisos). La barra de progreso refleja bytes reales,
  archivo actual, velocidad y botón *Cancelar*.
- **Restauración**: abrir copia de seguridad → tarjeta con fecha, equipo, Windows de origen,
  tamaño, categorías y estado de integridad → árbol de selección con destinos
  editables → política de conflictos → progreso → informe.
- **Estados**: cada botón de acción tiene estados *normal*, *ocupado* (anillo de
  progreso, deshabilitado), *éxito* y *error* (InfoBar con mensaje accionable).
- **Tipografía**: Segoe UI Variable (con *fallback* a Segoe UI). Iconos: Segoe Fluent
  Icons / Fluent System Icons de WPF-UI.
- Botones primarios a la derecha, ancho mínimo 120 px, altura 32 px, un único botón
  primario por vista.

---

## 6. Plan por fases

| Fase | Contenido | Criterio de salida |
|---|---|---|
| 0 | Este documento. | Revisado. |
| **1** | Core, Storage, Copia de seguridad, Restore; UI de Copia de seguridad y Restauración; ZIP, 7z (si está instalado) y carpeta; manifiesto + SHA-256; zip slip; conflictos; informes; idiomas es/en; configuración portable incluida en cada copia de seguridad; pruebas. | Pruebas en verde; copia de seguridad → restauración en otro usuario verificado en VM. |
| **1b** | Mis programas: biblioteca, identificación de fuente, GitHub Releases, manifiestos WinGet, adaptador Mozilla, descargas verificadas, instalación confirmada, preparar instaladores para formatear, detección de instalados. | Pruebas con HTTP simulado; instalación real solo en VM. |
| 2 | Adaptadores de configuración de apps y navegadores (restauración con la app cerrada), perfiles de copia de seguridad, modo simulación, diagnóstico de integridad independiente. | Restauración de adaptadores en VM. |
| 3 | Motor de ajustes + diario de reversión + punto de restauración; módulo de Privacidad. **Hecho en 0.2** (ver §7). | Cada ajuste probado en VM W10 22H2 y W11 24H2/25H2; reversión exacta. |
| 4 | Personalización, búsqueda de ajustes, preferencias de Windows en el copia de seguridad. **Hecho en 0.2.** | Igual que fase 3. |
| 5 | Copias incrementales, VSS opcional con admin, comprobación manual de actualizaciones. | — |

### Pruebas

- Unitarias e integración (xUnit) sobre directorios temporales: archivos grandes
  (generados dispersos), carpetas vacías, Unicode, rutas > 260, permisos denegados
  (ACL de prueba), archivos bloqueados (`FileShare.None`), destino sin espacio
  (simulado mediante cuota en el escritor), cancelación, ZIP truncado, entradas
  manipuladas (`..\`, rutas absolutas, hash alterado), colisiones de mayúsculas,
  restauración con otro nombre de usuario (remapeo de raíces), WinGet simulado.
- Los ajustes del sistema se prueban contra una abstracción de Registro en memoria;
  la validación real se hace solo en máquinas virtuales (Hyper-V / Windows Sandbox),
  nunca en el equipo principal.

---

## 7. Versión 0.2: ajustes de Windows con riesgo informado

**Cambio de criterio pedido por el usuario:** ya no se limitan los ajustes a los de riesgo
bajo. Se incluyen opciones moderadas y avanzadas, clasificadas y con confirmación, pero
nunca las que desactivan protecciones esenciales (Defender, SmartScreen, UAC, Windows
Update, firewall), tocan el arranque o no tienen reversión fiable; estas últimas quedan
como entradas informativas que abren la herramienta oficial.

### Referencias estudiadas (sin copiar código)

| Herramienta | Licencia | Ideas incorporadas |
|---|---|---|
| WinUtil | MIT | Ajustes de Explorador/barra de tareas, menú contextual clásico, servicios no esenciales, Game DVR, aceleración del ratón |
| O&O ShutUp10++ | Freeware propietario | Agrupación por riesgo, «aplicar recomendados», publicidad, diagnósticos, permisos de apps, Copilot/Recall |
| Win11Debloat | MIT | Recomendaciones de Inicio, sugerencias en Configuración, lista de apps de consumo quitables |
| Optimizer | GPL-3.0 | Aplicaciones de inicio, servicios opcionales (solo la idea; no se reutiliza código GPL) |
| Winaero Tweaker | Freeware propietario | Panel de navegación (Galería, Inicio), ruta completa, ajustes visuales |
| PowerToys | MIT | Fuera de alcance: herramientas propias, no ajustes del sistema |
| FluentTweaker | Sin licencia | Solo consulta |

### Diseño

- `catalog.json`: cada ajuste es dato (categoría, riesgo 1-3, compilaciones compatibles,
  valores por opción, reinicio necesario y textos es/en). Las pruebas exigen textos en
  ambos idiomas, advertencia en los avanzados y ninguna clave de protecciones esenciales.
- `TweakEngine`: compatibilidad (compilación y componentes) → estado real → valores
  originales al diario `state/tweak-journal.json` (propio de cada instalación) → escritura →
  verificación → deshacer si Windows lo rechaza. Revertir restaura el valor original y
  borra las claves que creó el ajuste.
- Escritura HKLM y puntos de restauración: una copia elevada de la aplicación
  (`--elevated`) que **solo acepta operaciones del catálogo**. HKCU se escribe siempre en
  el proceso del usuario.
- `tweaks.json` (preferencias elegidas) viaja en el copia de seguridad; el asistente de restauración
  las ofrece de nuevo, marcando por defecto solo las seguras.
- Aplicaciones preinstaladas: lista cerrada (`appx.json`); nunca la Tienda, WinGet, marcos
  ni componentes del sistema. Inicio: `StartupApproved`, sin borrar entradas; las de
  Seguridad de Windows no se pueden desactivar.

### Pendiente de validar en máquina virtual

Las pruebas usan un registro en memoria. Aplicar y revertir cada ajuste en Windows 10 22H2
y Windows 11 24H2/25H2 reales, quitar aplicaciones y crear puntos de restauración no se ha
hecho en el equipo principal, a propósito.
