# AppInsights Token Access - Visual Flow Diagram

## 📊 Flujo Completo - Opción 2 (Backend Proxy)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        USUARIO: cgareca@arrivia.com                         │
└─────────────────────────────────────────────────────────────────────────────┘

1. BROWSER - SSO LOGIN
   ┌──────────────────────────────────────────────────────────────────────┐
   │ Usuario hace click en "Ver Logs"                                    │
   │                                                                      │
   │ Browser redirige a: https://login.microsoftonline.com/...          │
   │ ↓                                                                   │
   │ Usuario ingresa:                                                   │
   │   • Email: cgareca@arrivia.com                                     │
   │   • Password: ****                                                 │
   │ ↓                                                                   │
   │ Entera ID valida credenciales ✓                                    │
   │ ↓                                                                   │
   │ Browser redirige a: https://portal.arrivia.com/callback            │
   │ ↓                                                                   │
   │ Frontend recibe JWT token:                                         │
   │ {                                                                  │
   │   "aud": "api://portal",                                          │
   │   "email": "cgareca@arrivia.com",                                 │
   │   "oid": "12345-abcde-user-object-id",                            │
   │   "tid": "abcde-1234-tenant-id",                                  │
   │   "iss": "https://sts.windows.net/...",                           │
   │   "exp": 1730278902,                                              │
   │   ...                                                              │
   │ }                                                                  │
   │ ↓                                                                   │
   │ Frontend guarda JWT en memory (NO en localStorage)                 │
   └──────────────────────────────────────────────────────────────────────┘

2. FRONTEND - LLAMADA A API
   ┌──────────────────────────────────────────────────────────────────────┐
   │ Usuario hace click en "Consultar Logs"                             │
   │ ↓                                                                   │
   │ Frontend JavaScript:                                               │
   │ const token = getAccessTokenFromMemory();                          │
   │ await fetch('/api/appinsights/logs', {                            │
   │   method: 'POST',                                                  │
   │   headers: {                                                       │
   │     'Authorization': `Bearer ${token}`,                            │
   │     'Content-Type': 'application/json'                             │
   │   },                                                               │
   │   body: JSON.stringify({                                           │
   │     query: 'customEvents | take 100',                              │
   │     timeRange: { startTime: ..., endTime: ... }                    │
   │   })                                                               │
   │ })                                                                 │
   │ ↓                                                                   │
   │ Solicitud HTTP:                                                    │
   │ POST /api/appinsights/logs                                         │
   │ Authorization: Bearer eyJ0eXAi...eyJpYXQi...                       │
   │ Content-Type: application/json                                     │
   │ {                                                                  │
   │   "query": "customEvents | take 100",                              │
   │   "timeRange": {...}                                               │
   │ }                                                                  │
   └──────────────────────────────────────────────────────────────────────┘

3. BACKEND - VALIDACIÓN Y RESOLUCIÓN DE TENANT
   ┌──────────────────────────────────────────────────────────────────────┐
   │ 🔧 AppInsightsController.QueryLogs()                               │
   │                                                                      │
   │ PASO A: Validar JWT                                                │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ if (string.IsNullOrEmpty(request?.Query))                   │ │
   │ │   return BadRequest("Query is required");                   │ │
   │ │ ✓ PASS                                                      │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO B: Middleware valida JWT (antes de llegar al controller)      │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ JWT Bearer Middleware:                                      │ │
   │ │                                                             │ │
   │ │ 1. Obtener token del header Authorization:                │ │
   │ │    → "eyJ0eXAi...eyJpYXQi..."                             │ │
   │ │                                                             │ │
   │ │ 2. Descodificar token (sin validar aún):                  │ │
   │ │    {                                                       │ │
   │ │      "header": {"alg": "RS256", "kid": "..."},           │ │
   │ │      "payload": {"email": "cgareca@arrivia.com", ...},   │ │
   │ │      "signature": "..."                                   │ │
   │ │    }                                                       │ │
   │ │                                                             │ │
   │ │ 3. Obtener public keys de Entera ID:                      │ │
   │ │    GET https://login.microsoftonline.com/.../keys         │ │
   │ │    → { "keys": [{"kid": "...", "n": "...", "e": "..."}] }│ │
   │ │                                                             │ │
   │ │ 4. Usar public key para validar firma:                    │ │
   │ │    verificarFirmaRS256(token, publicKey)                  │ │
   │ │    ✓ VÁLIDO                                               │ │
   │ │                                                             │ │
   │ │ 5. Si firma inválida:                                      │ │
   │ │    return 401 Unauthorized                                │ │
   │ │                                                             │ │
   │ │ 6. User.Identity es ahora un ClaimsPrincipal              │ │
   │ │    con todos los claims del JWT                           │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO C: Extraer email del JWT                                      │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ var userEmail = User.FindFirst(ClaimTypes.Email)?.Value      │ │
   │ │                 ?? User.FindFirst("email")?.Value            │ │
   │ │                                                             │ │
   │ │ → "cgareca@arrivia.com"                                    │ │
   │ │                                                             │ │
   │ │ if (string.IsNullOrEmpty(userEmail))                      │ │
   │ │   return Unauthorized("User email not found");            │ │
   │ │ ✓ PASS                                                      │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO D: Mapear email a tenant                                       │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ 🔧 TenantResolver.GetTenantKeyAsync(userEmail)              │ │
   │ │                                                             │ │
   │ │ 1. Extraer dominio del email:                              │ │
   │ │    "cgareca@arrivia.com" → "arrivia.com"                  │ │
   │ │                                                             │ │
   │ │ 2. Mapear dominio a tenant key:                            │ │
   │ │    switch (domain) {                                       │ │
   │ │      case "arrivia.com" => "arrivia",                     │ │
   │ │      case "ice.com" => "ice",                             │ │
   │ │      case "acmecorp.com" => "acme",                       │ │
   │ │      default => throw UnauthorizedAccessException        │ │
   │ │    }                                                       │ │
   │ │                                                             │ │
   │ │ → "arrivia"                                                │ │
   │ │ ✓ PASS                                                      │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO E: Obtener configuración del tenant                           │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ 🔧 TenantResolver.GetTenantConfigAsync(tenantKey)           │ │
   │ │                                                             │ │
   │ │ TenantConfig["arrivia"] = {                                │ │
   │ │   TenantKey: "arrivia",                                    │ │
   │ │   SubscriptionId: "00000000-0000-0000-0000-000000000001",  │ │
   │ │   ResourceGroup: "rg-arrivia-prod",                        │ │
   │ │   AppInsightsName: "ai-arrivia-prod",                      │ │
   │ │   AadTenantId: "11111111-1111-1111-1111-111111111111"     │ │
   │ │ }                                                           │ │
   │ │ ✓ PASS                                                      │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO F: Crear credential y obtener token Azure                     │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ 🔧 AppInsightsQueryService.QueryLogsAsync()                 │ │
   │ │                                                             │ │
   │ │ 1. Crear DefaultAzureCredential:                           │ │
   │ │    var credential = new DefaultAzureCredential();          │ │
   │ │                                                             │ │
   │ │    En Local:                                               │ │
   │ │      - Si hay env var AZURE_CLIENT_ID: Usa que             │ │
   │ │      - Si hay .azure/credentials: Usa eso                  │ │
   │ │      - Si hay VS Code Azure extension: Usa eso            │ │
   │ │                                                             │ │
   │ │    En Producción (App Service):                            │ │
   │ │      - Usa Managed Identity del App Service                │ │
   │ │      - No busca credenciales                               │ │
   │ │                                                             │ │
   │ │ 2. Obtener token:                                          │ │
   │ │    var tokenRequest = new TokenRequestContext(             │ │
   │ │      scopes: new[] {                                       │ │
   │ │        "https://management.azure.com/.default"             │ │
   │ │      });                                                   │ │
   │ │    var token = await credential.GetTokenAsync(             │ │
   │ │      tokenRequest);                                        │ │
   │ │                                                             │ │
   │ │    → AccessToken {                                         │ │
   │ │        Token: "eyJ0eXAi...base64...qw",                   │ │
   │ │        ExpiresOn: DateTimeOffset (3600s en el futuro)     │ │
   │ │      }                                                     │ │
   │ │ ✓ PASS                                                      │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   └──────────────────────────────────────────────────────────────────────┘

4. AZURE RESOURCE MANAGER - VALIDACIÓN Y AUTORIZACIÓN
   ┌──────────────────────────────────────────────────────────────────────┐
   │ 🔧 LogsQueryClient.QueryResourceAsync()                             │
   │                                                                      │
   │ PASO A: Crear cliente y armar solicitud                             │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ var client = new LogsQueryClient(credential);               │ │
   │ │                                                             │ │
   │ │ Construir Resource ID:                                      │ │
   │ │ /subscriptions/00000000-0000-0000-0000-000000000001         │ │
   │ │ /resourceGroups/rg-arrivia-prod                             │ │
   │ │ /providers/microsoft.insights/components/ai-arrivia-prod    │ │
   │ │                                                             │ │
   │ │ Solicitud KQL:                                              │ │
   │ │ customEvents                                                │ │
   │ │ | where timestamp > ago(7d)                                 │ │
   │ │ | project timestamp, name, customDimensions                │ │
   │ │ | take 100                                                  │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO B: Hacer solicitud HTTP a Azure                               │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ POST https://management.azure.com/subscriptions/00000.../   │ │
   │ │   ...resourceGroups/rg-arrivia.../providers/...             │ │
   │ │   microsoft.insights/components/ai-arrivia/api/query        │ │
   │ │                                                             │ │
   │ │ Headers:                                                    │ │
   │ │   Authorization: Bearer eyJ0eXAi...base64...qw              │ │
   │ │   (Token de Managed Identity)                               │ │
   │ │   Content-Type: application/json                            │ │
   │ │   Accept-Language: en-US                                    │ │
   │ │   User-Agent: azsdk-net-Monitor.Query/1.3.0                │ │
   │ │                                                             │ │
   │ │ Body:                                                       │ │
   │ │ {                                                           │ │
   │ │   "query": "customEvents | take 100",                       │ │
   │ │   "timespan": "PT7D"                                        │ │
   │ │ }                                                           │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO C: Azure valida token                                          │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ Azure API Gateway:                                          │ │
   │ │                                                             │ │
   │ │ 1. Validar token:                                          │ │
   │ │    ✓ Token válido (no expirado)                            │ │
   │ │    ✓ Firma válida                                          │ │
   │ │    ✓ Scope: management.azure.com                           │ │
   │ │                                                             │ │
   │ │ 2. Obtener identidad del token:                            │ │
   │ │    "appid": "12345-67890-managed-identity-app-id",         │ │
   │ │    → Identidad: Managed Identity del App Service           │ │
   │ │                                                             │ │
   │ │ 3. Resolver identidad a principal:                         │ │
   │ │    → Service Principal de App Service                      │ │
   │ │    → Object ID: 87654-32109-app-service-oid                │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO D: Azure valida RBAC                                           │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ RBAC Check:                                                 │ │
   │ │                                                             │ │
   │ │ Preguntas:                                                  │ │
   │ │ 1. ¿Existe un role assignment para este principal?         │ │
   │ │    → SÍ: Role "Monitoring Reader"                          │ │
   │ │    → Scope: /subscriptions/.../components/ai-arrivia-prod  │ │
   │ │                                                             │ │
   │ │ 2. ¿El role permite la acción solicitada?                  │ │
   │ │    → Acción: microsoft.insights/components/query/*         │ │
   │ │    → "Monitoring Reader" incluye esta acción               │ │
   │ │    → ✓ PERMITIDO                                           │ │
   │ │                                                             │ │
   │ │ Resultado: ✅ ACCESO PERMITIDO                              │ │
   │ │ (Si fuera "ice" en lugar de "arrivia": ❌ 403 Forbidden)  │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO E: Ejecutar query KQL                                          │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ Kusto Query Engine (en AppInsights):                        │ │
   │ │                                                             │ │
   │ │ 1. Parsear query:                                          │ │
   │ │    customEvents                                            │ │
   │ │    | where timestamp > ago(7d)                             │ │
   │ │    | project timestamp, name, customDimensions             │ │
   │ │    | take 100                                              │ │
   │ │                                                             │ │
   │ │ 2. Compilar plan de ejecución                              │ │
   │ │                                                             │ │
   │ │ 3. Ejecutar contra data lake de AppInsights                │ │
   │ │                                                             │ │
   │ │ 4. Retornar resultados:                                    │ │
   │ │    [                                                       │ │
   │ │      {                                                     │ │
   │ │        "timestamp": "2024-10-28T14:32:00Z",               │ │
   │ │        "name": "scout-run-started",                        │ │
   │ │        "customDimensions": {                               │ │
   │ │          "InvestigationPackageId": "pkg-123",              │ │
   │ │          "CorrelationId": "corr-456",                      │ │
   │ │          ...                                               │ │
   │ │        }                                                   │ │
   │ │      },                                                    │ │
   │ │      { ... más resultados ... },                           │ │
   │ │      { ... (hasta 100 total) ... }                         │ │
   │ │    ]                                                       │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   │                                                                      │
   │ PASO F: Retornar respuesta                                          │
   │ ┌───────────────────────────────────────────────────────────────┐ │
   │ │ HTTP 200 OK                                                 │ │
   │ │ Content-Type: application/json                              │ │
   │ │ Body: [                                                     │ │
   │ │   {                                                         │ │
   │ │     timestamp: "2024-10-28T14:32:00Z",                     │ │
   │ │     name: "scout-run-started",                              │ │
   │ │     customDimensions: { ... }                               │ │
   │ │   },                                                        │ │
   │ │   ...                                                       │ │
   │ │ ]                                                           │ │
   │ └───────────────────────────────────────────────────────────────┘ │
   └──────────────────────────────────────────────────────────────────────┘

5. BACKEND - RETORNAR RESPUESTA AL FRONTEND
   ┌──────────────────────────────────────────────────────────────────────┐
   │ Backend Controller:                                                  │
   │                                                                      │
   │ return Ok(result);                                                  │
   │                                                                      │
   │ → HTTP 200 OK                                                       │
   │   Body: [ { timestamp, name, customDimensions }, ... ]             │
   └──────────────────────────────────────────────────────────────────────┘

6. FRONTEND - MOSTRAR RESULTADOS
   ┌──────────────────────────────────────────────────────────────────────┐
   │ Frontend JavaScript:                                                │
   │                                                                      │
   │ const response = await fetch(...)                                   │
   │ const logs = await response.json();                                 │
   │                                                                      │
   │ // Mostrar en UI:                                                   │
   │ logs.forEach(log => {                                               │
   │   console.log(`${log.timestamp} - ${log.name}`);                   │
   │   // Renderear en tabla/gráfico                                     │
   │ });                                                                 │
   │                                                                      │
   │ ✅ Usuario ve logs de Arrivia                                       │
   └──────────────────────────────────────────────────────────────────────┘
```

---

## 🔐 Escenario de Ataque - Usuario Ice intenta acceder a Arrivia

```
Usuario: cgareca@ice.com
Email domain: ice.com
Tenant asignado: "ice"

INTENTO: Consultar logs de Arrivia

┌──────────────────────────────────────────────────────────────────┐
│ 1. Frontend hace request normalmente                            │
│    POST /api/appinsights/logs                                   │
│    Authorization: Bearer <JWT de ICE>                           │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 2. Backend valida JWT                                           │
│    ✓ JWT válido (del usuario Ice)                              │
│    Extrae email: cgareca@ice.com                               │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 3. TenantResolver mapea email                                   │
│    ice.com → "ice"                                              │
│    Obtiene config de "ice" AppInsights                         │
│    (No "arrivia")                                               │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 4. AppInsightsService obtiene token                             │
│    DefaultAzureCredential → Token de Managed Identity           │
│    Scope: management.azure.com                                  │
│    ✓ Token válido                                               │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 5. AppInsightsService intenta consultar ARRIVIA                 │
│    POST https://management.azure.com/.../ai-arrivia-prod/...   │
│    Authorization: Bearer <Token de Managed Identity>            │
│                                                                  │
│    PERO:                                                        │
│    Managed Identity solo tiene Reader en ai-ice-prod           │
│    NO tiene Reader en ai-arrivia-prod                          │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 6. Azure RBAC CHECK                                             │
│    ¿Tiene "Monitoring Reader" en ai-arrivia-prod?              │
│    → NO                                                         │
│                                                                  │
│    Resultado: ❌ 403 FORBIDDEN                                   │
│                                                                  │
│    Azure Response:                                              │
│    {                                                            │
│      "error": {                                                 │
│        "code": "AuthorizationFailed",                          │
│        "message": "The client ... does not have authorization   │
│          to perform action ..."                                │
│      }                                                          │
│    }                                                            │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 7. Backend recibe 403, lo propaga                               │
│    return StatusCode(403, new { error = "Access Denied" });     │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│ 8. Frontend recibe 403                                          │
│    Usuario ve mensaje: "No tienes permisos"                    │
│    ❌ ATAQUE BLOQUEADO                                          │
└──────────────────────────────────────────────────────────────────┘
```

---

## 📊 Comparativa - 3 Opciones

```
OPCIÓN 1: FRONTEND DIRECTO
┌──────────────────────────────────────────────────────┐
│ Frontend               │ (sin backend)               │
│ Browser → Azure API   │ (token en navegador)       │
│ AzureIdentityClient   │ @azure/identity npm         │
└──────────────────────────────────────────────────────┘
Flujo:
  Frontend obtiene token → Direct a Azure → Logs
Problemas:
  ❌ Token visible en navegador
  ❌ Sin validación servidor
  ❌ SPA vulnerable a XSS
  ❌ CORS issues


OPCIÓN 2: BACKEND PROXY (⭐ RECOMENDADO)
┌──────────────────────────────────────────────────────┐
│ Frontend (JWT)         │ Backend                     │
│         ↓              │ • Valida JWT               │
│ Portal API             │ • Mapea tenant             │
│         ↓              │ • Obtiene token Azure      │
│ Azure AppInsights      │ • Consulta API             │
│         ↓              │ • Retorna logs             │
│ Frontend (logs)        │ • Audit logging            │
└──────────────────────────────────────────────────────┘
Flujo:
  Frontend (JWT) → Backend (mapea) → Azure → Logs
Ventajas:
  ✅ Token NO expuesto
  ✅ Control servidor
  ✅ Validación tenant
  ✅ Audit automático
  ✅ Producción ready


OPCIÓN 3: ON-BEHALF-OF FLOW
┌──────────────────────────────────────────────────────┐
│ Frontend (JWT usuario)                              │
│         ↓                                            │
│ Backend:                                            │
│  • Token de usuario → OAuth 2.0 intercambio        │
│  • Obtiene token para Azure (delegated)             │
│  • Entera ID controla permisos                      │
│         ↓                                            │
│ Azure AppInsights                                   │
│         ↓                                            │
│ Frontend (logs)                                     │
└──────────────────────────────────────────────────────┘
Flujo:
  Frontend → Backend (intercambia token) → Azure
Ventajas:
  ✅ Máxima seguridad
  ✅ Token delegated (no impersonation)
Desventajas:
  ❌ Más complejo
  ❌ Overhead adicional
```

---

## 🎯 Decisión Final

| Factor | Opción 1 | Opción 2 | Opción 3 |
|--------|----------|----------|----------|
| **Tiempo** | 2-3h | 4-6h | 6-8h |
| **Seguridad** | ⚠️ Débil | ✅ Fuerte | ✅ Máxima |
| **Complejidad** | Baja | Media | Alta |
| **Producción** | ❌ NO | ✅ SÍ | ✅ SÍ |
| **Recomendación** | ❌ EVITAR | ✅ **USAR** | ⚠️ Si RBAC |

**→ OPCIÓN 2 (Backend Proxy) es tu mejor opción**

