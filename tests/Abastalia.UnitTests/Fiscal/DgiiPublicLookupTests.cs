using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
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

    private static (DgiiPublicSite Site, FakeDgiiPublic Fake, FakeTimeProvider Clock) Site(params (string Key, string Value)[] settings)
    {
        var fake = new FakeDgiiPublic();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        return (new DgiiPublicSite(new Factory(fake), config, clock, NullLogger<DgiiPublicSite>.Instance), fake, clock);
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
    public async Task Turned_off_it_never_asks()
    {
        var (off, offFake, _) = Site(("Fiscal:DgiiPublic:Enabled", "false"));
        offFake.Rnc("401506254", "rnc-found");
        (await off.RncAsync("401506254", default)).Kind.ShouldBe(DgiiAnswerKind.Unavailable);
        offFake.Requests.ShouldBeEmpty();
    }
}
