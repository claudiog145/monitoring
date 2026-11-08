# Multi-Tenant vs Multi-Company - Guía de Autenticación y Permisos

## 🎯 Aclaración Conceptual CRÍTICA

Lo que implementaron es **MULTI-COMPANY** (selector de empresa), NO **MULTI-TENANT** real.

### Diferencia Fundamental

```
┌─ MULTI-COMPANY (Lo que tienen hoy) ────────────────────┐
│                                                          │
│  Usuarios: 1 usuario logged-in                          │
│  Permiso:  Puede VER/CAMBIAR cual empresa se monitorea  │
│  Flujo:    Usuario → Portal → Dropdown → Elige empresa  │
│  Datos:    Aislados por ProfileKey en BD                │
│  Auth:     Opcional (puede ser anónimo o SSO)           │
│                                                          │
│  Casos:                                                 │
│  ✓ Admin global que monitorea todas las empresas       │
│  ✓ Operador que switchea entre empresas                │
│  ✓ Dashboard multi-tenant pero usuario único            │
│                                                          │
└────────────────────────────────────────────────────────┘

┌─ MULTI-TENANT REAL (Lo que FALTA) ───────────────────────┐
│                                                            │
│  Usuarios: cgareca@arrivia.com vs cgareca@iceenterprise   │
│  Permiso:  Usuario A solo ve datos de Arrivia            │
│            Usuario B solo ve datos de ICE Enterprise      │
│  Flujo:    Login con email → Sistema descubre empresa    │
│            Automático: datos = empresa del usuario        │
│  Datos:    Aislados por ProfileKey + TenantId del user   │
│  Auth:     REQUIRED - SSO con tenant detection           │
│                                                            │
│  Casos:                                                   │
│  ✗ Arrivia operator no puede ver ICE Enterprise data    │
│  ✗ Cada empresa tiene su propio login + dashboard       │
│  ✗ Sin dropdown - es automático                         │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

---

## 🔍 Qué Tiene Hoy la Aplicación

### 1. **Autenticación (Opcional)**

**Archivo: `Portal/src/JourneyMonitor.Web/Program.cs`**

```csharp
// Si AzureAd:TenantId está configurado → Requiere login
var hasEntraIdConfig = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);

if (hasEntraIdConfig)
{
    // Usa Microsoft Entra ID (Azure AD) SSO
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
    
    // FALLBACK POLICY: Toda página requiere usuario autenticado
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy;
    });
}
else
{
    // Sin TenantId → Todo es anónimo, cualquiera accede
    builder.Services.AddRazorPages();
}
```

**Configuración en `appsettings.json`:**
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "",        // ← Si está vacío = NO auth
    "ClientId": "",        // ← Si está vacío = NO auth
    "CallbackPath": "/signin-oidc"
  }
}
```

**Limitaciones:**
- ✅ Autentica POR TENANT de Azure AD
- ❌ PERO NO conecta tenant de AAD con empresa en BD
- ❌ Un usuario de ANY empresa puede cambiar a ANY otra empresa
- ❌ No hay autorización por empresa

---

### 2. **Autorización (Muy Débil)**

Hay DOS tipos de autorización:

#### A. **X-Settings-Write-Secret (API Key)**

**Dónde:** Todos los endpoints que escriben datos

```csharp
[Function("SetActiveCompanyProfile")]
public async Task<HttpResponseData> SetActiveAsync(
    [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/company-profiles-active")] 
    HttpRequestData req,
    CancellationToken ct)
{
    if (!HasWriteSecret(req))  // ← Checkea header X-Settings-Write-Secret
        return await UnauthorizedAsync(req);
    
    // ... proceso ...
}

private bool HasWriteSecret(HttpRequestData req)
{
    if (!req.Headers.TryGetValues("X-Settings-Write-Secret", out var values))
        return false;
    
    var provided = values.FirstOrDefault() ?? "";
    var configured = _monitoringOptions.Value.SettingsWriteSecret ?? "";
    
    return provided == configured;
}
```

**Problema:**
- ✅ Protege escrituras (cambiar empresa activa, editar ProfileJson)
- ❌ Todos los GETs son ANÓNIMOS (cualquiera puede leer datos de cualquier empresa)
- ❌ No diferencia entre empresas

#### B. **AuthorizationLevel.Anonymous**

```csharp
// TODOS los endpoints usan Anonymous en Azure Functions
[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/company-profiles")]
[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/company-profiles-active")]
[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/packages")]
// etc.
```

**Significa:** Incluso sin X-Settings-Write-Secret, puedo:
```bash
# ✓ Leer companies de CUALQUIER empresa
curl "http://localhost:7071/api/scout/packages?profileKey=arrivia"

# ✓ Leer data de CUALQUIER empresa  
curl "http://localhost:7071/api/scout/packages?profileKey=iceenterprise"

# ✗ Cambiar empresa activa (requiere secret)
curl -X PUT .../company-profiles-active  # → 401 sin secret
```

---

## 📋 Matriz de Permisos Actual vs Lo Que Falta

| Acción | Usuario Anónimo | Usuario Entra ID | Arrivia Admin | ICE Enterprise Admin |
|--------|-----------------|------------------|---------------|----------------------|
| **Leer packages de Arrivia** | ✅ | ✅ | ✅ | ✅ |
| **Leer packages de ICE** | ✅ | ✅ | ✅ | ✅ |
| **Cambiar empresa activa (con secret)** | ✅ | ✅ | ✅ | ✅ |
| **Cambiar empresa activa (sin secret)** | ❌ | ❌ | ❌ | ❌ |

**El problema:** No hay diferencia entre Arrivia Admin e ICE Enterprise Admin.

---

## 🚀 Opciones de Auth Disponibles

### Opción 1: **Mantener Como Está (Multi-Company Sin Auth)**

**Cuándo usar:** Entorno cerrado, admin único, desarrollo

**Cómo funciona:**
1. Usuario anónimo accede al portal
2. Ve dropdown de empresas
3. Elige cual empresa monitorear
4. Puede cambiar con X-Settings-Write-Secret

**Permisos:**
```
Todos → Pueden ver: ✓ Todos los datos
                   ✗ Cambiar empresa activa (sin secret)
```

**Config:**
```json
{
  "AzureAd": {
    "TenantId": "",  // Vacío = sin auth
    "ClientId": ""
  },
  "JourneyMonitorApi": {
    "SettingsWriteSecret": "secret-123"
  }
}
```

---

### Opción 2: **Agregar SSO Entera ID (Multi-Company + Auth UI)**

**Cuándo usar:** Múltiples operadores en MISMA empresa

**Cómo funciona:**
1. Usuario hace login con AAD (Office 365)
2. Lee todas las empresas de la BD
3. Puede cambiar cual empresa monitorear
4. Cambio persistido en `ActiveCompanyProfile`

**Permisos:**
```
Usuarios Entra ID → Solo pueden cambiar si tienen X-Settings-Write-Secret
                    Todos ven todos los datos (sin restrict por empresa)
```

**Config:**
```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",  // Tu tenant AAD
    "ClientId": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy"   // App Registration
  }
}
```

**Cambios de código:**
```csharp
// 1. En Program.cs ya está ready:
if (hasEntraIdConfig)
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
}

// 2. En app.js, obtener usuario actual (opcional):
async function getCurrentUser() {
    const response = await fetch("/user");  // Endpoint que expones
    return response.ok ? response.json() : null;
}
```

**Limitación:** Todos ven todos los datos. No hay segregación por empresa.

---

### Opción 3: **Multi-Tenant REAL (Lo que Necesitas)**

**Cuándo usar:** cgareca@arrivia.com vs cgareca@iceenterprise.com

**Arquitectura:**

```
┌─────────────────────────────────────────────────────────────────┐
│                        USER LOGIN                               │
│              cgareca@arrivia.com                                │
└─────────────┬───────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  1. Entera ID SSO                                              │
│     ├─ Tenant: common (multi-tenant app)                       │
│     ├─ Email claim: cgareca@arrivia.com                        │
│     └─ Groups claim: ["arrivia-monitoring-ops"] (opcional)     │
└─────────────┬───────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  2. Middleware: TenantDiscovery                                │
│     ├─ Parsea email: cgareca@arrivia.com                      │
│     ├─ Extrae: "arrivia"                                       │
│     ├─ Query: SELECT * FROM monitoring.CompanyProfile         │
│     │         WHERE ProfileKey = 'arrivia'                    │
│     └─ Configura context.TenantId = 'arrivia'                 │
└─────────────┬───────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  3. Autorización por TenantId                                  │
│     ├─ GET /api/scout/packages                                │
│     │   → WHERE ProfileKey = context.TenantId                 │
│     ├─ PUT /api/settings/company-profiles-active              │
│     │   → Solo si newCompany == context.TenantId              │
│     └─ RCA searches                                            │
│         → ProfileKey = context.TenantId OR ''                 │
└─────────────┬───────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  4. RESULTADO: Usuario solo ve datos de su empresa              │
│     ├─ Packages de "arrivia" = SÍ                             │
│     ├─ Packages de "iceenterprise" = NO (403 Forbidden)       │
│     └─ Cambiar a "iceenterprise" = NO (403 Forbidden)         │
└─────────────────────────────────────────────────────────────────┘
```

---

## 🛠️ Implementación de Multi-Tenant Real

### Paso 1: Crear Identity Claim Mapping

**Problema:** Entra ID da email, pero ProfileKey es "arrivia", "iceenterprise", etc.

**Solución A: Email Domain Mapping**
```csharp
// Portal/src/JourneyMonitor.Web/Services/TenantDiscoveryService.cs
public class TenantDiscoveryService
{
    private readonly ICompanyProfileStore _store;
    
    // Mapeo manual: email domain → ProfileKey
    private static readonly Dictionary<string, string> EmailDomainToTenant = new()
    {
        { "@arrivia.com", "arrivia" },
        { "@iceenterprise.com", "iceenterprise" },
        { "@acme.com", "acme" },
    };
    
    public async Task<string?> DiscoverTenantAsync(string email, CancellationToken ct)
    {
        // 1. Extraer domain del email
        var domain = email.Split('@').LastOrDefault()?.ToLower();
        if (domain is null) return null;
        
        // 2. Mapear a ProfileKey
        if (!EmailDomainToTenant.TryGetValue($"@{domain}", out var profileKey))
            return null;  // Usuario no autorizado
        
        // 3. Verificar que empresa existe
        var company = await _store.GetAsync(profileKey, ct);
        return company?.Enabled == true ? profileKey : null;
    }
}
```

**Solución B: Entera ID App Roles**
```csharp
// En Azure AD, asignar "roles" a usuarios:
// cgareca@arrivia.com → "arrivia-monitoring"
// cgareca@iceenterprise.com → "iceenterprise-monitoring"

public async Task<string?> DiscoverTenantAsync(ClaimsPrincipal user, CancellationToken ct)
{
    // Leer roles de token
    var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
    
    // Mapear role → ProfileKey
    var profileKey = roles switch
    {
        { } r when r.Contains("arrivia-monitoring") => "arrivia",
        { } r when r.Contains("iceenterprise-monitoring") => "iceenterprise",
        _ => null
    };
    
    return profileKey;
}
```

---

### Paso 2: Middleware de Tenant Discovery

**Archivo: `Portal/src/JourneyMonitor.Web/Middleware/TenantMiddleware.cs`**

```csharp
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    
    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }
    
    public async Task InvokeAsync(
        HttpContext context,
        TenantDiscoveryService discoveryService)
    {
        // 1. Si usuario autenticado
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var email = context.User.FindFirst("preferred_username")?.Value
                     ?? context.User.FindFirst(ClaimTypes.Email)?.Value;
            
            if (!string.IsNullOrEmpty(email))
            {
                // 2. Descubrir su tenant
                var tenant = await discoveryService.DiscoverTenantAsync(email, context.RequestAborted);
                
                if (tenant is not null)
                {
                    // 3. Configurar en contexto
                    context.Items["TenantId"] = tenant;
                }
                else
                {
                    // Usuario no tiene empresa asignada
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "User is not assigned to any monitored company." });
                    return;
                }
            }
        }
        
        await _next(context);
    }
}
```

**Registrar en Program.cs:**
```csharp
app.UseMiddleware<TenantMiddleware>();
```

---

### Paso 3: Filtrar Datos por Tenant

**En Portal (Razor/API):**
```csharp
app.MapGet("/api/scout/packages", async (HttpContext context, IJourneyMonitorApiClient client) =>
{
    // 1. Obtener tenant del contexto
    var tenantId = context.Items["TenantId"] as string;
    
    // 2. Si no hay tenant → 403
    if (string.IsNullOrEmpty(tenantId))
        return Results.Forbid();
    
    // 3. Pasar como profileKey al Functions app
    var packages = await client.GetPackagesAsync(profileKey: tenantId);
    
    return Results.Ok(new { packages });
});
```

**En Functions (Backend):**
```csharp
[Function("GetScoutPackages")]
public async Task<HttpResponseData> GetAsync(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/packages")] 
    HttpRequestData req,
    IInvestigationPackageRepository repo,
    CancellationToken ct)
{
    // 1. Leer profileKey (viene del Portal con tenant aplicado)
    var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
    var profileKey = query["profileKey"];
    
    // 2. Si no viene profileKey → error
    if (string.IsNullOrEmpty(profileKey))
        return await WriteJsonAsync(req, HttpStatusCode.BadRequest, 
            new { error = "profileKey is required (tenant detection failed)" });
    
    // 3. Repo ya filtra por profileKey
    var packages = await repo.GetRecentAsync(top: 50, profileKey);
    
    return await WriteJsonAsync(req, HttpStatusCode.OK, new { packages });
}
```

---

### Paso 4: Portal Config

**Archivo: `Portal/src/JourneyMonitor.Web/appsettings.Production.json`**

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "common",  // ← Importante: "common" para multi-tenant
    "ClientId": "abcd1234-...",
    "ClientSecret": "secret",  // ← Necesario para bearer token
    "CallbackPath": "/signin-oidc"
  },
  "TenantDiscovery": {
    "Method": "EmailDomain",  // o "AppRoles"
    "EmailDomainMapping": {
      "@arrivia.com": "arrivia",
      "@iceenterprise.com": "iceenterprise"
    }
  },
  "JourneyMonitorApi": {
    "BaseUrl": "https://functions-prod.azure.com",
    "SettingsWriteSecret": "strong-secret-here"
  }
}
```

---

## ✅ Comparativa: Qué Tienes vs Qué Necesitas

| Aspecto | Hoy (Multi-Company) | Opción 2 (+ SSO) | Opción 3 (Real Multi-Tenant) |
|---------|-------------------|-----------------|------------------------------|
| **Autenticación** | ❌ Opcional | ✅ Requerida (Entra ID) | ✅ Requerida (Entra ID) |
| **Usuario vé datos de su empresa** | ❌ NO | ❌ NO | ✅ SÍ |
| **Dropdown de empresas** | ✅ SÍ | ✅ SÍ | ❌ NO (automático) |
| **Puede cambiar a otra empresa** | ✅ SÍ | ✅ SÍ | ❌ NO (403) |
| **Autorización por empresa** | ❌ NO | ❌ NO | ✅ SÍ |
| **Casos de uso** | Single operator, dev | Multi operators, same company | Multiple tenants, security critical |
| **Complejidad** | ⭐ Muy baja | ⭐⭐ Media | ⭐⭐⭐ Alta |
| **Tiempo de implementación** | 0 horas | 4-6 horas | 2-3 días |

---

## 🚀 Recomendación Por Caso

### Si tienes un operador que monitorea múltiples empresas:
**→ Opción 1 (Actual) es suficiente**
- Usa X-Settings-Write-Secret para cambios
- El operador switchea en el dropdown

### Si tienes múltiples operadores EN LA MISMA EMPRESA (ej: Arrivia):
**→ Opción 2 (Multi-Company + SSO)**
- Agrega AzureAd:TenantId
- Todos ven todos los datos de Arrivia
- Requiere login pero no restricción por empresa

### Si tienes operadores EN DISTINTAS EMPRESAS (ej: Arrivia vs ICE):
**→ Opción 3 (Real Multi-Tenant)**
- Implementa TenantDiscoveryService
- Cada usuario solo ve su empresa
- Seguridad real entre tenants

---

## 📊 Matriz de Seguridad

```
┌─────────────────────────────────────────────────────────────┐
│                    SECURITY LEVELS                          │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│ Level 0: Anónimo + API Key                                  │
│ ├─ Usuario: Qualquiera                                      │
│ ├─ READ: Todos los datos (sin restrict)                    │
│ ├─ WRITE: Requiere X-Settings-Write-Secret                │
│ └─ Riesgo: ALTO (cualquiera ve datos de otros)            │
│    ✓ Bueno para: Dev local, admin único                   │
│                                                               │
│ Level 1: SSO + API Key                                      │
│ ├─ Usuario: Entra ID (empresa)                             │
│ ├─ READ: Todos los datos (sin restrict por empresa)        │
│ ├─ WRITE: Requiere X-Settings-Write-Secret                │
│ └─ Riesgo: MEDIO (autenticado pero sin segregación)       │
│    ✓ Bueno para: Multi-operator, same company              │
│                                                               │
│ Level 2: SSO + Tenant Authorization                         │
│ ├─ Usuario: Entra ID (empresa específica)                  │
│ ├─ READ: Solo datos de su empresa                          │
│ ├─ WRITE: Solo si es admin de su empresa                   │
│ └─ Riesgo: BAJO (segregación y autenticación)             │
│    ✓ Bueno para: Múltiples tenants, producción             │
│                                                               │
│ Level 3: SSO + RBAC + Audit                                 │
│ ├─ Usuario: Entra ID (empresa + rol específico)           │
│ ├─ READ: Solo si tiene rol "viewer" o "admin"             │
│ ├─ WRITE: Solo si tiene rol "admin"                        │
│ ├─ AUDIT: Quién hizo qué, cuándo                          │
│ └─ Riesgo: MUY BAJO (máxima seguridad)                     │
│    ✓ Bueno para: Compliance, SOC2, multi-tenant crítico    │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

---

## 📞 Resumen

**Hoy tienes:** Multi-Company (selector de empresa)
- Un usuario puede cambiar qué empresa monitorear
- Sin autenticación real
- Sin aislamiento por usuario

**Opciones:**

1. **Mantener como está** → Bueno para dev/single operator
2. **Agregar SSO** → Múltiples operadores, misma empresa
3. **Real Multi-Tenant** → Operadores en distintas empresas, segregación real

¿Cuál es tu caso de uso específico?

