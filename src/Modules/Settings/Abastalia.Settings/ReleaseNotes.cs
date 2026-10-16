using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Security;

namespace Abastalia.Settings;

/// <summary>Per-user choices kept on the store's server, so they follow the person and not the device.</summary>
internal sealed class UserPreference
{
    public Guid UserId { get; set; }
    /// <summary>TK-226: the last app version whose "Novedades" card this user closed.</summary>
    public string? ReleaseNotesSeen { get; set; }
}

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> b)
    {
        b.ToTable("UserPreferences");
        b.HasKey(p => p.UserId);
        b.Property(p => p.UserId).ValueGeneratedNever();
        b.Property(p => p.ReleaseNotesSeen).HasMaxLength(50);
    }
}

/// <summary>TK-226: the "Novedades" card on Resumen shows once per user and version; the notes themselves live in the client.</summary>
internal static class ReleaseNotesSeen
{
    public sealed record Seen(string? Version);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/settings/release-notes/seen", async (SettingsDbContext db, IUserContext user, CancellationToken ct) =>
                    Results.Ok(new Seen(await db.UserPreferences.Where(p => p.UserId == user.UserId).Select(p => p.ReleaseNotesSeen).SingleOrDefaultAsync(ct))))
                .RequireAuthorization().WithTags("Settings");

            app.MapPut("/api/settings/release-notes/seen", async (Seen seen, SettingsDbContext db, IUserContext user, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(seen.Version) || seen.Version.Length > 50) return Results.BadRequest();
                    var p = await db.UserPreferences.FindAsync([user.UserId], ct);
                    if (p is null) db.UserPreferences.Add(p = new UserPreference { UserId = user.UserId });
                    p.ReleaseNotesSeen = seen.Version;
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                })
                .RequireAuthorization().WithTags("Settings");
        }
    }
}
