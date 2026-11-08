# AppInsights - Token Access by Tenant (Resumen Ejecutivo)

## 🎯 Tu Pregunta

> "Necesito que `cgareca@arrivia.com` obtenga un token de Azure para consultar logs de AppInsights de Arrivia solamente, y que `cgareca@ice.com` solo vea logs de Ice."

## ✅ Respuesta Corta

**SÍ, es totalmente posible.** 

Necesitas implementar un backend proxy que:
1. Valide el JWT del usuario (SSO)
2. Mapee su email a su tenant (empresa)
3. Obtenga un token de Azure usando Managed Identity
4. Consulte AppInsights en contexto del tenant
5. Retorne solo datos permitidos

---

## 3️⃣ Opciones de Implementación

### **OPCIÓN 1: Frontend Directo**
```
Complejidad: Baja (2-3 horas)
Seguridad: ❌ Débil
Recomendación: ❌ NO USAR en producción
```

### **OPCIÓN 2: Backend Proxy** ⭐ RECOMENDADO
```
Complejidad: Media (4-6 horas)
Seguridad: ✅ Fuerte
Costo: $0 (Managed Identity es gratis)
Recomendación: ✅ USAR ESTA
```

### **OPCIÓN 3: On-Behalf-Of Flow**
```
Complejidad: Alta (6-8 horas)
Seguridad: ✅ Máxima
Recomendación: ⚠️ Solo si necesitas RBAC sofisticado
```

---

## 🏗️ OPCIÓN 2 - ARQUITECTURA (LA RECOMENDADA)

### Componentes Necesarios

#### 1️⃣ **TenantResolver** - Mapear email a tenant
```csharp
// Entrada: cgareca@arrivia.com
// Salida: "arrivia"
//
// Mapeos:
// "arrivia.com" → "arrivia"
// "ice.com" → "ice"
// "acmecorp.com" → "acme"
```

#### 2️⃣ **AppInsightsQueryService** - Consultar AppInsights
```csharp
// Pasos:
// 1. Recibe: tenantKey ("arrivia") + query (KQL)
// 2. Obtiene config del tenant (subscription, resource group)
// 3. Crea DefaultAzureCredential (Managed Identity)
// 4. Obtiene token de Azure
// 5. Consulta AppInsights API
// 6. Retorna logs
```

#### 3️⃣ **AppInsightsController** - Endpoint API
```csharp
// Endpoint: POST /api/appinsights/logs
// 
// Flujo:
// 1. Recibe JWT del usuario
// 2. Extrae email del JWT
// 3. Valida JWT (firma Entera ID)
// 4. Mapea email → tenant (TenantResolver)
// 5. Consulta AppInsights (AppInsightsQueryService)
// 6. Retorna logs
```

#### 4️⃣ **Managed Identity** - Autenticación segura
```
Azure App Service:
  • Tiene identidad automática
  • Genera tokens sin almacenar secrets
  • Azure RBAC controla acceso
```

---

## 🔐 Flujo Completo - Visual

```
┌──────────────────┐
│ cgareca@         │
│ arrivia.com      │
└────────┬─────────┘
         │
         │ 1. Browser SSO
         │    (login Entera ID)
         ▼
┌──────────────────────────┐
│ Portal Frontend          │
│                          │
│ Obtiene JWT token:       │
│ {                        │
│   email: ...@arrivia     │
│   oid: user-id           │
│   ...                    │
│ }                        │
└────────┬─────────────────┘
         │
         │ 2. POST /api/appinsights/logs
         │    Authorization: Bearer <JWT>
         │    Body: { query, timeRange }
         │
         ▼
┌──────────────────────────────────────────┐
│ Portal Backend (AppInsightsController)  │
│                                         │
│ 1. Valida JWT (firma Entera ID)        │
│    ✓ Válido                            │
│                                         │
│ 2. Extrae email                        │
│    → cgareca@arrivia.com               │
│                                         │
│ 3. Llama TenantResolver                │
│    → GetTenantKeyAsync(email)          │
│    ← "arrivia"                         │
│                                         │
│ 4. Llama AppInsightsQueryService       │
│    → QueryLogsAsync("arrivia", ...)    │
│                                         │
│    a. Obtiene config                   │
│       • SubscriptionId: sub-xxx        │
│       • ResourceGroup: rg-arrivia-prod │
│       • AppInsightsName: ai-arrivia    │
│                                         │
│    b. Crea DefaultAzureCredential()    │
│       • Usa Managed Identity            │
│                                         │
│    c. Obtiene token Azure              │
│       • Scope: management.azure.com    │
│                                         │
│    d. Consulta AppInsights API         │
│       POST https://management.azure.com/...
│       Authorization: Bearer <token>    │
│                                         │
│ 5. Retorna logs                        │
│    HTTP 200 OK                         │
│    Body: [ { timestamp, ..., }, ... ] │
└──────────────────┬───────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────┐
│ Azure (AppInsights + RBAC)              │
│                                         │
│ 1. Recibe token                        │
│ 2. Valida token                        │
│ 3. Identifica: Managed Identity        │
│ 4. Comprueba RBAC                      │
│    → ¿Tiene "Reader" en ai-arrivia?   │
│    → SÍ ✓                              │
│ 5. Ejecuta query                       │
│ 6. Retorna logs                        │
└──────────────────┬───────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────┐
│ Frontend muestra logs                │
│ • Solo de Arrivia                    │
│ • Usuario puede ver tendencias       │
│ • Puede exportar si necesita         │
└──────────────────────────────────────┘
```

---

## 🔒 Seguridad - ¿Por qué esto es seguro?

| Punto | Riesgo | Mitigación |
|-------|--------|-----------|
| **Token expuesto** | Alto | JWT en memory (no localStorage), solo en HTTPS |
| **Token falsificado** | Alto | Validado contra public keys de Entera ID |
| **Email alterado** | Alto | JWT es inmutable (protegido por firma) |
| **Acceso cross-tenant** | Medio | TenantResolver valida email domain |
| **Token sin permisos** | Medio | Managed Identity solo tiene Reader en su AppInsights |
| **Secrets comprometidos** | CERO | Managed Identity = sin secrets almacenados |
| **Queries maliciosas** | Bajo | LogsQueryClient parametriza queries |

---

## 📦 Qué Necesitas

### Software
- Visual Studio 2022 o VS Code
- .NET 6.0+ SDK
- Azure CLI

### Paquetes NuGet
```bash
dotnet add package Azure.Identity
dotnet add package Azure.Monitor.Query
```

### Azure Resources
- 2 Application Insights (Arrivia e Ice)
- 1 App Service (donde hostes Portal)
- Managed Identity habilitada en App Service

### Configuración
- SSO Entera ID funcionando
- JWT tokens con claim "email"

---

## ⏱️ Tiempo de Implementación

| Fase | Tiempo | Tareas |
|------|--------|--------|
| **LOCAL** | 1-2h | Crear 3 clases C#, registrar en Program.cs |
| **AZURE** | 30min | Habilitar Managed Identity, asignar RBAC |
| **TESTING** | 1-2h | Probar con 2 usuarios, verificar aislamiento |
| **DEPLOY** | 1h | Push a staging, verificación, push a prod |
| **TOTAL** | 4-6h | Listo para producción |

---

## 🚀 Pasos de Implementación

### PASO 1: Instalar Paquetes (5 min)
```bash
cd Portal
dotnet add package Azure.Identity
dotnet add package Azure.Monitor.Query
```

### PASO 2: Crear TenantResolver (30 min)
Archivo: `Portal/src/JourneyMonitor.Core/AppInsights/TenantResolver.cs`

```csharp
public class TenantResolver : ITenantResolver
{
    public async Task<string> GetTenantKeyAsync(string userEmail)
    {
        var domain = userEmail.Split('@').Last().ToLower();
        return domain switch
        {
            "arrivia.com" => "arrivia",
            "ice.com" => "ice",
            _ => throw new UnauthorizedAccessException()
        };
    }

    public async Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey)
    {
        return tenantKey switch
        {
            "arrivia" => new AppInsightsTenant 
            { 
                SubscriptionId = "sub-xxx",
                ResourceGroup = "rg-arrivia-prod",
                AppInsightsName = "ai-arrivia-prod"
            },
            "ice" => new AppInsightsTenant
            {
                SubscriptionId = "sub-yyy",
                ResourceGroup = "rg-ice-prod",
                AppInsightsName = "ai-ice-prod"
            },
            _ => throw new KeyNotFoundException()
        };
    }
}
```

### PASO 3: Crear AppInsightsQueryService (30 min)
Archivo: `Portal/src/JourneyMonitor.Core/AppInsights/AppInsightsQueryService.cs`

```csharp
public class AppInsightsQueryService : IAppInsightsQueryService
{
    private readonly ITenantResolver _tenantResolver;

    public async Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
        string tenantKey,
        string query,
        QueryTimeRange timeRange)
    {
        var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
        var credential = new DefaultAzureCredential(); // Managed Identity
        var client = new LogsQueryClient(credential);
        
        var resourceId = $"/subscriptions/{config.SubscriptionId}" +
                        $"/resourceGroups/{config.ResourceGroup}" +
                        $"/providers/microsoft.insights/components/{config.AppInsightsName}";

        return await client.QueryResourceAsync(
            resourceId: resourceId,
            kuery: query,
            timeRange: timeRange);
    }
}
```

### PASO 4: Crear Controller API (30 min)
Archivo: `Portal/src/JourneyMonitor.Functions/Functions/AppInsightsController.cs`

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize] // Requiere JWT
public class AppInsightsController : ControllerBase
{
    private readonly IAppInsightsQueryService _queryService;
    private readonly ITenantResolver _tenantResolver;

    [HttpPost("logs")]
    public async Task<IActionResult> QueryLogs([FromBody] LogsQueryRequest request)
    {
        // 1. Obtener email del JWT
        var email = User.FindFirst(ClaimTypes.Email).Value;
        
        // 2. Mapear a tenant
        var tenantKey = await _tenantResolver.GetTenantKeyAsync(email);
        
        // 3. Consultar AppInsights
        var result = await _queryService.QueryLogsAsync(
            tenantKey,
            request.Query,
            request.TimeRange);
        
        return Ok(result);
    }
}
```

### PASO 5: Registrar en Program.cs (10 min)
```csharp
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();

app.UseAuthentication();
app.UseAuthorization();
```

### PASO 6: Setup Azure (30 min)
```bash
# Habilitar Managed Identity
az webapp identity assign \
  --name your-app-service \
  --resource-group your-rg

# Obtener Object ID
$oid = az webapp identity show \
  --name your-app-service \
  --resource-group your-rg \
  --query principalId -o tsv

# Asignar Reader role a Arrivia AppInsights
az role assignment create \
  --assignee-object-id $oid \
  --role "Monitoring Reader" \
  --scope "/subscriptions/arrivia-sub/resourceGroups/rg-arrivia-prod/providers/microsoft.insights/components/ai-arrivia-prod"

# Asignar Reader role a Ice AppInsights
az role assignment create \
  --assignee-object-id $oid \
  --role "Monitoring Reader" \
  --scope "/subscriptions/ice-sub/resourceGroups/rg-ice-prod/providers/microsoft.insights/components/ai-ice-prod"
```

### PASO 7: Testing (1-2 horas)

#### Test 1: Health Check
```bash
curl https://localhost:7001/api/appinsights/health
# Response: {"status":"healthy","timestamp":"..."}
```

#### Test 2: Query Logs (Usuario Arrivia)
```bash
curl -X POST https://localhost:7001/api/appinsights/logs \
  -H "Authorization: Bearer <JWT-ARRIVIA>" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "customEvents | where timestamp > ago(7d) | take 10",
    "timeRange": {...}
  }'

# Expected: ✅ 200 OK con logs de Arrivia
```

#### Test 3: Query Logs (Usuario Ice)
```bash
curl -X POST https://localhost:7001/api/appinsights/logs \
  -H "Authorization: Bearer <JWT-ICE>" \
  -H "Content-Type: application/json" \
  -d '{...}'

# Expected: ✅ 200 OK con logs de Ice
```

#### Test 4: Cross-Tenant Attack (Usuario Ice intenta Arrivia)
```bash
# Modificar TenantResolver para retornar "arrivia"
# (simular ataque)

# Expected: ❌ 403 Forbidden (Managed Identity no tiene permisos)
```

---

## 📋 Checklist Final

### Implementación
- [ ] Instalar Azure.Identity + Azure.Monitor.Query
- [ ] Crear TenantResolver.cs
- [ ] Crear AppInsightsQueryService.cs
- [ ] Crear AppInsightsController.cs
- [ ] Registrar servicios en Program.cs
- [ ] Configurar appsettings.json

### Azure Setup
- [ ] Habilitar Managed Identity en App Service
- [ ] Obtener Object ID de Managed Identity
- [ ] Asignar Reader role a AppInsights de Arrivia
- [ ] Asignar Reader role a AppInsights de Ice
- [ ] Verificar roles con Azure CLI

### Testing
- [ ] Test health endpoint
- [ ] Test query logs con usuario Arrivia
- [ ] Test query logs con usuario Ice
- [ ] Test cross-tenant (debe fallar)
- [ ] Test error cases (token inválido, tenant inexistente)
- [ ] Verificar logs en Application Insights

### Deployment
- [ ] Test en staging
- [ ] Load test
- [ ] Security review
- [ ] Deploy a producción

---

## 💡 Preguntas Frecuentes

### ¿Por qué Managed Identity y no un App Secret?

**Secrets:**
- ❌ Necesitan almacenarse en appsettings
- ❌ Riesgo: si alguien accede al servidor, obtiene secret
- ❌ Necesitan rotación manual
- ❌ Inseguro para producción

**Managed Identity:**
- ✅ Sin secrets almacenados
- ✅ Token generado automáticamente cada hora
- ✅ Sin necesidad de rotación manual
- ✅ Audit automático en Azure
- ✅ Recomendado por Microsoft

### ¿Qué pasa si modifico el email en el JWT?

**Resultado:** Falla con 401 Unauthorized

**Por qué:**
- JWT tiene firma (algoritmo RS256)
- Si modificas algún claim, la firma no coincide
- Middleware rechaza

### ¿Qué pasa si intento acceder al AppInsights de otro tenant?

**Resultado:** Falla con 403 Forbidden

**Secuencia:**
1. Usuario Ice intenta acceder a logs de Arrivia
2. Backend mapea email → "ice"
3. Intenta consultar AppInsights de Arrivia con token de Managed Identity
4. Azure RBAC: ¿Token tiene permisos en ai-arrivia? NO
5. Azure rechaza: 403 Forbidden

### ¿Cuál es el costo?

- **Managed Identity:** $0/mes (incluido en App Service)
- **AppInsights:** Depende volumen (típicamente $2-10/GB)
- **Log storage:** Depende tamaño ($0.50-1 USD/GB retenidos)

**Estimado:** $50-200/mes según volumen de logs

### ¿Puedo cachear tokens?

**Sí, pero:**
- No es necesario (Azure SDK cachea automáticamente)
- DefaultAzureCredential maneja refresh automático
- Tokens expire cada ~3600 segundos (1 hora)
- Recomendación: No cachear manualmente

### ¿Qué pasa si AppInsights cae?

**Resultado:** Falla con 500 Service Unavailable

**Mitigación:**
- Implementar retry logic con exponential backoff
- Cachear últimos resultados conocidos
- Mostrar al usuario: "Datos pueden estar desactualizados"

---

## 📚 Recursos Incluidos

### Documentación Técnica
1. **APPINSIGHTS_TOKEN_ACCESS.md** (19 KB)
   - Implementación completa
   - Código copy-paste
   - Explicaciones línea por línea

2. **APPINSIGHTS_SETUP_STEPBYSTEP.md** (28 KB)
   - Setup local detallado
   - Comandos Azure CLI
   - Troubleshooting guide

3. **APPINSIGHTS_TOKEN_QUICK_GUIDE.md** (9 KB)
   - Resumen de opciones
   - FAQ rápidas
   - Decision matrix

---

## 🎯 Conclusión

Tienes **3 opciones**:

| Opción | Tiempo | Seguridad | Recomendación |
|--------|--------|-----------|---------------|
| Frontend Directo | 2-3h | ❌ Débil | ❌ NO USAR |
| Backend Proxy | 4-6h | ✅ Fuerte | ✅ **USA ESTA** |
| On-Behalf-Of | 6-8h | ✅ Máxima | ⚠️ Si RBAC |

**Recomendación final: OPCIÓN 2 (Backend Proxy)**

- Balance perfecto entre seguridad y complejidad
- 4-6 horas de implementación
- Listo para producción
- Mantenimiento mínimo
- Costo: $0 (Managed Identity es gratis)

---

## 🚀 Siguiente Paso

1. ✅ Lee este documento (10 min)
2. ✅ Lee APPINSIGHTS_SETUP_STEPBYSTEP.md (15 min)
3. 💻 Implementa Pasos 1-5 locales (2-3 horas)
4. ☁️ Setup Azure Pasos 1-3 (30 min)
5. 🧪 Testing (1-2 horas)
6. 🚀 Deploy

**Tiempo total: 4-6 horas**

¡Adelante!
