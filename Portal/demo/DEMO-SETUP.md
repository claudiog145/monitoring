# Demo en un equipo nuevo — `demo-setup.ps1`

> English: [DEMO-SETUP.en.md](DEMO-SETUP.en.md)

Deja el **Agentic Operations Portal** (http://localhost:5080) funcionando contra un **Azure falso** con datos
sintéticos de 4 empresas: Arrivia (turismo), Banco Andino (banca), Kinectify (AML) y Mercado Libre (retail).
No hace falta copiar datos entre equipos: la telemetría se **regenera** (determinística, misma semilla) y la
plataforma se **siembra** desde los paquetes del repositorio (`Portal/demo/packs`).

## Requisitos

| | |
|---|---|
| Windows + PowerShell 5.1 o superior | |
| Docker Desktop | con **14 GB o más** para contenedores (Settings → Resources). Con menos, el script usa escala `tiny`. |
| Disco libre | ~30 GB (la telemetría de 8 días ocupa ~23 GB en Kustainer) |
| Token del LLM (`Ai__GitHubToken`) | el script lo pide si falta. Sin token la detección funciona, pero no hay investigaciones con LLM ni Memory Bank. |
| Repositorio clonado | `git clone` del repo y estar en la rama de la demo |

## Preparar (una sola vez, ~45-60 min la primera vez)

```powershell
cd <repo>\Portal\demo\scripts
.\demo-setup.ps1
```

El script, en orden:

1. Verifica Docker y la memoria disponible.
2. Escribe en `Portal/.env` el bloque de demo (el token queda solo en ese archivo, que no se versiona).
3. Construye y levanta SQL, Azurite, Functions, Web, Kustainer, el Azure falso y el generador.
4. Genera **8 días de historia** (~12,8 M eventos, ~8 min) y reinicia Kustainer para liberar memoria.
5. Carga perfiles, fuentes de telemetría, consultas KQL y KPIs de las 4 empresas.
6. Importa al **Memory Bank** los RCAs de cada paquete.
7. Crea los KPIs **"· demo 1m"** (buckets de 1 minuto, umbral fijo: detección en ~2 min) y espera a que los
   daemons carguen su historia (12 h) para **calibrar** los umbrales. Es el paso más largo (20-40 min).
8. Deja Arrivia como compañía activa, monos y daemons activos, cola vacía.
9. Verifica todo y muestra un resumen por empresa.

Se puede volver a ejecutar cuando quieras: lo que ya existe se reutiliza.

| Opción | Para qué |
|---|---|
| `-Fresh` | Borra la telemetría sintética y la base `platformDB` (hace backup antes) y prepara todo de cero. Pide confirmación (`-Force` la omite). |
| `-SkipBuild` | No reconstruye imágenes. |
| `-SkipReset` | No regenera la telemetría si ya existe. |
| `-SkipCorpus` | No importa el Memory Bank. |
| `-IncludeArriviaCorpus` | Además importa para Arrivia los RCAs reales de `docs/RCAS-md` (no versionados; solo si los copiaste). |
| `-ActiveCompany banco` | Compañía activa al terminar. |
| `-Days 8 -Scale laptop` | Días de historia y escala (`tiny`, `laptop`, `showcase`). |

## El día de la demo

| Cuándo | Qué |
|---|---|
| 25 min antes | `docker restart jm-kustainer` (libera memoria; los datos persisten) |
| 20 min antes | `.\demo-incidents.ps1 -All -Minutes 90` (un incidente en cada empresa) |
| 15 min antes | `.\demo-incidents.ps1 -Detections` (las 4 empresas en `[OK]`) |
| 15 → 3 min antes | Opcional: pasar la compañía activa por las 4 (selector **Company**, ~3 min cada una) para que los Scouts y el Inspector ya tengan casos en cada empresa |
| Demo | Cambiar de empresa con el selector **Company**: Daemons, Triage y Simian Army muestran sus anomalías e investigaciones |
| Al terminar | `.\demo-incidents.ps1 -Stop` |

Durante la demo no ejecutes builds, tests ni `docker restart`.

## Cómo funciona la detección en la demo

- **Daemons** (KPI ingestion + monitores) evalúan las 4 empresas a la vez. Los KPIs "· demo 1m" detectan un
  incidente en ~1,5-2,5 min; los KPIs calibrados de los paquetes en 10-25 min.
- **Triage** crea una investigación por cada anomalía; el **Inspector** la completa en 30-90 s.
- **Scouts** (modo `Method`) siguen las anomalías de los daemons de la **compañía activa**: cada 2 min entregan un
  paquete "Investigate now" por anomalía, con evidencia de todo el servicio afectado, y el Inspector lo investiga.
- **RCA**: se escribe solo cuando el Inspector nombra una causa raíz o su confianza supera el 30%. Con datos
  sintéticos suele quedar por debajo; para mostrar uno, usá el botón del caso.

## Si algo falla

| Síntoma | Qué hacer |
|---|---|
| El portal o la API no responden | `docker stats --no-stream jm-kustainer`. Si está cerca de su límite de memoria: `docker restart jm-kustainer`. |
| `docker` responde "500 Internal Server Error" | Docker Desktop saturado: esperá 1-2 min; si sigue, `docker restart jm-kustainer` o reiniciá Docker Desktop. |
| `-Detections` no muestra una empresa | Esperá 3 min más; verificá `.\demo-incidents.ps1 -Check` y que el incidente esté activo (`-Status`). |
| El setup terminó con KPIs sin calibrar | Volvé a ejecutar `.\demo-setup.ps1 -SkipBuild -SkipReset -SkipCorpus`. |
| Querés empezar de cero | `.\demo-setup.ps1 -Fresh` |

## Qué NO se lleva

- Los RCAs reales de Arrivia (`docs/RCAS-md`) no están en el repositorio. En otro equipo Arrivia usa los RCAs de su
  paquete (`packs/tourism/corpus`), salvo que copies esa carpeta y uses `-IncludeArriviaCorpus`.
- Las investigaciones, casos y RCAs generados en otras demos: el setup empieza limpio.
