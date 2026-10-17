using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-366: the session locks back to the PIN after the user's idle minutes, warns a minute before, and tells the server while someone works.</summary>
public sealed class IdleGuardTests
{
    [Fact]
    public async Task It_warns_a_minute_before_reports_activity_and_locks_keeping_the_sale()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var session = ctx.Services.GetRequiredService<Session>();
        session.Open(new OpenedSession("token", Guid.NewGuid(), "Yudelka M.", DateTime.UtcNow.AddHours(12)),
            new Me(Guid.NewGuid(), "Yudelka M.", Guid.NewGuid(), ["sales.create"], IdleMinutes: 15));
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(new ProductByCode(Guid.NewGuid(), null, "Refresco", "Unit", "Exempt", 59m, null, 1m, null, null, [], null), null);
        api.Json("GET", "/api/identity/me", new { });
        api.Respond("DELETE", "/api/identity/sessions/current", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var clock = ctx.Services.GetRequiredService<FakeTimeProvider>();
        var guard = ctx.Render<IdleGuard>();
        void Idle(double seconds) => ctx.JSInterop.Setup<double>("escuadra.idleSeconds").SetResult(seconds);

        // Working: no warning, and the server hears it (its own lock counts only the person's requests).
        Idle(3);
        clock.Advance(IdleGuard.Tick);
        guard.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Method == "GET" && r.Route == "/api/identity/me"));
        guard.Markup.ShouldNotContain("¿Sigue ahí?");

        // A minute before: the warning, saying the sale is not lost; a key takes it away.
        Idle(14 * 60 + 5);
        clock.Advance(IdleGuard.Tick);
        guard.WaitForAssertion(() => guard.Markup.ShouldContain("¿Sigue ahí?"));
        guard.Markup.ShouldContain("La venta en curso no se pierde");
        Idle(1);
        clock.Advance(IdleGuard.Tick);
        guard.WaitForAssertion(() => guard.Markup.ShouldNotContain("¿Sigue ahí?"));

        // Fifteen minutes: the session closes and the register asks for the PIN; the cart stays for its cashier.
        Idle(15 * 60);
        clock.Advance(IdleGuard.Tick);
        guard.WaitForAssertion(() => session.Expired.ShouldBeTrue());
        api.Requests.ShouldContain(r => r.Method == "DELETE" && r.Route == "/api/identity/sessions/current");
        session.IsOpen.ShouldBeFalse();
        cart.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_idle_minutes_from_the_server_it_never_locks()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        ctx.JSInterop.Setup<double>("escuadra.idleSeconds").SetResult(24 * 3600);
        var guard = ctx.Render<IdleGuard>();
        ctx.Services.GetRequiredService<FakeTimeProvider>().Advance(IdleGuard.Tick);
        Thread.Sleep(100);
        ctx.Services.GetRequiredService<Session>().IsOpen.ShouldBeTrue();
        api.Requests.ShouldNotContain(r => r.Route == "/api/identity/sessions/current");
    }
}
