# AppInsights Token Access - Step-by-Step Setup

## 📋 Tabla de Contenidos

1. [Requisitos](#requisitos)
2. [Arquitectura](#arquitectura)
3. [Setup Local](#setup-local)
4. [Setup Azure](#setup-azure)
5. [Testing](#testing)
6. [Troubleshooting](#troubleshooting)

---

## 📦 Requisitos

### Software
- Visual Studio 2022 o VS Code
- .NET 6.0+ SDK
- Azure CLI (`az` command)
- Git

### Azure Resources
- Azure Subscription (con permisos para crear Managed Identity)
- 2+ Application Insights instances (para Arrivia y Ice)
- 1 App Service (o donde hostes Portal)

### Configuración Entera ID
- Entra ID tenant registrado
- App registration con Azure API permissions
- Clave secreta o certificado (si es dev)

---

## 🏗️ Arquitectura

```
┌──────────────────────────────────────────────────────────────┐
│ FLOW COMPLETO                                                │
└──────────────────────────────────────────────────────────────┘

1. USER LOGIN
   ┌──────────────────┐
   │  cgareca@        │
   │  arrivia.com     │
   └────────┬─────────┘
            │
            ├─ Clicks "Ver Logs"
            │
            └─→ Browser: SSO redirect to Entera ID
                  ↓
                  Login with credentials
                  ↓
                  Entera ID: Validado ✓
                  ↓
                  Redirect back to Portal
                  ↓
                  Frontend: Recibe JWT token
                     {
                       "aud": "api://portal",
                       "email": "cgareca@arrivia.com",
                       "oid": "user-object-id",
                       "tid": "tenant-id"
                     }

2. FRONTEND REQUEST
   ┌──────────────────────┐
   │ Frontend (React/Vue) │
   └────────┬─────────────┘
            │
            └─→ POST /api/appinsights/logs
                Headers:
                  Authorization: Bearer <JWT>
                  Content-Type: application/json
                Body:
                  {
                    "query": "customEvents | take 100",
                    "timeRange": {...}
                  }

3. BACKEND VALIDATION & RESOLUTION
   ┌────────────────────────────────────┐
   │ AppInsightsController              │
   └────────┬──────────────────────────┘
            │
            ├─ [1] Middleware validates JWT signature
            │      • Loads Entera ID public keys
            │      • Verifies signature: ✓ VALID
            │
            ├─ [2] Extract email from JWT claims
            │      • User.FindFirst(ClaimTypes.Email)
            │      • Result: "cgareca@arrivia.com"
            │
            ├─ [3] Call TenantResolver
            │      • GetTenantKeyAsync("cgareca@arrivia.com")
            │      • Map domain "arrivia.com" → "arrivia"
            │      • Result: "arrivia"
            │
            ├─ [4] Get Tenant Configuration
            │      • GetTenantConfigAsync("arrivia")
            │      • Load from TenantConfig dictionary:
            │        {
            │          TenantKey: "arrivia",
            │          SubscriptionId: "sub-123",
            │          ResourceGroup: "rg-arrivia",
            │          AppInsightsName: "ai-arrivia",
            │          AadTenantId: "aad-tenant-123"
            │        }
            │
            ├─ [5] Create DefaultAzureCredential
            │      • Uses Managed Identity
            │      • Authenticates to Azure as App Service
            │
            ├─ [6] Obtain Token for Azure
            │      • Credential.GetTokenAsync(scope: "https://management.azure.com/.default")
            │      • Result: Access Token
            │        {
            │          "access_token": "eyJ0eXAi...",
            │          "expires_in": 3599,
            │          "token_type": "Bearer"
            │        }
            │
            ├─ [7] Build Resource ID
            │      • /subscriptions/sub-123/resourceGroups/rg-arrivia/providers/microsoft.insights/components/ai-arrivia
            │
            └─→ [8] Query AppInsights API

4. AZURE API CALL
   ┌────────────────────────────────────┐
   │ Azure Resource Manager API         │
   └────────┬──────────────────────────┘
            │
            ├─ Request:
            │  POST https://management.azure.com/subscriptions/sub-123/...
            │  Headers:
            │    Authorization: Bearer <token-from-step-6>
            │    Content-Type: application/json
            │  Body:
            │    {
            │      "query": "customEvents | take 100",
            │      "timerange": "..."
            │    }
            │
            ├─ Azure Validation:
            │  • Checks if token is valid: ✓
            │  • Decodes token to get service principal
            │  • Looks up service principal identity
            │  • Checks RBAC: Does "App Service" have "Reader" on "ai-arrivia"?
            │    → YES (we assigned this in setup)
            │
            └─→ Execute Query & Return Results

5. RESPONSE BACK
   └─→ Azure returns logs data
        ↓
        Backend: Receives JSON
        ↓
        Backend: Returns to Frontend
        ↓
        Frontend: Displays in UI
```

---

## 🛠️ Setup Local

### PASO 1: Instalar Paquetes NuGet

```bash
cd C:\Arrivia\ai-reliable-platform\Portal
dotnet add package Azure.Identity
dotnet add package Azure.Monitor.Query
dotnet add package Azure.ResourceManager
```

Verificar en [Portal.csproj](../../Portal/Portal.csproj):
```xml
<ItemGroup>
  <PackageReference Include="Azure.Identity" Version="1.10.0" />
  <PackageReference Include="Azure.Monitor.Query" Version="1.3.0" />
  <PackageReference Include="Azure.ResourceManager" Version="1.7.0" />
</ItemGroup>
```

### PASO 2: Crear TenantResolver

**File: `Portal/src/JourneyMonitor.Core/AppInsights/TenantResolver.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace JourneyMonitor.Core.AppInsights
{
    /// <summary>
    /// Maps user email domain to company tenant configuration
    /// </summary>
    public interface ITenantResolver
    {
        Task<string> GetTenantKeyAsync(string userEmail);
        Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey);
    }

    /// <summary>
    /// Configuration for a single tenant's AppInsights resources
    /// </summary>
    public class AppInsightsTenant
    {
        /// <summary>
        /// Unique identifier for tenant (e.g., "arrivia", "ice")
        /// </summary>
        public string TenantKey { get; set; }

        /// <summary>
        /// Azure Subscription ID where AppInsights is located
        /// </summary>
        public string SubscriptionId { get; set; }

        /// <summary>
        /// Azure Resource Group containing AppInsights
        /// </summary>
        public string ResourceGroup { get; set; }

        /// <summary>
        /// Name of Application Insights resource
        /// </summary>
        public string AppInsightsName { get; set; }

        /// <summary>
        /// Azure AD tenant ID (for advanced RBAC)
        /// </summary>
        public string AadTenantId { get; set; }

        /// <summary>
        /// Full resource ID for Azure API calls
        /// </summary>
        public string GetResourceId()
        {
            return $"/subscriptions/{SubscriptionId}" +
                   $"/resourceGroups/{ResourceGroup}" +
                   $"/providers/microsoft.insights/components/{AppInsightsName}";
        }
    }

    /// <summary>
    /// Hardcoded tenant configuration (in production, load from DB or config)
    /// </summary>
    public class TenantResolver : ITenantResolver
    {
        // TODO: Load from appsettings.json or database
        private static readonly Dictionary<string, AppInsightsTenant> TenantConfig =
            new Dictionary<string, AppInsightsTenant>
            {
                {
                    "arrivia", new AppInsightsTenant
                    {
                        TenantKey = "arrivia",
                        SubscriptionId = "YOUR-ARRIVIA-SUBSCRIPTION-ID",
                        ResourceGroup = "rg-arrivia-prod",
                        AppInsightsName = "ai-arrivia-prod",
                        AadTenantId = "YOUR-ARRIVIA-AAD-TENANT-ID"
                    }
                },
                {
                    "ice", new AppInsightsTenant
                    {
                        TenantKey = "ice",
                        SubscriptionId = "YOUR-ICE-SUBSCRIPTION-ID",
                        ResourceGroup = "rg-ice-prod",
                        AppInsightsName = "ai-ice-prod",
                        AadTenantId = "YOUR-ICE-AAD-TENANT-ID"
                    }
                }
            };

        /// <summary>
        /// Map user email to tenant key based on domain
        /// Example: cgareca@arrivia.com → "arrivia"
        /// </summary>
        public Task<string> GetTenantKeyAsync(string userEmail)
        {
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new ArgumentNullException(nameof(userEmail));

            var domain = userEmail.Split('@').Last().ToLower();

            var tenantKey = domain switch
            {
                "arrivia.com" => "arrivia",
                "iceenterprise.com" => "ice",
                "ice.com" => "ice", // Alternative domain
                "acmecorp.com" => "acme", // Example: add more
                _ => null
            };

            if (tenantKey == null)
                throw new UnauthorizedAccessException(
                    $"User domain '{domain}' is not configured in this system");

            return Task.FromResult(tenantKey);
        }

        /// <summary>
        /// Get AppInsights configuration for a tenant
        /// </summary>
        public Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey)
        {
            if (!TenantConfig.TryGetValue(tenantKey, out var config))
                throw new KeyNotFoundException(
                    $"Tenant configuration not found for key: {tenantKey}");

            return Task.FromResult(config);
        }
    }
}
```

### PASO 3: Crear AppInsightsQueryService

**File: `Portal/src/JourneyMonitor.Core/AppInsights/AppInsightsQueryService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.AppInsights
{
    /// <summary>
    /// Query logs and metrics from AppInsights using tenant context
    /// </summary>
    public interface IAppInsightsQueryService
    {
        /// <summary>
        /// Execute KQL query against tenant's AppInsights
        /// </summary>
        Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
            string tenantKey,
            string query,
            QueryTimeRange timeRange);

        /// <summary>
        /// Get metrics from tenant's AppInsights
        /// </summary>
        Task<IReadOnlyList<MetricsResult>> GetMetricsAsync(
            string tenantKey,
            string metricName,
            TimeSpan? timespan = null);
    }

    /// <summary>
    /// Implementation using Azure SDK
    /// </summary>
    public class AppInsightsQueryService : IAppInsightsQueryService
    {
        private readonly ITenantResolver _tenantResolver;
        private readonly ILogger<AppInsightsQueryService> _logger;

        // Lazy-initialize clients to avoid creating them if not used
        private LogsQueryClient _logsQueryClient;
        private MetricsQueryClient _metricsQueryClient;

        public AppInsightsQueryService(
            ITenantResolver tenantResolver,
            ILogger<AppInsightsQueryService> logger)
        {
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<LogsQueryResult>> QueryLogsAsync(
            string tenantKey,
            string query,
            QueryTimeRange timeRange)
        {
            try
            {
                _logger.LogInformation($"Querying logs for tenant: {tenantKey}");

                // Get tenant configuration
                var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
                var resourceId = config.GetResourceId();

                _logger.LogDebug($"Resource ID: {resourceId}");
                _logger.LogDebug($"Query: {query}");

                // Initialize client if needed
                _logsQueryClient ??= new LogsQueryClient(new DefaultAzureCredential());

                // Execute query
                var result = await _logsQueryClient.QueryResourceAsync(
                    resourceId: resourceId,
                    kuery: query,
                    timeRange: timeRange);

                _logger.LogInformation(
                    $"Query succeeded for tenant {tenantKey}. " +
                    $"Results: {result.Value.Count} tables");

                return result.Value;
            }
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                _logger.LogError($"Authentication failed: {ex.Message}");
                throw new Exception("Failed to authenticate with Azure. " +
                    "Ensure Managed Identity is configured.", ex);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403)
            {
                _logger.LogError($"Access denied to AppInsights: {ex.Message}");
                throw new Exception(
                    $"Access denied to {tenantKey} AppInsights. " +
                    "Check RBAC permissions for Managed Identity.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Query failed: {ex}");
                throw new Exception(
                    $"Failed to query AppInsights for tenant {tenantKey}", ex);
            }
        }

        public async Task<IReadOnlyList<MetricsResult>> GetMetricsAsync(
            string tenantKey,
            string metricName,
            TimeSpan? timespan = null)
        {
            try
            {
                _logger.LogInformation($"Getting metrics '{metricName}' for tenant: {tenantKey}");

                var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);
                var resourceId = config.GetResourceId();

                _metricsQueryClient ??= new MetricsQueryClient(new DefaultAzureCredential());

                var response = await _metricsQueryClient.QueryResourceAsync(
                    resourceId: resourceId,
                    metricNames: new[] { metricName },
                    timespan: timespan);

                _logger.LogInformation(
                    $"Metrics query succeeded for tenant {tenantKey}. " +
                    $"Metrics: {response.Value.Metrics.Count}");

                return response.Value.Metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metrics query failed: {ex}");
                throw new Exception(
                    $"Failed to get metrics for tenant {tenantKey}", ex);
            }
        }
    }
}
```

### PASO 4: Crear Controller

**File: `Portal/src/JourneyMonitor.Functions/Functions/AppInsightsController.cs`**

```csharp
using System;
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
            _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Query logs from AppInsights for the user's tenant
        /// Only returns data for the tenant matching the user's email domain
        /// </summary>
        /// <remarks>
        /// Requires authentication. The user's email from the JWT token determines which
        /// tenant's AppInsights they can access.
        ///
        /// Example request:
        /// POST /api/appinsights/logs
        /// Authorization: Bearer eyJ0eXAi...
        /// {
        ///   "query": "customEvents | where name == 'scout-run-started' | take 100",
        ///   "timeRange": {
        ///     "startTime": "2024-10-23T00:00:00Z",
        ///     "endTime": "2024-10-30T00:00:00Z"
        ///   }
        /// }
        /// </remarks>
        [HttpPost("logs")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> QueryLogs([FromBody] LogsQueryRequest request)
        {
            try
            {
                // Validate request
                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                    return BadRequest("Query is required");

                // Extract email from JWT token
                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ??
                                User.FindFirst("preferred_username")?.Value ??
                                User.FindFirst("email")?.Value;

                if (string.IsNullOrWhiteSpace(userEmail))
                {
                    _logger.LogWarning("No email claim found in token");
                    return Unauthorized("User email not found in authentication token");
                }

                _logger.LogInformation($"User {userEmail} querying AppInsights");

                // Map email domain to tenant
                var tenantKey = await _tenantResolver.GetTenantKeyAsync(userEmail);

                _logger.LogInformation(
                    $"Mapped user {userEmail} to tenant {tenantKey}");

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
        /// Get specific metrics from AppInsights
        /// </summary>
        [HttpGet("metrics/{metricName}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetMetrics(
            string metricName,
            [FromQuery(Name = "days")] int? lastDays = 7)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(metricName))
                    return BadRequest("Metric name is required");

                var userEmail = User.FindFirst(ClaimTypes.Email)?.Value ??
                                User.FindFirst("preferred_username")?.Value ??
                                User.FindFirst("email")?.Value;

                if (string.IsNullOrWhiteSpace(userEmail))
                    return Unauthorized("User email not found");

                var tenantKey = await _tenantResolver.GetTenantKeyAsync(userEmail);

                var timespan = lastDays.HasValue
                    ? TimeSpan.FromDays(lastDays.Value)
                    : TimeSpan.FromDays(7);

                var result = await _queryService.GetMetricsAsync(
                    tenantKey,
                    metricName,
                    timespan);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting metrics: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Health check endpoint (no auth required)
        /// </summary>
        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult Health()
        {
            return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
        }
    }

    /// <summary>
    /// Request model for logs query
    /// </summary>
    public class LogsQueryRequest
    {
        /// <summary>
        /// KQL (Kusto Query Language) query
        /// Example: customEvents | where name == "scout-run-started" | take 100
        /// </summary>
        public string Query { get; set; }

        /// <summary>
        /// Time range for query
        /// If null, defaults to last 24 hours
        /// </summary>
        public Azure.Monitor.Query.Models.QueryTimeRange TimeRange { get; set; }
    }
}
```

### PASO 5: Registrar en Program.cs

**File: `Portal/src/JourneyMonitor.Web/Program.cs`**

Agregar después de otros servicios:

```csharp
// Add AppInsights services
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();

// Ensure authentication is enabled
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();

// Later in the app builder:
app.UseAuthentication();
app.UseAuthorization();
```

---

## ☁️ Setup Azure

### PASO 1: Crear Managed Identity en App Service

```bash
# Set variables
$appServiceName = "your-app-service-name"
$resourceGroup = "your-resource-group"

# Enable Managed Identity
az webapp identity assign \
  --name $appServiceName \
  --resource-group $resourceGroup

# Get the Object ID
$managedIdentityObjectId = az webapp identity show \
  --name $appServiceName \
  --resource-group $resourceGroup \
  --query principalId -o tsv

Write-Host "Managed Identity Object ID: $managedIdentityObjectId"
```

### PASO 2: Asignar Permisos RBAC

```bash
# For Arrivia AppInsights
$arrivaAiResourceId = "/subscriptions/YOUR-ARRIVIA-SUB/resourceGroups/rg-arrivia-prod/providers/microsoft.insights/components/ai-arrivia-prod"
$managedIdentityObjectId = "<value-from-step-1>"

az role assignment create \
  --assignee-object-id $managedIdentityObjectId \
  --role "Monitoring Reader" \
  --scope $arrivaAiResourceId

# For Ice AppInsights
$iceAiResourceId = "/subscriptions/YOUR-ICE-SUB/resourceGroups/rg-ice-prod/providers/microsoft.insights/components/ai-ice-prod"

az role assignment create \
  --assignee-object-id $managedIdentityObjectId \
  --role "Monitoring Reader" \
  --scope $iceAiResourceId
```

### PASO 3: Verificar Permisos

```bash
az role assignment list \
  --assignee-object-id $managedIdentityObjectId \
  --output table
```

---

## 🧪 Testing

### TEST 1: Verificar Endpoint Health

```bash
curl -X GET https://localhost:7001/api/appinsights/health
# Response: {"status":"healthy","timestamp":"2024-10-30T..."}
```

### TEST 2: Query Logs (Autenticado)

```bash
# 1. Obtain JWT token (from your SSO/dev environment)
$token = "eyJ0eXAi..."  # Your JWT here

# 2. Prepare KQL query
$query = "customEvents | where timestamp > ago(7d) | take 10"

# 3. Call API
$body = @{
  query = $query
  timeRange = @{
    startTime = (Get-Date).AddDays(-7).ToUniversalTime()
    endTime = (Get-Date).ToUniversalTime()
  }
} | ConvertTo-Json

Invoke-RestMethod `
  -Uri "https://localhost:7001/api/appinsights/logs" `
  -Method POST `
  -Headers @{"Authorization" = "Bearer $token"} `
  -ContentType "application/json" `
  -Body $body
```

### TEST 3: Verificar Aislamiento de Tenant

```bash
# Login as cgareca@arrivia.com
# Should work: ✓

# Login as cgareca@ice.com
# Try to query with Arrivia context: Should fail 403
```

---

## 🔧 Troubleshooting

### Error: "Failed to authenticate with Azure"

```
Causa: Managed Identity no está habilitada en App Service
Solución:
  1. Go to App Service → Identity
  2. Enable "System-assigned" identity
  3. Copy Object ID
  4. Assign RBAC roles
```

### Error: "Access denied to AppInsights"

```
Causa: Managed Identity no tiene permisos en AppInsights
Solución:
  1. Check role assignment:
     az role assignment list --assignee-object-id <OID>
  2. Verify role is "Monitoring Reader" or higher
  3. Verify resource ID is correct
```

### Error: "User email not found in token"

```
Causa: JWT token no tiene claim "email"
Solución:
  1. Check AzureAd configuration in appsettings.json
  2. Verify Entera ID app registration includes "email" scope
  3. Check token manually: jwt.ms
```

### Error: "Tenant configuration not found"

```
Causa: Email domain no está mapeado en TenantResolver
Solución:
  1. Edit TenantResolver.cs
  2. Add domain mapping for new company:
     "yourdomain.com" => "your-tenant-key"
  3. Rebuild and redeploy
```

---

## 📋 Checklist Final

- [ ] Instalar NuGet packages
- [ ] Crear TenantResolver.cs
- [ ] Crear AppInsightsQueryService.cs
- [ ] Crear AppInsightsController.cs
- [ ] Registrar en Program.cs
- [ ] Configurar appsettings.json con Entera ID
- [ ] Habilitar Managed Identity en App Service
- [ ] Asignar Reader role a cada AppInsights
- [ ] Test health endpoint
- [ ] Test query logs con token válido
- [ ] Test con usuario Ice (debe fallar 403)
- [ ] Revisar logs en Application Insights
- [ ] Deploy a staging
- [ ] Deploy a producción

---

## 📚 Referencia Rápida

| Comando | Descripción |
|---------|------------|
| `az webapp identity assign` | Habilitar Managed Identity |
| `az webapp identity show` | Ver Object ID |
| `az role assignment create` | Asignar permissions |
| `az role assignment list` | Listar permissions |
| `dotnet add package` | Instalar NuGet |

---

## 🎓 Conceptos Clave

### Managed Identity
- Identidad asignada por Azure al App Service
- No requiere secrets o credenciales
- Tokens generados automáticamente
- RBAC controla acceso

### Tenant Resolver
- Mapea email domain a tenant
- Previene acceso a otros tenants
- Centraliza configuración

### AppInsights Query
- KQL (Kusto Query Language)
- Ejecutado en contexto de tenant
- Filtrado por RBAC de Azure

---

**Estimado total: 4-6 horas de implementación**
