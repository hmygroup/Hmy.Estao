# Harness Hub: instalador personal

## Fuente y cobertura

El origen fijo es `hmygroup/hmy-prompt-engineer`, branch `main`. Cada operación resuelve el commit una vez y obtiene tree/blobs por SHA. No requiere Git, Node ni npm para instalar archivos. Nunca ejecuta scripts de la biblioteca.

El contrato bootstrap revisado está en `src/Hmy.Estao.Core/Harnesses/Installer/estao-harness.json`, incluido como recurso. Este mismo archivo es el entregable propuesto para los responsables de la fuente; **no se ha publicado allí**.

Snapshot bootstrap: `86145451d35141cfe2d246d68047d7be63d72b0d`, tree `d1f86100c5cecdd4065eefca46f7096af6285655`. Sustituye el inventario histórico de trece prompts y recoge la sustitución upstream de `itab-group-style` por `itab-design`. Contiene 22 skills/450 archivos de recursos. Codex recibe las 22; Claude Code, Copilot CLI y OpenCode reciben 21, excluyendo `hatch-pet` por su formato de mascota exclusivo de Codex.

Los tres documentos de `prompts/` son flujos para ChatGPT, no skills nativas. Los agentes `revisor-biblioteca` y el comando `validar-biblioteca` dependen del checkout y contrato CMP: no se registran globalmente. Documentación, tooling, fixtures y arte gráfico quedan excluidos. La vista de detalles informa categoría, motivo y cantidad de archivos por cada grupo, además de todos los destinos.

Instalar una skill no verifica sus runtimes: Tengu, Jira, navegadores, TFVC, herramientas de imagen y agentes internos siguen necesitando infraestructura y autorización propias. Los scripts, referencias y licencias dentro de la carpeta de una skill se conservan sin modificación. No se registran automáticamente sus agentes internos ni se activan hooks.

## Contrato `estao-harness.json`, schemaVersion 1

- `repository`, `version`: identidad y versión del contrato.
- `commit`, `tree`: pin opcional. En el bootstrap son obligatorios y coinciden con el snapshot completo. Un manifiesto mantenido **dentro** del repositorio debe omitirlos o dejarlos vacíos (no puede incluir su propio hash de árbol).
- `artifacts`: `id`, `source`, `category`, `classification`, `destination`, `transformation`, `harnesses`, `requirements`, `dependencies`, `minimumVersions`.
- `source`: archivo relativo o carpeta completa de skill; todas las referencias/assets/scripts/licencias dentro de esa carpeta forman un único grupo transaccional.
- `category` y `destination`: `skill`, `agent`, `command`, `mcp`. No se aceptan rutas de destino absolutas del repositorio.
- `classification`: `shared` o `harness-specific`. Es independiente del destino user-global.
- `transformation`: actualmente `native`. No se convierte un prompt genérico en agente ni se traduce automáticamente política de herramientas.
- `exclusions`: prefijos/archivos explícitos con clasificación `documentation`, `support`, `unsupported` o `external-runtime` y un motivo.

Cada archivo del árbol debe pertenecer exactamente a un artefacto o exclusión. Las nuevas rutas no declaradas, la cobertura ambigua, enlaces, submódulos, ciclos o dependencias ausentes bloquean el snapshot antes de escribir. No hay fallback silencioso a una revisión anterior. Un cambio de main sin manifiesto publicado y compatible requiere actualizar el bootstrap tras revisar la fuente.

## Autorización

Se comprueba acceso efectivo al contenido privado, no solo `/user`. Se pueden reutilizar una autorización dedicada guardada, `HMY_ESTAO_GITHUB_TOKEN`, una sesión de `gh` opcional o el token Copilot **solo si realmente tiene acceso**. Ni Git ni `gh` son requisitos: un fine-grained token restringido a este repositorio con `Contents: read` puede conectarse desde el diálogo. No se amplían scopes Copilot.

La credencial dedicada se guarda con DPAPI CurrentUser en `<datos Estao>/harness-github/oauth-secrets.json`, dentro de un directorio con ACL limitada al usuario. No se imprime el token ni se pasa por argumentos. No se incorpora ninguna credencial al código.

Si el administrador configura `HMY_ESTAO_GITHUB_APP_CLIENT_ID`, Conectar GitHub usa Device Flow de esa App. Requisitos externos: App propia, Device Flow habilitado, `Contents: read` e instalación organizativa restringida a la biblioteca. No se distribuye client secret. Ante expiración/revocación se solicita reconexión; no se implementa refresh con un secreto embebido. SSO y permisos corporativos siguen siendo aplicables.

## Destinos y capacidades

- Codex: skills en `~/.agents/skills`; `CODEX_HOME` para configuración y agentes nativos. Se detectan skills legacy coincidentes bajo el perfil Codex.
- Claude: `CLAUDE_CONFIG_DIR` o `~/.claude`; skills/agents/commands personales. MCP user en el JSON del perfil.
- Copilot CLI: `COPILOT_HOME` o `~/.copilot`; skills, agents `.agent.md`, MCP en `mcp-config.json`. No se usan `.prompt.md` de VS Code como comandos CLI.
- OpenCode: `OPENCODE_CONFIG_DIR`, `XDG_CONFIG_HOME` y `OPENCODE_CONFIG`, según corresponda. Se conserva JSONC existente y se bloquea ambigüedad JSON/JSONC. Agentes/MCP requieren versión V2 verificada; skills usan formato estándar.

Los overrides deben ser rutas locales absolutas sin enlaces. El instalador no deduce un proyecto desde el directorio de trabajo de la bandeja. La detección PATH es indicativa: no ejecuta ni instala el harness. Si no se detecta, pide confirmación para preparar solo sus archivos.

Los adapters validan formatos nativos y rechazan políticas de agentes no soportadas. El bootstrap actual no instala agentes, MCP ni comandos. Una futura declaración MCP queda bloqueada hasta disponer de aprobación individual de endpoints; la infraestructura de merge no constituye autorización para habilitar servidores desconocidos. No se afirma aceptación runtime de todos los hosts a partir de validaciones de formato.

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

La integración real está separada y requiere `HMY_ESTAO_TEST_GITHUB_TOKEN` en el entorno del proceso. Valida el commit bootstrap explícito, para no depender de ediciones concurrentes de main durante QA; la UI normal siempre resuelve main y lo contrasta con su contrato. Solo descarga contenido y escribe perfiles temporales propios del test; no ejecuta scripts ni toca la configuración real de los harnesses. El test de integración devuelve sin contactar GitHub cuando no se habilita expresamente; un resultado general verde no implica integración real ni aceptación visual/runtime.
