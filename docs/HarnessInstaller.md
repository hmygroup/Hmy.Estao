# Harness Hub: instalador personal

## Fuente y catálogo

El origen fijo es `hmygroup/hmy-prompt-engineer`, branch `main`. Cada operación resuelve el commit una vez y obtiene tree/blobs por SHA. No requiere Git, Node ni npm para instalar archivos. Nunca ejecuta scripts de la biblioteca.

El catálogo se obtiene de **`hub-manifest.json` en la raíz del repositorio fuente**. Sustituye el contrato `estao-harness.json` y su fallback bootstrap. La especificación, el esquema JSON y un ejemplo para los responsables de HMY están en [HubManifest.md](HubManifest.md); estos entregables **no se han publicado allí**.

HMY muestra únicamente elementos declarados. Other muestra skills del repositorio no catalogadas y skills locales sin correspondencia. Installed mantiene el inventario local. Instrucciones y otros archivos solo se muestran si están declarados; no se convierten en skills ni se registran automáticamente como instrucciones globales. La ausencia de un elemento del catálogo no significa incompatibilidad con una herramienta.

Instalar una skill no verifica sus runtimes: Tengu, Jira, navegadores, TFVC, herramientas de imagen y agentes internos siguen necesitando infraestructura y autorización propias. Los scripts, referencias y licencias dentro de la carpeta de una skill se conservan sin modificación. No se registran automáticamente sus agentes internos ni se activan hooks.

## Catálogo `hub-manifest.json`, schemaVersion 1

- `defaultLanguage`: idioma de fallback mediante código ISO 639-1 de dos letras.
- `categories`: identificadores estables y etiquetas traducidas.
- `items`: elementos con `id`, `type` (`skill`, `instruction`, `file`), `path`, `description` traducida y `categories` separadas por comas. Dependencias de skills opcionales en `dependencies`.
- Una skill referencia su carpeta completa: recursos, scripts y licencias forman el mismo grupo transaccional. Otros tipos referencian archivos concretos.
- No contiene listas de harnesses, rutas de instalación, comandos ni pins de commit/tree. No exige clasificar todos los archivos del repositorio.

La validación de catálogo comprueba rutas, identidad, traducciones, categorías y dependencias. Si falta o no es válido, se muestra un único error de catálogo; no se ocultan las skills locales ni se bloquea por ese motivo la revisión de una skill válida de Other. Los blobs y rutas siguen sujetos a límites y comprobaciones de integridad. El snapshot se fija durante cada revisión, no en el manifiesto mantenido por HMY.

## Autorización

Se comprueba acceso efectivo al contenido privado, no solo `/user`. Se pueden reutilizar una autorización dedicada guardada, `HMY_ESTAO_GITHUB_TOKEN`, una sesión de `gh` opcional o el token Copilot **solo si realmente tiene acceso**. Ni Git ni `gh` son requisitos: un fine-grained token restringido a este repositorio con `Contents: read` puede conectarse desde el diálogo. No se amplían scopes Copilot.

La credencial dedicada se guarda con DPAPI CurrentUser en `<datos Estao>/harness-github/oauth-secrets.json`, dentro de un directorio con ACL limitada al usuario. No se imprime el token ni se pasa por argumentos. No se incorpora ninguna credencial al código.

Si el administrador configura `HMY_ESTAO_GITHUB_APP_CLIENT_ID`, Conectar GitHub usa Device Flow de esa App. Requisitos externos: App propia, Device Flow habilitado, `Contents: read` e instalación organizativa restringida a la biblioteca. No se distribuye client secret. Ante expiración/revocación se solicita reconexión; no se implementa refresh con un secreto embebido. SSO y permisos corporativos siguen siendo aplicables.

## Destinos y capacidades

- Codex: skills en `~/.agents/skills`; `CODEX_HOME` para configuración y agentes nativos. Se detectan skills legacy coincidentes bajo el perfil Codex.
- Claude: `CLAUDE_CONFIG_DIR` o `~/.claude`; skills/agents/commands personales. MCP user en el JSON del perfil.
- Copilot CLI: `COPILOT_HOME` o `~/.copilot`; skills, agents `.agent.md`, MCP en `mcp-config.json`. No se usan `.prompt.md` de VS Code como comandos CLI.
- OpenCode: `OPENCODE_CONFIG_DIR`, `XDG_CONFIG_HOME` y `OPENCODE_CONFIG`, según corresponda. Se conserva JSONC existente y se bloquea ambigüedad JSON/JSONC. Agentes/MCP requieren versión V2 verificada; skills usan formato estándar.

Los overrides deben ser rutas locales absolutas sin enlaces. El instalador no deduce un proyecto desde el directorio de trabajo de la bandeja. La detección PATH es indicativa: no ejecuta ni instala el harness. Las herramientas no detectadas no se muestran ni se seleccionan para instalar.

Los adapters validan el formato estándar de skills y conservan el contenido fuente. El catálogo no instala agentes, MCP ni comandos. Las instrucciones y archivos se exportan mediante Guardar como explícito; no se fusionan automáticamente en configuraciones globales o de proyecto. No se afirma aceptación runtime de todos los hosts a partir de validaciones de formato.

## Política aditiva y recuperación

1. Preparar snapshot completo, validar formatos y construir plan antes de escribir.
2. Detectar también skills coincidentes en ubicaciones compatibles: equivalentes no se duplican; diferentes no se ocultan mediante una nueva copia.
3. Crear archivos nuevos; no reescribir idénticos ni reclamar su propiedad. Actualizar solo archivos previamente administrados e intactos.
4. Conservar archivos ajenos/modificados y el grupo completo de sus recursos/dependencias si hay un conflicto.
5. JSON/JSONC: añadir nodos ausentes sin regenerar el documento ni perder comentarios; claves duplicadas y valores diferentes se rechazan. TOML: solo se añaden tablas top-level ausentes; tablas superpuestas requieren integración manual. Esta limitación conservadora evita corrupción, no simula un merge universal.
6. No borrar archivos retirados upstream. No limpiar configuraciones ni backups legacy.
7. Journal y estado nuevos bajo `<datos Estao>/harness-installer`, ACL privada, lock exclusivo, backups y staging persistentes, hashes antes de escribir, reemplazos por archivo y rollback condicionado.
8. Recuperación automática ante errores y acción Recuperar tras una interrupción. Un archivo editado después de la escritura no se reemplaza durante rollback; se conserva y se muestra recuperación pendiente.

Los checks de rutas y hashes se repiten antes de aplicar. No constituyen aislamiento frente a un proceso malicioso del mismo usuario que sustituya rutas exactamente entre llamadas al sistema; tampoco proporcionan atomicidad multiarchivo. Se rechazan UNC, ADS, nombres reservados y reparse points. Las recuperaciones pendientes se conservan para no perder evidencia; no se aplica limpieza automática del journal.

## Compatibilidad legacy

La UI antigua y el camino de instalación desde Settings se han retirado. El CLI `estao usage`/`estao config`, schema de configuración v1 y datos legacy se mantienen. Los servicios históricos de paquetes/restore en Core se conservan para compatibilidad y pruebas, pero no son invocados desde la aplicación. Su política histórica de sobrescritura **no** es la política del instalador nuevo.

## Validación reproducible

```
dotnet restore Hmy.Estao.slnx
dotnet build Hmy.Estao.slnx -c Release --no-restore
dotnet test tests/Hmy.Estao.Tests/Hmy.Estao.Tests.csproj -c Release --no-restore
```

Las pruebas usan respuestas GitHub simuladas y perfiles temporales. No necesitan autorización real ni modifican herramientas personales. Los checks de UI con fixtures no equivalen a integración con el repositorio privado ni a aceptación runtime de los hosts.
