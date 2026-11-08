# AppInsights Token Access - Quick Decision Guide

## 🎯 La Pregunta

> "Necesito que cgareca@arrivia.com obtenga un token para ver logs de AppInsights de Arrivia solamente, y cgareca@ice.com solo vea logs de Ice"

## ✅ Respuesta Corta

**SÍ, es totalmente posible.** Necesitas:

1. **Entra ID SSO** (tu usuario login)
2. **Backend proxy** (valida tenant del usuario)
3. **Azure Managed Identity** (obtiene token sin secrets)
4. **Tenant mapping** (email domain → empresa)

---

## 3️⃣ Opciones

### OPCIÓN 1: Frontend Directo a AppInsights
```
Ventajas:
  • Rápido (2-3 horas)
  • Menos código

Desventajas:
  • Token expuesto en navegador
  • Sin validación de permisos en servidor
  • Riesgoso para producción
  
RECOMENDACIÓN: ❌ NO usar en producción
```

### OPCIÓN 2: Backend Proxy (⭐ RECOMENDADO)
```
Flujo:
  1. cgareca@arrivia.com hace login
  2. Recibe JWT token con email
  3. Frontend llama: POST /api/appinsights/logs
  4. Backend extrae email del JWT
  5. Backend mapea email → TenantKey ("arrivia")
  6. Backend obtiene token Azure (Managed Identity)
  7. Backend consulta AppInsights con ese token
  8. Backend retorna solo logs de Arrivia
  
Ventajas:
  ✅ Token NUNCA expuesto
  ✅ Control total en servidor
  ✅ Validación de tenant
  ✅ Audit logging automático
  ✅ Balance seguridad/complejidad

Tiempo: 4-6 horas
RECOMENDACIÓN: ✅ USA ESTA
```

### OPCIÓN 3: On-Behalf-Of Flow (Token Exchange)
```
Ventajas:
  • Máxima seguridad
  • Token intercambiado (delegated)
  • Entera ID controla permisos

Desventajas:
  • Más complejo (6-8 horas)
  • Requiere app roles en Entera ID

RECOMENDACIÓN: ⚠️ Solo si tienes RBAC sofisticado
```

---

## 🔑 Key Components - OPCIÓN 2

### 1️⃣ TenantResolver (Mapear email → tenant)

```csharp
// Entrada: cgareca@arrivia.com
// Salida: { TenantKey: "arrivia", SubscriptionId: "...", ... }

public class TenantResolver : ITenantResolver
{
    public async Task<string> GetTenantKeyAsync(string userEmail)
    {
        var domain = userEmail.Split('@').Last();
        
        return domain switch
        {
            "arrivia.com" => "arrivia",
            "iceenterprise.com" => "ice",
            _ => throw new UnauthorizedAccessException()
        };
    }
}
```

### 2️⃣ AppInsightsQueryService (Consultar logs)

```csharp
// Obtiene token Azure y consulta AppInsights

public class AppInsightsQueryService : IAppInsightsQueryService
{
    public async Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
        string tenantKey,    // "arrivia"
        string query,        // KQL query
        QueryTimeRange time)
    {
        var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
        var credential = new DefaultAzureCredential(); // Managed Identity
        var client = new LogsQueryClient(credential);
        
        return await client.QueryResourceAsync(
            resourceId: config.ResourceId,
            kuery: query,
            timeRange: time);
    }
}
```

### 3️⃣ AppInsightsController (API endpoint)

```csharp
[ApiController]
[Route("api/appinsights")]
[Authorize]  // Requiere JWT
public class AppInsightsController
{
    [HttpPost("logs")]
    public async Task<IActionResult> QueryLogs([FromBody] LogsQueryRequest req)
    {
        // 1. Obtener email del JWT
        var email = User.FindFirst(ClaimTypes.Email).Value;
        // → cgareca@arrivia.com
        
        // 2. Mapear a tenant
        var tenantKey = await _tenantResolver.GetTenantKeyAsync(email);
        // → "arrivia"
        
        // 3. Consultar AppInsights
        var result = await _queryService.QueryLogsAsync(
            tenantKey,
            req.Query,
            req.TimeRange);
        // → Solo logs de Arrivia
        
        return Ok(result);
    }
}
```

---

## 🚀 Implementación (Pasos Rápidos)

### PASO 1: Instalar NuGet
```bash
cd Portal
dotnet add package Azure.Identity
dotnet add package Azure.Monitor.Query
```

### PASO 2: Crear archivos (copiar de APPINSIGHTS_TOKEN_ACCESS.md)
- `TenantResolver.cs`
- `AppInsightsQueryService.cs`
- `AppInsightsController.cs`

### PASO 3: Registrar en Program.cs
```csharp
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();
```

### PASO 4: Configurar Azure
```
App Service → Identity → Enable Managed Identity
AppInsights → Access Control → Add Reader role to App Service
```

### PASO 5: Test
```bash
# User: cgareca@arrivia.com
POST /api/appinsights/logs
Authorization: Bearer <JWT>

# ✅ Resultado: logs de Arrivia
# ❌ Si intentas Arrivia desde Ice user: 403 Forbidden
```

---

## 🔒 Seguridad - Checklist

```
✅ Frontend: Token en memory (no localStorage)
✅ Backend: Valida JWT signature
✅ Backend: Mapea email → tenant
✅ Backend: Usa Managed Identity (sin secrets)
✅ Azure: RBAC limita a AppInsights de cada tenant
✅ Audit: Logs en Azure Activity Log automáticamente
```

---

## 📊 Flujo Completo - Visual

```
USER: cgareca@arrivia.com
  │
  ├─→ Click "Ver Logs"
  │
  └─→ Frontend: GET /api/appinsights/logs
       Headers: Authorization: Bearer <JWT>
       │
       └─→ Backend:
            1. Valida JWT (firma Entera ID)
            2. Extrae: email = cgareca@arrivia.com
            3. Llama: GetTenantKeyAsync(email)
            4. Obtiene: tenantKey = "arrivia"
            5. Llama: GetTenantConfigAsync("arrivia")
            6. Obtiene: {SubscriptionId, ResourceGroup, ...}
            7. Crea: DefaultAzureCredential()
            8. Obtiene: Token Azure (Managed Identity)
            9. Llama: AppInsights API con token
               POST https://management.azure.com/subscriptions/.../providers/microsoft.insights/components/ai-arrivia-prod/api/query
            10. Azure valida: ¿Token tiene acceso a ai-arrivia-prod?
               → SÍ (Managed Identity tiene Reader role)
            11. Retorna: Logs de Arrivia
            │
            └─→ Backend: Retorna JSON al Frontend
                │
                └─→ Frontend: Muestra logs

SEGURIDAD:
  • Si usuario intenta email falso: JWT validation falla (401)
  • Si usuario Arrivia intenta Ice: Backend rechaza (403)
  • Si backend no tiene token: Azure rechaza (401)
```

---

## ❓ Preguntas Frecuentes

### ¿Por qué Managed Identity y no secrets?

```
❌ SECRETS (Mala idea):
  • Guardar client secret en appsettings
  • Riesgo: si alguien accede al servidor, obtiene secret
  • Necesita rotación manual

✅ MANAGED IDENTITY (Buena idea):
  • Azure asigna identidad al App Service
  • Token generado automáticamente cada hora
  • No hay secrets almacenados
  • Audit automático
```

### ¿Qué pasa si cambio email en el JWT?

```
❌ NO FUNCIONA:
  • Backend valida firma del JWT (Entera ID publica keys)
  • Si cambias email, firma no coincide
  • 401 Unauthorized
```

### ¿Y si alguien de Ice intenta acceder?

```
Opción A: Fuerza bruta email domain
  → TenantResolver rechaza (UnauthorizedAccessException)
  → 403 Forbidden

Opción B: Intenta cambiar token
  → Firma inválida
  → 401 Unauthorized

Opción C: Obtiene token Ice, intenta consultar Arrivia
  → Managed Identity solo tiene Reader en ai-ice-prod
  → Azure rechaza
  → 403 Forbidden
```

### ¿Cuánto cuesta en Azure?

```
Managed Identity: GRATIS
AppInsights queries: ~$2-5/GB (depende volumen)
Token caching: Reduce queries (ahorra $$)
```

---

## 📋 Checklist de Implementación

- [ ] Instalar Azure.Identity + Azure.Monitor.Query
- [ ] Crear TenantResolver.cs
- [ ] Crear AppInsightsQueryService.cs
- [ ] Crear AppInsightsController.cs
- [ ] Registrar servicios en Program.cs
- [ ] Configurar Managed Identity en App Service
- [ ] Asignar Reader role a cada AppInsights
- [ ] Test: Login como arrivia user
- [ ] Test: Consultar logs (debe funcionar)
- [ ] Test: Intentar como ice user (debe fallar 403)
- [ ] Test: Cambiar email en JWT (debe fallar 401)
- [ ] Documentar endpoints en Swagger
- [ ] Agregar logging/audit
- [ ] Deploy a staging

---

## 🎁 Archivos Incluidos

1. **APPINSIGHTS_TOKEN_ACCESS.md** (19 KB)
   - Implementación completa
   - Código copy-paste
   - Explicaciones detalladas

2. **APPINSIGHTS_TOKEN_QUICK_GUIDE.md** (Este archivo)
   - Resumen de opciones
   - Decision matrix
   - FAQ

---

## 🎯 Siguiente Paso

→ Lee `APPINSIGHTS_TOKEN_ACCESS.md` para implementar Opción 2
→ Copia los 3 archivos C# (.cs)
→ Registra en Program.cs
→ Test en local antes de deploy

**Estimado: 4-6 horas de implementación + 1 hora de testing**
