namespace Toki.BuildingBlocks.Security;

/// <summary>Sellable functions switched on by the license (docs/funcionalidades-v1.md §5). None of them is built in V1 yet.</summary>
public enum Funcion { ModuloCarniceria, ModuloMayorista, CopiaNube, AsistenteIA }

public enum Limite { Cajas }

/// <summary>Prueba: no license installed. Gracia: expired less than 15 days ago (offline stores keep everything).
/// Restringida: expired longer: paid functions off and trial limits, but selling, e-CF, exports and backups never stop.</summary>
public enum EstadoLicencia { Prueba, Activa, Gracia, Restringida }

/// <summary>What the store's license allows (arquitectura-v1-vsa §9). Selling, fiscal and backup code must never ask it (an architecture test checks).</summary>
public interface IFeatureService
{
    EstadoLicencia Estado { get; }
    bool EstaActiva(Funcion funcion);
    int LimiteDe(Limite limite);
}
