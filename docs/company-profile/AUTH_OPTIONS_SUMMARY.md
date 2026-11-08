# ⚠️ ACLARACIÓN: Multi-Company vs Multi-Tenant

## La Pregunta Importante

**"¿Qué pasa si tengo cgareca@arrivia.com y cgareca@iceenterprise.com?"**

La respuesta es: **Depende de qué quieres lograr.**

---

## 🎯 Diferencia Crítica

### ❌ LO QUE TIENEN HOY: Multi-Company

```
┌─────────────────────────────────────────────────┐
│  Portal: "¿Cuál empresa quiero monitorear?"    │
│                                                  │
│  Usuario → Dropdown → Elige "arrivia" o "ice"  │
│                                                  │
│  Resultado: Todos ven todos los datos           │
│            (si tienen acceso al portal)          │
│                                                  │
│ SEGURIDAD: ⭐ Muy débil para multi-tenant      │
└─────────────────────────────────────────────────┘
```

### ✅ LO QUE NECESITAS: Real Multi-Tenant

```
┌─────────────────────────────────────────────────┐
│  Login: Email + Password                        │
│                                                  │
│  cgareca@arrivia.com → Entera ID descubre      │
│                      → Configura TenantId       │
│                      → Solo ve datos de Arrivia │
│                                                  │
│  cgareca@iceenterprise.com → Configurar Ice   │
│                            → Solo ve Ice        │
│                                                  │
│ SEGURIDAD: ⭐⭐⭐ Real, segregado              │
└─────────────────────────────────────────────────┘
```

---

## 📋 Opciones de Inicio de Sesión

### Opción 1: Sin Autenticación (Hoy)

```bash
# Ir al portal
http://localhost:3000

# ✗ Sin login
# ✗ Cualquiera accede
# ✓ Desarrollo/admin único
```

---

### Opción 2: SSO Entra ID (Mismo Tenant)

```bash
# Configurar
AzureAd:TenantId = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
AzureAd:ClientId = "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy"

# Resultado
http://localhost:3000
  → Redirige a login.microsoftonline.com
  → Usuario hace login con Office 365
  → Accede al portal
  → PERO: Sigue viendo todos los datos de todas las empresas
```

---

### Opción 3: SSO Multi-Tenant (Real)

```bash
# Configurar
AzureAd:TenantId = "common"  # Multi-tenant app
AzureAd:ClientId = "..."
TenantDiscovery:Method = "EmailDomain"

# Resultado
cgareca@arrivia.com
  → Login en Entera ID
  → Sistema descubre: "arrivia"
  → Solo ve datos de Arrivia
  → No puede cambiar a Ice Enterprise

cgareca@iceenterprise.com
  → Login en Entera ID
  → Sistema descubre: "iceenterprise"
  → Solo ve datos de Ice
  → No puede cambiar a Arrivia
```

---

## 🔐 Opciones de Autenticación

### ✓ Opción 1: API Key (X-Settings-Write-Secret)

```bash
# Para cambiar empresa activa
curl -X PUT http://localhost:7071/api/settings/company-profiles-active \
  -H "X-Settings-Write-Secret: secret-123" \
  -d '{"profileKey": "arrivia"}'
```

**Hoy usa esto para:** Proteger escrituras
**Problema:** No diferencia entre usuarios

---

### ✓ Opción 2: Entera ID SSO

```bash
# Usuario hace login
http://localhost:3000
→ Redirijo a login.microsoftonline.com
→ Usuario entra con email + password
→ Token JWT con claims (email, roles, etc.)
```

**Archivo listo:** `Program.cs` ya tiene AddMicrosoftIdentityWebApp()
**Solo falta:** AzureAd:TenantId + ClientId

---

### ✓ Opción 3: Multi-Tenant App

```bash
# App registrada en AAD como MULTI-TENANT
# Soporta login de CUALQUIER Azure AD (no solo tuyo)

# Además de SSO:
# + TenantDiscoveryService (nuevo)
# + TenantMiddleware (nuevo)
# + Email domain mapping (nuevo)
```

**Arquitectura:**
```
Login → Entera ID descubre email
     → TenantDiscoveryService mapea @domain → ProfileKey
     → TenantMiddleware inyecta TenantId en contexto
     → Todos los GETs filtran por ProfileKey = TenantId
     → Usuario solo ve datos de su empresa
```

---

## 🛠️ Cómo Gestionar Permisos

### HOY (Multi-Company)

| Acción | Permiso |
|--------|---------|
| Leer packages de Arrivia | ✅ Cualquiera |
| Leer packages de Ice | ✅ Cualquiera |
| Cambiar empresa activa | ❌ Solo con X-Settings-Write-Secret |

**Problema:** No hay diferencia de "qué empresa es del usuario"

---

### OPCIÓN 2 (SSO + Multi-Company)

| Acción | Permiso |
|--------|---------|
| Leer packages de Arrivia | ✅ Usuario autenticado |
| Leer packages de Ice | ✅ Usuario autenticado |
| Cambiar empresa activa | ❌ Solo con X-Settings-Write-Secret |

**Mejor:** Al menos requiere login, pero sin segregación

---

### OPCIÓN 3 (Real Multi-Tenant)

| Usuario | Leer Arrivia | Leer Ice | Cambiar Empresa |
|---------|-------------|----------|-----------------|
| cgareca@arrivia.com | ✅ SÍ | ❌ NO | ❌ NO |
| cgareca@iceenterprise.com | ❌ NO | ✅ SÍ | ❌ NO |

**Mejor:** Segregación hermética por usuario

---

## ⚡ Implementación Rápida

### Para Habilitar Opción 2 (SSO básico):

```json
// appsettings.json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "ClientId": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy",
    "CallbackPath": "/signin-oidc"
  }
}
```

**Tiempo:** 30 minutos (config + testing)
**Código:** YA ESTÁ EN Program.cs

---

### Para Implementar Opción 3 (Real Multi-Tenant):

**Paso 1:** Crear TenantDiscoveryService
```csharp
// Service que mapea email → ProfileKey
private static readonly Dictionary<string, string> EmailDomainMap = new()
{
    { "@arrivia.com", "arrivia" },
    { "@iceenterprise.com", "iceenterprise" },
};
```

**Paso 2:** Crear TenantMiddleware
```csharp
// Middleware que inyecta TenantId en cada request
context.Items["TenantId"] = discoveredTenant;
```

**Paso 3:** Filtrar datos por TenantId
```csharp
// En cada GET: ProfileKey = context.Items["TenantId"]
var packages = await repo.GetAsync(
    profileKey: context.Items["TenantId"] as string
);
```

**Tiempo:** 2-3 días
**Complejidad:** Media (testing importante)

---

## 🎓 Matriz de Decisión

**¿Cuándo cada opción?**

| Caso | Opción | Por Qué |
|------|--------|---------|
| Admin único que monitorea TODO | 1 (Actual) | Simple, seguro para un usuario |
| Múltiples ops en ARRIVIA solamente | 2 (SSO) | Autenticación, mismo tenant |
| Ops en Arrivia + Ice + ACME separados | 3 (Multi-Tenant) | Segregación real, seguridad |
| Compliance/SOC2 requerido | 3 (Multi-Tenant) | Auditoría y aislamiento |

---

## 📊 Resumen Rápido

```
HOY:
  • Multi-Company (selector)
  • Sin auth requerida
  • Todos ven todos los datos
  • Perfecta para: admin único + dev

OPCIÓN 2 (Fácil, 4 horas):
  • Entera ID SSO
  • Login requerido
  • Todavía sin segregación
  • Perfecta para: múltiples ops, misma empresa

OPCIÓN 3 (Completo, 2-3 días):
  • Real multi-tenant
  • Login + tenant discovery
  • Segregación hermética
  • Perfecta para: múltiples empresas, compliance
```

---

## 🚀 Próximos Pasos

**Lee:** `MULTI_TENANT_AUTH_GUIDE.md`
- Explicación detallada de cada opción
- Código de ejemplo
- Configuración paso a paso

**Decide:** Cuál caso es el tuyo
- Admin único → Opción 1
- Multi-op mismo tenant → Opción 2
- Multi-tenant → Opción 3

**Implementa:** La arquitectura que necesites

