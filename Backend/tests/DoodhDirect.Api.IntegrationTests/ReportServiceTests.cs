using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Reports;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Reports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ReportServiceTests
{
    [Fact]
    public async Task All_report_queries_translate_on_sqlite()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var actor = new ReportActor(1, [], true);
        var filter = new ReportFilter();

        await harness.Service.GetDashboardAsync(actor, new DashboardRequest(), CancellationToken.None);
        await harness.Service.GetCustomersAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetEmployeesAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetOrdersAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetSubscriptionsAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetPaymentsAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetWalletsAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetDeliveriesAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetDairyAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetMilkTestsAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetCamerasAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetNotificationsAsync(actor, filter, CancellationToken.None);
        await harness.Service.GetAuditAsync(actor, filter, CancellationToken.None);
    }

    [Fact]
    public async Task Csv_export_uses_clock_utf8_bom_and_bounded_empty_result()
    {
        await using var harness = await ReportHarness.CreateAsync();

        var result = await harness.Service.ExportAsync(
            new ReportActor(1, [], true),
            new ExportRequest(ReportModule.Customers, new ReportFilter(), ReportExportFormat.Csv),
            CancellationToken.None);

        Assert.Equal("customers-20260817120000.csv", result.FileName);
        Assert.Equal("text/csv; charset=utf-8", result.ContentType);
        Assert.Equal(0, result.RowCount);
        Assert.Equal([0xEF, 0xBB, 0xBF], result.Content.Take(3));
    }

    [Fact]
    public void Csv_export_neutralizes_formulas_and_formats_collections()
    {
        var row = new EmployeeReportRow(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "  =SUM(1,1)",
            "+919999999999",
            "employee@example.test",
            true,
            ["OWNER", "ACCOUNTANT"],
            [
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
            ],
            3,
            2);

        var content = ReportTabularExporter.Csv(
            ReportModule.Employees,
            [row]);
        var csv = Encoding.UTF8.GetString(content.AsSpan(3));

        Assert.StartsWith(
            "Id,DisplayName,Mobile,Email,IsActive,Roles,BranchIds,AssignedDeliveries,CompletedDeliveries",
            csv,
            StringComparison.Ordinal);
        Assert.Contains("\"'  =SUM(1,1)\"", csv, StringComparison.Ordinal);
        Assert.Contains("'+919999999999", csv, StringComparison.Ordinal);
        Assert.Contains("OWNER; ACCOUNTANT", csv, StringComparison.Ordinal);
        Assert.Contains(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb; cccccccc-cccc-cccc-cccc-cccccccccccc",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_csv_export_contains_the_module_schema()
    {
        var content = ReportTabularExporter.Csv(
            ReportModule.Customers,
            []);
        var csv = Encoding.UTF8.GetString(content.AsSpan(3));

        Assert.Equal(
            "Id,DisplayName,Mobile,Email,IsActive,CreatedAtUtc,OrderCount,LifetimeOrderValue,WalletBalance" +
            Environment.NewLine,
            csv);
    }

    [Fact]
    public void Xlsx_export_is_a_valid_minimal_package_with_typed_cells()
    {
        var row = new CustomerReportRow(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Customer & Co",
            null,
            "customer@example.test",
            true,
            new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc),
            4,
            125.50m,
            20m);

        var content = ReportTabularExporter.Xlsx(
            ReportModule.Customers,
            [row]);
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/_rels/workbook.xml.rels"));
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(worksheet);
        using var worksheetStream = worksheet.Open();
        var document = XDocument.Load(worksheetStream);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(spreadsheet + "row").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains(
            rows[0].Descendants(spreadsheet + "t"),
            x => x.Value == "DisplayName");
        Assert.Contains(
            rows[1].Descendants(spreadsheet + "t"),
            x => x.Value == "Customer & Co");
        Assert.Contains(
            rows[1].Elements(spreadsheet + "c"),
            x => (string?)x.Attribute("t") == "b" &&
                 x.Element(spreadsheet + "v")?.Value == "1");
        Assert.Contains(
            rows[1].Elements(spreadsheet + "c"),
            x => x.Element(spreadsheet + "v")?.Value == "125.50");
    }

    [Fact]
    public async Task Xlsx_service_export_uses_expected_metadata_and_empty_schema()
    {
        await using var harness = await ReportHarness.CreateAsync();

        var result = await harness.Service.ExportAsync(
            new ReportActor(1, [], true),
            new ExportRequest(
                ReportModule.Customers,
                new ReportFilter(),
                ReportExportFormat.Xlsx),
            CancellationToken.None);

        Assert.Equal("customers-20260817120000.xlsx", result.FileName);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            result.ContentType);
        Assert.Equal(0, result.RowCount);
        using var stream = new MemoryStream(result.Content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public async Task Branch_scoped_actor_requires_at_least_one_branch()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var actor = new ReportActor(1, [], false);

        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.Service.GetOrdersAsync(actor, new ReportFilter(), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 251)]
    public async Task Invalid_report_paging_is_a_validation_error(
        int page,
        int pageSize)
    {
        await using var harness = await ReportHarness.CreateAsync();
        var filter = new ReportFilter(Page: page, PageSize: pageSize);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetOrdersAsync(
                new ReportActor(1, [], true),
                filter,
                CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_report_dates_and_statuses_are_validation_errors()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var actor = new ReportActor(1, [], true);
        var reversedRange = new ReportDateRange(
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));
        var localRange = new ReportDateRange(
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Local),
            null);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(DateRange: reversedRange),
                CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(DateRange: localRange),
                CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(Statuses: ["not-an-order-status"]),
                CancellationToken.None));
    }

    [Fact]
    public async Task Customer_sort_is_case_insensitive_directional_and_stable_across_pages()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var createdAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var alpha = Customer("Alpha", createdAt);
        var betaFirst = Customer("Beta", createdAt);
        var betaSecond = Customer("Beta", createdAt);
        harness.Db.Users.AddRange(alpha, betaFirst, betaSecond);
        await harness.Db.SaveChangesAsync();
        var actor = new ReportActor(1, [], true);

        var ascending = await harness.Service.GetCustomersAsync(
            actor,
            new ReportFilter(
                PageSize: 3,
                SortBy: " DISPLAYNAME ",
                SortDirection: ReportSortDirection.Ascending),
            CancellationToken.None);
        var descendingFirstPage = await harness.Service.GetCustomersAsync(
            actor,
            new ReportFilter(
                PageSize: 1,
                SortBy: "displayName",
                SortDirection: ReportSortDirection.Descending),
            CancellationToken.None);
        var descendingSecondPage = await harness.Service.GetCustomersAsync(
            actor,
            new ReportFilter(
                Page: 2,
                PageSize: 1,
                SortBy: "displayName",
                SortDirection: ReportSortDirection.Descending),
            CancellationToken.None);

        Assert.Equal([alpha.PublicId, betaFirst.PublicId, betaSecond.PublicId], ascending.Items.Select(x => x.Id));
        Assert.Equal(betaSecond.PublicId, Assert.Single(descendingFirstPage.Items).Id);
        Assert.Equal(betaFirst.PublicId, Assert.Single(descendingSecondPage.Items).Id);
        Assert.True(descendingFirstPage.HasNextPage);
    }

    [Fact]
    public async Task Every_report_rejects_an_unsupported_sort_field()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var actor = new ReportActor(1, [], true);
        var filter = new ReportFilter(SortBy: "not-a-report-field");
        Func<Task>[] queries =
        [
            async () => await harness.Service.GetCustomersAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetEmployeesAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetOrdersAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetSubscriptionsAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetPaymentsAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetWalletsAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetDeliveriesAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetDairyAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetMilkTestsAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetCamerasAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetNotificationsAsync(actor, filter, CancellationToken.None),
            async () => await harness.Service.GetAuditAsync(actor, filter, CancellationToken.None)
        ];

        foreach (var query in queries)
        {
            var exception = await Assert.ThrowsAsync<ValidationAppException>(query);
            Assert.Equal(nameof(ReportFilter.SortBy), exception.Field);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_sort_fields_are_validation_errors(string sortBy)
    {
        await using var harness = await ReportHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetCustomersAsync(
                new ReportActor(1, [], true),
                new ReportFilter(SortBy: sortBy),
                CancellationToken.None));
    }

    [Fact]
    public async Task Undefined_sort_direction_is_a_validation_error()
    {
        await using var harness = await ReportHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetCustomersAsync(
                new ReportActor(1, [], true),
                new ReportFilter(SortDirection: (ReportSortDirection)99),
                CancellationToken.None));
    }

    [Fact]
    public async Task Dashboard_uses_the_same_date_validation_as_reports()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var actor = new ReportActor(1, [], true);
        var request = new DashboardRequest(new ReportDateRange(
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc)));

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetDashboardAsync(
                actor,
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task Requested_branches_must_exist_and_be_fully_authorized()
    {
        await using var harness = await ReportHarness.CreateAsync();
        var main = new Branch(
            "MAIN",
            "Main Branch",
            "Bengaluru",
            "Karnataka",
            12.9716m,
            77.5946m);
        var north = new Branch(
            "NORTH",
            "North Branch",
            "Bengaluru",
            "Karnataka",
            13.0358m,
            77.5970m);
        harness.Db.Branches.AddRange(main, north);
        await harness.Db.SaveChangesAsync();
        var actor = new ReportActor(1, [main.Id], false);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(BranchIds: [Guid.NewGuid()]),
                CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(BranchIds: [north.PublicId]),
                CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            harness.Service.GetOrdersAsync(
                actor,
                new ReportFilter(BranchIds: [main.PublicId, north.PublicId]),
                CancellationToken.None));
    }

    private static User Customer(string displayName, DateTime createdAtUtc)
    {
        var customer = new User(UserType.Customer);
        customer.SetProfile(displayName);
        customer.SetContact(null, $"{displayName.ToLowerInvariant()}-{Guid.NewGuid():N}@example.test");
        customer.SetCreated(createdAtUtc);
        return customer;
    }

    private sealed class ReportHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ReportHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            ReportService service)
        {
            this.connection = connection;
            Db = db;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public ReportService Service { get; }

        public static async Task<ReportHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var clock = new ReportTestClock(
                new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc));

            return new ReportHarness(
                connection,
                db,
                new ReportService(db, clock));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReportTestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
