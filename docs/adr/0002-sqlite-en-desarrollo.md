# ADR-0002: SQLite para desarrollo y pruebas locales; SQL Server en CI y producción

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

Producción usa SQL Server Express 2025 (`arquitectura-v1.md`). En la PC de desarrollo no hay permisos de administrador ni Docker, así que no se puede instalar SQL Server ni usar Testcontainers localmente.

## Decisión

- El proveedor de base de datos se elige por configuración: `Database:Provider` = `SqlServer` o `Sqlite`.
- Desarrollo local y la suite rápida de pruebas usan **SQLite** (un archivo por prueba).
- **CI corre la misma suite contra SQL Server 2025 en un contenedor** (`ABASTALIA_TEST_DB=sqlserver`, Testcontainers).
- El código evita SQL específico de un motor: operaciones atómicas con `ExecuteUpdateAsync` (stock, secuencias e-NCF, cabeza de auditoría, autorizaciones), que funcionan igual en los dos.
- SQLite ignora los esquemas, así que los nombres de tabla deben ser únicos entre módulos.
- Las tablas se crean con `CreateTables` cuando la base es nueva. Las migraciones por módulo para SQL Server son el ticket TK-090, antes del piloto.

## Consecuencias

- Las diferencias de comportamiento entre motores (bloqueos, tipos) solo se ven en CI. Por eso CI siempre corre la suite de SQL Server.
- No se usa el proveedor InMemory de EF Core.
