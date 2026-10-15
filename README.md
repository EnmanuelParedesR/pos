# Toki

Punto de venta, inventario y caja para el comercio de alimentos en República Dominicana: carnicerías primero, luego delis, colmados y minimarkets.

Este repositorio tiene el **backend de V1**: una API local que corre en la PC principal de la tienda (servicio de Windows sobre SQL Server Express) y a la que se conectan las cajas.

## Documentos

| Documento | Qué tiene |
|---|---|
| `read-v1.md` | Contexto del producto |
| `docs/funcionalidades-v1.md` | Alcance de V1 y módulos vendibles |
| `docs/arquitectura-v1.md` | Despliegue híbrido local-primero |
| `docs/arquitectura-v1-vsa.md` | Organización del código (vertical slices, outbox, seguridad, pruebas) |
| `docs/adr/` | Decisiones tomadas al construir |
| `backlog/BACKLOG.md` | Tickets y su estado |

## Estructura

```
src/
  BuildingBlocks/   Toki.SharedKernel (Result, Error, dinero, día de negocio)
                    Toki.BuildingBlocks (comandos, decoradores, unidad de trabajo, outbox, auditoría, permisos)
  Modules/          un proyecto por módulo; cada caso de uso en un archivo de Features/
  Hosts/            Toki.Host.Local (API de la tienda)
tests/
  Toki.UnitTests            reglas de dominio
  Toki.IntegrationTests     la API real en memoria, por HTTP, sobre una base nueva
  Toki.ArchitectureTests    dependencias entre módulos, sin SP/triggers, sin paquetes comerciales
  Toki.E2ETests             el proceso real de la API y un día completo de tienda (3 cajas, reinicio, cierres)
```

## Correr

Requiere el SDK de .NET 10.

```bash
dotnet test
```

```bash
dotnet run --project src/Hosts/Toki.Host.Local
```

En desarrollo usa SQLite (`toki.dev.db`); la documentación de la API queda en `/scalar`. Para SQL Server, cambie `Database` en `appsettings.json`. Para correr las pruebas contra SQL Server en contenedor: `TOKI_TEST_DB=sqlserver dotnet test` (necesita Docker).

Contra SQL Server la API aplica sola las migraciones de cada módulo al arrancar. Para crear una migración nueva vea docs/adr/0005-migraciones-por-modulo.md (`dotnet tool restore` instala `dotnet-ef`).

En producción (ppsettings.Production.json) escucha solo HTTPS en el puerto 5443 con un certificado propio de la PC principal, y escribe logs diarios en C:\ProgramData\Toki\logs.

Primer uso: `POST /api/identidad/instalacion` crea el administrador y empareja la PC principal; luego `POST /api/identidad/sesiones` con el encabezado `X-Dispositivo`.

## Reglas

- Toda la lógica en C#: sin procedimientos almacenados ni triggers.
- Una transacción por comando; inventario y caja como movimientos que no se borran.
- Permisos validados en el servidor; auditoría encadenada por hash.
- Paquetes con licencia comercial prohibidos (MediatR ≥ 13, AutoMapper ≥ 15, MassTransit ≥ 9, FluentAssertions ≥ 8).
- Se trabaja con la skill Ponytail (`.claude/skills/ponytail`): el cambio más pequeño que resuelve todo; los atajos llevan un comentario `shortcut:`.
