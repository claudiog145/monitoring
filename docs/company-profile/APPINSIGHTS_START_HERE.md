# 🚀 AppInsights Token Access - START HERE

## Tu Pregunta

> "¿Existe alguna opción donde yo obtenga un token para gestionar recursos de AppInsights de Azure según mi usuario y tenant?
> 
> cgareca@arrivia.com → token Azure → lectura en AppInsights
> cgareca@ice.com → token Azure → lectura en AppInsights"

## ✅ Respuesta: SÍ

**OPCIÓN RECOMENDADA: Backend Proxy (4-6 horas)**

---

## 📚 Leer Documentos en Este Orden

### 1️⃣ **PRIMERO: Este archivo** (5 min) ← Estás aquí
   - Orientación general
   - Qué esperar
   - Cómo proceder

### 2️⃣ **LUEGO: Resumen Ejecutivo** (10 min)
   📄 [APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md](./APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md)
   - En español
   - Explicación completa
   - FAQ rápidas
   - Pasos de implementación

### 3️⃣ **DESPUÉS: Diagramas Visuales** (10 min)
   📐 [APPINSIGHTS_VISUAL_FLOW.md](./APPINSIGHTS_VISUAL_FLOW.md)
   - Flujo paso a paso
   - Escenarios de ataque (cómo se bloquean)
   - Comparativa de opciones

### 4️⃣ **IMPLEMENTACIÓN: Setup Detallado** (30 min lectura)
   📋 [APPINSIGHTS_SETUP_STEPBYSTEP.md](./APPINSIGHTS_SETUP_STEPBYSTEP.md)
   - Setup local línea por línea
   - Comandos Azure CLI copy-paste
   - Testing guide
   - Troubleshooting

### 5️⃣ **CÓDIGO: Implementación** (1-2 horas)
   💻 [APPINSIGHTS_TOKEN_ACCESS.md](./APPINSIGHTS_TOKEN_ACCESS.md)
   - Código C# completo
   - 3 clases (.cs files)
   - Configuración
   - Copy-paste ready

### 6️⃣ **REFERENCIA RÁPIDA** (siempre disponible)
   📖 [APPINSIGHTS_TOKEN_QUICK_GUIDE.md](./APPINSIGHTS_TOKEN_QUICK_GUIDE.md)
   - Resumen de opciones
   - Quick reference
   - FAQ rápidas

---

## 🏗️ Qué Vas a Implementar

### Arquitectura Simple

```
Usuario login (SSO)
     ↓
Frontend obtiene JWT token
     ↓
Llamada API: POST /api/appinsights/logs + JWT
     ↓
Backend:
  • Valida JWT (firma Entera ID)
  • Extrae email: cgareca@arrivia.com
  • Mapea a tenant: "arrivia"
  • Obtiene token Azure (Managed Identity)
  • Consulta AppInsights
     ↓
Azure RBAC:
  • Valida que Managed Identity tiene Reader en ai-arrivia-prod
  • ✓ PERMITIDO
     ↓
Retorna logs SOLO de Arrivia
```

### Componentes a Crear

```
TenantResolver.cs
├─ Mapea email domain a tenant
├─ Carga configuración de AppInsights
└─ Valida permisos

AppInsightsQueryService.cs
├─ Obtiene token Azure (Managed Identity)
├─ Consulta AppInsights API
└─ Retorna logs

AppInsightsController.cs
├─ Endpoint: POST /api/appinsights/logs
├─ Valida JWT
└─ Delega a TenantResolver + Service
```

---

## ⏱️ Timeline

| Fase | Tiempo | Tareas |
|------|--------|--------|
| Lectura | 35 min | Leer docs |
| Instalación | 5 min | NuGet packages |
| Codificación | 1-1.5h | 3 clases C# |
| Configuración | 10 min | Program.cs |
| Azure Setup | 30 min | Managed ID + RBAC |
| Testing | 1-2h | Verificar funcionamiento |
| Deployment | 1h | Deploy a staging/prod |
| **TOTAL** | **4-6 HORAS** | **Listo para producción** |

---

## 🔐 Seguridad Garantizada

✅ Token NUNCA expuesto (siempre en backend)
✅ Managed Identity (sin almacenar secrets)
✅ RBAC de Azure (multi-layer protection)
✅ Validación de tenant hermética
✅ Audit automático
✅ JWT validado contra Entera ID

---

## 3️⃣ Opciones Disponibles

### Opción 1: Frontend Directo
- ❌ Rápido (2-3h) pero inseguro
- ❌ Token expuesto en navegador
- ❌ NO recomendado para producción

### Opción 2: Backend Proxy ⭐ **RECOMENDADO**
- ✅ 4-6 horas
- ✅ Totalmente seguro
- ✅ Producción ready
- ✅ Escalable

### Opción 3: On-Behalf-Of Flow
- ⚠️ 6-8 horas
- ✅ Máxima seguridad
- ⚠️ Solo si necesitas RBAC muy sofisticado

---

## 📋 Quick Checklist

### Antes de Empezar
- [ ] Tienes acceso al repositorio
- [ ] VS Code o Visual Studio 2022
- [ ] .NET 6.0+ SDK
- [ ] Azure CLI
- [ ] Acceso a Azure (para Managed Identity)

### Implementación
- [ ] Leer 6 documentos en orden (35 min)
- [ ] Instalar NuGet packages (5 min)
- [ ] Crear 3 clases C# (1-1.5h)
- [ ] Registrar en Program.cs (10 min)
- [ ] Setup Managed Identity en Azure (30 min)
- [ ] Testing (1-2h)

### Deployment
- [ ] Test en staging
- [ ] Deploy a producción

---

## 🎯 Resultado Final

### Para Usuario Arrivia

```bash
Usuario: cgareca@arrivia.com
Login: ✅ SSO Entera ID
Query logs: ✅ GET /api/appinsights/logs
Resultado: ✅ Ve SOLO logs de Arrivia
Cross-tenant: ❌ No puede acceder a Ice (403)
```

### Para Usuario Ice

```bash
Usuario: cgareca@ice.com
Login: ✅ SSO Entera ID
Query logs: ✅ GET /api/appinsights/logs
Resultado: ✅ Ve SOLO logs de Ice
Cross-tenant: ❌ No puede acceder a Arrivia (403)
```

### Seguridad

```bash
Ataque: Usuario Ice intenta acceder a Arrivia
Resultado: ❌ 403 FORBIDDEN (Azure RBAC bloquea)
Audit: ✅ Intento registrado en Activity Log
```

---

## 🚀 Cómo Proceder

### Paso 1: Lectura (35 minutos)
```
1. [APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md](./APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md) - 10 min
2. [APPINSIGHTS_VISUAL_FLOW.md](./APPINSIGHTS_VISUAL_FLOW.md) - 10 min
3. [APPINSIGHTS_SETUP_STEPBYSTEP.md](./APPINSIGHTS_SETUP_STEPBYSTEP.md) - 15 min (lectura)
```

### Paso 2: Implementación Local (1.5-2 horas)
```
1. Instalar NuGet packages
2. Crear TenantResolver.cs
3. Crear AppInsightsQueryService.cs
4. Crear AppInsightsController.cs
5. Registrar en Program.cs
6. Test local
```

### Paso 3: Setup Azure (1 hora)
```
1. Habilitar Managed Identity
2. Obtener Object ID
3. Asignar Reader role a AppInsights
4. Verificar RBAC
```

### Paso 4: Testing (1-2 horas)
```
1. Health check
2. Query logs (usuario Arrivia)
3. Query logs (usuario Ice)
4. Cross-tenant attack (verificar que falla)
```

### Paso 5: Deployment (1 hora)
```
1. Push a staging
2. Test end-to-end
3. Security review
4. Push a producción
```

---

## ❓ Preguntas Rápidas

### ¿Puedo empezar hoy?
Sí. Todos los documentos están completos. Comienza leyendo el Resumen Ejecutivo.

### ¿Es seguro para producción?
Sí. Opción 2 está diseñada específicamente para producción con datos sensibles.

### ¿Cuánto cuesta?
- Managed Identity: $0/mes (incluido en App Service)
- AppInsights: Depende volumen ($50-200/mes típico)

### ¿Necesito cambiar Portal?
No. Solo añades 3 clases C# y registras en Program.cs. Sin cambios breaking.

### ¿Y si tengo más de 2 empresas?
Fácil. Expande el mapeo en TenantResolver:
```csharp
"acme.com" => "acme",
"xyz.com" => "xyz",
```

### ¿Puedo usar esto con Scout?
Sí. De hecho, es perfecto para que Scout log desde AppInsights.

---

## 📞 Soporte

Si tienes dudas:

1. Revisa FAQ en [APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md](./APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md)
2. Consulta Troubleshooting en [APPINSIGHTS_SETUP_STEPBYSTEP.md](./APPINSIGHTS_SETUP_STEPBYSTEP.md)
3. Revisa attack scenarios en [APPINSIGHTS_VISUAL_FLOW.md](./APPINSIGHTS_VISUAL_FLOW.md)

---

## ✅ Próximo Paso

👉 **[Lee APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md](./APPINSIGHTS_RESUMEN_EJECUTIVO_ES.md) ahora**

Tiempo: 10 minutos
Resultado: Comprenderás exactamente qué vas a implementar

---

**¡Adelante! Estás a 4-6 horas de tener token access a AppInsights por tenant en producción.** 🚀
