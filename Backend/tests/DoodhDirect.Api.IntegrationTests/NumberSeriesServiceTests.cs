using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class NumberSeriesServiceTests
{
    // ---------------------------------------------------------------- allocation

    [Fact]
    public async Task GetNextNumber_AllocatesStartingNumber_AndFormatsWithPadding()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var first = await service.GetNextNumberAsync("customer", 11, CancellationToken.None);
        var second = await service.GetNextNumberAsync("CUSTOMER", 11, CancellationToken.None);

        Assert.Equal("CUST/0001", first);
        Assert.Equal("CUST/0002", second);
    }

    [Fact]
    public async Task GetNextNumber_AdvancesByIncrement()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{NUMBER:000000}", 100, 5, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var first = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None);
        var second = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None);

        Assert.Equal("ORD/000100", first);
        Assert.Equal("ORD/000105", second);
    }

    [Fact]
    public async Task GetNextNumber_PrefixToken_RendersNormalizedSeriesCode()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "BR", "Branch Number", "{PREFIX}/{NUMBER:000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var number = await service.GetNextNumberAsync("BR", 11, CancellationToken.None);

        Assert.Equal("BR/001", number);
    }

    [Fact]
    public async Task GetNextNumber_PersistsCounterAndRecordsLastUsed()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "DEL", "Delivery Number", "DEL/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        await service.GetNextNumberAsync("DEL", 11, CancellationToken.None);

        var series = await db.NumberSeries.SingleAsync(item => item.Code == "DEL");
        Assert.Equal(1, series.LastUsedNumber);
        Assert.Equal(time.Now, series.LastUsedAt);
        Assert.Equal(11, series.UpdatedByUserId);
    }

    [Fact]
    public async Task GetNextNumber_InactiveSeries_ThrowsBusinessRule()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var series = new NumberSeries(
            "ORDER", "Order Number", "ORD/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never);
        series.Deactivate(1, time.Now);
        db.NumberSeries.Add(series);
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.GetNextNumberAsync("ORDER", 11, CancellationToken.None));

        Assert.Contains("inactive", exception.Message);
    }

    [Fact]
    public async Task GetNextNumber_MissingSeries_ThrowsNotFound()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetNextNumberAsync("CUSTOMER", 11, CancellationToken.None));
    }

    // ---------------------------------------------------------------- scoping

    [Fact]
    public async Task GetNextNumber_ScopedSeries_AllocatesIndependentCountersPerScope()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NIT"));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var mainFirst = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "MAIN");
        var mainSecond = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "MAIN");
        var nitFirst = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "NIT");

        // Each scope keeps its own counter and the {SCOPE} token renders the scope key.
        Assert.Equal("ORD/MAIN/000001", mainFirst);
        Assert.Equal("ORD/MAIN/000002", mainSecond);
        Assert.Equal("ORD/NIT/000001", nitFirst);

        var main = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "MAIN");
        var nit = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "NIT");
        Assert.Equal(2, main.LastUsedNumber);
        Assert.Equal(1, nit.LastUsedNumber);
    }

    [Fact]
    public async Task GetNextNumber_ScopedSeries_ScopeNormalizedToUpperAndTrimmed()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var number = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "  main  ");

        Assert.Equal("ORD/MAIN/000001", number);
    }

    [Fact]
    public async Task GetNextNumber_ScopedAndGlobalSeries_RemainIndependent()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        // Legacy global ORDER series (empty scope) plus a scoped ORDER series for MAIN.
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var global = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None);
        var scoped = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "MAIN");

        Assert.Equal("ORD/000001", global);
        Assert.Equal("ORD/MAIN/000001", scoped);

        var globalAgain = await service.GetNextNumberAsync("ORDER", 11, CancellationToken.None);
        Assert.Equal("ORD/000002", globalAgain);
    }

    [Fact]
    public async Task PreviewNextNumber_WithScope_FormatsScopeTokenWithoutConsuming()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var preview = await service.PreviewNextNumberAsync("ORDER", CancellationToken.None, "MAIN");

        Assert.Equal("ORDER", preview.Code);
        Assert.Equal("MAIN", preview.ScopeKey);
        Assert.Equal(1, preview.NextNumber);
        Assert.Equal("ORD/MAIN/000001", preview.FormattedNumber);
        Assert.Equal(0, (await db.NumberSeries.SingleAsync()).LastUsedNumber);
    }

    [Fact]
    public async Task Create_ScopedSeries_SameCodeAllowedAcrossScopesButNotWithinScope()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);

        var main = await service.CreateAsync(
            new CreateNumberSeriesRequest(
                "ORDER", "Main order numbers", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"),
            7,
            CancellationToken.None);
        var nit = await service.CreateAsync(
            new CreateNumberSeriesRequest(
                "ORDER", "NIT order numbers", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NIT"),
            7,
            CancellationToken.None);

        Assert.Equal("MAIN", main.ScopeKey);
        Assert.Equal("NIT", nit.ScopeKey);

        // Duplicate (code, scope) is rejected…
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            new CreateNumberSeriesRequest(
                "ORDER", "Another main", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"),
            7,
            CancellationToken.None));

        // …but the same code in a different scope is fine.
        var delhi = await service.CreateAsync(
            new CreateNumberSeriesRequest(
                "ORDER", "DELHI order numbers", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "DELHI"),
            7,
            CancellationToken.None);
        Assert.Equal("DELHI", delhi.ScopeKey);
    }

    [Fact]
    public async Task Update_ScopedSeries_EditsOnlyThatScope()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "Main order numbers", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        db.NumberSeries.Add(new NumberSeries(
            "ORDER", "NIT order numbers", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NIT"));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var updated = await service.UpdateAsync(
            "ORDER",
            new UpdateNumberSeriesRequest(
                "Updated MAIN description", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never),
            9,
            CancellationToken.None,
            "MAIN");

        Assert.Equal("Updated MAIN description", updated.Description);

        var nit = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "NIT");
        Assert.Equal("NIT order numbers", nit.Description); // untouched
    }

    [Fact]
    public async Task GetNextNumber_ScopedSeries_InactiveScope_ThrowsBusinessRule()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var series = new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");
        series.Deactivate(1, time.Now);
        db.NumberSeries.Add(series);
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.GetNextNumberAsync("ORDER", 11, CancellationToken.None, "MAIN"));

        Assert.Contains("inactive", exception.Message);
    }

    // ---------------------------------------------------------------- rollback

    [Fact]
    public async Task GetNextNumber_WithinRolledBackTransaction_DoesNotConsumeCounter()
    {
        // InMemory treats BeginTransaction as a no-op, so use a real SQLite connection.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new DoodhDirectDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        // Simulate a business flow that allocates then rolls back.
        var outer = await db.Database.BeginTransactionAsync();
        await service.GetNextNumberAsync("CUSTOMER", 11, CancellationToken.None);
        await outer.RollbackAsync();

        // The rollback undid the counter write, but EF still tracks the stale entity
        // (LastUsedNumber=1 in memory). Clear the tracker so the next allocation
        // re-reads LastUsedNumber from the database and allocates 0001 again.
        db.ChangeTracker.Clear();

        var number = await service.GetNextNumberAsync("CUSTOMER", 11, CancellationToken.None);
        Assert.Equal("CUST/0001", number);
    }

    // ---------------------------------------------------------------- reset policies

    [Fact]
    public async Task ResetPolicy_Daily_RestartsOnNewIndiaDay()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 23, 59, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "DEL", "Delivery Number", "DEL/{DATE:yyyyMMdd}/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Daily));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var day1First = await service.GetNextNumberAsync("DEL", 11, CancellationToken.None);
        var day1Second = await service.GetNextNumberAsync("DEL", 11, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2)); // next India day

        var day2First = await service.GetNextNumberAsync("DEL", 11, CancellationToken.None);

        Assert.Equal("DEL/20260828/0001", day1First);
        Assert.Equal("DEL/20260828/0002", day1Second);
        Assert.Equal("DEL/20260829/0001", day2First);
    }

    [Fact]
    public async Task ResetPolicy_CalendarYear_RestartsOnNewYear()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "INV", "Invoice Number", "INV/{YEAR}/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.CalendarYear));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var first = await service.GetNextNumberAsync("INV", 11, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2)); // new year

        var second = await service.GetNextNumberAsync("INV", 11, CancellationToken.None);

        Assert.Equal("INV/2026/0001", first);
        Assert.Equal("INV/2027/0001", second);
    }

    [Fact]
    public async Task ResetPolicy_FinancialYear_ResetsOnIndiaFinancialYearBoundary()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 3, 31, 23, 59, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "FY", "FY Number", "FY/{FY}/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.FinancialYear));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var fy2526Last = await service.GetNextNumberAsync("FY", 11, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2)); // 1 April 2026 — new FY starts

        var fy2627First = await service.GetNextNumberAsync("FY", 11, CancellationToken.None);

        // FY 25-26 runs 1 Apr 2025..31 Mar 2026 -> token "25-26"; new FY -> "26-27".
        Assert.Equal("FY/25-26/0001", fy2526Last);
        Assert.Equal("FY/26-27/0001", fy2627First);
    }

    [Fact]
    public async Task ResetPolicy_Monthly_RestartsOnNewMonth()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 31, 23, 59, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "MON", "Monthly Number", "MON/{MONTH}/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Monthly));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var first = await service.GetNextNumberAsync("MON", 11, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2)); // September

        var second = await service.GetNextNumberAsync("MON", 11, CancellationToken.None);

        Assert.Equal("MON/08/0001", first);
        Assert.Equal("MON/09/0001", second);
    }

    [Fact]
    public void FinancialYear_ComputesIndiaFinancialYearBoundaries()
    {
        Assert.Equal((2025, 2026), NumberSeries.FinancialYear(new DateOnly(2026, 3, 31)));
        Assert.Equal((2026, 2027), NumberSeries.FinancialYear(new DateOnly(2026, 4, 1)));
        Assert.Equal((2026, 2027), NumberSeries.FinancialYear(new DateOnly(2026, 8, 28)));
        Assert.Equal((2026, 2027), NumberSeries.FinancialYear(new DateOnly(2027, 3, 31)));
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task PreviewNextNumber_DoesNotConsumeTheSequence()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        var preview = await service.PreviewNextNumberAsync("CUSTOMER", CancellationToken.None);
        var afterPreview = await service.PreviewNextNumberAsync("CUSTOMER", CancellationToken.None);

        Assert.Equal(1, preview.NextNumber);
        Assert.Equal("CUST/0001", preview.FormattedNumber);
        Assert.Equal(1, afterPreview.NextNumber); // still 1 — not consumed
        Assert.Equal(1, (await db.NumberSeries.SingleAsync()).LastUsedNumber + 1);
    }

    [Fact]
    public async Task PreviewNextNumber_ReflectsResetPolicyWithoutConsuming()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "DEL", "Delivery Number", "DEL/{DATE:yyyyMMdd}/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Daily));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);

        await service.GetNextNumberAsync("DEL", 11, CancellationToken.None);
        var preview = await service.PreviewNextNumberAsync("DEL", CancellationToken.None);

        Assert.Equal(2, preview.NextNumber);

        time.Advance(TimeSpan.FromDays(1));
        var previewNextDay = await service.PreviewNextNumberAsync("DEL", CancellationToken.None);

        Assert.Equal(1, previewNextDay.NextNumber);
        Assert.Equal("DEL/20260829/0001", previewNextDay.FormattedNumber);
    }

    [Fact]
    public void PreviewTemplate_FormatsWithoutTouchingAnySeriesRow()
    {
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(CreateDb(), time);

        var preview = service.PreviewTemplate("CUST", "CUST/{NUMBER:0000}", 42);

        Assert.Equal("CUST", preview.Code);
        Assert.Equal("CUST/{NUMBER:0000}", preview.Template);
        Assert.Equal(42, preview.NextNumber);
        Assert.Equal("CUST/0042", preview.FormattedNumber);
    }

    // ---------------------------------------------------------------- template validation

    [Fact]
    public void PreviewTemplate_RejectsUnsupportedToken()
    {
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(CreateDb(), time);

        var exception = Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", "CUST/{NUMBER:0000}/{QUARTER}", 1));

        Assert.Equal("Template", exception.Field);
        Assert.Contains("unsupported token", exception.Message);
    }

    [Fact]
    public void PreviewTemplate_RejectsMissingOrMultipleNumberTokens()
    {
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(CreateDb(), time);

        Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", "CUST/0000", 1));
        Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", "CUST/{NUMBER:0000}/{NUMBER:0000}", 1));
    }

    [Fact]
    public void PreviewTemplate_RejectsInvalidNumberWidth()
    {
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(CreateDb(), time);

        var exception = Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", "CUST/{NUMBER:0}", 1));

        Assert.Contains("zero-padded width", exception.Message);
    }

    [Fact]
    public void PreviewTemplate_RejectsMalformedDateTokenAndOverlongTemplate()
    {
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(CreateDb(), time);

        Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", "CUST/{DATE:Q}/{NUMBER:0000}", 1));
        Assert.Throws<ValidationAppException>(
            () => service.PreviewTemplate("CUST", new string('X', 121), 1));
    }

    // ---------------------------------------------------------------- CRUD + safe edit

    [Fact]
    public async Task Create_NormalizesCodeAndTemplateAndWritesCreatedAudit()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);

        var result = await service.CreateAsync(
            new CreateNumberSeriesRequest(
                "  cust  ", "Customer Number", " CUST/{NUMBER:0000} ",
                1, 1, NumberSeriesResetPolicy.Never),
            7,
            CancellationToken.None);

        Assert.Equal("CUST", result.Code);
        Assert.Equal("CUST/{NUMBER:0000}", result.Template);
        Assert.Equal(0, result.LastUsedNumber);
        Assert.Equal("CUST/0001", result.NextNumber);
        Assert.Equal(7, result.CreatedByUserId);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(NumberSeriesService.ActionCreated, audit.Action);
        Assert.Equal("NumberSeries", audit.EntityType);
        Assert.Equal("CUST", audit.EntityId);
        Assert.Equal(7, audit.UserId);
        Assert.Null(audit.OldValueJson);
        Assert.Contains("\"Code\":\"CUST\"", audit.NewValueJson);
    }

    [Fact]
    public async Task Create_DuplicateCode_ThrowsConflict()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);
        var request = new CreateNumberSeriesRequest(
            "CUST", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never);
        await service.CreateAsync(request, 7, CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(request, 7, CancellationToken.None));
    }

    [Fact]
    public async Task Update_BeforeFirstUse_AllowsFreeEditing()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);
        await service.CreateAsync(
            new CreateNumberSeriesRequest("CUST", "Customer", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never),
            7, CancellationToken.None);

        var updated = await service.UpdateAsync(
            "CUST",
            new UpdateNumberSeriesRequest("New description", "C-{NUMBER:000000}", 500, 2, NumberSeriesResetPolicy.Never),
            9,
            CancellationToken.None);

        Assert.Equal("New description", updated.Description);
        Assert.Equal("C-{NUMBER:000000}", updated.Template);
        Assert.Equal(500, updated.StartingNumber);
        Assert.Equal(2, updated.IncrementBy);
        Assert.Equal(9, updated.UpdatedByUserId);
    }

    [Fact]
    public async Task Update_AfterUse_RejectsBackwardOrForwardStartingNumberChanges()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        // The template carries a period token so a Daily reset policy is valid:
        // UpdateAsync runs ValidateResetPolicy (line 194) before EnsureSafeEdit (line 197),
        // and EnsureSafeEdit checks the template change before the reset-policy change.
        // Therefore every case below shares this template, and only the policy differs
        // for differentPolicy, so each assertion exercises the intended rule.
        const string template = "CUST/{DATE:yyyyMMdd}/{NUMBER:0000}";
        db.NumberSeries.Add(new NumberSeries(
            "CUST", "Customer", template, 10, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var service = new NumberSeriesService(db, time);
        await service.GetNextNumberAsync("CUST", 11, CancellationToken.None); // LastUsedNumber=10

        var backward = new UpdateNumberSeriesRequest(
            "Customer", template, 9, 1, NumberSeriesResetPolicy.Never);
        var forward = new UpdateNumberSeriesRequest(
            "Customer", template, 11, 1, NumberSeriesResetPolicy.Never);
        var differentIncrement = new UpdateNumberSeriesRequest(
            "Customer", template, 10, 2, NumberSeriesResetPolicy.Never);
        var differentTemplate = new UpdateNumberSeriesRequest(
            "Customer", "CUST/{NUMBER:000000}", 10, 1, NumberSeriesResetPolicy.Never);
        var differentPolicy = new UpdateNumberSeriesRequest(
            "Customer", template, 10, 1, NumberSeriesResetPolicy.Daily);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync("CUST", backward, 9, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync("CUST", forward, 9, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync("CUST", differentIncrement, 9, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync("CUST", differentTemplate, 9, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync("CUST", differentPolicy, 9, CancellationToken.None));
    }

    [Fact]
    public async Task Update_WritesUpdatedAuditWithOldAndNewSnapshots()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);
        await service.CreateAsync(
            new CreateNumberSeriesRequest("CUST", "Customer", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never),
            7, CancellationToken.None);

        await service.UpdateAsync(
            "CUST",
            new UpdateNumberSeriesRequest("Updated description", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never),
            9,
            CancellationToken.None);

        var audit = await db.AuditLogs.SingleAsync(item => item.Action == NumberSeriesService.ActionUpdated);
        Assert.Equal(9, audit.UserId);
        Assert.Contains("\"Description\":\"Customer\"", audit.OldValueJson);
        Assert.Contains("\"Description\":\"Updated description\"", audit.NewValueJson);
    }

    // ---------------------------------------------------------------- activate / deactivate

    [Fact]
    public async Task ActivateDeactivate_WritesAuditWithActor()
    {
        await using var db = CreateDb();
        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        var service = new NumberSeriesService(db, time);
        await service.CreateAsync(
            new CreateNumberSeriesRequest("CUST", "Customer", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never),
            7, CancellationToken.None);

        var deactivated = await service.SetActiveAsync("CUST", false, 13, CancellationToken.None);
        var reactivated = await service.SetActiveAsync("CUST", true, 15, CancellationToken.None);

        Assert.False(deactivated.IsActive);
        Assert.True(reactivated.IsActive);

        var deactivateAudit = await db.AuditLogs.SingleAsync(item => item.Action == NumberSeriesService.ActionDeactivated);
        var activateAudit = await db.AuditLogs.SingleAsync(item => item.Action == NumberSeriesService.ActionActivated);
        Assert.Equal(13, deactivateAudit.UserId);
        Assert.Equal(15, activateAudit.UserId);
        Assert.Equal("CUST", deactivateAudit.EntityId);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task ConcurrentAllocations_NeverProduceTheSameNumber()
    {
        // Shared-cache in-memory SQLite: every worker context sees the same database,
        // with a keeper connection alive for the lifetime of the test.
        var connectionString = $"Data Source=file:number-series-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();

        var time = new TestClock(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified));
        // SQLite has no retrying execution strategy; instead, Microsoft.Data.Sqlite issues
        // BEGIN IMMEDIATE for Serializable transactions, so concurrent writers serialize
        // on the write lock and the busy timeout absorbs transient contention.
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30))
            .Options;
        await using (var seedDb = new DoodhDirectDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.NumberSeries.Add(new NumberSeries(
                "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
            await seedDb.SaveChangesAsync();
        }

        // GetNextNumberAsync does a read (FindAsync) then a write (SaveChangesAsync), so
        // each worker must allocate inside a Serializable transaction (mirroring
        // OrderService.ExecuteSerializableAsync). SQLite serializes concurrent writers
        // on the write lock (BEGIN IMMEDIATE) and the busy timeout absorbs contention.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await using var workerDb = new DoodhDirectDbContext(options);
                var workerService = new NumberSeriesService(workerDb, time);
                var strategy = workerDb.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await workerDb.Database
                        .BeginTransactionAsync(IsolationLevel.Serializable);
                    var number = await workerService.GetNextNumberAsync("CUSTOMER", 11, CancellationToken.None);
                    await transaction.CommitAsync();
                    return number;
                });
            })));

        Assert.Equal(8, results.Length);
        Assert.Equal(8, results.Distinct().Count());
    }

    // ---------------------------------------------------------------- seed service

    [Fact]
    public async Task SeedService_CreatesInitialSeriesIdempotently_WithScopedOrderSeriesPerBranch()
    {
        await using var db = CreateDb();
        db.Branches.Add(new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m));
        await db.SaveChangesAsync();
        var seed = new NumberSeriesSeedService(db);

        await seed.SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        // CUSTOMER, ORDER@MAIN, BRANCH, DELIVERY — the global ORDER series is no
        // longer seeded; order numbers are scoped per active branch.
        Assert.Equal(4, await db.NumberSeries.CountAsync());
        Assert.Contains(await db.NumberSeries.Select(x => x.Code).ToListAsync(), code => code == "CUSTOMER");
        Assert.Contains(await db.NumberSeries.Select(x => x.Code).ToListAsync(), code => code == "ORDER");
        Assert.Contains(await db.NumberSeries.Select(x => x.Code).ToListAsync(), code => code == "BRANCH");
        Assert.Contains(await db.NumberSeries.Select(x => x.Code).ToListAsync(), code => code == "DELIVERY");
    }

    [Fact]
    public async Task SeedService_SeedsExpectedTemplatesAndResetPolicies()
    {
        await using var db = CreateDb();
        db.Branches.Add(new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m));
        await db.SaveChangesAsync();
        var seed = new NumberSeriesSeedService(db);
        await seed.SeedAsync(CancellationToken.None);

        var series = await db.NumberSeries.ToDictionaryAsync(item => item.Code);
        var order = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER");

        Assert.Equal("CUST/{NUMBER:0000}", series["CUSTOMER"].Template);
        // Order numbers are scoped per branch and reset each Indian financial year,
        // so the template renders branch + FY and the counter restarts on 1 April.
        Assert.Equal("ORD/MAIN/{FY}/{NUMBER:000000}", order.Template);
        Assert.Equal("MAIN", order.ScopeKey);
        Assert.Equal(NumberSeriesResetPolicy.FinancialYear, order.ResetPolicy);
        Assert.Equal("BR/{NUMBER:000}", series["BRANCH"].Template);
        Assert.Equal("DEL/{NUMBER:000000}", series["DELIVERY"].Template);
        Assert.Equal(NumberSeriesResetPolicy.Never, series["CUSTOMER"].ResetPolicy);
        Assert.Equal(NumberSeriesResetPolicy.Never, series["BRANCH"].ResetPolicy);
        Assert.Equal(NumberSeriesResetPolicy.Never, series["DELIVERY"].ResetPolicy);
        Assert.All(series.Values, item => Assert.True(item.IsActive));
    }

    [Fact]
    public async Task SeedService_UpgradesNeverUsedLegacyScopedOrderRows_LeavesUsedRowsUntouched()
    {
        await using var db = CreateDb();
        // A pre-existing scoped ORDER row on the legacy template with a live counter.
        var used = new NumberSeries(
            "ORDER",
            "One-time and subscription order numbers for branch MAIN",
            "ORD/MAIN/{NUMBER:000000}",
            startingNumber: 1,
            incrementBy: 1,
            NumberSeriesResetPolicy.Never,
            "MAIN");
        // Simulate a number already issued so the safe-edit guard would reject changes.
        used.NextNumber(
            NumberSeriesResetPolicy.Never,
            new DateOnly(2026, 8, 2),
            new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Unspecified),
            updatedByUserId: null);
        db.NumberSeries.Add(used);

        // A never-used scoped ORDER row on the legacy template — must be upgraded.
        db.NumberSeries.Add(new NumberSeries(
            "ORDER",
            "One-time and subscription order numbers for branch BLR",
            "ORD/BLR/{NUMBER:000000}",
            startingNumber: 1,
            incrementBy: 1,
            NumberSeriesResetPolicy.Never,
            "BLR"));
        await db.SaveChangesAsync();

        var seed = new NumberSeriesSeedService(db);
        await seed.SeedAsync(CancellationToken.None);

        var main = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "MAIN");
        var blr = await db.NumberSeries.SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "BLR");

        // Never-used legacy row is upgraded to the FY template + policy.
        Assert.Equal("ORD/BLR/{FY}/{NUMBER:000000}", blr.Template);
        Assert.Equal(NumberSeriesResetPolicy.FinancialYear, blr.ResetPolicy);

        // Used row is left completely untouched — counter and config preserved.
        Assert.Equal("ORD/MAIN/{NUMBER:000000}", main.Template);
        Assert.Equal(NumberSeriesResetPolicy.Never, main.ResetPolicy);
        Assert.True(main.LastUsedNumber >= 1);
    }

    // ---------------------------------------------------------------- helpers

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"number-series-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }
}
