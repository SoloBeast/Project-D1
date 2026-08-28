using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class MilkTestServiceTests
{
    [Fact]
    public async Task Request_enforces_customer_ownership_prevents_duplicates_and_writes_audits()
    {
        await using var harness = await MilkTestHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.RequestAsync(
            harness.CustomerActor(harness.OtherCustomer),
            harness.Delivery.PublicId,
            CancellationToken.None));

        var result = await harness.RequestAsync();

        Assert.Equal(MilkTestStatus.Requested, result.Status);
        Assert.Equal(MilkTestCustomerDecision.Pending, result.CustomerDecision);
        Assert.Empty(result.Images);
        await Assert.ThrowsAsync<ConflictException>(() => harness.RequestAsync());

        var actions = await harness.Db.AuditLogs
            .Where(x => x.EntityId == result.MilkTestId.ToString())
            .Select(x => x.Action)
            .OrderBy(x => x)
            .ToArrayAsync();
        Assert.Equal(["MILK_TEST.CREATE", "MILK_TEST.REQUEST"], actions);

        var notificationEvent = await harness.Db.NotificationEvents.SingleAsync();
        Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
        Assert.Equal(NotificationEventTypes.MilkTestRequested, notificationEvent.EventType);
        Assert.Equal($"milk-test:{result.MilkTestId:N}:requested", notificationEvent.EventKey);
        Assert.True(notificationEvent.IsCritical);
        Assert.Equal(result.RequestedAt, notificationEvent.OccurredAt);
        Assert.Equal($"/deliveries/{harness.Delivery.PublicId}/milk-test", Payload(notificationEvent).GetProperty("DeepLink").GetString());
        var variables = Variables(notificationEvent);
        Assert.Equal(result.MilkTestId.ToString(), variables.GetProperty("milkTestId").GetString());
        Assert.Equal(harness.Delivery.PublicId.ToString(), variables.GetProperty("deliveryId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_rejects_terminal_deliveries(bool delivered)
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        await harness.MakeTerminalAsync(delivered);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.RequestAsync());

        Assert.Equal("A doorstep test can only be requested for an active delivery.", exception.Message);
        Assert.Empty(await harness.Db.MilkTests.ToArrayAsync());
    }

    [Fact]
    public async Task Staff_access_requires_assignment_and_branch_unless_global_access_is_granted()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForStaffAsync(
            harness.StaffActor(harness.OtherStaff),
            harness.Delivery.PublicId,
            CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForStaffAsync(
            new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.OtherBranch.Id }, false),
            harness.Delivery.PublicId,
            CancellationToken.None));

        var globalResult = await harness.Service.GetForStaffAsync(
            new MilkTestActor(harness.Staff.Id, new HashSet<long>(), true),
            harness.Delivery.PublicId,
            CancellationToken.None);

        Assert.NotNull(globalResult);
        Assert.Equal(requested.MilkTestId, globalResult.MilkTestId);
    }

    [Fact]
    public async Task Upload_records_validated_metadata_uploader_and_versioned_content_path()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();

        var result = await harness.UploadAsync(requested.MilkTestId);

        Assert.Equal("proof.jpg", result.FileName);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(harness.ImageBytes.Length, result.FileSize);
        Assert.Equal($"/api/v1/milk-tests/{requested.MilkTestId}/images/{result.ImageId}/content", result.ContentPath);
        var image = await harness.Db.MilkTestImages.SingleAsync();
        Assert.Equal(harness.Staff.Id, image.UploadedByUserId);
        Assert.Contains($"/{harness.Branch.Id}/{requested.MilkTestId:N}/", image.StorageKey);
        Assert.Equal(harness.ImageBytes, harness.Storage.Files[image.StorageKey]);
        Assert.Equal("MILK_TEST.IMAGE_UPLOAD", (await harness.Db.AuditLogs.OrderBy(x => x.Id).LastAsync()).Action);
    }

    [Fact]
    public async Task Upload_and_completion_require_assigned_branch_authorized_staff()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.UploadImageAsync(
            harness.StaffActor(harness.OtherStaff),
            requested.MilkTestId,
            new MemoryStream(harness.ImageBytes, writable: false),
            "proof.jpg",
            "image/jpeg",
            CancellationToken.None));

        await harness.UploadAsync(requested.MilkTestId);
        await harness.AdvanceToArrivedAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.CompleteAsync(
            new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.OtherBranch.Id }, false),
            requested.MilkTestId,
            harness.CompletionRequest(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Upload_deletes_stored_content_when_persisted_size_does_not_match()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        harness.Storage.ReportedSizeOffset = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.UploadAsync(requested.MilkTestId));

        Assert.Empty(harness.Storage.Files);
        Assert.Single(harness.Storage.DeletedKeys);
        Assert.Empty(await harness.Db.MilkTestImages.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_rejects_terminal_deliveries(bool delivered)
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        await harness.MakeTerminalAsync(delivered);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.UploadAsync(requested.MilkTestId));

        Assert.Equal("Images cannot be added to a terminal delivery.", exception.Message);
        Assert.Empty(harness.Storage.Files);
        Assert.Empty(await harness.Db.MilkTestImages.ToArrayAsync());
    }

    [Fact]
    public async Task Upload_rejects_completed_tests_without_storing_another_image()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var completed = await harness.CreateCompletedAsync();
        var storedImageCount = harness.Storage.Files.Count;

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.UploadAsync(completed.MilkTestId));

        Assert.Equal("Images cannot be added after the doorstep test is completed.", exception.Message);
        Assert.Equal(storedImageCount, harness.Storage.Files.Count);
        Assert.Single(await harness.Db.MilkTestImages.ToArrayAsync());
    }

    [Fact]
    public async Task Completion_requires_arrival_image_and_at_least_one_valid_unique_reading()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var validRequest = harness.CompletionRequest();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, validRequest, CancellationToken.None));

        await harness.AdvanceToArrivedAsync();
        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, validRequest, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            requested.MilkTestId,
            new CompleteMilkTestRequest([], null),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            requested.MilkTestId,
            new CompleteMilkTestRequest(
            [
                new("FAT", "Fat", 4.1m, "%"),
                new("fat", "Fat duplicate", 4.2m, "%")
            ], null),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("1.0000001")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    public async Task Completion_rejects_values_outside_decimal_18_6(string rawValue)
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        await harness.UploadAsync(requested.MilkTestId);
        await harness.AdvanceToArrivedAsync();
        var request = new CompleteMilkTestRequest(
            [new MilkTestParameterRequest("FAT", "Fat", decimal.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture), "%")],
            null);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, request, CancellationToken.None));

        Assert.Equal("value", exception.Field);
    }

    [Fact]
    public async Task Completion_accepts_six_decimal_places_and_decimal_18_6_magnitude_boundary()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        await harness.UploadAsync(requested.MilkTestId);
        await harness.AdvanceToArrivedAsync();
        var request = new CompleteMilkTestRequest(
        [
            new MilkTestParameterRequest("FAT", "Fat", 4.123456m, "%"),
            new MilkTestParameterRequest("LIMIT", "Device limit", 999999999999.999999m, "unit")
        ], null);

        var result = await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, request, CancellationToken.None);

        Assert.Equal(MilkTestStatus.Completed, result.Status);
        Assert.Equal(4.123456m, result.Parameters.Single(x => x.Code == "FAT").Value);
        Assert.Equal(999999999999.999999m, result.Parameters.Single(x => x.Code == "LIMIT").Value);
    }

    [Fact]
    public async Task Completion_exposes_readings_to_staff_but_not_customer_and_releases_images()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);
        var beforeCompletion = await harness.Service.GetForCustomerAsync(
            harness.CustomerActor(harness.Customer), harness.Delivery.PublicId, CancellationToken.None);
        Assert.NotNull(beforeCompletion);
        Assert.Empty(beforeCompletion.Images);
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.Customer), requested.MilkTestId, image.ImageId, CancellationToken.None));

        await harness.AdvanceToArrivedAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var staffResult = await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            requested.MilkTestId,
            harness.CompletionRequest(),
            CancellationToken.None);
        var customerResult = await harness.Service.GetForCustomerAsync(
            harness.CustomerActor(harness.Customer), harness.Delivery.PublicId, CancellationToken.None);

        Assert.Single(staffResult.Parameters);
        Assert.Equal(4.2m, staffResult.Parameters.Single().Value);
        Assert.NotNull(customerResult);
        Assert.Single(customerResult.Images);
        Assert.NotNull(customerResult.CompletedAt);

        var notificationEvent = await harness.Db.NotificationEvents.SingleAsync(
            x => x.EventType == NotificationEventTypes.MilkTestCompleted);
        Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
        Assert.Equal($"milk-test:{requested.MilkTestId:N}:completed", notificationEvent.EventKey);
        Assert.True(notificationEvent.IsCritical);
        Assert.Equal(staffResult.CompletedAt, notificationEvent.OccurredAt);
        Assert.Equal($"/deliveries/{harness.Delivery.PublicId}/milk-test", Payload(notificationEvent).GetProperty("DeepLink").GetString());
        var variables = Variables(notificationEvent);
        Assert.Equal(requested.MilkTestId.ToString(), variables.GetProperty("milkTestId").GetString());
        Assert.Equal(harness.Delivery.PublicId.ToString(), variables.GetProperty("deliveryId").GetString());
        Assert.Equal("1", variables.GetProperty("readingCount").GetString());
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.OtherCustomer), requested.MilkTestId, image.ImageId, CancellationToken.None));
        await using var content = await harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.Customer), requested.MilkTestId, image.ImageId, CancellationToken.None);
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy);
        Assert.Equal(harness.ImageBytes, copy.ToArray());
        Assert.Contains("MILK_TEST.COMPLETE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Theory]
    [InlineData(true, MilkTestCustomerDecision.Confirmed, "MILK_TEST.CONFIRM")]
    [InlineData(false, MilkTestCustomerDecision.Rejected, "MILK_TEST.REJECT")]
    public async Task Customer_decision_is_owned_audited_and_terminal(
        bool confirm,
        MilkTestCustomerDecision expectedDecision,
        string expectedAudit)
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var completed = await harness.CreateCompletedAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var request = new DecideMilkTestRequest("  customer note  ");

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.ConfirmAsync(
            harness.CustomerActor(harness.OtherCustomer), completed.MilkTestId, request, CancellationToken.None));
        var result = confirm
            ? await harness.Service.ConfirmAsync(harness.CustomerActor(harness.Customer), completed.MilkTestId, request, CancellationToken.None)
            : await harness.Service.RejectAsync(harness.CustomerActor(harness.Customer), completed.MilkTestId, request, CancellationToken.None);

        Assert.Equal(expectedDecision, result.CustomerDecision);
        Assert.Equal("customer note", result.CustomerRemarks);
        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.ConfirmAsync(
            harness.CustomerActor(harness.Customer), completed.MilkTestId, request, CancellationToken.None));
        Assert.Contains(expectedAudit, await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task DeleteImage_removes_only_the_selected_image_and_audits_without_ef_conceptual_null()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var first = await harness.UploadAsync(requested.MilkTestId);
        var second = await harness.UploadAsync(requested.MilkTestId);

        var result = await harness.Service.DeleteImageAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, first.ImageId, CancellationToken.None);

        Assert.Single(result.Images);
        Assert.Equal(second.ImageId, result.Images.Single().ImageId);
        Assert.Equal([second.ImageId], await harness.Db.MilkTestImages.Select(x => x.PublicId).ToArrayAsync());
        Assert.Single(harness.Storage.DeletedKeys);
        Assert.Equal([second.ImageId], await harness.Db.MilkTestImages.Select(x => x.PublicId).ToArrayAsync());
        Assert.Contains("MILK_TEST.IMAGE_DELETE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task DeleteImage_removes_the_only_image_row_and_blob_after_commit()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);
        var storageKey = (await harness.Db.MilkTestImages.SingleAsync()).StorageKey;

        var result = await harness.Service.DeleteImageAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, image.ImageId, CancellationToken.None);

        Assert.Empty(result.Images);
        Assert.Empty(await harness.Db.MilkTestImages.ToArrayAsync());
        Assert.Empty(harness.Storage.Files);
        Assert.Equal([storageKey], harness.Storage.DeletedKeys);
        Assert.Contains("MILK_TEST.IMAGE_DELETE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task DeleteImage_does_not_delete_blob_when_database_mutation_fails()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);
        var storageKey = (await harness.Db.MilkTestImages.SingleAsync()).StorageKey;
        harness.Db.MilkTests.Add(new MilkTest(
            harness.Delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Customer.Id, harness.Clock.Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Service.DeleteImageAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, image.ImageId, CancellationToken.None));

        Assert.Empty(harness.Storage.DeletedKeys);
        Assert.Single(harness.Storage.Files);
        Assert.Equal(storageKey, (await harness.Db.MilkTestImages.SingleAsync()).StorageKey);
        Assert.DoesNotContain("MILK_TEST.IMAGE_DELETE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task ReplaceImage_removes_old_row_creates_new_row_and_audits_without_ef_conceptual_null()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var original = await harness.UploadAsync(requested.MilkTestId);
        var originalKey = (await harness.Db.MilkTestImages.SingleAsync()).StorageKey;

        var replaced = await harness.Service.ReplaceImageAsync(
            harness.StaffActor(harness.Staff),
            requested.MilkTestId,
            original.ImageId,
            new MemoryStream(harness.ImageBytes, writable: false),
            "replacement.jpg",
            "image/jpeg",
            CancellationToken.None);

        Assert.NotEqual(original.ImageId, replaced.ImageId);
        var row = Assert.Single(await harness.Db.MilkTestImages.ToArrayAsync());
        Assert.Equal(replaced.ImageId, row.PublicId);
        Assert.Contains(originalKey, harness.Storage.DeletedKeys);
        Assert.Contains("MILK_TEST.IMAGE_REPLACE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task ReplaceImage_does_not_delete_old_blob_or_persist_when_database_mutation_fails()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var original = await harness.UploadAsync(requested.MilkTestId);
        var originalKey = (await harness.Db.MilkTestImages.SingleAsync()).StorageKey;
        harness.Db.MilkTests.Add(new MilkTest(
            harness.Delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Customer.Id, harness.Clock.Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Service.ReplaceImageAsync(
            harness.StaffActor(harness.Staff),
            requested.MilkTestId,
            original.ImageId,
            new MemoryStream(harness.ImageBytes, writable: false),
            "replacement.jpg",
            "image/jpeg",
            CancellationToken.None));

        Assert.DoesNotContain(originalKey, harness.Storage.DeletedKeys);
        var remaining = Assert.Single(await harness.Db.MilkTestImages.ToArrayAsync());
        Assert.Equal(originalKey, remaining.StorageKey);
        Assert.DoesNotContain("MILK_TEST.IMAGE_REPLACE", await harness.Db.AuditLogs.Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task OpenImage_serves_content_to_assigned_staff_on_requested_test()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);

        await using var content = await harness.Service.OpenImageAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, image.ImageId, CancellationToken.None);
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy);

        Assert.Equal(harness.ImageBytes, copy.ToArray());
    }

    [Fact]
    public async Task OpenImage_denies_unassigned_or_branch_mismatched_staff()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            harness.StaffActor(harness.OtherStaff), requested.MilkTestId, image.ImageId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.OtherBranch.Id }, false),
            requested.MilkTestId,
            image.ImageId,
            CancellationToken.None));
    }

    [Fact]
    public async Task OpenImage_serves_content_to_customer_owner_and_assigned_staff_after_completion()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        var requested = await harness.RequestAsync();
        var image = await harness.UploadAsync(requested.MilkTestId);
        await harness.AdvanceToArrivedAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, harness.CompletionRequest(), CancellationToken.None);

        await using var staffContent = await harness.Service.OpenImageAsync(
            harness.StaffActor(harness.Staff), requested.MilkTestId, image.ImageId, CancellationToken.None);
        await using var customerContent = await harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.Customer), requested.MilkTestId, image.ImageId, CancellationToken.None);

        foreach (var content in new[] { staffContent, customerContent })
        {
            using var copy = new MemoryStream();
            await content.Content.CopyToAsync(copy);
            Assert.Equal(harness.ImageBytes, copy.ToArray());
        }
    }

    [Fact]
    public async Task Database_enforces_one_test_per_delivery()
    {
        await using var harness = await MilkTestHarness.CreateAsync();
        await harness.RequestAsync();
        harness.Db.MilkTests.Add(new MilkTest(
            harness.Delivery.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            harness.Customer.Id,
            harness.Clock.Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Db.SaveChangesAsync());
    }

    private sealed class MilkTestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private MilkTestHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            CapturingMediaStorage storage,
            MilkTestService service,
            User customer,
            User otherCustomer,
            User staff,
            User otherStaff,
            Branch branch,
            Branch otherBranch,
            Delivery delivery)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            Storage = storage;
            Service = service;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Staff = staff;
            OtherStaff = otherStaff;
            Branch = branch;
            OtherBranch = otherBranch;
            Delivery = delivery;
        }

        public byte[] ImageBytes { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];
        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public CapturingMediaStorage Storage { get; }
        public MilkTestService Service { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Staff { get; }
        public User OtherStaff { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public Delivery Delivery { get; }

        public MilkTestActor CustomerActor(User customer) => new(customer.Id, new HashSet<long>(), false);
        public MilkTestActor StaffActor(User staff) => new(staff.Id, new HashSet<long> { Branch.Id }, false);

        public Task<CustomerMilkTestResult> RequestAsync() => Service.RequestAsync(
            CustomerActor(Customer), Delivery.PublicId, CancellationToken.None);

        public Task<MilkTestImageResult> UploadAsync(Guid milkTestId) => Service.UploadImageAsync(
            StaffActor(Staff),
            milkTestId,
            new MemoryStream(ImageBytes, writable: false),
            "proof.jpg",
            "image/jpeg",
            CancellationToken.None);

        public CompleteMilkTestRequest CompletionRequest() => new(
            [new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")],
            "Test completed");

        public async Task AdvanceToArrivedAsync()
        {
            Delivery.PickUp(Staff.Id, Clock.Now, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.Start(Staff.Id, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.Arrive(Staff.Id, Clock.Now);
            await Db.SaveChangesAsync();
        }

        public async Task MakeTerminalAsync(bool delivered)
        {
            if (delivered)
            {
                await AdvanceToArrivedAsync();
                Clock.Advance(TimeSpan.FromMinutes(1));
                Delivery.RecordOtpVerified(Staff.Id, Clock.Now);
                Clock.Advance(TimeSpan.FromMinutes(1));
                Delivery.Complete(Staff.Id, Clock.Now, null);
            }
            else
            {
                Delivery.Fail(
                    Staff.Id,
                    Clock.Now,
                    DeliveryFailureReasons.CustomerNotAvailable,
                    null,
                    null,
                    null);
            }
            await Db.SaveChangesAsync();
        }

        public async Task<StaffMilkTestResult> CreateCompletedAsync()
        {
            var requested = await RequestAsync();
            await UploadAsync(requested.MilkTestId);
            await AdvanceToArrivedAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            return await Service.CompleteAsync(
                StaffActor(Staff), requested.MilkTestId, CompletionRequest(), CancellationToken.None);
        }

        public static async Task<MilkTestHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = CreateUser(UserType.Customer, "Customer", "9999999999");
            var otherCustomer = CreateUser(UserType.Customer, "Other Customer", "9999999998");
            var staff = CreateUser(UserType.Employee, "Assigned Staff", "9000000001");
            var otherStaff = CreateUser(UserType.Employee, "Other Staff", "9000000002");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var otherBranch = new Branch("NORTH", "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
            db.AddRange(customer, otherCustomer, staff, otherStaff, branch, otherBranch);
            await db.SaveChangesAsync();

            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.CustomerAddresses.Add(address);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id,
                address.Id,
                branch.Id,
                "milk-test-order-1",
                "ORD-MILK-001",
                80m,
                0m,
                branch.Code,
                branch.Name,
                address.Label,
                address.AddressLine1,
                address.AddressLine2,
                address.Locality,
                address.City,
                address.State,
                address.PinCode,
                address.Landmark,
                address.DeliveryInstructions,
                address.ContactName,
                address.ContactMobile,
                address.Latitude,
                address.Longitude);
            order.ConfirmPayment();
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var now = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);
            var delivery = Delivery.ForOrder(
                order.Id,
                customer.Id,
                branch.Id,
                DateOnly.FromDateTime(now),
                order.OrderNumber,
                "Customer",
                customer.Mobile!,
                "1 Main Road, Central, Bengaluru, Karnataka 560001",
                null,
                address.Latitude,
                address.Longitude);
            delivery.Assign(staff.Id, staff.Id, now, null);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            var clock = new TestClock(now.AddMinutes(1));
            var storage = new CapturingMediaStorage();
            var service = new MilkTestService(
                db,
                clock,
                storage,
                new DeterministicImageValidator(),
                new TestNotificationEventWriter(db, clock));
            return new MilkTestHarness(
                connection,
                db,
                clock,
                storage,
                service,
                customer,
                otherCustomer,
                staff,
                otherStaff,
                branch,
                otherBranch,
                delivery);
        }

        private static User CreateUser(UserType type, string name, string mobile)
        {
            var user = new User(type);
            user.SetProfile(name);
            user.SetContact(mobile, null);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static JsonElement Payload(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    private sealed class DeterministicImageValidator : IMilkTestImageValidator
    {
        public long MaximumFileSize => 1024;

        public async Task<ValidatedMilkTestImage> ValidateAsync(
            Stream content,
            string fileName,
            string? declaredContentType,
            CancellationToken cancellationToken)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;
            return new ValidatedMilkTestImage("proof.jpg", "image/jpeg", buffered.Length, buffered);
        }
    }

    private sealed class CapturingMediaStorage : IMediaStorage
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> DeletedKeys { get; } = [];
        public long ReportedSizeOffset { get; set; }

        public async Task<StoredMediaResult> SaveAsync(
            string storageKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            using var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken);
            Files.Add(storageKey, buffered.ToArray());
            return new StoredMediaResult(storageKey, contentType, buffered.Length + ReportedSizeOffset);
        }

        public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        {
            if (!Files.TryGetValue(storageKey, out var content))
            {
                throw new NotFoundException("The media was not found.");
            }
            return Task.FromResult(new StoredMediaContent(
                new MemoryStream(content, writable: false),
                "image/jpeg",
                content.Length));
        }

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
        {
            Files.Remove(storageKey);
            DeletedKeys.Add(storageKey);
            return Task.CompletedTask;
        }
    }
}
