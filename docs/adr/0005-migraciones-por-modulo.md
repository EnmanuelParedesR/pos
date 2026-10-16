# ADR-0005: Migraciones EF por módulo, solo para SQL Server

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

Cada tienda guarda sus datos en SQL Server Express. Después del piloto, cualquier cambio del modelo tiene que llegar a bases que ya tienen ventas, sin perder nada. Hasta ahora la base se creaba entera con `CreateTables` y una base existente se daba por actualizada. En desarrollo y en las pruebas locales usamos SQLite (ADR-0002).

## Decisión

- Cada módulo tiene sus propias migraciones en `Persistence/Migrations`, generadas con `dotnet-ef` (herramienta local, `dotnet tool restore`) a través de una fábrica de diseño (`DisenoDbContext<T>`). Un módulo nunca toca las tablas de otro.
- Todas comparten la tabla `__EFMigrationsHistory`; los nombres llevan el módulo (`InicialVentas`) para que los ids no choquen.
- Al arrancar contra SQL Server, la API aplica las migraciones pendientes de cada módulo (`MigrateAsync`). SQLite sigue creando las tablas directamente.
- Una prueba (`MigrationsTests`) falla si el modelo de un módulo cambió sin su migración. El job de SQL Server de CI aplica las migraciones en una base nueva en cada corrida, y la suite e2e también.

Para agregar una migración:

```
dotnet tool restore
dotnet build Abastalia.slnx
dotnet dotnet-ef migrations add <Name><Module> --project src/Modules/<Módulo>/Abastalia.<Módulo> --startup-project src/Modules/<Módulo>/Abastalia.<Módulo> --context <Módulo>DbContext --output-dir Persistence/Migrations --no-build
```

- Las migraciones corren en el `StartingAsync` del host (después de avisar a Windows que el servicio arrancó y antes de que Kestrel escuche), con 30 minutos por comando.
- Una migración posterior a `Inicial*` no puede insertar, cambiar ni borrar filas que el dueño edita (roles salvo Administrador, `Negocio`, `FormatosEtiqueta`); una prueba lo verifica. Esos cambios se escriben con `migrationBuilder.Sql` respetando lo que la tienda ya cambió.
- Una base de SQL Server creada antes de las migraciones no arranca: la API pide respaldarla y crearla de nuevo.

## Consecuencias

- Las migraciones son SQL de SQL Server; SQLite no las usa. Un error que solo exista en las migraciones aparece en el job de SQL Server, no en local.
- Antes de cada versión hay que revisar los scripts (`dotnet dotnet-ef migrations script`) por si alguno pierde datos (renombres, columnas que se borran).
