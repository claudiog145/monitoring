# 🚀 EJECUTAR PORTAL EN LOCAL

## ✅ Estado Actual

- ✅ Código compilado (Release)
- ✅ Publicado a local
- ✅ 0 errores de compilación
- ✅ Listo para ejecutar

## 📍 Ubicaciones

**Web publicado:**
```
C:\Arrivia\ai-reliable-platform\Portal\publish\web
```

**Functions publicado:**
```
C:\Arrivia\ai-reliable-platform\Portal\publish\functions
```

---

## 🏃 OPCIÓN 1: Ejecutar WEB (Recomendado para desarrollo)

### Desde el directorio source (con hot reload):

```bash
cd C:\Arrivia\ai-reliable-platform\Portal\src\JourneyMonitor.Web
dotnet run --configuration Release
```

**Acceder a:**
```
http://localhost:7001
```

O verifica en la consola qué puerto está usando.

---

## 🏃 OPCIÓN 2: Ejecutar desde Publish

### Ejecutar Web:

```bash
cd C:\Arrivia\ai-reliable-platform\Portal\publish\web
dotnet JourneyMonitor.Web.dll
```

**Acceder a:**
```
http://localhost:5000
```

---

## ⚙️ Configuración Requerida (ANTES de ejecutar)

### 1. Migración SQL

Ejecutar la migración para crear la tabla de tokens:

```bash
# En SQL Server Management Studio:
# Archivo: Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/
#          20241030_CreateAppInsightsTokensTable.sql
# Click: Execute (F5)
```

**Verificar:**
```sql
SELECT * FROM [AppInsightsTokens]
```

### 2. Configurar Tenants

**Archivo:** `Portal/src/JourneyMonitor.Core/Monitoring/TenantResolver.cs`

**Líneas 62-90:** Reemplazar valores

```csharp
{
    "arrivia", new AppInsightsTenant
    {
        TenantKey = "arrivia",
        SubscriptionId = "YOUR-ARRIVIA-SUBSCRIPTION-ID",     // ← REEMPLAZAR
        ResourceGroup = "YOUR-ARRIVIA-RESOURCE-GROUP",       // ← REEMPLAZAR
        AppInsightsName = "YOUR-ARRIVIA-APPINSIGHTS-NAME",   // ← REEMPLAZAR
        AadTenantId = "YOUR-ARRIVIA-AAD-TENANT-ID"           // ← REEMPLAZAR
    }
},
{
    "ice", new AppInsightsTenant
    {
        TenantKey = "ice",
        SubscriptionId = "YOUR-ICE-SUBSCRIPTION-ID",         // ← REEMPLAZAR
        ResourceGroup = "YOUR-ICE-RESOURCE-GROUP",           // ← REEMPLAZAR
        AppInsightsName = "YOUR-ICE-APPINSIGHTS-NAME",       // ← REEMPLAZAR
        AadTenantId = "YOUR-ICE-AAD-TENANT-ID"               // ← REEMPLAZAR
    }
}
```

**Cómo obtener estos valores:**

```bash
# Login a Arrivia
az login --tenant ARRIVIA-TENANT-ID

# Listar recursos
az resource list --resource-type "microsoft.insights/components" \
  --query "[].{name:name, resourceGroup:resourceGroup, subscriptionId:id}" -o table

# Copiar valores y reemplazar arriba
```

---

## 🧪 Testing

### Test 1: Agregar Token Arrivia

```bash
# 1. Generar token
az login --tenant ARRIVIA-TENANT-ID
az account get-access-token --scope https://management.azure.com/

# 2. Ir a: http://localhost:7001/AppInsightsTokens

# 3. Llenar formulario:
#    Tenant: arrivia
#    Email: cgareca@arrivia.com
#    Token: [pega el token]
#    Click: "Save Token"

# ✅ Esperado: "Token saved successfully"
```

### Test 2: Agregar Token Ice

```bash
# 1. Generar token (diferente tenant)
az login --tenant ICE-TENANT-ID
az account get-access-token --scope https://management.azure.com/

# 2. Ir a: http://localhost:7001/AppInsightsTokens

# 3. Llenar formulario:
#    Tenant: ice
#    Email: cgareca@ice.com
#    Token: [pega el token]
#    Click: "Save Token"

# ✅ Esperado: "Token saved successfully"
```

### Test 3: Consultar Logs

```bash
# Con curl, Postman, o desde el código:

curl -X POST http://localhost:7001/api/appinsights/logs \
  -H "Content-Type: application/json" \
  -d '{
    "tenantKey": "arrivia",
    "query": "customEvents | take 10",
    "timeRange": {
      "startTime": "2024-10-23T00:00:00Z",
      "endTime": "2024-10-30T23:59:59Z"
    }
  }'

# ✅ Esperado: 200 OK con logs de Arrivia
```

---

## 🐛 Troubleshooting

### Error: "Port already in use"

```bash
# El puerto 7001 (o 5000) está siendo usado
# Opción 1: Usar otro puerto
dotnet run --configuration Release -- --urls="http://localhost:8080"

# Opción 2: Matar el proceso usando el puerto
Get-Process | Where-Object {$_.Name -like "*dotnet*"} | Stop-Process -Force
```

### Error: "Connection string not configured"

Verificar que existe la conexión a BD SQL Server en:
```
C:\Arrivia\ai-reliable-platform\Portal\publish\web\appsettings.json
```

### Error: "Token validation failed"

El token debe ser generado con exactamente este comando:
```bash
az account get-access-token --scope https://management.azure.com/
```

**Copiar:** El valor completo del campo `accessToken` (no copies el JSON completo)

---

## 📊 URLs Importantes

| URL | Propósito |
|-----|-----------|
| http://localhost:7001 | Portal principal |
| http://localhost:7001/AppInsightsTokens | Gestión de tokens ⭐ |
| http://localhost:7001/api/appinsights/tokens | API: Listar tokens |
| http://localhost:7001/api/appinsights/logs | API: Consultar logs |
| http://localhost:7001/api/appinsights/health | API: Health check |

---

## 📝 Logs

Para ver logs detallados, agregar a `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "JourneyMonitor": "Debug"
    }
  }
}
```

---

## 🔒 Notas de Seguridad

- Tokens almacenados ENCRIPTADOS en BD
- NUNCA dejar tokens en logs
- Tokens expiran en ~1 hora
- Usar HTTPS en producción

---

## ✅ Checklist Antes de Ejecutar

- [ ] Ejecuté la migración SQL
- [ ] Configuré tenants en TenantResolver.cs
- [ ] Verifiqué conexión a BD SQL Server
- [ ] Tengo generados tokens de Arrivia e Ice
- [ ] Puerto 7001 (o 5000) disponible

---

**¡LISTO PARA EJECUTAR!** 🚀
