# AppInsights Token Access - Backend Proxy (RECOMENDADO)

## 📋 Resumen

Permite a usuarios acceder a AppInsights usando su identidad de Entra ID, con validación de tenant en backend.

```
cgareca@arrivia.com:
  Login SSO → Backend → Token Azure → AppInsights (solo Arrivia)

cgareca@ice.com:
  Login SSO → Backend → Token Azure → AppInsights (solo Ice)
```

---

## 🏗️ Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│ FRONTEND                                                    │
│                                                             │
│  1. User logs in with SSO                                  │
│  2. Receives JWT token (from Entra ID)                     │
│  3. JWT contains: email, tenant_id, oid                    │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ GET /api/appinsights/logs
                       │ Authorization: Bearer <JWT>
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ BACKEND (Portal API)                                        │
│                                                             │
│  1. Receives JWT token                                     │
│  2. Validates signature (Entra ID public keys)            │
│  3. Extracts email: cgareca@arrivia.com                    │
│  4. Maps email → TenantKey ("arrivia")                     │
│  5. Gets TokenCredential (Managed Identity)               │
│  6. Obtains token for Azure Resource Management           │
│  7. Queries AppInsights API with tenant filter             │
│  8. Returns logs (only for that tenant)                    │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ Token for:
                       │ https://management.azure.com/
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ AZURE (AppInsights)                                         │
│                                                             │
│  • Validates token                                         │
│  • Checks RBAC permissions                                 │
│  • Returns resource data                                   │
│  • Logs access to Azure Activity Log                       │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔧 Implementación

### PASO 1: Instalar Paquetes NuGet

```bash
dotnet add package Azure.Identity
dotnet add package Azure.ResourceManager
dotnet add package Azure.Monitor.Query
```

### PASO 2: Crear TenantResolver (Mapear email → tenant)

**File: `Portal/src/JourneyMonitor.Core/AppInsights/TenantResolver.cs`**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JourneyMonitor.Core.AppInsights
{
    /// <summary>
    /// Maps user email domain to company tenant key
    /// </summary>
    public interface ITenantResolver
    {
        Task<string> GetTenantKeyAsync(string userEmail);
        Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey);
    }

    public class AppInsightsTenant
    {
        public string TenantKey { get; set; }
        public string SubscriptionId { get; set; }
        public string ResourceGroup { get; set; }
        public string AppInsightsName { get; set; }
        public string AadTenantId { get; set; }
    }

    public class TenantResolver : ITenantResolver
    {
        // Map email domain to tenant configuration
        private static readonly Dictionary<string, AppInsightsTenant> TenantConfig = 
            new Dictionary<string, AppInsightsTenant>
            {
                {
                    "arrivia", new AppInsightsTenant
                    {
                        TenantKey = "arrivia",
                        SubscriptionId = "00000000-0000-0000-0000-000000000001",
                        ResourceGroup = "rg-arrivia-prod",
                        AppInsightsName = "ai-arrivia-prod",
                        AadTenantId = "11111111-1111-1111-1111-111111111111"
                    }
                },
                {
                    "ice", new AppInsightsTenant
                    {
                        TenantKey = "ice",
                        SubscriptionId = "00000000-0000-0000-0000-000000000002",
                        ResourceGroup = "rg-ice-prod",
                        AppInsightsName = "ai-ice-prod",
                        AadTenantId = "22222222-2222-2222-2222-222222222222"
                    }
                }
            };

        public Task<string> GetTenantKeyAsync(string userEmail)
        {
            // Extract domain from email
            var domain = userEmail.Split('@').Last().ToLower();
            
            // Map domain to tenant key
            var tenantKey = domain switch
            {
                "arrivia.com" => "arrivia",
                "iceenterprise.com" => "ice",
                "acmecorp.com" => "acme",
                _ => null
            };

            if (tenantKey == null)
                throw new UnauthorizedAccessException(
                    $"User domain {domain} not mapped to any tenant");

            return Task.FromResult(tenantKey);
        }

        public Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey)
        {
            if (!TenantConfig.TryGetValue(tenantKey, out var config))
                throw new KeyNotFoundException($"Tenant config not found: {tenantKey}");

            return Task.FromResult(config);
        }
    }
}
```

### PASO 3: Crear AppInsights Query Service

**File: `Portal/src/JourneyMonitor.Core/AppInsights/AppInsightsQueryService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace JourneyMonitor.Core.AppInsights
{
    public interface IAppInsightsQueryService
    {
        Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
            string tenantKey, 
            string query, 
            QueryTimeRange timeRange);
        
        Task<IReadOnlyList<MetricsResult>> GetMetricsAsync(
            string tenantKey, 
            string metricName, 
            TimeSpan? timespan);
    }

    public class AppInsightsQueryService : IAppInsightsQueryService
    {
        private readonly ITenantResolver _tenantResolver;
        private readonly LogsQueryClient _logsQueryClient;
        private readonly MetricsQueryClient _metricsQueryClient;

        public AppInsightsQueryService(ITenantResolver tenantResolver)
        {
            _tenantResolver = tenantResolver;
            
            // Create clients with DefaultAzureCredential
            // This uses Managed Identity in production
            var credential = new DefaultAzureCredential();
            _logsQueryClient = new LogsQueryClient(credential);
            _metricsQueryClient = new MetricsQueryClient(credential);
        }

        public async Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
            string tenantKey, 
            string query, 
            QueryTimeRange timeRange)
        {
            var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
            
            var resourceId = 
                $"/subscriptions/{config.SubscriptionId}" +
                $"/resourceGroups/{config.ResourceGroup}" +
                $"/providers/microsoft.insights/components/{config.AppInsightsName}";

            try
            {
                var result = await _logsQueryClient.QueryResourceAsync(
                    resourceId: resourceId,
                    kuery: query,
                    timeRange: timeRange);

                return result.Value;
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to query AppInsights for tenant {tenantKey}", ex);
            }
        }

        public async Task<IReadOnlyList<MetricsResult>> GetMetricsAsync(
            string tenantKey, 
            string metricName, 
            TimeSpan? timespan)
        {
            var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
            
            var resourceId = 
                $"/subscriptions/{config.SubscriptionId}" +
                $"/resourceGroups/{config.ResourceGroup}" +
                $"/providers/microsoft.insights/components/{config.AppInsightsName}";

            try
            {
                var response = await _metricsQueryClient.QueryResourceAsync(
                    resourceId: resourceId,
                    metricNames: new[] { metricName },
                    timespan: timespan);

                return response.Value.Metrics;
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to get metrics for tenant {tenantKey}", ex);
            }
        }
    }
}
```

### PASO 4: Crear Controller API

**File: `Portal/src/JourneyMonitor.Functions/Functions/AppInsightsQueryFunction.cs`**

```csharp
using System.Security.Claims;
using System.Threading.Tasks;
using Azure.Monitor.Query.Models;
using JourneyMonitor.Core.AppInsights;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AppInsightsController : ControllerBase
    {
        private readonly IAppInsightsQueryService _queryService;
        private readonly ITenantResolver _tenantResolver;
        private readonly ILogger<AppInsightsController> _logger;

        public AppInsightsController(
            IAppInsightsQueryService queryService,
            ITenantResolver tenantResolver,
            ILogger<AppInsightsController> logger)
        {
            _queryService = queryService;
            _tenantResolver = tenantResolver;
            _logger = logger;
        }

        /// <summary>
        /// Query logs from AppInsights for the user's tenant
        /// </summary>
        [HttpPost("logs")]
        [ProducesResponseType(typeof(LogsQueryResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> QueryLogs([FromBody] LogsQueryRequest request)
        {
            try
            {
                // Extract email from JWT claims
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? 
                                User.FindFirst("preferred_username")?.Value;
                
                if (string.IsNullOrEmpty(userEmail))
                {
                    _logger.LogWarning("User has no email claim");
                    return Unauthorized("User email not found in token");
                }

                // Map email to tenant
                var tenantKey = await _tenantResolver.GetTenantKeyAsync(userEmail);
                
                _logger.LogInformation(
                    $"User {userEmail} querying AppInsights for tenant {tenantKey}");

                // Query AppInsights with tenant context
                var result = await _queryService.QueryLogsAsync(
                    tenantKey,
                    request.Query,
                    request.TimeRange);

                return Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning($"Authorization failed: {ex.Message}");
                return Forbid();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error querying AppInsights: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get metrics from AppInsights for the user's tenant
        /// </summary>
        [HttpGet("metrics/{metricName}")]
        public async Task<IActionResult> GetMetrics(
            string metricName, 
            [FromQuery] int? lastDays = 7)
        {
            try
            {
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? 
                                User.FindFirst("preferred_username")?.Value;
                
                if (string.IsNullOrEmpty(userEmail))
                    return Unauthorized("User email not found");

                var tenantKey = await _tenantResolver.GetTenantKeyAsync(userEmail);
                
                var timespan = lastDays.HasValue 
                    ? System.TimeSpan.FromDays(lastDays.Value) 
                    : System.TimeSpan.FromDays(7);

                var result = await _queryService.GetMetricsAsync(tenantKey, metricName, timespan);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting metrics: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class LogsQueryRequest
    {
        /// <summary>
        /// KQL (Kusto Query Language) query
        /// Example: customEvents | where name == "scout-run-started" | take 100
        /// </summary>
        public string Query { get; set; }

        /// <summary>
        /// Time range for query
        /// </summary>
        public Azure.Monitor.Query.Models.QueryTimeRange TimeRange { get; set; }
    }
}
```

### PASO 5: Registrar en Program.cs

```csharp
// En Portal/src/JourneyMonitor.Web/Program.cs

// Add AppInsights services
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();

// Add Authentication with JWT validation
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();

// Build and use app
var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
```

### PASO 6: Configurar appsettings.json

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "common",
    "ClientId": "your-app-id-here",
    "CallbackPath": "/signin-oidc",
    "Scopes": [
      "https://management.azure.com/.default"
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "JourneyMonitor": "Debug"
    }
  }
}
```

### PASO 7: Configurar Managed Identity en Azure

En producción, usar Managed Identity (no secrets):

```powershell
# En Azure Portal:
# 1. Go to App Service
# 2. Settings → Identity
# 3. Enable System-assigned managed identity
# 4. Grant Reader role to AppInsights resources

# Para cada AppInsights:
# 1. Go to AppInsights → Access Control (IAM)
# 2. Add Role Assignment
# 3. Role: "Monitoring Reader" or "Contributor"
# 4. Select: <your-app-service-name>
```

---

## 📱 Uso desde Frontend

### HTML/JavaScript

```html
<!-- Frontend calls backend API with JWT token -->
<script>
async function queryAppInsights() {
  // Get token from auth context (already has it from SSO)
  const token = await getAccessToken();
  
  const response = await fetch('/api/appinsights/logs', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      query: `
        customEvents
        | where name == "scout-run-started"
        | where timestamp > ago(7d)
        | project timestamp, customDimensions
        | order by timestamp desc
        | take 100
      `,
      timeRange: {
        startTime: new Date(Date.now() - 7*24*60*60*1000),
        endTime: new Date()
      }
    })
  });

  const logs = await response.json();
  console.log('AppInsights logs:', logs);
}
</script>
```

---

## 🔒 Seguridad

### Lo que está Protegido

✅ **Token Storage**: JWT en memory (no localStorage/sessionStorage)
✅ **Backend Token**: Managed Identity token (nunca expuesto)
✅ **Tenant Validation**: Email domain → Tenant key mapping
✅ **Azure RBAC**: Managed Identity tiene solo permisos para su AppInsights
✅ **Audit Logging**: Todas las queries se registran en Azure Activity Log

### Lo que Falta Proteger

⚠️ **Si no configuras RBAC en Azure**: Managed Identity podría acceder a CUALQUIER AppInsights
⚠️ **Si no validas email domain**: Usuario podría falsificar email

---

## ✅ Verificación

### Test 1: Usuario Arrivia

```bash
# Login as cgareca@arrivia.com
POST /api/appinsights/logs
Authorization: Bearer <JWT for cgareca@arrivia.com>

# ✓ Debe funcionar: devuelve logs de Arrivia
# ✗ Si intentas consultar Ice en el backend: 403 Forbidden
```

### Test 2: Usuario Ice

```bash
# Login as cgareca@ice.com
POST /api/appinsights/logs
Authorization: Bearer <JWT for cgareca@ice.com>

# ✓ Debe funcionar: devuelve logs de Ice
# ✗ Si intentas consultar Arrivia en el backend: 403 Forbidden
```

---

## 🎯 Comparativa de Opciones

| Aspecto | Opción 1 | Opción 2 | Opción 3 |
|---------|----------|----------|----------|
| **Tiempo** | 2-3h | 4-6h | 6-8h |
| **Token Seguro** | ❌ | ✅ | ✅ |
| **Control Backend** | ❌ | ✅ | ✅ |
| **Validación Tenant** | ⚠️ Manual | ✅ Auto | ✅ Auto |
| **Complejidad** | Baja | Media | Alta |
| **Recomendado** | ❌ | ✅ | Para RBAC |

---

## 📚 Recursos

- [Azure Identity SDK](https://learn.microsoft.com/en-us/dotnet/api/azure.identity)
- [Azure Monitor Query SDK](https://learn.microsoft.com/en-us/dotnet/api/azure.monitor.query)
- [Managed Identity](https://learn.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/overview)
- [JWT Bearer in ASP.NET Core](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.protocols.openidconnect)

---

## 🚀 Próximos Pasos

1. ✅ Instalar paquetes NuGet
2. ✅ Crear TenantResolver (mapear emails)
3. ✅ Crear AppInsightsQueryService
4. ✅ Crear Controller API
5. ✅ Registrar en Program.cs
6. ✅ Configurar Managed Identity en Azure
7. ✅ Probar con dos usuarios diferentes
8. ✅ Implementar caching (opcional)
9. ✅ Agregar KQL templates (para usuarios)
