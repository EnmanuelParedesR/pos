# ADR-0001: Monolito modular con vertical slices, sin MediatR

- Fecha: 2026-10-15
- Estado: aceptada

## Decisión

Se sigue `docs/arquitectura-v1-vsa.md` con estas simplificaciones:

1. **Un solo proyecto de bloques base** (`Toki.BuildingBlocks`) en lugar de Application + Infrastructure. Son pocas clases y siempre se usan juntas.
2. **Contracts solo cuando otro módulo los necesita.** Un módulo sin consumidores externos no tiene proyecto `.Contracts`.
3. **Comandos y consultas propios** (`ICommand<T>`, `IQuery<T>`) con decoradores Scrutor: validación → transacción → autorización → handler. La autorización corre dentro de la transacción para que una autorización de supervisor se consuma junto con el comando.
4. **Una conexión y una transacción por petición**, compartidas por los `DbContext` de cada módulo. El decorador de transacción guarda todos los contextos con cambios y hace commit una sola vez.
5. **El outbox se escribe directamente** en la tabla `infra.OutboxMensajes` dentro de la transacción (sin eventos de dominio intermedios). El procesador que los reparte y sube a la nube se construye cuando haya un consumidor (TK-088).
6. **Sin `TenantId` en cada fila del nodo local.** El nodo es de una sola tienda; el inquilino viaja en el sobre del evento cuando suba a la nube.
7. **Fechas en `DateTime` UTC** (no `DateTimeOffset`): SQLite no compara ni ordena `DateTimeOffset`. El día de negocio se calcula con UTC−4 (República Dominicana no tiene horario de verano).

## Consecuencias

- Menos proyectos y menos ceremonia; las reglas de dependencia siguen verificadas por las pruebas de arquitectura.
- Si un módulo crece, se le puede separar su `.Contracts` o su dominio sin tocar los demás.
