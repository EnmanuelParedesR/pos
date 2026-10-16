using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>TK-209: every AbastaliaApp on SQL Server already migrates a new database (CreateDatabaseAsync). This one upgrades a store that has data
/// from before the direct DGII path: the Fiscal migration <c>DgiiDirect</c> (TK-144..150) must keep its ranges and documents as Simulated,
/// let a 32 range drop its expiry, and leave the new columns with their defaults. Runs in the SQL Server CI job (ABASTALIA_TEST_DB=sqlserver).</summary>
public sealed class SqlServerUpgradeTests
{
    [Fact]
    public async Task The_DGII_migration_keeps_a_stores_ranges_and_documents_as_simulated()
    {
        if (!TestSqlServer.IsEnabled) Assert.Skip("Sqlite creates the tables from the model; migrations only run on SQL Server (ABASTALIA_TEST_DB=sqlserver).");
        var connection = new SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString) { InitialCatalog = $"abastalia_test_{Guid.NewGuid():N}" }.ConnectionString;
        await using var db = new FiscalDbContext(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlServer(connection).Options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261015152435_InitialFiscal");
            var (range, document) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            var signed = DateTime.UtcNow;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO fiscal.EcfSequences (Id, Type, [From], [To], Next, ExpiresOn, CreatedUtc) VALUES ({range}, 32, 1, 1000, 2, '2027-12-31', {signed});
                INSERT INTO fiscal.EcfDocuments (Id, ENcf, Type, ReferenceId, Itbis, Total, SignedUtc, SecurityCode, SignedXml, Status, SubmitDeadlineUtc, Attempts)
                VALUES ({document}, 'E320000000001', 32, {Guid.CreateVersion7()}, 9, 59, {signed}, 'ABC123', {EcfSubmissionQueue.Zip("<ECF/>")}, 'Simulated', {signed.AddHours(72)}, 0);
                """);

            await migrator.MigrateAsync();
            (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();

            var s = await db.Sequences.AsNoTracking().SingleAsync();
            (s.Environment, s.ExpiresOn, s.Next).ShouldBe((FiscalEnvironment.Simulated, new DateOnly(2027, 12, 31), 2L));
            var d = await db.Documents.AsNoTracking().SingleAsync();
            (d.Environment, d.Channel, d.Contingency, d.Status, d.ReplacesENcf, d.DgiiCode).ShouldBe((FiscalEnvironment.Simulated, EcfChannel.Ecf, false, SubmissionStatus.Simulated, null, null));
            EcfSubmissionQueue.Unzip(d.SignedXml).ShouldBe("<ECF/>");

            // What the new code writes fits the new columns: a 32 range without expiry, an RFCE, a voided document and its ANECF (TK-205).
            db.Sequences.Add(new NcfSequence { Id = Guid.CreateVersion7(), Type = EcfType.Consumer32, Environment = FiscalEnvironment.TesteCF, From = 1, To = 50_000_000, Next = 1, CreatedUtc = signed });
            var tracked = await db.Documents.SingleAsync();
            (tracked.Status, tracked.RfceXml, tracked.SequenceExpiresOn) = (SubmissionStatus.Voided, EcfSubmissionQueue.Zip(new string('x', 20_000)), null);
            db.Voids.Add(new EcfVoid
            {
                Id = Guid.CreateVersion7(), SequenceId = range, Type = EcfType.Consumer32, Environment = FiscalEnvironment.Simulated, From = 1, To = 1, CreatedUtc = signed,
                SignedXml = EcfSubmissionQueue.Zip("<ANECF/>"), Status = SubmissionStatus.Simulated,
            });
            await db.SaveChangesAsync();
            (await db.Sequences.CountAsync(x => x.ExpiresOn == null)).ShouldBe(1);
            (await db.Documents.AsNoTracking().SingleAsync()).Status.ShouldBe(SubmissionStatus.Voided);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
