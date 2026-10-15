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
  Client/           Toki.Escuadra (sistema de diseño), Toki.Client.UI (pantallas),
                    Toki.Client.Wpf (app de la tienda), Toki.Client.Web (pantallas en el navegador, desarrollo)
tests/
  Toki.UnitTests            reglas de dominio
  Toki.IntegrationTests     la API real en memoria, por HTTP, sobre una base nueva
  Toki.ArchitectureTests    dependencias entre módulos, sin SP/triggers, sin paquetes comerciales
  Toki.E2ETests             el proceso real de la API y un día completo de tienda (3 cajas, reinicio, cierres)
  Toki.Client.UI.Tests      pantallas con bUnit sobre una API falsa
```

## Correr

Requiere el SDK de .NET 10.

```bash
dotnet test
```

```bash
dotnet run --project src/Hosts/Toki.Host.Local
```

En desarrollo usa SQLite (`toki.dev.db`); la documentación de la API queda en `/scalar`. Con SQL Server Express instalado: `dotnet run --project src/Hosts/Toki.Host.Local --launch-profile SqlExpress`. Para correr las pruebas contra SQL Server: `TOKI_TEST_DB=sqlserver dotnet test`, con `TOKI_TEST_SQLSERVER="Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True"` para usar el SQL Express local o, sin esa variable, un contenedor (necesita Docker).

Pantallas: con la API corriendo en `http://localhost:5000`, `dotnet run --project src/Client/Toki.Client.Web` y abra `http://localhost:5280` (la primera vez pide instalar la tienda o unir el equipo con un código). La app de la tienda es `src/Client/Toki.Client.Wpf` (Windows; la URL de la API está en su `appsettings.json`). Ver docs/adr/0007-frontend-blazor-hybrid.md.

Panel del dueño (nube, ADR-0008): `src/Cloud/panel` (React + Vite + TypeScript) habla con `Toki.Host.Cloud`. Con la API de la nube corriendo en `http://localhost:5100` (`dotnet run --project src/Hosts/Toki.Host.Cloud`), en `src/Cloud/panel`: `npm install`, `npm run dev` y abra `http://localhost:5173`; otra API se elige con `VITE_API_URL`. `npm run check` corre el chequeo de tipos y las pruebas (Vitest). `npm run dev` y `npm run build` necesitan Node 18 o más nuevo; la publicación está en docs/despliegue-azure.md.

Contra SQL Server la API aplica sola las migraciones de cada módulo al arrancar. Para crear una migración nueva vea docs/adr/0005-migraciones-por-modulo.md (`dotnet tool restore` instala `dotnet-ef`).

En producción (`appsettings.Production.json`) escucha solo HTTPS en el puerto 5443 con un certificado propio de la PC principal, y escribe logs diarios en C:\ProgramData\Toki\logs.

Respaldos: el dueño define una frase (PUT /api/backups/passphrase); cada noche a las 23:30 la API respalda la base, la cifra con esa frase (AES-256-GCM) y guarda los 14 más nuevos en C:\ProgramData\Toki\backups (y una copia en Backup:CopyTo, si se configura, por ejemplo una memoria USB o una carpeta de red). Para abrir un respaldo en cualquier PC: `Toki.Host.Local decrypt-backup <archivo> <salida>` con la frase.

Primer uso: `POST /api/identity/setup` crea el administrador y empareja la PC principal; luego `POST /api/identity/sessions` con el encabezado `X-Device`.

## Instalar en la tienda

En la PC principal (Windows, con SQL Server Express), como administrador:

```powershell
dotnet publish src/Hosts/Toki.Host.Local -c Release -r win-x64 --self-contained -o publicado
.\deploy\install.ps1 -Source .\publicado
```

Crea el servicio `Toki` con su propia cuenta virtual (`NT SERVICE\Toki`, sin permisos de administrador), el certificado HTTPS del nodo, la carpeta `C:\ProgramData\Toki` y el login de SQL Server, y abre el puerto 5443 solo en redes privadas. `deploy\uninstall.ps1` quita el servicio y deja los datos.

## Reglas

- Toda la lógica en C#: sin procedimientos almacenados ni triggers.
- Una transacción por comando; inventario y caja como movimientos que no se borran.
- Permisos validados en el servidor; auditoría encadenada por hash.
- Paquetes con licencia comercial prohibidos (MediatR ≥ 13, AutoMapper ≥ 15, MassTransit ≥ 9, FluentAssertions ≥ 8).
- Se trabaja con la skill Ponytail (`.claude/skills/ponytail`): el cambio más pequeño que resuelve todo; los atajos llevan un comentario `shortcut:`.
