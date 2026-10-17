using System.Text.Json;
using Shouldly;
using Abastalia.Fiscal.Domain;

namespace Abastalia.Lite.Tests;

/// <summary>TK-324: what Abastalia.Client.Lite does after a sale, with the browser's own code (lite-signer fiscal.js, run by Node with its
/// WebCrypto) against the real host: the sale's 32 waits for the device, the device signs the e-CF and its RFCE with an imported .p12, the host
/// checks both, and the DGII's answer (a fetch stub: TesteCF waits for TK-141) closes it. Needs <c>node</c>; skips otherwise.</summary>
public sealed class BrowserFiscalTests(KestrelLiteApp app) : IClassFixture<KestrelLiteApp>
{
    private const string Script = """
        import { DOMParser } from '@xmldom/xmldom';
        import { pairDevice, deviceFetch, memoryStore, importP12, deviceSigner, dgiiClient, processDocuments, ECF_TYPES } from './index.js';
        import { testP12 } from './fixtures/make-p12.mjs';
        let input = '';
        for await (const chunk of process.stdin) input += chunk;
        const { baseUrl, code, pin, rnc } = JSON.parse(input);
        const store = memoryStore();
        await pairDevice({ baseUrl, code, name: 'Celular', store });
        const send = deviceFetch({ baseUrl, store });
        const { token } = await (await send('/api/identity/sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: 'dueno', pin }) })).json();
        await importP12(testP12('clave').p12, 'clave', store);
        const signer = deviceSigner({ rnc, types: ECF_TYPES, store, DOMParser });
        const json = v => new Response(JSON.stringify(v), { headers: { 'Content-Type': 'application/json' } });
        const dgiiCalls = [];
        const dgii = dgiiClient({ environment: 'TesteCF', signer, fetch: async url => {
          dgiiCalls.push(new URL(url).pathname);
          if (url.endsWith('/semilla')) return new Response('<SemillaModel><valor>v</valor><fecha>2026-10-17T10:00:00</fecha></SemillaModel>');
          if (url.endsWith('/validarsemilla')) return json({ token: 'dgii', expira: new Date(Date.now() + 3_600_000).toISOString() });
          return json({ codigo: 1, estado: 'Aceptado', secuenciaUtilizada: true, mensajes: [] });
        } });
        const done = await processDocuments({ send, token, signer, dgiiFor: () => dgii, rnc });
        process.stdout.write(JSON.stringify({ done, dgiiCalls, signed: (await signer.log()).map(l => l.document) }));
        """;

    [Fact]
    public async Task A_sale_leaves_its_e_cf_to_the_device_which_signs_it_and_reports_the_dgii_answer()
    {
        var baseUrl = app.Address;
        var shop = await Shops.OpenAsync(app, "Colmado del celular");
        var encf = await shop.SellAsync();
        (await app.DocumentAsync(shop.Owner, encf)).Status.ShouldBe(SubmissionStatus.AwaitingDeviceSignature);
        // The browser takes the plan's one selling device: the owner revokes the one that set the shop up.
        (await app.Panel().PostAsync($"/internal/tenants/{shop.Owner.TenantId}/devices/{shop.Owner.Device.Id}/revoke", null)).EnsureSuccessStatusCode();

        using var result = await BrowserDeviceTests.NodeAsync(Script,
            JsonSerializer.Serialize(new { baseUrl, code = await app.CodeAsync(shop.Owner.TenantId), pin = LiteApp.Pin, rnc = Shops.Rnc }));

        var done = result.RootElement.GetProperty("done").EnumerateArray().Single();
        (done.Text("eNcf"), done.Text("step"), done.Text("status")).ShouldBe((encf, "Done", "Accepted"), result.RootElement.ToString());
        result.RootElement.GetProperty("signed").EnumerateArray().Select(e => e.GetString()).ShouldBe(["ECF", "RFCE", "SemillaModel"]);
        result.RootElement.GetProperty("dgiiCalls").EnumerateArray().Last().GetString().ShouldBe("/testecf/recepcionfc/api/recepcion/ecf");
        var stored = await app.DocumentAsync(shop.Owner, encf);
        (stored.Status, stored.SecurityCode.Length).ShouldBe((SubmissionStatus.Accepted, 6));
    }
}
