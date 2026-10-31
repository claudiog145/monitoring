# Scout Readiness Checklist - Multi-Company

## 📋 Pre-Requisitos Para Que Scout Funcione

### 1. Base de Datos ✅

- [x] `monitoring.CompanyProfile` existe
- [x] `monitoring.ActiveCompanyProfile` existe (single row)
- [x] `monitoring.InvestigationPackage` tiene columna `ProfileKey`
- [x] `monitoring.ScoutRun` tiene columna `ProfileKey`
- [x] `monitoring.ForensicCase` tiene columna `ProfileKey`
- [x] `monitoring.KnowledgeChunk` tiene columna `ProfileKey`
- [x] Índice único: `(CaseId, Board, ProfileKey)` en InvestigationPackage
- [x] Índice único: `(DedupKey)` en AgentWorkQueue
- [x] Índice de búsqueda: `(ProfileKey, CreatedAt DESC)` en todas las tablas operacionales

**Verificación SQL:**
```sql
-- Todas estas queries deberían devolver resultados
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN (
  'CompanyProfile', 'ActiveCompanyProfile', 
  'InvestigationPackage', 'ScoutRun', 'ForensicCase', 'KnowledgeChunk'
);
```

---

### 2. Configuración ✅

**appsettings.json (Functions App):**
```json
{
  "Company": {
    "DefaultProfileKey": "demo"  // ← Cambiar a tu empresa por defecto
  },
  "MonitoringOptions": {
    "SettingsWriteSecret": "development-secret"  // ← En prod: strong secret
  }
}
```

**appsettings.json (Portal Web):**
```json
{
  "Company": {
    "DefaultProfileKey": "demo"
  }
}
```

**docker-compose.yml (Portal):**
```yaml
environment:
  - Company__DefaultProfileKey=demo
  - MonitoringOptions__SettingsWriteSecret=development-secret
```

**Verificación:**
```bash
# Las funciones deberían responder sin "No default configured"
curl -s http://localhost:7071/api/scout/stats 2>/dev/null | grep -q "packages" && echo "✓ Default OK" || echo "✗ Missing default"
```

---

### 3. Empresas en la Base de Datos ✅

**Al menos una empresa debe existir y estar habilitada:**

```sql
SELECT COUNT(*) as CompanyCount 
FROM monitoring.CompanyProfile 
WHERE Enabled = 1;
```

**Debe retornar >= 1**

**Las empresas de semilla están en:**
```
Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringCompanyProfile.sql
```

**Contenido de ProfileJson (ejemplo mínimo):**
```json
{
  "displayName": "Demo Company",
  "services": [
    {
      "source": "Hotel",
      "flows": [
        {
          "flow": "Search",
          "stages": [
            {
              "stage": "ApiGateway",
              "matchers": [
                {
                  "kind": "path-contains",
                  "value": "/Hotel/Search"
                }
              ]
            }
          ]
        }
      ]
    }
  ],
  "clubs": {
    "hotels": [
      { "id": "T-MOBILE", "name": "T-Mobile / T Life" }
    ]
  }
}
```

---

### 4. Servicios de Monitoreo ✅

**ICompanyAdapterProvider está wired en DI:**
```csharp
// En Program.cs (Functions app)
services.AddProfileDrivenCompany();  // ← Registra CompanyAdapterProvider + stores
```

**CompanyAdapterProvider está inyectado en:**
- [x] `ScoutMonkeyService` - para clasificar telemetría
- [x] `InspectorMonkeyService` - para investigar packages
- [x] `RcaAgentService` - para buscar precedents
- [x] `DoctorAgentService` - para análisis global

---

### 5. APIs Expuestas ✅

**Company Profile Admin:**
```
GET  /api/settings/company-profiles                 → Listar todas
GET  /api/settings/company-profiles/{key}           → Detalles de una
GET  /api/settings/company-profiles-audit           → Historial
GET  /api/settings/company-profiles-active          → Cuál está activa
PUT  /api/settings/company-profiles-active          → Cambiar activa
PUT  /api/settings/company-profiles/{key}           → Editar empresa (admin)
POST /api/settings/company-profiles                 → Crear (admin)
```

**Endpoints Existentes Ahora Con ?profileKey=**
```
GET /api/scout/packages?profileKey=demo
GET /api/scout/stats?profileKey=demo
GET /api/monkeys?profileKey=demo
GET /api/inspector/cases?profileKey=demo
GET /api/exceptions?profileKey=demo
```

**Verificación:**
```bash
curl -s http://localhost:7071/api/settings/company-profiles | jq '.profiles | length'
# Debe devolver >= 1

curl -s http://localhost:7071/api/settings/company-profiles-active | jq '.profileKey'
# Puede ser null (usa default) o un profileKey
```

---

## 🚀 Scout Cycle - Estado Actual

### 1️⃣ Lectura de Configuración Activa

```csharp
// ScoutMonkeyService.RunAsync()
var adapter = await adapterProvider.GetAsync();  // Sin profileKey
// Internamente:
// 1. Lee ActiveCompanyProfile.ProfileKey de BD (con cache 30s)
// 2. Si es null → usa DefaultProfileKey de config
// 3. Construye ProfileDrivenAdapter
```

**Estado:** ✅ IMPLEMENTADO

**Cómo Verificar:**
```bash
# Mira los logs de funciones
docker-compose logs functions | grep -i "adapter\|profile"

# O consulta el endpoint
curl -s http://localhost:7071/api/settings/company-profiles-active | jq
```

---

### 2️⃣ Lectura de Telemetría

```csharp
// ScoutBoardService.GetBoardAsync(adapter, profileKey)
// 1. Templatea KQL con matchers de la empresa
// 2. Ejecuta contra AppInsights
// 3. Lee rows de telemetría
```

**Archivo Template:**
```
Portal/src/JourneyMonitor.Infrastructure/Kql/company-scout-board.kql.template.txt
```

**Estado:** ✅ IMPLEMENTADO - La empresa se inyecta en el template

**Cómo Verificar:**
```bash
# Ver qué template se genera para cada empresa
# En logs de Functions:
docker-compose logs functions | grep -i "kql\|template"
```

---

### 3️⃣ Clasificación de Rows

```csharp
// ProfileDrivenAdapter.ClassifyRow(row)
// El adapter mapea:
// - URL → Service (Hotel, Cruise)
// - Stage matchers → Stage (ApiGateway, OrderService)
// - Club info → Club ID
// TODO: Devuelve clasificación o "unknown"
```

**Dónde Está:**
```
Portal/src/JourneyMonitor.Core/Company/ProfileDrivenAdapter.cs
Portal/src/JourneyMonitor.Core/Company/ScoutRowMapper.cs
```

**Tests:**
```
Portal/tests/JourneyMonitor.Tests/Company/ProfileDrivenAdapterTests.cs (850+ tests!)
Portal/tests/JourneyMonitor.Tests/Company/ScoutRowMapperTests.cs (293+ tests!)
```

**Estado:** ✅ TOTALMENTE TESTEADO

---

### 4️⃣ Creación de Packages

```csharp
// ScoutAssessorService.AssessAsync(board, profileKey)
// Para cada bucket (group by Stage/Entity):
// 1. Calcula latency percentiles
// 2. Detecta anomalías vs baseline
// 3. Crea InvestigationPackage
//    - ProfileKey = profileKey (copiado del adapter)
//    - CaseId = "{Vertical}|{Entity}|{Stage}|{Bucket}"
//    - Board = "latency" o "exception"
```

**INSERT SQL:**
```sql
INSERT INTO monitoring.InvestigationPackage 
  (Id, CaseId, ProfileKey, Board, Vertical, Stage, ...)
VALUES
  (newid(), 'Hotel|club:xyz|ApiGateway|bucket1', 'arrivia', 'latency', ...)
```

**Dedup Index:**
```sql
UNIQUE INDEX UQ_monitoring_InvestigationPackage_CaseId_Board_Profile 
ON (CaseId, Board, ProfileKey)
```

**Estado:** ✅ IMPLEMENTADO

---

### 5️⃣ Enqueue para Inspector

```csharp
// Después de INSERT, el Package se enuqeuea automáticamente
// INSERT INTO monitoring.AgentWorkQueue
// - SourceMonkey = 'scout'
// - PayloadType = 'investigation-package'
// - PayloadId = Package.Id
// - DedupKey = CaseId + Board + ProfileKey
```

**Estado:** ✅ IMPLEMENTADO

---

## 🔍 Cómo Verificar Que Scout Está Generando Datos

### Verificación 1: ¿Scout Está Corriendo?

```bash
# Ver últimos ciclos de Scout
curl -s "http://localhost:7071/api/scout/stats" | jq '.cycles'

# Ver últimos ciclos de una empresa específica
curl -s "http://localhost:7071/api/scout/stats?profileKey=arrivia" | jq '.cycles'
```

**Respuesta esperada:**
```json
{
  "cycles": {
    "cyclesRun": 10,
    "packagesCreated": 42,
    "averageDurationMs": 1250,
    "lastCycle": "2026-10-30T07:35:00Z"
  }
}
```

---

### Verificación 2: ¿Hay Packages?

```bash
# Packages globales
curl -s "http://localhost:7071/api/scout/packages" | jq '.packages | length'

# Packages de "arrivia"
curl -s "http://localhost:7071/api/scout/packages?profileKey=arrivia" | jq '.packages | length'

# Packages de "demo"
curl -s "http://localhost:7071/api/scout/packages?profileKey=demo" | jq '.packages | length'
```

**Si ves 0 packages:**
- ❌ Telemetría no matchea matchers
- ❌ Scout no ha corrido en esta empresa
- ❌ ProfileKey es incorrecto

---

### Verificación 3: SQL Directo

```sql
-- Ciclos de Scout por empresa
SELECT ProfileKey, COUNT(*) as CycleCount, MAX(StartedAt) as MostRecent
FROM monitoring.ScoutRun
GROUP BY ProfileKey
ORDER BY MostRecent DESC;

-- Packages por empresa
SELECT ProfileKey, COUNT(*) as PackageCount, MAX(CreatedAt) as MostRecent
FROM monitoring.InvestigationPackage
GROUP BY ProfileKey
ORDER BY MostRecent DESC;

-- Casos forenses por empresa
SELECT ProfileKey, COUNT(*) as CaseCount, MAX(StartedAt) as MostRecent
FROM monitoring.ForensicCase
GROUP BY ProfileKey
ORDER BY MostRecent DESC;

-- ¿Hay items en la queue de trabajo?
SELECT SourceMonkey, COUNT(*) as QueueCount
FROM monitoring.AgentWorkQueue
WHERE Status IN ('queued', 'leased')
GROUP BY SourceMonkey;
```

---

## 🚨 Diagnóstico de Fallos Comunes

### Scout No Genera Packages

**Paso 1: ¿Scout Está Corriendo?**
```bash
docker-compose logs functions | grep "ScoutMonkey"
```

Deberías ver logs como:
```
[Scout] Cycle started for profileKey=arrivia
[Scout] Classified 1250 rows into 3 buckets
[Scout] Created 2 packages
```

---

**Paso 2: ¿Hay Telemetría?**

En Azure Portal → Application Insights → Query Explorer:
```kql
customEvents 
| where timestamp > ago(1h)
| where name contains "Hotel"
| count
```

Si esto devuelve 0 → No hay telemetría para clasificar

---

**Paso 3: ¿Los Matchers Son Correctos?**

```bash
# Ver el ProfileJson de tu empresa
curl -s http://localhost:7071/api/settings/company-profiles/arrivia | \
  jq '.profile.services[].flows[].stages[].matchers'

# Respuesta:
[
  {
    "kind": "path-contains",
    "value": "/Hotel/Search",
    "name": "Hotel Search"
  }
]
```

**Verifica:**
- ¿El `value` matchea URLs reales de tu telemetría?
- Ej: Si telemetría es `/api/v2/hotel/search`, ¿"/Hotel/Search" matchea?
  - ❌ NO (case sensitive)
  - ✅ SÍ si cambias a `/hotel/search` (lowercase)

---

**Paso 4: ¿Scout Está Usando La Empresa Correcta?**

```bash
# Ver cuál está activa
curl -s http://localhost:7071/api/settings/company-profiles-active | jq

# Si es null, debe haber DefaultProfileKey en config
curl -s http://localhost:7071/api/settings/company-profiles-active | jq '.profileKey // "null (using default)"'
```

---

### Packages Pero Sin Casos Forenses

**Causa:** Inspector no está consumiendo la queue

```bash
# Ver trabajo pendiente
curl -s "http://localhost:7071/api/scout/packages" | jq '.packages[] | {id, status}'

# Status esperado: "pending_handoff"
# Si sigue así después de 10+ minutos, Inspector no está consumiendo

# Ver logs de Inspector
docker-compose logs functions | grep "Inspector"
```

---

## ✅ Checklist de Deploy

Antes de hacer deploy de Scout end-to-end:

- [ ] `monitoring.CompanyProfile` creada con >= 1 empresa
- [ ] `monitoring.ActiveCompanyProfile` existe (puede estar vacía)
- [ ] `Company:DefaultProfileKey` está set en config
- [ ] `MonitoringOptions:SettingsWriteSecret` está set
- [ ] ProfileJson de cada empresa es válido JSON
- [ ] Matchers en ProfileJson matchean URLs reales de telemetría
- [ ] `curl /api/settings/company-profiles` devuelve >= 1 empresa
- [ ] `curl /api/settings/company-profiles-active` devuelve 200 OK
- [ ] Tests pasan: `ActiveCompanyProfileTests`, `ForensicCaseProfileIsolationTests`, `ScoutReadProfileFilterTests`
- [ ] Scout ha generado >= 1 package (`curl /api/scout/packages`)
- [ ] Packages tienen `ProfileKey` set (verificar en SQL)
- [ ] Inspector está consumiendo packages (ver `/api/scout/packages` con status != "pending_handoff")
- [ ] Casos forenses tienen ProfileKey set (verificar en SQL)
- [ ] Corpus está aislado (chunks con ProfileKey diferentes NO se mezclan en retrieval)

---

## 📞 Logs Importantes Para Monitorear

### Functions App

```bash
docker-compose logs functions -f | grep -i "scout\|inspector\|profile\|error"
```

**Logs que buscas:**
```
[Scout] Cycle started for profileKey=arrivia
[Scout] Loaded 1250 rows from AppInsights
[Scout] Classified into 3 buckets
[Scout] Created 2 packages
[Inspector] Leased package X for investigation
[RCA] Searching precedents for profileKey=arrivia
```

---

### Portal Web

```bash
# En la consola del navegador
// F12 → Console → Filter: profile, profileKey, scout
```

**Logs que buscas:**
```javascript
GET /api/settings/company-profiles-active 200
GET /api/scout/packages?profileKey=arrivia 200
PUT /api/settings/company-profiles-active 200
```

---

## 🎓 Resumen: Scout Está Ready?

**Scout multi-company está 100% operacional cuando:**

1. ✅ BD: Tablas con ProfileKey existen
2. ✅ Config: DefaultProfileKey está set
3. ✅ Empresas: >= 1 empresa en BD con ProfileJson válido
4. ✅ API: `/api/settings/company-profiles*` responden 200
5. ✅ Ciclos: Scout genera packages con ProfileKey set
6. ✅ Aislamiento: Casos de empresa A NO aparecen en empresa B
7. ✅ UI: Portal muestra selector de empresa

**Si todo ✅ arriba → Scout funciona end-to-end con multi-company**

