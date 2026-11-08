# Testing Multi-Company - Guía Práctica

## 🚀 Inicio Rápido

### 1. Verificar que el Sistema Está Corriendo

```bash
# Terminal 1: Start el stack local
cd Portal
docker-compose up
```

El stack incluye:
- SQL Server (localhost:1433)
- Functions app (http://localhost:7071)
- Portal web (http://localhost:3000)

### 2. Verificar que las Empresas Existen

```bash
# Ver todas las empresas registradas
curl -s http://localhost:7071/api/settings/company-profiles | jq

# Respuesta esperada:
{
  "profiles": [
    {
      "profileKey": "arrivia",
      "displayName": "Arrivia Test",
      "enabled": true,
      "version": 1,
      ...
    },
    {
      "profileKey": "demo",
      "displayName": "Demo Company",
      "enabled": true,
      "version": 1,
      ...
    }
  ]
}
```

### 3. Ver Cuál Está Activa AHORA

```bash
curl -s http://localhost:7071/api/settings/company-profiles-active | jq

# Respuesta esperada:
{
  "profileKey": null,  // "Nada activo → usa defaultProfileKey de config"
  "updatedAt": null,
  "updatedBy": null
}
```

---

## 📋 Flujo de Testing Básico

### Test 1: Scout Genera Datos Para Empresa A

```bash
# Activar "arrivia" (la empresa por defecto está sin activar)
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "Content-Type: application/json" \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "arrivia"}'

# Respuesta esperada: 200 OK
# {
#   "profileKey": "arrivia",
#   "updatedAt": "2026-10-30T...",
#   "updatedBy": "..."
# }
```

**¿Qué pasó internamente?**
- `monitoring.ActiveCompanyProfile` ahora tiene (ProfileKey='arrivia', UpdatedAt=now, UpdatedBy=...)
- `CompanyAdapterProvider` cachea por 30 segundos
- El siguiente ciclo de Scout usa el adapter de "arrivia"
- Los packages que cree tendrán `ProfileKey='arrivia'`

```bash
# Ver packages de arrivia
curl -s "http://localhost:7071/api/scout/packages?profileKey=arrivia" | jq '.packages | length'

# Ver packages SIN filter (todas las empresas)
curl -s "http://localhost:7071/api/scout/packages" | jq '.packages | length'

# Ver solo stats de arrivia
curl -s "http://localhost:7071/api/scout/stats?profileKey=arrivia" | jq
```

### Test 2: Cambiar a Empresa B

```bash
# Cambiar a "demo"
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "Content-Type: application/json" \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "demo"}'

# El cache se limpia automáticamente
# Los siguientes packages van a "demo"
```

**¿Por qué esto es importante?**
- Dos operadores miran el mismo servidor → ven la MISMA empresa
- El estado está en la BD, no en memoria local
- Si reinicia el Functions app → recupera lo que estaba en `ActiveCompanyProfile`

```bash
# Ver packages de demo
curl -s "http://localhost:7071/api/scout/packages?profileKey=demo" | jq '.packages | length'

# IMPORTANTE: Esto te muestra solo packages de "demo"
# Packages de "arrivia" no aparecen aquí
```

### Test 3: Casos Forenses (RCA) Están Aislados

```bash
# Crear un caso investigando un package de arrivia
# (Esto lo hace el Inspector automáticamente, pero aquí mostramos el aislamiento)

# Ver casos de arrivia
curl -s "http://localhost:7071/api/inspector/cases?profileKey=arrivia" | jq

# Ver casos de demo
curl -s "http://localhost:7071/api/inspector/cases?profileKey=demo" | jq

# Ver TODOS los casos (no recomendado en producción)
curl -s "http://localhost:7071/api/inspector/cases" | jq
```

**¿Qué se verifica?**
- Un caso de "arrivia" NUNCA aparece en results de "demo"
- Cada caso lleva su `ProfileKey` copied del package que investigaba
- La RCA de un caso busca precedents SOLO de esa empresa (+ shared corpus)

---

## 🧪 Tests Unitarios

### Ejecutar Tests de Multi-Company

```bash
# Todos los tests de multi-company
dotnet test --filter "Category=MultiCompany" -v

# O por categoría:
dotnet test Portal/tests/JourneyMonitor.Tests.csproj \
  --filter "ClassName~ActiveCompanyProfileTests" -v

# Isolation tests
dotnet test Portal/tests/JourneyMonitor.Tests.csproj \
  --filter "ClassName~ForensicCaseProfileIsolationTests" -v

# Scout filter tests
dotnet test Portal/tests/JourneyMonitor.Tests.csproj \
  --filter "ClassName~ScoutReadProfileFilterTests" -v
```

### Qué Verifican

**ActiveCompanyProfileTests (40+ tests)**
```csharp
// Precedencia
[Fact]
public async Task GetAsync_WithNoKey_PrefersTheActiveCompanyOverTheConfiguredDefault()
{
    var provider = Provider(new FakeActiveStore("profile-b"), defaultProfileKey: "profile-a");
    var adapter = await provider.GetAsync();
    Assert.Equal("profile-b", adapter.ProfileKey);
}

// Cache
[Fact]
public async Task GetAsync_CachesTheActiveCompanyUntilInvalidate()
{
    var active = new FakeActiveStore("profile-a");
    var provider = Provider(active);
    await provider.GetAsync();
    await provider.GetAsync();
    Assert.Equal(1, active.GetCalls);  // ← Cached, una sola lectura
    
    provider.Invalidate();
    await provider.GetAsync();
    Assert.Equal(2, active.GetCalls);  // ← Cache limpio, lee de nuevo
}

// Fallos degradados
[Fact]
public async Task GetAsync_WhenTheActiveStoreFaults_FallsBackToTheConfiguredDefault()
{
    var active = new FakeActiveStore("profile-b") { 
        Fault = new InvalidOperationException("pointer unreadable") 
    };
    var provider = Provider(active, defaultProfileKey: "profile-a");
    var adapter = await provider.GetAsync();
    Assert.Equal("profile-a", adapter.ProfileKey);  // ← Usa default, no falla
}
```

**ForensicCaseProfileIsolationTests (20+ tests)**
```csharp
// Casos aislados
[Fact]
public async Task ReadingCasesScopedToACompanyReturnsOnlyThatCompanys()
{
    var cases = await TwoCompaniesAsync();
    var forDemo = await cases.GetRecentAsync(50, profileKey: "demo");
    Assert.Equal("demo", Assert.Single(forDemo).ProfileKey);
}

// Corpus aislado
[Fact]
public async Task RetrievalNeverReturnsAnotherCompanysPrecedentsButDoesReturnTheSharedOnes()
{
    var retriever = new InMemoryKnowledgeRetriever(...);
    await retriever.IndexAsync(Chunk("arrivia timeout"), CancellationToken.None, "arrivia");
    await retriever.IndexAsync(Chunk("demo timeout"), CancellationToken.None, "demo");
    await retriever.IndexAsync(Chunk("shared timeout"), CancellationToken.None, "");

    var hits = await retriever.RetrieveAsync("timeout", topK: 10, "demo");
    
    Assert.Equal(2, hits.Count);  // demo + shared, NOT arrivia
    Assert.Contains(hits, h => h.Content.StartsWith("demo"));
    Assert.Contains(hits, h => h.Content.StartsWith("shared"));
    Assert.DoesNotContain(hits, h => h.Content.StartsWith("arrivia"));
}
```

---

## 🔴 Errores Comunes & Cómo Arreglarlos

### Error 1: "No company profile was requested, none is active"

```
Error: No company profile was requested, none is active, and no default is configured 
(activate one with PUT /api/settings/company-profiles-active, or set Company:DefaultProfileKey).
```

**Causa:** 
- No hay `Company:DefaultProfileKey` en config
- No hay empresa activada en `monitoring.ActiveCompanyProfile`

**Solución:**
```json
{
  "Company": {
    "DefaultProfileKey": "demo"
  }
}
```

Y/o:
```bash
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "demo"}'
```

---

### Error 2: "No enabled company profile is registered under 'typo'"

```
Error: No enabled company profile is registered under "typo".
```

**Causa:** 
- Intentaste activar una empresa que no existe
- Nombre mal escrito

**Solución:**
```bash
# Verifica primero cuáles existen
curl -s http://localhost:7071/api/settings/company-profiles | jq '.profiles[].profileKey'

# Luego activa una que exista
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "arrivia"}'
```

---

### Error 3: "401 Unauthorized" en PUT

```
Error: 401 Unauthorized
```

**Causa:**
- Falta el header `X-Settings-Write-Secret`
- El secret es incorrecto

**Solución:**
```bash
# Con el secret correcto
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "Content-Type: application/json" \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "arrivia"}'

# Verifica que el secret en config es "development-secret"
# En production usa un secret strong
```

---

### Error 4: Scout No Genera Packages

**Causa más probable:** El ProfileJson es inválido o los matchers no matchean la telemetría

**Verificación:**

```bash
# 1. ¿La empresa está activa?
curl -s http://localhost:7071/api/settings/company-profiles-active | jq

# 2. ¿El ProfileJson es válido? (debe tener "services" array)
curl -s http://localhost:7071/api/settings/company-profiles/arrivia | jq '.profile.services'

# 3. ¿Hay telemetría en AppInsights?
# (esto requiere acceso a Azure portal)

# 4. ¿Scout está corriendo?
# Ver logs del Functions app
docker-compose logs functions
```

**Fix común:** Actualizar el ProfileJson con matchers correctos

```bash
curl -X PUT http://localhost:7071/api/settings/company-profiles/arrivia \
  -H "Content-Type: application/json" \
  -H "X-Settings-Write-Secret: development-secret" \
  -d @- << 'EOF'
{
  "profileKey": "arrivia",
  "displayName": "Arrivia",
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
                  "value": "/Hotel/Search",
                  "name": "Hotel Search"
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
EOF
```

---

## 📊 Queries SQL para Verificar el Estado

### Ver Todas las Empresas

```sql
SELECT ProfileKey, DisplayName, Enabled, Version, UpdatedAt, UpdatedBy
FROM monitoring.CompanyProfile
ORDER BY UpdatedAt DESC;
```

### Ver Cuál Está Activa

```sql
SELECT ProfileKey, UpdatedAt, UpdatedBy
FROM monitoring.ActiveCompanyProfile;
```

### Ver Packages de Cada Empresa

```sql
SELECT ProfileKey, COUNT(*) as PackageCount
FROM monitoring.InvestigationPackage
GROUP BY ProfileKey
ORDER BY ProfileKey;
```

### Ver Casos de Cada Empresa

```sql
SELECT ProfileKey, COUNT(*) as CaseCount, MAX(StartedAt) as MostRecent
FROM monitoring.ForensicCase
GROUP BY ProfileKey
ORDER BY MostRecent DESC;
```

### Ver Corpus por Empresa

```sql
SELECT 
  CASE WHEN ProfileKey = '' THEN '[SHARED]' ELSE ProfileKey END as CompanyOrShared,
  COUNT(*) as ChunkCount
FROM monitoring.KnowledgeChunk
GROUP BY ProfileKey
ORDER BY ChunkCount DESC;
```

---

## 🎯 Caso de Uso: Onboarding Nueva Empresa (e.g., "acme")

### Paso 1: Crear el Perfil

```bash
curl -X POST http://localhost:7071/api/settings/company-profiles \
  -H "Content-Type: application/json" \
  -H "X-Settings-Write-Secret: development-secret" \
  -d @- << 'EOF'
{
  "profileKey": "acme",
  "displayName": "ACME Corporation",
  "services": [
    {
      "source": "Hotel",
      "flows": [
        {
          "flow": "Search",
          "stages": [
            {
              "stage": "Frontend",
              "matchers": [
                {
                  "kind": "url-contains",
                  "value": "acme.com/search",
                  "name": "Hotel Search"
                }
              ]
            },
            {
              "stage": "ServiceA",
              "matchers": [
                {
                  "kind": "path-contains",
                  "value": "/api/search",
                  "name": "Search API"
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
      { "id": "ACME-SFO", "name": "ACME San Francisco" }
    ]
  }
}
EOF
```

### Paso 2: Activar

```bash
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "acme"}'
```

### Paso 3: Scout Comienza a Generar Datos

El siguiente ciclo de Scout:
1. Lee `ActiveCompanyProfile` → "acme"
2. Construye adaptador de ACME
3. Lee telemetría de AppInsights
4. Clasifica según los matchers de ACME
5. Genera packages con `ProfileKey='acme'`

### Paso 4: Ver Datos de ACME

```bash
curl -s "http://localhost:7071/api/scout/packages?profileKey=acme" | jq

curl -s "http://localhost:7071/api/inspector/cases?profileKey=acme" | jq
```

---

## 🧠 Regla Mental

**Toda consulta en el sistema ahora es:**
- **Sin `?profileKey=`** → Todos los datos (desarrollo/auditoría)
- **Con `?profileKey=key`** → Solo datos de esa empresa (operación normal)
- **En la BD:** ProfileKey es parte de la PK (junto con CaseId, DedupKey, etc.)

Si tu query no filtra por ProfileKey, estás rompiendo el aislamiento → bug serio.

