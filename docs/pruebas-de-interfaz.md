# Pruebas de interfaz (Playwright)

`tests/Abastalia.UiTests`: Chromium sin ventana contra los procesos reales (la API local `Abastalia.Host.Local` en una base Sqlite nueva, el host web de la caja `Abastalia.Client.Web` y, para el panel, `Abastalia.Host.Cloud` con `vite preview`). Nunca abre la aplicación WPF. No está en `Abastalia.slnx`: necesita Chromium y Node, así que CI la corre en su propio trabajo de Windows («Pruebas de interfaz (Playwright)» en `.github/workflows`).

## Cómo correrlas

```powershell
dotnet build tests/Abastalia.UiTests/Abastalia.UiTests.csproj
pwsh tests/Abastalia.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium   # una vez por versión de Microsoft.Playwright
cd src/Cloud/panel; npm ci; cd ../../..                                          # solo para PanelSmokeTests

dotnet test --project tests/Abastalia.UiTests/Abastalia.UiTests.csproj           # todas (~2 min)
# Una clase, con el ejecutable de xUnit v3:
tests/Abastalia.UiTests/bin/Debug/net10.0/Abastalia.UiTests.exe -class Abastalia.UiTests.CriticalActionsTests
```

Si una falla, el mensaje trae lo que escribieron la API y el host de la caja, y la traza de Playwright queda en `bin/<config>/net10.0/traces/<nombre>.zip` (CI la sube como `playwright-traces`). Se abre con `playwright.ps1 show-trace <archivo>.zip`.

`ABASTALIA_TEST_DB=sqlserver` corre la API sobre SQL Server (un contenedor, o el servidor de `ABASTALIA_TEST_SQLSERVER`).

## Qué cubren

| Clase | Escenario |
|---|---|
| `RegisterDayTests` | Un día de caja con el teclado: primera instalación, abrir caja contando el fondo, ventas en efectivo (con descuento y cambio de precio autorizados), tarjeta + efectivo, a crédito dentro del límite (con supervisor), venta en espera, retiro con supervisor, sesión vencida, servidor caído al cobrar, anular y devolver con supervisor, cierre ciego con diferencia y su Z |
| `CriticalActionsTests` (TK-365) | A crédito **por encima del límite** (supervisor para el crédito y para el límite); el cuadre ciego **no muestra lo esperado** al cajero hasta cerrar; un cajero **sin permiso** es rechazado (otro cajero no puede autorizar; cancelar no abre el turno; el dueño sí); **licencia bloqueada** (TK-279): no se vende ni se abre turno, sí se cierra el turno abierto y el dueño exporta el 607; una **caja unida a la PC principal** con código vende por ella |
| `EcfQueueTests` (TK-365) | La cola e-CF en TesteCF contra la DGII falsa: dos comprobantes «Por enviar» mientras la DGII no contesta, uno aceptado y otro **rechazado** con su motivo, el **reemplazo** (dos toques) aceptado y enlazado a ambos lados |
| `BackupRestoreTests` (TK-365) | Frase, respaldo, restaurar con la frase equivocada (no abre) y con la buena: el dato creado después del respaldo desaparece y todos vuelven a entrar |
| `PanelSmokeTests` | El panel del dueño entra hasta Resumen; ninguna página del dueño más ancha que un teléfono (320, 375, 390 px) |
| `CloudConnectionTests` | La tienda se registra en la nube real, sube una venta, copia un respaldo y baja su licencia (sin navegador) |
| `EscuadraVisualTests` | Capturas de `/dev/escuadra` contra `Baselines/` |
| `ProductVideoTests` | Las tomas del video del sitio (solo con `ABASTALIA_VIDEO=1`) |

Las tres clases de TK-365 comparten la colección `TK-365 critical actions` (corren una tras otra, al lado de las demás) y los ayudantes de `Till.cs`: `Till` maneja una página de caja como un cajero (teclas, Tab y Enter) y `Store` arma la tienda (primera instalación por pantalla, lo demás por la API con la sesión del dueño). Cada escenario de `CriticalActionsTests` crea su propio cajero y su propia caja, así que corren en cualquier orden sobre la misma tienda.

## La DGII falsa

`EcfQueueTests` usa la `FakeDgii` de las pruebas de integración (`tests/Abastalia.IntegrationTests/Infra/FakeDgii.cs`), servida por HTTP en 127.0.0.1 (`DgiiServer.cs`). La API la usa en lugar de la DGII con `Fiscal:Dgii:TestServer`, que **solo** se lee en el ambiente `E2E` (`Program.cs` → `FiscalModule.UseTestDgii`): `https://ecf.dgii.gov.do/testecf/x` va a `{servidor}/ecf.dgii.gov.do/testecf/x`. El certificado es autofirmado y se crea en cada corrida.

## Reglas para escribirlas

- Nada de esperas fijas: se espera lo que la pantalla muestra (`Expect(...)`). Donde la pantalla no se refresca sola (Comprobantes e-CF), se espera el estado del servidor y luego se abre la página otra vez.
- Selectores por rol, nombre y etiqueta (`GetByRole`, `GetByLabel`), nunca por diseño ni clases CSS.
- La sesión vive en el circuito de Blazor: `GotoAsync` empieza uno nuevo y hay que entrar otra vez. Dentro de la administración se navega con los enlaces del menú.
