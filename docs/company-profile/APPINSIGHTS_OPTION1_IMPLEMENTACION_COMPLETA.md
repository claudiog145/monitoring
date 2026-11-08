# AppInsights - OPCIÓN 1 (Token Externo) - IMPLEMENTACIÓN COMPLETA

## 🎯 Resumen de la Solución

Esta es la **OPCIÓN 1 (Token Externo)** completamente implementada para que:
- `cgareca@arrivia.com` genere su propio token en Azure
- `cgareca@ice.com` genere su propio token en Azure
- Ambos peguen sus tokens en Portal
- Portal guarde tokens encriptados en BD
- Cuando consulten AppInsights, Portal usa el token guardado

## ✅ Archivos Implementados

### Backend (.NET C#)

1. **`AppInsightsToken.cs`** - Modelo de datos
   - Ubicación: `Portal/src/JourneyMonitor.Core/Monitoring/`
   - Contiene: Token encriptado, metadata, audit trail

2. **`TenantResolver.cs`** - Mapeo de tenants
   - Ubicación: `Portal/src/JourneyMonitor.Core/Monitoring/`
   - Contiene: Mapeo de tenant keys a configuración de AppInsights

3. **`AppInsightsTokenService.cs`** - Lógica de tokens
   - Ubicación: `Portal/src/JourneyMonitor.Core/Monitoring/`
   - Contiene: Guardar, obtener, validar, encriptar tokens

4. **`SqlAppInsightsTokenRepository.cs`** - Acceso a datos
   - Ubicación: `Portal/src/JourneyMonitor.Core/Monitoring/`
   - Contiene: CRUD de tokens en SQL Server

5. **`AppInsightsQueryService.cs`** - Consulta de logs
   - Ubicación: `Portal/src/JourneyMonitor.Core/Monitoring/`
   - Contiene: Consulta AppInsights usando tokens almacenados

6. **`AppInsightsTokenController.cs`** - API endpoints
   - Ubicación: `Portal/src/JourneyMonitor.Functions/Functions/`
   - Endpoints:
     - `POST /api/appinsights/tokens` - Agregar token
     - `POST /api/appinsights/tokens/validate` - Validar token
     - `GET /api/appinsights/tokens` - Listar tokens
     - `GET /api/appinsights/tokens/{tenantKey}/status` - Ver estado
     - `POST /api/appinsights/tokens/{tenantKey}/deactivate` - Desactivar

7. **`AppInsightsLogsController.cs`** - API de consultas
   - Ubicación: `Portal/src/JourneyMonitor.Functions/Functions/`
   - Endpoints:
     - `POST /api/appinsights/logs` - Consultar logs
     - `GET /api/appinsights/metrics` - Obtener métricas
     - `GET /api/appinsights/samples` - Ver queries de ejemplo

### Base de Datos

8. **`20241030_CreateAppInsightsTokensTable.sql`** - Migración
   - Ubicación: `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/`
   - Crea tabla `AppInsightsTokens` con índices

### Frontend

9. **`AppInsightsTokens.cshtml`** - UI para tokens
   - Ubicación: `Portal/src/JourneyMonitor.Web/Pages/`
   - Incluye: Formulario para agregar tokens, lista de tokens, ejemplos de queries

### Configuración

10. **`APPINSIGHTS_OPTION1_PROGRAM_CS_CONFIG.txt`** - Setup en Program.cs
    - Ubicación: Raíz del repositorio
    - Contiene: Registros de servicios a agregar en Program.cs

---

## 🚀 PASO A PASO - IMPLEMENTACIÓN

### PASO 1: Crear la Base de Datos (SQL Server)

```bash
# Ejecutar la migración SQL
# Archivo: Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/20241030_CreateAppInsightsTokensTable.sql

# En SQL Server Management Studio:
# 1. Abre el archivo
# 2. Click en "Execute"
# 3. Verifica que se cree la tabla [AppInsightsTokens]
```

**Verificación:**
```sql
SELECT * FROM [AppInsightsTokens]
-- Debe retornar 0 filas (tabla vacía pero creada)
```

### PASO 2: Copiar Archivos C# al Proyecto

```bash
# Todos estos archivos ya están creados, copiar a sus ubicaciones:

# Core (Lógica de negocio)
Portal/src/JourneyMonitor.Core/Monitoring/
  ├── AppInsightsToken.cs              [ya existe]
  ├── TenantResolver.cs                [ya existe]
  ├── AppInsightsTokenService.cs       [ya existe]
  ├── SqlAppInsightsTokenRepository.cs [ya existe]
  └── AppInsightsQueryService.cs       [ya existe]

# Functions (API Controllers)
Portal/src/JourneyMonitor.Functions/Functions/
  ├── AppInsightsTokenController.cs    [ya existe]
  └── AppInsightsLogsController.cs     [ya existe]
```

### PASO 3: Instalar Paquetes NuGet (si no están)

```bash
cd Portal

# Verificar que estos paquetes estén en el csproj:
# - Azure.Monitor.Query
# - Azure.Identity
# - Microsoft.AspNetCore.DataProtection

# Si faltan, instalar:
dotnet add package Azure.Monitor.Query --version 1.3.0
dotnet add package Azure.Identity --version 1.10.0
```

### PASO 4: Configurar Program.cs

**Abre:** `Portal/src/JourneyMonitor.Web/Program.cs`

**Agrega estos using statements arriba:**
```csharp
using JourneyMonitor.Core.Monitoring;
using Microsoft.AspNetCore.DataProtection;
```

**Agrega esta configuración después de otros `builder.Services.Add*`:**
```csharp
// AppInsights Token Management (Option 1 - External Tokens)
builder.Services.AddDataProtection();

builder.Services.AddSingleton<IAppInsightsTokenRepository>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var logger = sp.GetRequiredService<ILogger<SqlAppInsightsTokenRepository>>();
    return new SqlAppInsightsTokenRepository(connectionString, logger);
});

builder.Services.AddScoped<IAppInsightsTokenService, AppInsightsTokenService>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();
builder.Services.AddSingleton<ITenantResolver, TenantResolver>();
```

### PASO 5: Configurar Tenants

**Abre:** `Portal/src/JourneyMonitor.Core/Monitoring/TenantResolver.cs`

**Reemplaza los valores marcados con `YOUR-`:**

```csharp
{
    "arrivia", new AppInsightsTenant
    {
        TenantKey = "arrivia",
        SubscriptionId = "YOUR-ARRIVIA-SUBSCRIPTION-ID",  // ← REEMPLAZAR
        ResourceGroup = "YOUR-ARRIVIA-RESOURCE-GROUP",     // ← REEMPLAZAR
        AppInsightsName = "YOUR-ARRIVIA-APPINSIGHTS-NAME", // ← REEMPLAZAR
        AadTenantId = "YOUR-ARRIVIA-AAD-TENANT-ID"         // ← REEMPLAZAR
    }
},
{
    "ice", new AppInsightsTenant
    {
        TenantKey = "ice",
        SubscriptionId = "YOUR-ICE-SUBSCRIPTION-ID",       // ← REEMPLAZAR
        ResourceGroup = "YOUR-ICE-RESOURCE-GROUP",         // ← REEMPLAZAR
        AppInsightsName = "YOUR-ICE-APPINSIGHTS-NAME",     // ← REEMPLAZAR
        AadTenantId = "YOUR-ICE-AAD-TENANT-ID"             // ← REEMPLAZAR
    }
}
```

**Cómo obtener estos valores:**
```bash
# Para Arrivia:
az account set --subscription "YOUR-SUBSCRIPTION-NAME"
az resource list --resource-type "microsoft.insights/components" --query "[].{name:name, resourceGroup:resourceGroup, subscriptionId:id}" -o table

# Copia: subscriptionId, resourceGroup, name
```

### PASO 6: Copiar Frontend UI

**Copia:** `Portal/src/JourneyMonitor.Web/Pages/AppInsightsTokens.cshtml`

**Agrega link en el layout o menú:**
```html
<a href="/AppInsightsTokens">AppInsights Settings</a>
```

### PASO 7: Compilar y Probar

```bash
cd Portal

# Compilar
dotnet build

# Debe compilar sin errores

# Ejecutar
dotnet run

# Accede a: http://localhost:7001/AppInsightsTokens (o tu puerto)
```

---

## 🧪 TESTING

### TEST 1: Agregar Token (Usuario Arrivia)

```bash
# 1. Genera token en tu máquina:
az login --tenant YOUR-ARRIVIA-TENANT-ID
az account get-access-token --scope https://management.azure.com/

# Copia el accessToken (el string largo)

# 2. Abre http://localhost:7001/AppInsightsTokens

# 3. Llena el formulario:
#    Tenant: arrivia
#    Email: cgareca@arrivia.com
#    Token: [pega el token]

# 4. Click "Save Token"

# ESPERADO: ✅ Token saved successfully
```

### TEST 2: Agregar Token (Usuario Ice)

```bash
# 1. Genera token DIFERENTE (otro tenant):
az login --tenant YOUR-ICE-TENANT-ID
az account get-access-token --scope https://management.azure.com/

# 2. Llena formulario:
#    Tenant: ice
#    Email: cgareca@ice.com
#    Token: [pega el token]

# 3. Click "Save Token"

# ESPERADO: ✅ Token saved successfully
```

### TEST 3: Consultar Logs (Arrivia)

```bash
# Usar Postman, curl o el frontend

POST http://localhost:7001/api/appinsights/logs
Content-Type: application/json

{
  "tenantKey": "arrivia",
  "query": "customEvents | take 10",
  "timeRange": {
    "startTime": "2024-10-23T00:00:00Z",
    "endTime": "2024-10-30T23:59:59Z"
  }
}

# ESPERADO: ✅ 200 OK con logs de Arrivia
```

### TEST 4: Consultar Logs (Ice)

```bash
POST http://localhost:7001/api/appinsights/logs
Content-Type: application/json

{
  "tenantKey": "ice",
  "query": "customEvents | take 10",
  "timeRange": {...}
}

# ESPERADO: ✅ 200 OK con logs de Ice
```

### TEST 5: Token Expirado

```bash
# Espera a que token expire (~1 hora)
# O simula editando la BD:
UPDATE AppInsightsTokens SET ExpiresAt = GETUTCDATE() WHERE TenantKey = 'arrivia'

# Intenta consultar:
POST http://localhost:7001/api/appinsights/logs
# ESPERADO: ❌ 400 Bad Request - "Token expired"
```

---

## 🔒 Seguridad

### ✅ Lo que está Protegido

```
✓ Tokens almacenados ENCRIPTADOS (Data Protection API)
✓ Tokens NUNCA en logs
✓ Tokens NUNCA en respuestas API
✓ Validación al guardar (se intenta usar con Azure)
✓ Audit trail (email, fecha, uso)
✓ Token puede deactivarse sin borrar
✓ Expiry automático (~1 hora)
```

### ⚠️ Lo que Falta

```
⚠️ Podría agregar autenticación en los endpoints
  → `[Authorize]` si necesitas restringir quién puede ver tokens
  
⚠️ Podría refrescar tokens automáticamente
  → Requeriría guardar `refresh_token` (más complejo)
```

---

## 📊 Flujo de Ejecución

```
Usuario: cgareca@arrivia.com

1. GENERAR TOKEN (en su máquina)
   az login --tenant arrivia-id
   az account get-access-token --scope https://management.azure.com/
   → Token generado (válido 1 hora)

2. PEGAR TOKEN (en Portal)
   POST /api/appinsights/tokens
   {
     tenantKey: "arrivia",
     email: "cgareca@arrivia.com",
     token: "eyJ0eXAi..."
   }

3. BACKEND VALIDA Y GUARDA
   ✓ Valida JWT format
   ✓ Encripta token
   ✓ Guarda en [AppInsightsTokens]
   → SQL: INSERT INTO AppInsightsTokens (TenantKey, EncryptedToken, ...)

4. USUARIO CONSULTA LOGS
   POST /api/appinsights/logs
   {
     tenantKey: "arrivia",
     query: "customEvents | take 10"
   }

5. BACKEND RECUPERA Y USA TOKEN
   ✓ Lee token de BD
   ✓ Desencripta
   ✓ Usa para consultar AppInsights API
   ✓ Retorna logs

6. USUARIO VE LOGS
   ✅ Solo datos de Arrivia
   ✅ Token nunca expuesto al usuario
```

---

## 🛠️ Troubleshooting

### Error: "Table AppInsightsTokens doesn't exist"

**Solución:** Ejecutar la migración SQL:
```sql
-- Archivo: Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/20241030_CreateAppInsightsTokensTable.sql
-- En SQL Server Management Studio, abrir y ejecutar
```

### Error: "Token validation failed"

**Causas posibles:**
- Token inválido o malformado
- Token expirado
- Token generado con scope incorrecto

**Solución:**
```bash
# Generar nuevo token:
az login --tenant YOUR-TENANT-ID
az account get-access-token --scope https://management.azure.com/
# Pega el COMPLETO accessToken value
```

### Error: "Tenant configuration not found"

**Solución:** Editar `TenantResolver.cs` y agregar el tenant:
```csharp
{
    "your-tenant", new AppInsightsTenant
    {
        TenantKey = "your-tenant",
        SubscriptionId = "...",
        ResourceGroup = "...",
        AppInsightsName = "...",
        AadTenantId = "..."
    }
}
```

### Error: "Access denied" al consultar AppInsights

**Causas:**
- Token válido pero sin permisos en AppInsights
- SubscriptionId/ResourceGroup incorrecto en config

**Solución:**
```bash
# Verifica permisos del token:
az account get-access-token --scope https://management.azure.com/ | jq .
# Verifica que sea del tenant correcto

# Verifica que la cuenta tenga Reader role en AppInsights:
az role assignment list --scope "/subscriptions/YOUR-SUB/resourceGroups/YOUR-RG/providers/microsoft.insights/components/YOUR-AI"
```

---

## 📈 Monitoreo

### Ver tokens almacenados:

```sql
SELECT 
    TenantKey,
    Email,
    CreatedAt,
    ExpiresAt,
    IsActive,
    UsageCount,
    LastUsedAt
FROM AppInsightsTokens
ORDER BY CreatedAt DESC
```

### Ver histórico de uso:

```sql
SELECT 
    TenantKey,
    Email,
    UsageCount,
    LastUsedAt
FROM AppInsightsTokens
WHERE UsageCount > 0
ORDER BY LastUsedAt DESC
```

### Limpiar tokens expirados:

```sql
UPDATE AppInsightsTokens
SET IsActive = 0
WHERE ExpiresAt < GETUTCDATE()
```

---

## 🎯 Próximos Pasos

1. ✅ Ejecutar migración SQL
2. ✅ Copiar archivos C# (ya creados)
3. ✅ Instalar NuGet packages (si faltan)
4. ✅ Actualizar Program.cs
5. ✅ Configurar tenants en TenantResolver.cs
6. ✅ Compilar y ejecutar
7. ✅ Test: Agregar tokens
8. ✅ Test: Consultar logs
9. ✅ Deploy a staging
10. ✅ Deploy a producción

---

## 📞 Resumen de APIs

### Endpoints de Tokens

```
POST   /api/appinsights/tokens              - Agregar token
POST   /api/appinsights/tokens/validate     - Validar token
GET    /api/appinsights/tokens              - Listar tokens
GET    /api/appinsights/tokens/{key}/status - Ver estado
POST   /api/appinsights/tokens/{key}/deactivate - Desactivar
GET    /api/appinsights/tenants             - Ver tenants
GET    /api/appinsights/health              - Health check
```

### Endpoints de Logs

```
POST   /api/appinsights/logs        - Consultar logs
GET    /api/appinsights/metrics     - Obtener métricas
GET    /api/appinsights/samples     - Ver queries ejemplo
GET    /api/appinsights/health      - Health check
```

---

**¡IMPLEMENTACIÓN COMPLETA LISTA!** 🚀

Todos los archivos están creados y listos para usar. Solo sigue los pasos de PASO A PASO.
