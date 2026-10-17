using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Features;
using Abastalia.Tests;

namespace Abastalia.UnitTests.Fiscal;

/// <summary>TK-252: the DGII public consultation pages, read from HTML recorded on 2026-10-16 (tests/DgiiPages); never the live site.</summary>
public sealed class DgiiPublicLookupTests
{
    private static readonly DgiiTaxpayer Dgii = new("401506254", "DIRECCION GENERAL DE IMPUESTOS INTERNOS", "DGII", "ACTIVO", null, "NORMAL", true);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (DgiiPublicSite Site, FakeDgiiPublic Fake, FakeTimeProvider Clock) Site(params (string Key, string Value)[] settings) =>
        Site(Database(), new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero)), settings);

    /// <summary>A store database (a Sqlite file) that outlives the app instances built on it.</summary>
    private static string Database()
    {
        var file = Path.Combine(Path.GetTempPath(), $"abastalia_dgii_{Guid.NewGuid():N}.db");
        using var db = new FiscalDbContext(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlite(Connection(file)).Options);
        db.Database.EnsureCreated();
        return file;
    }

    private static string Connection(string file) => $"Data Source={file};Pooling=False";

    /// <summary>One app instance (what a restart of the store server builds anew) on <paramref name="database"/>.</summary>
    private static (DgiiPublicSite Site, FakeDgiiPublic Fake, FakeTimeProvider Clock) Site(string database, FakeTimeProvider clock, params (string Key, string Value)[] settings)
    {
        var fake = new FakeDgiiPublic();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        var scopes = new ServiceCollection().AddDbContext<FiscalDbContext>(o => o.UseSqlite(Connection(database))).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return (new DgiiPublicSite(new Factory(fake), scopes, config, clock, NullLogger<DgiiPublicSite>.Instance), fake, clock);
    }

    private static int Rows(string database)
    {
        using var db = new FiscalDbContext(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlite(Connection(database)).Options);
        return db.DgiiPublicAnswers.Count();
    }

    private static int Posts(FakeDgiiPublic fake) => fake.Requests.Count(r => r.Method == HttpMethod.Post);

    [Fact]
    public void A_registered_taxpayer_is_read_with_its_status_category_and_e_CF_flag()
    {
        var r = DgiiPages.ReadRnc(FakeDgiiPublic.Page("rnc-found"));

        r.ShouldBe(new RncLookup(DgiiAnswerKind.Found, Dgii));
        r!.Taxpayer!.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void The_pages_are_read_whatever_the_whitespace_case_and_attributes_between_tags()
    {
        var html = FakeDgiiPublic.Page("rnc-found").Replace("<tr>", "<TR class=\"fila\">\r\n\t ").Replace("<td>", "<td style=\"x\">\n   ").Replace("</td>", "  \n </TD>")
            .Replace("ACTIVO", "  SUSPENDIDO\r\n");

        DgiiPages.ReadRnc(html).ShouldBe(new RncLookup(DgiiAnswerKind.Found, Dgii with { Status = "SUSPENDIDO" }));
        DgiiPages.ReadRnc(html)!.Taxpayer!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Not_registered_is_a_clear_answer_and_a_blocked_or_unknown_page_is_none()
    {
        DgiiPages.ReadRnc(FakeDgiiPublic.Page("rnc-not-registered")).ShouldBe(new RncLookup(DgiiAnswerKind.NotRegistered));
        DgiiPages.ReadRnc(FakeDgiiPublic.Page("rnc-form")).ShouldBeNull();
        DgiiPages.ReadRnc(FakeDgiiPublic.Page("waf-denied")).ShouldBeNull();
        DgiiPages.ReadNcf(FakeDgiiPublic.Page("waf-denied")).ShouldBeNull();
        DgiiPages.ReadNcf(FakeDgiiPublic.Page("ncf-form")).ShouldBeNull();
    }

    [Fact]
    public void An_NCF_is_valid_expired_with_its_last_day_or_not_matching_the_RNC()
    {
        var expired = DgiiPages.ReadNcf(FakeDgiiPublic.Page("ncf-expired"))!;
        expired.ShouldBe(new NcfCheck(NcfValidity.Expired, new DateOnly(2019, 12, 31), "DIRECCION GENERAL DE IMPUESTOS INTERNOS", "FACTURA DE CRÉDITO FISCAL"));
        expired.RefusedOn(new DateOnly(2019, 12, 20)).ShouldBeFalse(); // an invoice of 2019 was a valid receipt
        expired.RefusedOn(new DateOnly(2026, 10, 10)).ShouldBeTrue();

        DgiiPages.ReadNcf(FakeDgiiPublic.Page("ncf-valid"))!.Validity.ShouldBe(NcfValidity.Valid);
        var notMatching = DgiiPages.ReadNcf(FakeDgiiPublic.Page("ncf-not-matching"))!;
        notMatching.Validity.ShouldBe(NcfValidity.NotMatching);
        notMatching.RefusedOn(new DateOnly(2026, 10, 10)).ShouldBeTrue();
        NcfCheck.Unknown.RefusedOn(new DateOnly(2026, 10, 10)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_lookup_posts_the_form_back_with_its_ViewState_and_cookie_and_is_cached_for_7_days()
    {
        var (site, fake, clock) = Site();
        fake.Rnc("401506254", "rnc-found");

        (await site.RncAsync("401-50625-4", default)).ShouldBe(new RncLookup(DgiiAnswerKind.Found, Dgii));
        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Form["__VIEWSTATE"].ShouldBe(DgiiPages.HiddenFields(FakeDgiiPublic.Page("rnc-form"))["__VIEWSTATE"]);
        post.Form.ShouldContainKey("__EVENTVALIDATION");
        post.Form["ctl00$cphMain$txtRNCCedula"].ShouldBe("401506254");
        post.Cookie.ShouldBe("NSC_TEST=abc123");

        clock.Advance(TimeSpan.FromDays(6));
        (await site.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Found);
        Posts(fake).ShouldBe(1);
        clock.Advance(TimeSpan.FromDays(2));
        await site.RncAsync("401506254", default);
        Posts(fake).ShouldBe(2);
    }

    [Fact]
    public async Task A_valid_or_expired_NCF_is_asked_once_and_one_not_matching_again_after_an_hour()
    {
        var (site, fake, clock) = Site();
        fake.Ncf("401506254", "B0100000001", "ncf-expired").Ncf("401506254", "B0199999999", "ncf-not-matching");

        (await site.NcfAsync("401506254", "b0100000001", default)).Validity.ShouldBe(NcfValidity.Expired);
        (await site.NcfAsync("401506254", "B0199999999", default)).Validity.ShouldBe(NcfValidity.NotMatching);
        clock.Advance(TimeSpan.FromDays(400));
        await site.NcfAsync("401506254", "B0100000001", default);
        await site.NcfAsync("401506254", "B0199999999", default);

        fake.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Form["ctl00$cphMain$txtNCF"])
            .ShouldBe(["B0100000001", "B0199999999", "B0199999999"]);
    }

    [Fact]
    public async Task An_e_NCF_or_a_wrong_RNC_is_unknown_without_asking()
    {
        var (site, fake, _) = Site();

        (await site.NcfAsync("401506254", "E310000000001", default)).ShouldBe(NcfCheck.Unknown);
        (await site.NcfAsync("4015", "B0100000001", default)).ShouldBe(NcfCheck.Unknown);
        (await site.RncAsync("12", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        fake.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_store_asks_at_most_its_limit_per_minute_and_per_day()
    {
        var (site, fake, clock) = Site(("Fiscal:DgiiPublic:PerMinute", "2"), ("Fiscal:DgiiPublic:PerDay", "3"));
        foreach (var rnc in new[] { "101000001", "101000002", "101000003", "101000004" }) fake.Rnc(rnc, "rnc-not-registered");

        (await site.RncAsync("101000001", default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
        (await site.RncAsync("101000002", default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
        (await site.RncAsync("101000003", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable); // third in the same minute
        clock.Advance(TimeSpan.FromMinutes(1));
        (await site.RncAsync("101000003", default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
        (await site.RncAsync("101000004", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable); // fourth of the day
        Posts(fake).ShouldBe(3);
        clock.Advance(TimeSpan.FromDays(1));
        (await site.RncAsync("101000004", default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
    }

    [Fact]
    public async Task A_blocked_or_failing_DGII_is_unavailable_and_left_alone_for_two_minutes()
    {
        var (site, fake, clock) = Site();
        fake.Answer("rnc:401506254", () => FakeDgiiPublic.Html(FakeDgiiPublic.Page("waf-denied"), HttpStatusCode.Forbidden));

        (await site.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        fake.Rnc("401506254", "rnc-found");
        clock.Advance(TimeSpan.FromMinutes(1));
        (await site.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        Posts(fake).ShouldBe(1);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await site.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Found);
    }

    [Fact]
    public async Task The_answers_survive_a_restart_until_they_expire_and_expired_rows_are_purged()
    {
        var database = Database();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero));
        var (first, fake, _) = Site(database, clock);
        fake.Rnc("401506254", "rnc-found").Ncf("401506254", "B0100000001", "ncf-expired").Ncf("401506254", "B0199999999", "ncf-not-matching");
        var expired = await first.NcfAsync("401506254", "B0100000001", default);
        await first.RncAsync("401506254", default);
        await first.NcfAsync("401506254", "B0199999999", default);
        await first.Written;

        clock.Advance(TimeSpan.FromDays(6));
        var (second, nobody, _) = Site(database, clock); // a new app instance, the DGII programmed for nothing
        (await second.RncAsync("401506254", default)).ShouldBe(new RncLookup(DgiiAnswerKind.Found, Dgii));
        (await second.NcfAsync("401506254", "B0100000001", default)).ShouldBe(expired);
        (await second.NcfAsync("401506254", "B0199999999", default)).ShouldBe(NcfCheck.Unknown); // "does not match" lasted an hour: asks, no answer
        nobody.Requests.Count(r => r.Method == HttpMethod.Get).ShouldBe(1);
        await second.Written;

        clock.Advance(TimeSpan.FromDays(2)); // the RNC is 8 days old: asked again
        var (third, again, _) = Site(database, clock);
        again.Rnc("401506254", "rnc-found");
        await third.RncAsync("401506254", default);
        Posts(again).ShouldBe(1);
        await third.Written;
        // Left: the NCF kept for good, the RNC just asked, today's count. Gone: the RNC of 8 days ago (replaced), "does not match", the past days' counts.
        Rows(database).ShouldBe(3);
    }

    [Fact]
    public async Task The_day_allowance_survives_a_restart()
    {
        var database = Database();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero));
        var (first, fake, _) = Site(database, clock, ("Fiscal:DgiiPublic:PerDay", "2"));
        fake.Rnc("101000001", "rnc-not-registered").Rnc("101000002", "rnc-not-registered");
        await first.RncAsync("101000001", default);
        await first.RncAsync("101000002", default);
        await first.Written;

        var (second, again, _) = Site(database, clock, ("Fiscal:DgiiPublic:PerDay", "2"));
        again.Rnc("101000003", "rnc-not-registered");
        (await second.RncAsync("101000003", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        again.Requests.ShouldBeEmpty();
        clock.Advance(TimeSpan.FromDays(1));
        (await second.RncAsync("101000003", default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
    }

    [Fact]
    public async Task RNC_lookups_leave_a_fifth_of_the_day_for_the_NCF_checks_of_purchases()
    {
        var (site, fake, _) = Site(("Fiscal:DgiiPublic:PerMinute", "100"), ("Fiscal:DgiiPublic:PerDay", "5"));
        foreach (var rnc in new[] { "101000001", "101000002", "101000003", "101000004", "101000005" }) fake.Rnc(rnc, "rnc-not-registered");
        fake.Ncf("401506254", "B0100000001", "ncf-expired");

        foreach (var rnc in new[] { "101000001", "101000002", "101000003", "101000004" })
            (await site.RncAsync(rnc, default)).Kind.ShouldBe(DgiiAnswerKind.NotRegistered);
        (await site.RncAsync("101000005", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable); // R12: the last fifth is the purchases'
        (await site.NcfAsync("401506254", "B0100000001", default)).Validity.ShouldBe(NcfValidity.Expired);
    }

    [Fact]
    public async Task A_cached_row_that_cannot_be_read_is_as_if_not_cached()
    {
        var database = Database();
        using (var db = new FiscalDbContext(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlite(Connection(database)).Options))
        {
            db.DgiiPublicAnswers.Add(new DgiiPublicAnswer { Key = "rnc:401506254", Answer = "{not json", ExpiresUtc = DateTime.MaxValue });
            db.SaveChanges();
        }
        var (site, fake, _) = Site(database, new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero)));
        fake.Rnc("401506254", "rnc-found");

        (await site.RncAsync("401506254", default)).ShouldBe(new RncLookup(DgiiAnswerKind.Found, Dgii));
        Posts(fake).ShouldBe(1);

        // A database it cannot read at all (no table): still an answer, never an error.
        var broken = Path.Combine(Path.GetTempPath(), $"abastalia_dgii_{Guid.NewGuid():N}.db");
        var (other, otherFake, _) = Site(broken, new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero)));
        otherFake.Rnc("401506254", "rnc-found");
        (await other.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Found);
    }

    [Fact]
    public async Task Turned_off_it_never_asks()
    {
        var (off, offFake, _) = Site(("Fiscal:DgiiPublic:Enabled", "false"));
        offFake.Rnc("401506254", "rnc-found");
        (await off.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        offFake.Requests.ShouldBeEmpty();
    }
}
