# Generando Datos de Prueba Para Multi-Company

## 🎯 Objetivo

Crear datos de prueba para Scout de manera que dos empresas tengan datos completamente separados y aislados.

---

## 📊 Estrategia de Datos

### Escenario 1: Semillas SQL (Más Rápido)

**Dónde agregar:**
```
Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringCompanyProfile.sql
```

**Patrón:**
```sql
-- Semilla de empresa de test con datos hardcoded
IF NOT EXISTS (SELECT 1 FROM monitoring.CompanyProfile WHERE ProfileKey = 'test-arrivia')
BEGIN
    INSERT INTO monitoring.CompanyProfile (ProfileKey, DisplayName, Enabled, Version, ProfileJson)
    VALUES (
        N'test-arrivia',
        N'Arrivia Test Company',
        1,
        1,
        N'{
            "displayName": "Arrivia Test Company",
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
            ],
            "clubs": {
                "hotels": [
                    { "id": "ARRIVIA-TEST-1", "name": "Arrivia Test Club 1" }
                ]
            }
        }'
    );
END
GO
```

---

### Escenario 2: Datos en AppInsights (Realista)

Para que Scout REALMENTE funcione, necesita telemetría real en Application Insights.

**Pasos:**

#### A. Generar Telemetría Artificial

Crear un script que envíe eventos a AppInsights:

```powershell
# generate-telemetry.ps1
param(
    [string]$InstrumentationKey = "your-ai-key",
    [string]$Company = "arrivia"
)

$headers = @{
    "Content-Type" = "application/x-www-form-urlencoded"
}

for ($i = 0; $i -lt 100; $i++) {
    $method = @("GET", "POST", "PUT")[Get-Random -Minimum 0 -Maximum 3]
    $latency = Get-Random -Minimum 50 -Maximum 5000
    
    # Simular URLs diferentes por empresa
    $urls = @(
        "https://arrivia-api.example.com/Hotel/Search?club=ARRIVIA-1",
        "https://arrivia-api.example.com/Hotel/Booking",
        "https://arrivia-api.example.com/api/v2/checkout"
    )
    
    if ($Company -eq "demo") {
        $urls = @(
            "https://demo-api.example.com/Hotel/Search?club=DEMO-1",
            "https://demo-api.example.com/Hotel/Booking",
            "https://demo-api.example.com/api/v2/checkout"
        )
    }
    
    $url = $urls[Get-Random -Minimum 0 -Maximum $urls.Length]
    $statusCode = if ((Get-Random -Minimum 1 -Maximum 100) -lt 95) { 200 } else { 500 }
    
    $body = @{
        "name" = "request_completed"
        "timestamp" = (Get-Date -AsUTC).ToString("O")
        "sampleRate" = 100
        "instrumentationKey" = $InstrumentationKey
        "tags" = @{
            "ai.application.ver" = "1.0"
            "ai.device.id" = "browser"
            "ai.internal.sdkVersion" = "javascript:2.8.1"
            "ai.session.id" = [guid]::NewGuid().ToString()
            "ai.user.id" = "user_$(Get-Random -Minimum 1000 -Maximum 9999)"
        }
        "data" = @{
            "baseType" = "RequestData"
            "baseData" = @{
                "id" = "|$(New-Guid)"
                "name" = "$method $url"
                "source" = "Browser"
                "duration" = "00:00:$($latency.ToString('00').Substring(0,2))"
                "responseCode" = $statusCode
                "success" = ($statusCode -lt 400)
                "url" = $url
                "properties" = @{
                    "company" = $Company
                    "environment" = "production"
                }
            }
        }
    } | ConvertTo-Json -Depth 10
    
    # Enviar a AppInsights
    Invoke-WebRequest `
        -Uri "https://dc.applicationinsights.azure.com/v2/track" `
        -Method POST `
        -Headers $headers `
        -Body $body | Out-Null
    
    Write-Host "Sent telemetry $i for $Company (HTTP $statusCode, ${latency}ms)"
    Start-Sleep -Milliseconds 100
}

Write-Host "Done generating telemetry for $Company"
```

**Ejecutar:**
```powershell
.\generate-telemetry.ps1 -InstrumentationKey "your-key" -Company "arrivia"
.\generate-telemetry.ps1 -InstrumentationKey "your-key" -Company "demo"
```

---

#### B. O Usar Azure CLI Para Generar Data

```bash
#!/bin/bash

RESOURCE_GROUP="your-rg"
WORKSPACE_NAME="your-ai-workspace"
COMPANY="arrivia"

# Query para insertar datos simulados (via Azure Monitor)
az monitor metrics declare \
  --resource-group $RESOURCE_GROUP \
  --name "CustomMetric_${COMPANY}" \
  --value 100 \
  --timestamp "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
```

---

### Escenario 3: Poblamiento Controlado Vía API

**Script que popula la BD directamente (para desarrollo):**

```csharp
// Tests/JourneyMonitor.Tests/EndToEnd/TestDataGenerator.cs

public class TestDataGenerator
{
    private readonly SqlConnection _connection;
    
    public async Task PopulateMultiCompanyDataAsync()
    {
        // 1. Crear empresas
        await CreateCompanyProfileAsync("arrivia", ArriviaProfile());
        await CreateCompanyProfileAsync("demo", DemoProfile());
        
        // 2. Activar una
        await SetActiveCompanyAsync("arrivia");
        
        // 3. Generar packages de prueba
        await GenerateInvestigationPackagesAsync("arrivia", count: 5);
        await GenerateInvestigationPackagesAsync("demo", count: 3);
        
        // 4. Generar casos de prueba
        await GenerateForensicCasesAsync("arrivia", count: 2);
        await GenerateForensicCasesAsync("demo", count: 1);
        
        // 5. Verificar aislamiento
        var arriviaPackages = await GetPackagesAsync("arrivia");
        var demoPackages = await GetPackagesAsync("demo");
        
        Assert.Equal(5, arriviaPackages.Count);
        Assert.Equal(3, demoPackages.Count);
    }
    
    private string ArriviaProfile() => @"{
        ""displayName"": ""Arrivia Test Company"",
        ""services"": [
            {
                ""source"": ""Hotel"",
                ""flows"": [
                    {
                        ""flow"": ""Search"",
                        ""stages"": [
                            {
                                ""stage"": ""ApiGateway"",
                                ""matchers"": [
                                    { ""kind"": ""path-contains"", ""value"": ""/Hotel/Search"" }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    }";
    
    private string DemoProfile() => @"{
        ""displayName"": ""Demo Company"",
        ""services"": [
            {
                ""source"": ""Hotel"",
                ""flows"": [
                    {
                        ""flow"": ""Search"",
                        ""stages"": [
                            {
                                ""stage"": ""Frontend"",
                                ""matchers"": [
                                    { ""kind"": ""url-contains"", ""value"": ""demo.example.com"" }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    }";
    
    private async Task GenerateInvestigationPackagesAsync(string profileKey, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var package = new
            {
                Id = Guid.NewGuid(),
                CaseId = $"Hotel|club:{profileKey}-{i}|ApiGateway|bucket1",
                ProfileKey = profileKey,
                Board = "latency",
                Vertical = "Hotel",
                Stage = "ApiGateway",
                Source = "AppInsights",
                EntityKey = $"club:{profileKey}-{i}",
                EntityKind = "club",
                ClubId = $"{profileKey}-{i}",
                ClubResolvedBy = "club-id",
                Priority = "high",
                PriorityRank = 1,
                Reason = $"P95 latency exceeds threshold (test data for {profileKey})",
                WindowFrom = DateTime.UtcNow.AddHours(-1),
                WindowTo = DateTime.UtcNow,
                DeterministicJson = @"{}",
                AssessmentJson = @"{}",
                Status = "pending_handoff",
                ScoutRunId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow
            };
            
            await _connection.ExecuteAsync(
                @"INSERT INTO monitoring.InvestigationPackage 
                  (Id, CaseId, ProfileKey, Board, Vertical, Stage, Source, EntityKey, 
                   EntityKind, ClubId, ClubResolvedBy, Priority, PriorityRank, Reason,
                   WindowFrom, WindowTo, DeterministicJson, AssessmentJson, Status,
                   ScoutRunId, CreatedAt)
                  VALUES (@Id, @CaseId, @ProfileKey, @Board, @Vertical, @Stage, @Source,
                          @EntityKey, @EntityKind, @ClubId, @ClubResolvedBy, @Priority,
                          @PriorityRank, @Reason, @WindowFrom, @WindowTo, @DeterministicJson,
                          @AssessmentJson, @Status, @ScoutRunId, @CreatedAt)",
                package);
        }
    }
}
```

---

## 🧪 Verificación de Aislamiento

Después de generar datos, verificar que están aislados:

```bash
# 1. ¿Packages de cada empresa?
curl -s "http://localhost:7071/api/scout/packages?profileKey=arrivia" | \
  jq '.packages | length'
# Debe ser 5

curl -s "http://localhost:7071/api/scout/packages?profileKey=demo" | \
  jq '.packages | length'
# Debe ser 3

# 2. ¿Casos de cada empresa?
curl -s "http://localhost:7071/api/inspector/cases?profileKey=arrivia" | \
  jq '.cases | length'
# Debe ser >= 2

# 3. ¿Sin ?profileKey= muestra TODO?
curl -s "http://localhost:7071/api/scout/packages" | \
  jq '.packages | length'
# Debe ser 8 (5 + 3)

# 4. ¿Corpus está aislado? (SQL)
sqlite3 journeymonitor.db "
  SELECT profileKey, COUNT(*) FROM KnowledgeChunk 
  WHERE profileKey IN ('arrivia', 'demo', '')
  GROUP BY profileKey;
"
```

---

## 🔄 Workflow de Testing Completo

### 1. Limpiar Datos Previos

```sql
-- En SQL Server
DELETE FROM monitoring.InvestigationPackage;
DELETE FROM monitoring.ScoutRun;
DELETE FROM monitoring.ForensicCase;
DELETE FROM monitoring.AgentWorkQueue;
DELETE FROM monitoring.KnowledgeChunk;
DELETE FROM monitoring.ActiveCompanyProfile;
-- NO elimines CompanyProfile, son las definiciones
```

### 2. Activar Empresa

```bash
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "arrivia"}'
```

### 3. Generar Telemetría (Opción A: AppInsights)

```powershell
.\generate-telemetry.ps1 -Company "arrivia" -Count 100
.\generate-telemetry.ps1 -Company "demo" -Count 100
```

### 4. Esperar a Que Scout Corra

```bash
# Monitorea los logs
docker-compose logs functions -f | grep -i scout
```

### 5. Verificar Resultados

```bash
# ¿Scout generó packages?
curl -s "http://localhost:7071/api/scout/packages?profileKey=arrivia" | jq '.packages | length'

# ¿Inspector consumió packages?
curl -s "http://localhost:7071/api/scout/packages?profileKey=arrivia" | \
  jq '.packages[] | select(.status != "pending_handoff") | .status' | wc -l

# ¿RCA generó casos?
curl -s "http://localhost:7071/api/inspector/cases?profileKey=arrivia" | jq '.cases | length'
```

### 6. Cambiar a Segunda Empresa

```bash
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: development-secret" \
  -d '{"profileKey": "demo"}'
```

### 7. Verificar Aislamiento

```bash
# ¿Demo NO ve datos de Arrivia?
curl -s "http://localhost:7071/api/scout/packages?profileKey=demo" | \
  jq '.packages[] | .profileKey' | sort | uniq -c
# Debe mostrar solo "demo", nunca "arrivia"

curl -s "http://localhost:7071/api/inspector/cases?profileKey=demo" | \
  jq '.cases[] | .profileKey' | sort | uniq -c
# Debe mostrar solo "demo", nunca "arrivia"
```

---

## 📊 Ejemplo: Datos de Prueba Específicos

### Scout Cycle Data

```sql
-- Insertar ciclo de Scout para "arrivia"
INSERT INTO monitoring.ScoutRun (
    Id, ProfileKey, StartedAt, CompletedAt, Status, WindowFrom, WindowTo,
    BoardRowsEvaluated, CandidatesFound, PackagesCreated, DurationMs
)
VALUES (
    NEWID(), 'arrivia',
    DATETIMEOFFSET('2026-10-30T07:00:00', '+00:00'),
    DATETIMEOFFSET('2026-10-30T07:05:00', '+00:00'),
    'clear',
    DATETIMEOFFSET('2026-10-30T06:00:00', '+00:00'),
    DATETIMEOFFSET('2026-10-30T07:00:00', '+00:00'),
    1250, 3, 2, 300000
);

-- Insertar ciclo de Scout para "demo"
INSERT INTO monitoring.ScoutRun (
    Id, ProfileKey, StartedAt, CompletedAt, Status, WindowFrom, WindowTo,
    BoardRowsEvaluated, CandidatesFound, PackagesCreated, DurationMs
)
VALUES (
    NEWID(), 'demo',
    DATETIMEOFFSET('2026-10-30T07:03:00', '+00:00'),
    DATETIMEOFFSET('2026-10-30T07:06:00', '+00:00'),
    'clear',
    DATETIMEOFFSET('2026-10-30T06:00:00', '+00:00'),
    DATETIMEOFFSET('2026-10-30T07:00:00', '+00:00'),
    980, 2, 1, 250000
);
```

---

## ✅ Checklist: Datos de Prueba Listos?

- [ ] `monitoring.CompanyProfile` tiene >= 2 empresas
- [ ] `monitoring.ActiveCompanyProfile` tiene un ProfileKey set
- [ ] `monitoring.ScoutRun` tiene >= 1 ciclo por empresa
- [ ] `monitoring.InvestigationPackage` tiene >= 1 package por empresa
- [ ] `monitoring.ForensicCase` tiene >= 1 caso por empresa
- [ ] `monitoring.KnowledgeChunk` tiene chunks con ProfileKey = '' (shared) y profileKey específicos
- [ ] API `/api/scout/packages?profileKey=arrivia` devuelve solo packages de arrivia
- [ ] API `/api/scout/packages?profileKey=demo` devuelve solo packages de demo
- [ ] API `/api/inspector/cases?profileKey=arrivia` devuelve solo casos de arrivia
- [ ] API `/api/inspector/cases?profileKey=demo` devuelve solo casos de demo
- [ ] SQL query: `SELECT DISTINCT ProfileKey FROM InvestigationPackage` devuelve arrivia, demo
- [ ] SQL query: `SELECT DISTINCT ProfileKey FROM KnowledgeChunk` devuelve arrivia, demo, (empty)

---

## 🎯 Próximo Paso

Una vez que tienes datos de prueba:

1. Ejecuta `dotnet test` y verifica que todos los tests multi-company pasan
2. Abre el Portal en browser y verifica que el dropdown de empresa funciona
3. Cambio entre empresas y verifica que los datos cambian
4. Revisa los logs para ver que Scout está usando el ProfileKey correcto

