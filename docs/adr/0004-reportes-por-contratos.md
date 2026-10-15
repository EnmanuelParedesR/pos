# ADR-0004: Reportes armados con consultas de los contratos de cada módulo

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

`arquitectura-v1-vsa.md` §3.4 propone vistas SQL en un esquema `reportes`. Con dos motores (ADR-0002) eso obliga a escribir y probar SQL distinto para cada uno: SQLite no tiene esquemas y su SQL de vistas difiere del de SQL Server.

## Decisión

- Cada módulo publica en su proyecto `Contracts` una interfaz de reportes de solo lectura (`IVentasReportes`, `IComprasReportes`…) que devuelve datos ya agregados, con consultas EF que funcionan en los dos motores.
- El módulo **Reportes** no tiene tablas: llama esas interfaces, une los resultados (por ejemplo, nombres de categoría de Inventario) y exporta a CSV o Excel (ClosedXML, MIT).
- Los totales salen de los mismos registros que usan la caja y el inventario (ventas, líneas con su costo histórico, devoluciones), así los reportes cuadran con el cierre (dolor #1).

## Consecuencias

- Agregar un reporte toca el contrato del módulo dueño de los datos y una pantalla de Reportes; no hay SQL a mano.
- Para la réplica en la nube y la IA (fase 2) se podrán crear vistas específicas de SQL Server sin cambiar estos reportes.
