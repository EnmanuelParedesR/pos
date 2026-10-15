# Rename map, phase B: Inventory module

Spanish → English renames done in `src/Modules/Inventory/Toki.Inventory/**`. Behavior did not change. Contracts names come from `docs/rename-map.md` (`Toki.Inventory.Contracts` section). The API serializes in camelCase with string enums, so the JSON field is the property name below with a lowercase first letter.

## Files

| Old | New |
|---|---|
| `InventarioModule.cs` | `InventoryModule.cs` |
| `InventarioReportes.cs` | `InventoryReports.cs` |
| `Domain/Inventario.cs` | `Domain/Inventory.cs` |
| `Domain/Conteo.cs` | `Domain/StockCount.cs` |
| `Features/Ajustes.cs` | `Features/Adjustments.cs` |
| `Features/Categorias.cs` | `Features/Categories.cs` |
| `Features/CodigosYPresentaciones.cs` | `Features/BarcodesAndPresentations.cs` |
| `Features/Conteos.cs` | `Features/StockCounts.cs` |
| `Features/Existencias.cs` | `Features/Stock.cs` |
| `Features/Productos.cs` | `Features/Products.cs` |
| `Persistence/InventarioDbContext.cs` | `Persistence/InventoryDbContext.cs` |
| `Persistence/Migraciones/` (`20261015095640_InicialInventario`) | `Persistence/Migrations/` (`20261015152651_InitialInventory`, regenerated) |

## Routes

OpenAPI tag `Inventario` → `Inventory`. Permissions are the new constants from `Toki.BuildingBlocks.Security.Permissions`; which permission each route needs did not change.

| Old | New | Permission |
|---|---|---|
| `POST /api/inventario/categorias` (Location `/api/inventario/categorias/{id}`) | `POST /api/inventory/categories` (Location `/api/inventory/categories/{id}`) | `products.manage` |
| `PUT /api/inventario/categorias/{id}` | `PUT /api/inventory/categories/{id}` | `products.manage` |
| `GET /api/inventario/categorias` | `GET /api/inventory/categories` | authenticated |
| `POST /api/inventario/productos` (Location `/api/inventario/productos/{id}`) | `POST /api/inventory/products` (Location `/api/inventory/products/{id}`) | `products.manage` (+ `prices.change` in handler) |
| `PUT /api/inventario/productos/{id}` | `PUT /api/inventory/products/{id}` | `products.manage` (+ `prices.change` for price/PLU/ITBIS) |
| `GET /api/inventario/productos/{id}` | `GET /api/inventory/products/{id}` | `inventory.view` |
| `GET /api/inventario/productos?buscar=&categoriaId=&incluirInactivos=&pagina=` | `GET /api/inventory/products?search=&categoryId=&includeInactive=&page=` | authenticated |
| `GET /api/inventario/productos/por-codigo/{codigo}` | `GET /api/inventory/products/by-code/{code}` | authenticated |
| `POST /api/inventario/productos/{id}/codigos` | `POST /api/inventory/products/{id}/barcodes` | `products.manage` (+ `prices.change`) |
| `DELETE /api/inventario/productos/{id}/codigos/{codigo}` | `DELETE /api/inventory/products/{id}/barcodes/{code}` | `products.manage` (+ `prices.change`) |
| `POST /api/inventario/productos/{id}/presentaciones` (Location `/api/inventario/productos/{id}`) | `POST /api/inventory/products/{id}/presentations` (Location `/api/inventory/products/{id}`) | `products.manage` (+ `prices.change`) |
| `PUT /api/inventario/productos/{id}/presentaciones/{presentacionId}` | `PUT /api/inventory/products/{id}/presentations/{presentationId}` | `products.manage` (+ `prices.change`) |
| `GET /api/inventario/productos/{id}/movimientos?ultimos=` | `GET /api/inventory/products/{id}/movements?last=` | `inventory.view` |
| `POST /api/inventario/ajustes` | `POST /api/inventory/adjustments` | `inventory.adjust` |
| `POST /api/inventario/transferencias` | `POST /api/inventory/transfers` | `inventory.transfer` |
| `POST /api/inventario/conteos` (Location `/api/inventario/conteos/{conteoId}`) | `POST /api/inventory/stock-counts` (Location `/api/inventory/stock-counts/{stockCountId}`) | `inventory.count` |
| `PUT /api/inventario/conteos/{id}/lineas` | `PUT /api/inventory/stock-counts/{id}/lines` | `inventory.count` |
| `POST /api/inventario/conteos/{id}/aplicar` | `POST /api/inventory/stock-counts/{id}/apply` | `inventory.count` (+ `inventory.adjust` or supervisor approval) |
| `GET /api/inventario/conteos/{id}` | `GET /api/inventory/stock-counts/{id}` | `inventory.count` |
| `POST /api/inventario/almacenes` (Location `/api/inventario/almacenes/{id}`) | `POST /api/inventory/warehouses` (Location `/api/inventory/warehouses/{id}`) | `products.manage` |
| `GET /api/inventario/almacenes` | `GET /api/inventory/warehouses` | authenticated |
| `GET /api/inventario/existencias?almacenId=&soloBajoMinimo=&soloPendientes=` | `GET /api/inventory/stock?warehouseId=&belowMinimumOnly=&needsCountOnly=` | `inventory.view` |
| `GET /api/inventario/formatos-etiqueta` | `GET /api/inventory/label-formats` | `products.manage` |
| `PUT /api/inventario/formatos-etiqueta` | `PUT /api/inventory/label-formats` | `settings.manage` |

## Requests and responses (wire shapes)

Property order is the record order. `Body` records are the JSON body when the route carries the id.

### Categories

| Old | New |
|---|---|
| `GuardarCategoria` | `SaveCategory` |
| `GuardarCategoria.Body(Nombre, Color, Notas, Activa = true)` | `SaveCategory.Body(Name, Color, Notes, IsActive = true)` |
| `GuardarCategoria.Command(Id, Nombre, Color, Notas, Activa = true)` | `SaveCategory.Command(Id, Name, Color, Notes, IsActive = true)` |
| `GuardarCategoria.Response(Id)` | `SaveCategory.Response(Id)` |
| `ListarCategorias` | `ListCategories` |
| `ListarCategorias.Item(Id, Nombre, Color, Notas, Activa)` | `ListCategories.Item(Id, Name, Color, Notes, IsActive)` |

### Products

| Old | New |
|---|---|
| `IDatosProducto` (`Nombre, CategoriaId, Unidad, Plu, TasaItbis, Precio, PrecioMayorista, VenderSinExistencia, StockMinimo`) | `IProductData` (`Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, SellWithoutStock, MinimumStock`) |
| `DatosProductoValidator` | `ProductDataValidator` |
| `CrearProducto` | `CreateProduct` |
| `CrearProducto.Command(Nombre, CategoriaId, Unidad, Plu, TasaItbis, Precio, PrecioMayorista, Costo, VenderSinExistencia, StockMinimo, Codigos, ExistenciaInicial = 0)` (also the POST body) | `CreateProduct.Command(Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, Cost, SellWithoutStock, MinimumStock, Barcodes, InitialStock = 0)` |
| `CrearProducto.Response(Id)` | `CreateProduct.Response(Id)` |
| `ActualizarProducto` | `UpdateProduct` |
| `ActualizarProducto.Body(Version, Nombre, CategoriaId, Unidad, Plu, TasaItbis, Precio, PrecioMayorista, VenderSinExistencia, StockMinimo, Activo)` | `UpdateProduct.Body(Version, Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, SellWithoutStock, MinimumStock, IsActive)` |
| `ActualizarProducto.Command(Id, Version, Nombre, …, Activo)` | `UpdateProduct.Command(Id, Version, Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, SellWithoutStock, MinimumStock, IsActive)` |
| `ObtenerProducto` | `GetProduct` |
| `ObtenerProducto.PresentacionDto(Id, Nombre, Factor, Precio, PrecioMayorista, Activa)` | `GetProduct.PresentationDto(Id, Name, Factor, Price, WholesalePrice, IsActive)` |
| `ObtenerProducto.CodigoDto(Codigo, PresentacionId)` | `GetProduct.BarcodeDto(Code, PresentationId)` |
| `ObtenerProducto.ExistenciaDto(AlmacenId, Almacen, Cantidad, PendienteContar)` | `GetProduct.StockDto(WarehouseId, Warehouse, Quantity, NeedsCount)` |
| `ObtenerProducto.Response(Id, Version, Nombre, CategoriaId, Unidad, Plu, TasaItbis, Precio, PrecioMayorista, Costo, VenderSinExistencia, StockMinimo, Activo, Presentaciones, Codigos, Existencias)` | `GetProduct.Response(Id, Version, Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, Cost, SellWithoutStock, MinimumStock, IsActive, Presentations, Barcodes, Stock)` |
| `BuscarProductos` | `SearchProducts` |
| `BuscarProductos.TamanoPagina` (50) | `SearchProducts.PageSize` (50) |
| `BuscarProductos.Item(Id, Nombre, Categoria, Unidad, TasaItbis, Precio, PrecioMayorista, Existencia, Activo)` | `SearchProducts.Item(Id, Name, Category, Unit, ItbisRate, Price, WholesalePrice, Stock, IsActive)` |
| `BuscarPorCodigo` | `FindByCode` |
| `BuscarPorCodigo.Response(ProductoId, PresentacionId, Nombre, Unidad, TasaItbis, Precio, PrecioMayorista, Factor, CantidadEtiqueta, MontoEtiqueta, Notas, Existencia)` | `FindByCode.Response(ProductId, PresentationId, Name, Unit, ItbisRate, Price, WholesalePrice, Factor, LabelQuantity, LabelAmount, Notes, Stock)` |

### Barcodes and presentations

| Old | New |
|---|---|
| `Codigos` (static class) | `Barcodes` |
| `Codigos.Agregar(ProductoId, Codigo, PresentacionId)` | `Barcodes.Add(ProductId, Code, PresentationId)` |
| `Codigos.Quitar(ProductoId, Codigo)` | `Barcodes.Remove(ProductId, Code)` |
| `Codigos.AgregarHandler` / `QuitarHandler` | `Barcodes.AddHandler` / `RemoveHandler` |
| `Codigos.Body(Codigo, PresentacionId)` | `Barcodes.Body(Code, PresentationId)` |
| `GuardarPresentacion` | `SavePresentation` |
| `GuardarPresentacion.Body(Nombre, Factor, Precio, PrecioMayorista, Activa = true)` | `SavePresentation.Body(Name, Factor, Price, WholesalePrice, IsActive = true)` |
| `GuardarPresentacion.Command(ProductoId, Id, Nombre, Factor, Precio, PrecioMayorista, Activa = true)` | `SavePresentation.Command(ProductId, Id, Name, Factor, Price, WholesalePrice, IsActive = true)` |
| `GuardarPresentacion.Response(Id)` | `SavePresentation.Response(Id)` |

### Adjustments and transfers

| Old | New |
|---|---|
| `AjustarInventario` | `AdjustStock` |
| `AjustarInventario.Command(ProductoId, AlmacenId, Cantidad, Motivo, Nota)` (POST body) | `AdjustStock.Command(ProductId, WarehouseId, Quantity, Reason, Note)` |
| `AjustarInventario.Response(AjusteId)` | `AdjustStock.Response(AdjustmentId)` |
| `TransferirInventario` | `TransferStock` |
| `TransferirInventario.Linea(ProductoId, Cantidad)` | `TransferStock.Line(ProductId, Quantity)` |
| `TransferirInventario.Command(Origen, Destino, Lineas)` (POST body) | `TransferStock.Command(Source, Destination, Lines)` |
| `TransferirInventario.Response(TransferenciaId)` | `TransferStock.Response(TransferId)` |

### Stock counts

| Old | New |
|---|---|
| `IniciarConteo` | `StartStockCount` |
| `IniciarConteo.Command(AlmacenId, CategoriaId)` (POST body) | `StartStockCount.Command(WarehouseId, CategoryId)` |
| `IniciarConteo.Response(ConteoId, Productos)` | `StartStockCount.Response(StockCountId, ProductCount)` |
| `RegistrarConteo` | `RecordStockCount` |
| `RegistrarConteo.Linea(ProductoId, Contado)` | `RecordStockCount.Line(ProductId, Counted)` |
| `RegistrarConteo.Body(Lineas)` | `RecordStockCount.Body(Lines)` |
| `RegistrarConteo.Command(ConteoId, Lineas)` | `RecordStockCount.Command(StockCountId, Lines)` |
| `AplicarConteo` | `ApplyStockCount` |
| `AplicarConteo.Body(AutorizacionSupervisorId)` (body optional) | `ApplyStockCount.Body(SupervisorApprovalId)` |
| `AplicarConteo.Command(ConteoId, AutorizacionSupervisorId)` (`IRequiereAutorizacion`: `Permiso`, `RecursoId`) | `ApplyStockCount.Command(StockCountId, SupervisorApprovalId)` (`IRequiresApproval`: `Permission` = `inventory.adjust`, `ResourceId` = `StockCountId`) |
| `AplicarConteo.DiferenciaConteo(ProductoId, Producto, Sistema, Contado, Diferencia, Valor)` | `ApplyStockCount.CountDifference(ProductId, Product, SystemStock, Counted, Difference, Value)` |
| `AplicarConteo.Response(Diferencias, ValorDiferencia)` | `ApplyStockCount.Response(Differences, DifferenceValue)` |
| `ObtenerConteo` | `GetStockCount` |
| `ObtenerConteo.Linea(ProductoId, Producto, Unidad, Contado)` | `GetStockCount.Line(ProductId, Product, Unit, Counted)` |
| `ObtenerConteo.Response(Id, Estado, AlmacenId, Lineas)` | `GetStockCount.Response(Id, Status, WarehouseId, Lines)` |
| `ConteoErrors.NoEncontrado` / `NoAbierto` | `StockCountErrors.NotFound` / `NotOpen` |

### Warehouses, stock, kardex, label formats

| Old | New |
|---|---|
| `Almacenes` (static class) | `Warehouses` |
| `Almacenes.Crear(Nombre)` (POST body) | `Warehouses.Create(Name)` |
| `Almacenes.Response(Id)` | `Warehouses.Response(Id)` |
| `Almacenes.Item(Id, Nombre, Activo, Principal)` | `Warehouses.Item(Id, Name, IsActive, IsMain)` |
| `ListarExistencias` | `ListStock` |
| `ListarExistencias.Item(ProductoId, Nombre, Categoria, Unidad, Cantidad, Costo, Valor, BajoMinimo, PendienteContar)` | `ListStock.Item(ProductId, Name, Category, Unit, Quantity, Cost, Value, BelowMinimum, NeedsCount)` |
| `Kardex` | `ListStockMovements` |
| `Kardex.Item(Secuencia, FechaUtc, Tipo, AlmacenId, Cantidad, CostoUnitario, ExistenciaResultante, ReferenciaId, Motivo)` | `ListStockMovements.Item(Sequence, DateUtc, Type, WarehouseId, Quantity, UnitCost, ResultingStock, ReferenceId, Reason)` |
| `FormatosEtiquetaBalanza` | `ScaleLabelFormats` |
| `FormatosEtiquetaBalanza.Formato(Prefijo, LongitudPlu, EsPrecio, Decimales)` (GET item and PUT item) | `ScaleLabelFormats.Format(Prefix, PluLength, IsPrice, Decimals)` |
| `FormatosEtiquetaBalanza.Command(Formatos)` (PUT body) | `ScaleLabelFormats.Command(Formats)` |

## Enum values

| Old | New | Where |
|---|---|---|
| `MotivoAjuste` | `AdjustmentReason` | `AdjustStock.Command.Reason` |
| `Merma` / `Danio` / `ConsumoInterno` / `Regalo` / `Correccion` | `Shrinkage` / `Damage` / `InternalUse` / `Gift` / `Correction` | |
| `EstadoConteo` | `StockCountStatus` | `GetStockCount.Response.Status`, column `StockCounts.Status` |
| `Abierto` / `Aplicado` | `Open` / `Applied` | |
| `UnidadMedida.Unidad` / `Libra` | `UnitOfMeasure.Unit` / `Pound` | Contracts |
| `TasaItbis.*` | `ItbisRate.General18` / `Reduced16` / `Zero` / `Exempt` | SharedKernel |
| `TipoMovimiento.*` | `StockMovementType.*` | Contracts; kardex `Type`, column `StockMovements.Type` |

Kardex `Reason` text for an adjustment is built with `AdjustmentReasonNames.Display` (`"{text}"` or `"{text}: {Note}"`), which maps each enum value back to the word stored before the rename (`Merma`, `Danio`, `ConsumoInterno`, `Regalo`, `Correccion`), so the owner still reads `Merma: …`. The fixed reasons `"Existencia inicial"` (opening stock) and `"Conteo físico"` (applied count) stay in Spanish.

## Error codes

`detail` texts did not change.

| Old | New | Status |
|---|---|---|
| `Inventario.ProductoNoEncontrado` | `Inventory.ProductNotFound` | 404 |
| `Inventario.CategoriaNoEncontrada` | `Inventory.CategoryNotFound` | 404 |
| `Inventario.AlmacenNoEncontrado` | `Inventory.WarehouseNotFound` | 404 |
| `Inventario.CodigoNoEncontrado` | `Inventory.CodeNotFound` | 404 |
| `Inventario.PresentacionNoEncontrada` | `Inventory.PresentationNotFound` | 404 |
| `Inventario.ConteoNoEncontrado` | `Inventory.StockCountNotFound` | 404 |
| `Inventario.CodigoDuplicado` | `Inventory.DuplicateBarcode` | 409 |
| `Inventario.PluDuplicado` | `Inventory.DuplicatePlu` | 409 |
| `Inventario.SinExistencia` | `Inventory.OutOfStock` | 409 |
| `Inventario.ProductoInactivo` | `Inventory.ProductInactive` | 409 |
| `Inventario.ConteoCerrado` | `Inventory.StockCountClosed` | 409 |
| `Inventario.SinPermisoPrecios` | `Inventory.NoPricePermission` | 403 |
| `Inventario.CantidadInvalida` | `Inventory.InvalidQuantity` | 400 |
| `Inventario.NoEsEtiqueta` | `Inventory.NotAScaleLabel` | 400 (only from `ICatalogQueries.ReadScaleLabelAsync`) |
| `Inventario.EtiquetaMalLeida` | `Inventory.LabelMisread` | 400 |
| `Inventario.EtiquetaSinValor` | `Inventory.LabelZeroValue` | 400 |
| `Inventario.EtiquetaNoAplica` | `Inventory.LabelNotApplicable` | 400 |
| `Concurrencia` | `Concurrency` | 409 (product `Version` mismatch) |

## Audit actions

| Old action | New action | Resource type old → new | Data |
|---|---|---|---|
| `inventario.guardar-categoria` | `inventory.save-category` | `Categoria` → `Category` | `{ Name, IsActive }` |
| `inventario.crear-producto` | `inventory.create-product` | `Producto` → `Product` | the `CreateProduct.Command` |
| `inventario.actualizar-producto` | `inventory.update-product` | `Producto` → `Product` | `{ before: { Name, Price, WholesalePrice, ItbisRate, Plu, IsActive }, after: Command }` (was `antes` / `despues`) |
| `inventario.agregar-codigo` | `inventory.add-barcode` | `Producto` → `Product` | the `Barcodes.Add` command |
| `inventario.quitar-codigo` | `inventory.remove-barcode` | `Producto` → `Product` | the `Barcodes.Remove` command |
| `inventario.guardar-presentacion` | `inventory.save-presentation` | `Producto` → `Product` | `{ Presentation, before: { Name, Factor, Price, WholesalePrice, IsActive }, after: Command }` |
| `inventario.ajuste` | `inventory.adjustment` | `Producto` → `Product` | the `AdjustStock.Command` |
| `inventario.transferencia` | `inventory.transfer` | `Transferencia` → `Transfer` | the `TransferStock.Command` |
| `inventario.aplicar-conteo` | `inventory.apply-stock-count` | `Conteo` → `StockCount` | `{ Lines, Differences, Value }` (was `Lineas`, `Diferencias`, `Valor`) |
| `inventario.crear-almacen` | `inventory.create-warehouse` | `Almacen` → `Warehouse` | the `Warehouses.Create` command |
| `inventario.formatos-etiqueta` | `inventory.label-formats` | `FormatoEtiqueta` → `LabelFormat` | the `ScaleLabelFormats.Command` |

## Config keys

None in this module.

## Database (schema `inventario` → `inventory`)

Seed data kept: warehouse `0199e000-0000-7000-8000-0000000000a1` named `Tienda`; label formats `20` (PLU 5, weight, 3 decimals), `21` (PLU 5, weight, 3 decimals), `22` (PLU 4, price, 2 decimals). Enum columns store the English names now.

| Old table | New table |
|---|---|
| `Categorias` | `Categories` |
| `Productos` | `Products` |
| `Presentaciones` | `Presentations` |
| `CodigosBarras` | `Barcodes` |
| `Almacenes` | `Warehouses` |
| `Existencias` | `Stock` |
| `MovimientosInventario` | `StockMovements` |
| `FormatosEtiqueta` | `LabelFormats` |
| `ConteosFisicos` | `StockCounts` |
| `ConteosFisicosLineas` | `StockCountLines` |

| Table | Old column | New column |
|---|---|---|
| Categories | `Id`, `Nombre`, `Color`, `Notas`, `Activa` | `Id`, `Name`, `Color`, `Notes`, `IsActive` |
| Products | `Id`, `Nombre`, `CategoriaId`, `Unidad`, `Plu`, `TasaItbis`, `Precio`, `PrecioMayorista`, `Costo`, `VenderSinExistencia`, `StockMinimo`, `Activo`, `Version` | `Id`, `Name`, `CategoryId`, `Unit`, `Plu`, `ItbisRate`, `Price`, `WholesalePrice`, `Cost`, `SellWithoutStock`, `MinimumStock`, `IsActive`, `Version` |
| Presentations | `Id`, `ProductoId`, `Nombre`, `Factor`, `Precio`, `PrecioMayorista`, `Activa` | `Id`, `ProductId`, `Name`, `Factor`, `Price`, `WholesalePrice`, `IsActive` |
| Barcodes | `Codigo` (PK), `ProductoId`, `PresentacionId` | `Code` (PK), `ProductId`, `PresentationId` |
| Warehouses | `Id`, `Nombre`, `Activo` | `Id`, `Name`, `IsActive` |
| Stock | `ProductoId`, `AlmacenId`, `Cantidad`, `PendienteContar` | `ProductId`, `WarehouseId`, `Quantity`, `NeedsCount` |
| StockMovements | `Secuencia` (PK, identity), `Id`, `ProductoId`, `AlmacenId`, `Tipo`, `Cantidad`, `CostoUnitario`, `ExistenciaResultante`, `ReferenciaId`, `Motivo`, `UsuarioId`, `FechaUtc` | `Sequence` (PK, identity), `Id`, `ProductId`, `WarehouseId`, `Type`, `Quantity`, `UnitCost`, `ResultingStock`, `ReferenceId`, `Reason`, `UserId`, `DateUtc` |
| LabelFormats | `Prefijo` (PK), `LongitudPlu`, `EsPrecio`, `Decimales` | `Prefix` (PK), `PluLength`, `IsPrice`, `Decimals` |
| StockCounts | `Id`, `AlmacenId`, `CategoriaId`, `Estado`, `UsuarioId`, `CreadoUtc`, `AplicadoUtc` | `Id`, `WarehouseId`, `CategoryId`, `Status`, `UserId`, `CreatedUtc`, `AppliedUtc` |
| StockCountLines | `ConteoId`, `ProductoId`, `Contado`, `SistemaAlContar` | `StockCountId`, `ProductId`, `Counted`, `SystemAtCount` |

Indexes: `IX_Products_Name`, `IX_Products_Plu` (unique, filter `[Plu] IS NOT NULL`), `IX_Presentations_ProductId`, `IX_Barcodes_ProductId`, `IX_StockMovements_Id` (unique), `IX_StockMovements_ProductId_Sequence`, `IX_StockMovements_ReferenceId`; FK `FK_StockCountLines_StockCounts_StockCountId`. `Products.Name` keeps collation `Latin1_General_100_CI_AI` on SQL Server.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialInventory --project src/Modules/Inventory/Toki.Inventory --startup-project src/Modules/Inventory/Toki.Inventory --context InventoryDbContext --output-dir Persistence/Migrations --no-build
```

## Internal types other code references

`InternalsVisibleTo` still covers `Toki.UnitTests` and `Toki.IntegrationTests`.

### Module and services (`Toki.Inventory`)

| Old | New |
|---|---|
| `InventarioModule` | `InventoryModule` |
| `InventarioModule.AddInventarioModule(services)` | `InventoryModule.AddInventoryModule(services)` |
| `CatalogoConsultas` (implements `ICatalogQueries`) | `CatalogQueries` |
| `InventarioReportes` (implements `IInventoryReports`) | `InventoryReports` |
| `MovimientosInventario` (implements `IStockMovements`, also registered as itself) | `StockMovements` |
| `MovimientosInventario.SalidaAsync(tipo, referenciaId, lineas, permitirNegativoSiProductoLoPermite, motivo, ct, almacenId, revertirCosto)` | `StockMovements.OutboundAsync(type, referenceId, lines, allowNegativeIfProductAllows, reason, ct, warehouseId, reverseCost)` |
| `MovimientosInventario.EntradaAsync(tipo, referenciaId, lineas, actualizarCosto, motivo, ct, almacenId)` | `StockMovements.InboundAsync(type, referenceId, lines, updateCost, reason, ct, warehouseId)` |
| `MovimientosInventario.AplicarDiferenciaAsync(tipo, referenciaId, productoId, almacen, diferencia, costo, motivo, ct)` | `StockMovements.ApplyDifferenceAsync(type, referenceId, productId, warehouse, difference, cost, reason, ct)` |
| private `Registrar`, `CantidadAsync`, `ValidarCantidad`, `Agrupar` | `Record`, `QuantityAsync`, `ValidateQuantity`, `Group` |

### Persistence (`Toki.Inventory.Persistence`)

| Old | New |
|---|---|
| `InventarioDbContext` | `InventoryDbContext` |
| `.Categorias` | `.Categories` |
| `.Productos` | `.Products` |
| `.Presentaciones` | `.Presentations` |
| `.Codigos` | `.Barcodes` |
| `.Almacenes` | `.Warehouses` |
| `.Existencias` | `.Stock` |
| `.Movimientos` | `.StockMovements` |
| `.FormatosEtiqueta` | `.LabelFormats` |
| `.Conteos` | `.StockCounts` |
| `ConteoConfiguration` | `StockCountConfiguration` |
| `InventarioConfiguration` | `InventoryConfiguration` |
| `DisenoInventario` | `InventoryDesignTimeFactory` |

### Domain (`Toki.Inventory.Domain`)

| Old | New |
|---|---|
| `Categoria` (`Id, Nombre, Color, Notas, Activa`) | `Category` (`Id, Name, Color, Notes, IsActive`) |
| `Producto` (`Id, Nombre, CategoriaId, Unidad, Plu, TasaItbis, Precio, PrecioMayorista, Costo, VenderSinExistencia, StockMinimo, Activo, Version`) | `Product` (`Id, Name, CategoryId, Unit, Plu, ItbisRate, Price, WholesalePrice, Cost, SellWithoutStock, MinimumStock, IsActive, Version`) |
| `Producto.NormalizarPlu(plu)` | `Product.NormalizePlu(plu)` |
| `Presentacion` (`Id, ProductoId, Nombre, Factor, Precio, PrecioMayorista, Activa`) | `Presentation` (`Id, ProductId, Name, Factor, Price, WholesalePrice, IsActive`) |
| `CodigoBarras` (`Codigo, ProductoId, PresentacionId`) | `Barcode` (`Code, ProductId, PresentationId`) |
| `Almacen` (`Id, Nombre, Activo`) | `Warehouse` (`Id, Name, IsActive`) |
| `Almacen.PrincipalId` | `Warehouse.MainId` |
| `Existencia` (`ProductoId, AlmacenId, Cantidad, PendienteContar`) | `Stock` (`ProductId, WarehouseId, Quantity, NeedsCount`) |
| `Movimiento` (`Secuencia, Id, ProductoId, AlmacenId, Tipo, Cantidad, CostoUnitario, ExistenciaResultante, ReferenciaId, Motivo, UsuarioId, FechaUtc`) | `StockMovement` (`Sequence, Id, ProductId, WarehouseId, Type, Quantity, UnitCost, ResultingStock, ReferenceId, Reason, UserId, DateUtc`) |
| `FormatoEtiqueta` (`Prefijo, LongitudPlu, EsPrecio, Decimales`) | `LabelFormat` (`Prefix, PluLength, IsPrice, Decimals`) |
| `FormatoEtiqueta.Predeterminados()` | `LabelFormat.Defaults()` |
| `LecturaEtiqueta(Plu, Peso, Precio)` | `ParsedScaleLabel(Plu, Weight, Price)` |
| `EtiquetaBalanza` | `ScaleLabel` |
| `EtiquetaBalanza.Leer(string codigo, IEnumerable<FormatoEtiqueta> formatos)` → `Result<LecturaEtiqueta>?` | `ScaleLabel.Read(string code, IEnumerable<LabelFormat> formats)` → `Result<ParsedScaleLabel>?` |
| `EtiquetaBalanza.DigitoVerificador(ReadOnlySpan<char> doce)` | `ScaleLabel.CheckDigit(ReadOnlySpan<char> twelve)` |
| `CostoPromedio` | `AverageCost` |
| `CostoPromedio.Calcular(existencia, costoActual, cantidad, costoEntrada)` | `AverageCost.Calculate(stock, currentCost, quantity, inboundCost)` |
| `CostoPromedio.Revertir(existenciaDespues, costoActual, cantidad, costoEntrada)` | `AverageCost.Reverse(stockAfter, currentCost, quantity, inboundCost)` |
| `InventarioErrors` | `InventoryErrors` |
| `.ProductoNoEncontrado` / `.CategoriaNoEncontrada` / `.AlmacenNoEncontrado` / `.CodigoNoEncontrado` | `.ProductNotFound` / `.CategoryNotFound` / `.WarehouseNotFound` / `.CodeNotFound` |
| `.CodigoDuplicado` / `.PluDuplicado` / `.SinPermisoPrecios` | `.DuplicateBarcode` / `.DuplicatePlu` / `.NoPricePermission` |
| `.SinExistencia(producto, disponible)` | `.OutOfStock(product, available)` |
| `EstadoConteo { Abierto, Aplicado }` | `StockCountStatus { Open, Applied }` |
| `ConteoFisico` (`Id, AlmacenId, CategoriaId, Estado, UsuarioId, CreadoUtc, AplicadoUtc, Lineas`) | `StockCount` (`Id, WarehouseId, CategoryId, Status, UserId, CreatedUtc, AppliedUtc, Lines`) |
| `ConteoLinea` (`ConteoId, ProductoId, Contado, SistemaAlContar`) | `StockCountLine` (`StockCountId, ProductId, Counted, SystemAtCount`) |

### Features (`Toki.Inventory.Features`)

All slice classes are listed above under "Requests and responses"; also `MotivoAjuste` → `AdjustmentReason` (internal enum in `Features/Adjustments.cs`).

## Notes for other agents

- Tests in `tests/Toki.UnitTests/Inventario/InventarioTests.cs` call `CostoPromedio.Calcular/Revertir` with named arguments: use `stock:`, `currentCost:`, `quantity:`, `inboundCost:` and `stockAfter:`. They assert error code `Inventario.EtiquetaMalLeida` → `Inventory.LabelMisread`.
- `tests/Toki.IntegrationTests/Infra/TokiApp.cs` (kardex check) reads `db.Stock` / `db.StockMovements` and `ProductId`, `WarehouseId`, `Sequence`, `Quantity`, `ResultingStock`.
- Sqlite ignores schemas, so table names must be unique across modules: this module now owns `Categories`, `Products`, `Presentations`, `Barcodes`, `Warehouses`, `Stock`, `StockMovements`, `LabelFormats`, `StockCounts`, `StockCountLines`.
- The `InvalidQuantity` detail still says "se vende por unidad/libra": it used the enum name before, and now maps `Pound` → `libra`, `Unit` → `unidad` so the Spanish text stays the same.
