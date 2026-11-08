# Criterio de aceptación CONGELADO para los paquetes de empresa (decisión del usuario: estricto). No cambia más.

1. Barrido: ≥24 horarios de reset determinísticos que cubran los 7 días y todas las horas (p.ej. cada 7 h durante 7 días), evaluados con el KpiEvaluator REAL de la plataforma (consola de replay), incluyendo el muestreo por bucket de la plataforma (KpiSampling) y buckets de negocio con relleno de ceros.
2. Toda detección que el README afirma sin calificar ("siempre"/negrita) dispara en ≥95 % de las corridas del barrido (histórica + en vivo). Se permite una ventana de reset documentada (p.ej. "resetear entre 09:00 y 21:00 hora local") solo si el README la indica de forma destacada; entonces el ≥95 % se mide sobre los resets dentro de esa ventana.
3. Lo que esté por debajo de 95 % se escribe "usualmente (~NN %)" con la tasa medida (±5 puntos) o se elimina.
4. Pico legítimo: 0 disparos en toda KPI y toda entidad, durante el pico y 2 h después, en TODO el barrido (todas las horas).
5. Falsos disparos fuera de incidentes: ≤2 por día por KPI.
6. El test de replay del paquete afirma exactamente el conjunto no calificado del README sobre el mismo barrido (< 90 s) y coincide con el evaluador real.
7. Archivos UTF-8 sin BOM, sin mojibake ('Ã', 'Â').
La respuesta final del implementador y del tester incluye la tabla promesa → tasa medida.

**Enmienda (2026-11-08, orquestador):** el límite de tiempo del test de replay (punto 6) se mide en una máquina sin carga; el assert de reloj del test usa 180 s como techo de seguridad porque varios gates corren en paralelo en la misma máquina (con 90 s fallaba solo por carga: 23 s aislado, 137 s con carga).
