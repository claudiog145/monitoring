---
name: spec1000-a5-triage
description: "Spec 1000 tarea A5: el Inspector procesa TriageSignal (prioridad sobre paquetes de Scout), recolecta evidencia de 6 dimensiones, produce TriageResult con el JSON del spec, respeta modo Off/Shadow/Live y budget, con tests."
agent: spec1000-implementer
argument-hint: "TASK=A5 WORKTREE=<ruta> ITERATION=<n>"
---
# A5 · Triage Monkey dentro del Inspector

**Objetivo:** que el Inspector, además de los paquetes de Scout, procese investigaciones `TriageSignal` y produzca un `TriageResult` (contrato §2), **sin alterar** el flujo actual de `InvestigationPackage`.

## Archivos que te pertenecen

Ver `tasks.json` → `A5`. En `MonkeyRunners.cs` e `InspectorMonkeyService.cs` hacé cambios **mínimos** (puntos de enganche); la lógica nueva va en archivos `Triage*.cs` y en `Core/Monitoring/Evidence/`.

## Antes de empezar

Leé completos `InspectorMonkeyRunner` (en `MonkeyRunners.cs`), `InspectorMonkeyService.cs`, `IInspector.cs`, `RoutingLlmClient`/`ILlmClient`, el budget de agentes (`IAgentBudgetStore` o equivalente), la pausa de agentes y `DoctorAgentService.cs` (patrón defensivo de JSON del LLM). Anotá las firmas reales antes de diseñar.

## Diseño obligatorio

1. **Runner:** en cada corrida, `InspectorMonkeyRunner` intenta primero `TryLeaseAsync(AgentWorkPayloadType.TriageSignal, …)`; si obtiene un ítem lo procesa con `TriageMonkeyService`; si no, sigue con el flujo actual de `InvestigationPackage` **sin cambios**. Completa/falla el ítem con `CompleteAsync`/`FailAsync` usando el mismo `leasedBy`.
2. **`Core/Monitoring/Inspector/TriageMonkeyService.cs`** — `public sealed class TriageMonkeyService`, método `Task<TriageResult?> InvestigateAsync(Guid investigationId, CancellationToken ct)`. Dependencias: `ITriageInvestigationStore`, `IEnumerable<IEvidenceProvider>`, el cliente LLM y el budget/pausa existentes, `INotificationService`, `IOptions<TriageOptions>`, `TimeProvider`, `ILogger<…>`.
   - Investigación inexistente o no `Queued` → log y `null` (el runner completa el ítem igual).
   - `UpdateStatusAsync(Running)`.
   - Ventana: `[DetectedAt - EvidenceWindowMinutes, DetectedAt + 15 min]`.
   - Evidencia: para **cada una** de las 6 `TriageDimensions.All`, el provider de esa dimensión; si no hay provider → `EvidenceSection(dim, false, null, "no provider")`; si el provider lanza → `EvidenceSection(dim, false, null, "error: <mensaje corto>")` y se sigue.
   - `Mode == Off` → resultado sin LLM: `alertLevel = informational`, `possibleRedAlert = false`, `confidence = null`, `summary = "Triage disabled (Mode=Off)"`.
   - Budget agotado o agente pausado (`TriageOptions.AgentKey`) → mismo resultado sin LLM con `summary` que lo explique.
   - LLM: prompt con la señal + evidencia disponible, pidiendo **solo JSON** `{alertLevel, possibleRedAlert, confidence, summary, recommendedNextActions}`. Validá: `alertLevel ∈ TriageAlertLevels.All`, `confidence ∈ [0,1]` o null, `summary` no vacío (truncá a 2000), `recommendedNextActions` ≤ 10 ítems. JSON inválido o error del LLM → `informational`, `possibleRedAlert = false`, `confidence = null`, `summary = "LLM unavailable or invalid response"`. Registrá la llamada en el log de LLM existente si lo hay.
   - `TriageResult`: `Trigger.Condition = signal.Detection`; `Correlation` con el `Data` de cada sección (null si no disponible).
   - Notificación: `Shadow` → **nunca** llamar a `INotificationService`; `Live` → solo si `alertLevel == red_alert`. `CompleteAsync(id, result, notified)`.
   - Excepción inesperada → `UpdateStatusAsync(Failed, mensaje)` y relanzar para que el runner haga `FailAsync`.
3. **Evidence providers** (`Core/Monitoring/Evidence/`):
   - `ApplicationEvidenceProvider`: buckets de KPIs de Application del dominio en la ventana (`IKpiBucketStore` + `IKpiDefinitionStore`) e incidentes recientes del dominio (lector de incidentes existente).
   - `KnowledgeEvidenceProvider`: hasta 5 chunks del retriever de conocimiento existente para el texto de la señal.
   - `InfrastructureEvidenceProvider`, `CodeReliabilityEvidenceProvider`, `RecentChangesEvidenceProvider`: devuelven `Available = false`, `UnavailableReason = "not implemented (Fase 3)"`.
   - `Infrastructure/Fabric/FabricBusinessEvidenceProvider.cs`: KPIs de negocio de Hotels en la ventana usando el source Fabric existente; otros dominios → `Available = false, "no business source for <domain>"`.

## Tests requeridos

`Runner_LeasesTriageSignalBeforeInvestigationPackages`, `Runner_ExistingInvestigationPackageFlowUnchanged` (un paquete de Scout se procesa igual que antes), `Triage_CollectsAllSixDimensions_UnavailableOnesMarked`, `Triage_EvidenceProviderFailure_DoesNotFailInvestigation`, `Triage_ProducesContractValidJson` (serializar con `TriageJson.Options` y validar las rutas de §2), `Triage_InvalidLlmJson_FallsBackToInformational`, `Triage_ShadowMode_NeverNotifies` (aun con `red_alert`), `Triage_LiveMode_NotifiesOnlyRedAlert`, `Triage_BudgetExhausted_DoesNotCallLlm`, `Triage_ModeOff_CompletesWithoutLlm`, `Triage_UpdatesStatusRunningThenCompleted`.

## Criterios de aceptación

1. Los tests existentes del Inspector/Scout siguen verdes sin modificaciones.
2. `TriageSignal` se atiende antes que `InvestigationPackage`.
3. Siempre se producen las 6 dimensiones en `Correlation` (disponibles o no).
4. Modo Shadow no notifica nunca; Live solo para `red_alert`.
5. Budget/pausa/LLM inválido degradan a `informational` sin excepción.
6. 11 tests requeridos presentes y verdes.
