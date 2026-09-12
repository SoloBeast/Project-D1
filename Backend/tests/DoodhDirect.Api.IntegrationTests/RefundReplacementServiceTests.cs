using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Integration tests for the customer refund/replacement workflow. They pin the
/// server-authoritative behaviour: ownership, delivered-state, the configured
/// post-delivery window (deadline = delivery completion + window hours), the
/// milk-test-rejection special flow (exempt from the window and from the proof
/// image), the one-active-request rule, branch-scoped staff access, the proof
/// image, audit and notification side effects.
/// </summary>
public sealed class RefundReplacementServiceTests
{
    // ------------------------------------------------------------- configuration

    [Fact]
    public async Task Configuration_returns_default_window_when_unset()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        var result = await harness.ConfigurationService.GetAsync(CancellationToken.None);

        Assert.Equal(48, result.WindowHours);
        Assert.Equal(RefundReplacementConfigurationService.DefaultWindowHours, result.WindowHours);
        Assert.Equal("Configured", result.Status);
    }

    [Fact]
    public async Task Configuration_update_persists_window_and_writes_audit()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        var updated = await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(24),
            harness.Staff.Id,
            "127.0.0.1",
            "xunit",
            CancellationToken.None);

        Assert.Equal(24, updated.WindowHours);
        Assert.Equal(24, (await harness.ConfigurationService.GetAsync(CancellationToken.None)).WindowHours);

        var audit = await harness.Db.AuditLogs.SingleAsync();
        Assert.Equal(RefundReplacementConfigurationService.ActionConfigUpdated, audit.Action);
        Assert.Equal("RefundReplacementConfiguration", audit.EntityType);
        Assert.Equal("RequestWindow", audit.EntityId);
        Assert.Equal(harness.Staff.Id, audit.UserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8761)]
    public async Task Configuration_update_rejects_out_of_range_window(int windowHours)
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(
            () => harness.ConfigurationService.UpdateAsync(
                new UpdateRefundReplacementConfigurationRequest(windowHours),
                harness.Staff.Id,
                null,
                null,
                CancellationToken.None));

        Assert.Equal(
            "The refund/replacement request window must be between 1 and 8760 hours.",
            exception.Message);
        Assert.Empty(await harness.Db.AuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task Configuration_update_with_null_keeps_existing_window()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(12),
            harness.Staff.Id,
            null,
            null,
            CancellationToken.None);

        var result = await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null),
            harness.Staff.Id,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(12, result.WindowHours);
    }

    // ---------------------------------------------------------------- eligibility

    [Fact]
    public async Task Eligibility_enforces_customer_ownership()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => harness.Service.GetEligibilityAsync(
                harness.CustomerActor(harness.OtherCustomer),
                harness.Delivery.PublicId,
                CancellationToken.None));

        Assert.Equal("The delivery was not found.", exception.Message);
    }

    [Fact]
    public async Task Eligibility_reports_completion_required_before_delivery()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();

        var result = await harness.EligibilityAsync();

        Assert.False(result.IsEligible);
        Assert.Equal(
            "Refund or replacement requests are only available after the delivery is completed.",
            result.IneligibleReason);
        Assert.True(result.ProofImageRequired);
        Assert.False(result.IsMilkTestRejectedFlow);
    }

    [Fact]
    public async Task Eligibility_is_eligible_within_window_and_exposes_server_deadline()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var result = await harness.EligibilityAsync();

        Assert.True(result.IsEligible);
        Assert.Null(result.IneligibleReason);
        Assert.Equal(48, result.WindowHours);
        Assert.False(result.HasActiveRequest);
        Assert.True(result.ProofImageRequired);
        Assert.False(result.IsMilkTestRejectedFlow);
        Assert.Equal(
            harness.Delivery.CompletedAt!.Value.AddHours(48),
            result.Deadline);
    }

    [Fact]
    public async Task Eligibility_expires_at_the_configured_deadline()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        harness.Clock.Advance(TimeSpan.FromHours(48));

        var result = await harness.EligibilityAsync();

        Assert.False(result.IsEligible);
        Assert.Equal(
            "The refund or replacement request window of 48 hours has closed.",
            result.IneligibleReason);
    }

    [Fact]
    public async Task Eligibility_stays_eligible_just_before_the_deadline()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        harness.Clock.Advance(TimeSpan.FromHours(47) + TimeSpan.FromMinutes(59));

        var result = await harness.EligibilityAsync();

        Assert.True(result.IsEligible);
    }

    [Fact]
    public async Task Eligibility_reports_active_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        await harness.SubmitAsync();

        var result = await harness.EligibilityAsync();

        Assert.False(result.IsEligible);
        Assert.True(result.HasActiveRequest);
        Assert.Equal(
            "An active refund or replacement request already exists for this delivery.",
            result.IneligibleReason);
    }

    [Fact]
    public async Task Eligibility_milk_test_rejection_unlocks_flow_and_waives_proof()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.CreateRejectedMilkTestOnFailedDeliveryAsync();
        harness.Clock.Advance(TimeSpan.FromHours(48)); // normal post-delivery window is irrelevant

        var result = await harness.EligibilityAsync();

        Assert.True(result.IsEligible);
        Assert.Null(result.IneligibleReason);
        Assert.True(result.IsMilkTestRejectedFlow);
        Assert.False(result.ProofImageRequired);
    }

    [Fact]
    public async Task Eligibility_rejected_test_on_delivered_delivery_uses_normal_flow()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        await harness.CreateRejectedMilkTestAsync();

        var result = await harness.EligibilityAsync();

        Assert.False(result.IsMilkTestRejectedFlow);
        Assert.True(result.IsEligible);
        Assert.True(result.ProofImageRequired);
        Assert.NotNull(result.Deadline);
    }

    // --------------------------------------------------------------------- submit

    [Fact]
    public async Task Submit_creates_pending_request_with_number_notification_and_audit()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var result = await harness.SubmitAsync(remarks: "Please review");

        Assert.Equal("RR/000001", result.RequestNumber);
        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.Equal(RefundReplacementSource.CustomerPostDelivery, result.Source);
        Assert.Equal(RefundReplacementType.Refund, result.Type);
        Assert.True(result.ProofImageRequired);
        Assert.Null(result.MilkTestId);
        Assert.Equal(harness.Delivery.CompletedAt!.Value.AddHours(48), result.Deadline);
        Assert.Equal(harness.Customer.Id, result.CustomerId);
        Assert.Equal(harness.Branch.Id, result.BranchId);

        var actions = await AuditActionsAsync(harness, result.RequestId);
        Assert.Equal(["REFUND_REPLACEMENT.SUBMIT"], actions);

        var notificationEvent = await harness.Db.NotificationEvents.SingleAsync();
        Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
        Assert.Equal(NotificationEventTypes.RefundReplacementSubmitted, notificationEvent.EventType);
        Assert.Equal($"refund-replacement:{result.RequestId:N}:submitted", notificationEvent.EventKey);
        Assert.False(notificationEvent.IsCritical);
        Assert.Equal($"/refund-replacements/{result.RequestId}", DeepLink(notificationEvent));
        var variables = Variables(notificationEvent);
        Assert.Equal(result.RequestId.ToString(), variables.GetProperty("requestId").GetString());
        Assert.Equal("RR/000001", variables.GetProperty("requestNumber").GetString());
        Assert.Equal("Pending", variables.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submit_allocates_next_number_after_a_terminal_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var first = await harness.SubmitAsync();
        await harness.Service.RejectAsync(
            harness.StaffActor(harness.Staff),
            first.RequestId,
            new DecideRefundReplacementRequest("Not accepted"),
            CancellationToken.None);

        var second = await harness.SubmitAsync();

        Assert.Equal("RR/000001", first.RequestNumber);
        Assert.Equal("RR/000002", second.RequestNumber);
    }

    [Fact]
    public async Task Submit_rejects_request_after_window_closes()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        harness.Clock.Advance(TimeSpan.FromHours(48));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.SubmitAsync());

        Assert.Equal(
            "The refund or replacement request window of 48 hours has closed.",
            exception.Message);
        Assert.Empty(await harness.Db.RefundReplacementRequests.ToArrayAsync());
    }

    [Fact]
    public async Task Submit_requires_reason()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(
            () => harness.SubmitAsync(reason: "   "));

        Assert.Equal("reason is required.", exception.Message);
    }

    [Fact]
    public async Task Submit_rejects_overlong_reason()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(
            () => harness.SubmitAsync(reason: new string('a', 1001)));

        Assert.Equal("reason cannot exceed 1000 characters.", exception.Message);
    }

    [Fact]
    public async Task Submit_rejects_duplicate_active_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        await harness.SubmitAsync();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => harness.SubmitAsync());

        Assert.Equal(
            "An active refund or replacement request already exists for this delivery.",
            exception.Message);
        Assert.Single(await harness.Db.RefundReplacementRequests.ToArrayAsync());
    }

    [Fact]
    public async Task Submit_autolinks_latest_rejected_milk_test()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        var milkTest = await harness.CreateRejectedMilkTestOnFailedDeliveryAsync();

        var result = await harness.SubmitAsync();

        Assert.Equal(RefundReplacementSource.MilkTestRejected, result.Source);
        Assert.Equal(milkTest.Id, result.MilkTestId);
        Assert.Equal(milkTest.PublicId, result.MilkTestPublicId);
        Assert.False(result.ProofImageRequired);
        Assert.Null(result.Deadline);
    }

    [Fact]
    public async Task Submit_milk_test_rejection_creates_pending_request_without_deadline_or_proof()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        var milkTest = await harness.CreateRejectedMilkTestOnFailedDeliveryAsync(
            "Milk test result not acceptable.");

        var result = await harness.SubmitAsync(milkTestId: milkTest.PublicId);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.Equal(RefundReplacementSource.MilkTestRejected, result.Source);
        Assert.Equal(milkTest.Id, result.MilkTestId);
        Assert.Null(result.Deadline);
        Assert.False(result.ProofImageRequired);
        Assert.Equal(harness.Delivery.PublicId, result.DeliveryId);
        Assert.Equal(harness.Order.Id, result.OrderId);
    }

    [Fact]
    public async Task Submit_requires_rejected_milk_test_when_referenced()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var milkTest = await harness.CreateCompletedMilkTestAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.SubmitAsync(milkTestId: milkTest.PublicId));

        Assert.Equal(
            "The refund/replacement special flow requires a customer-rejected doorstep milk test.",
            exception.Message);
    }

    [Fact]
    public async Task Submit_rejects_unknown_milk_test_reference()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => harness.SubmitAsync(milkTestId: Guid.NewGuid()));

        Assert.Equal("The doorstep test was not found.", exception.Message);
    }

    [Fact]
    public async Task Submit_enforces_customer_ownership()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => harness.Service.SubmitAsync(
                harness.CustomerActor(harness.OtherCustomer),
                new SubmitRefundReplacementRequest(
                    harness.Delivery.PublicId,
                    RefundReplacementType.Refund,
                    "Not mine",
                    null),
                CancellationToken.None));

        Assert.Equal("The delivery was not found.", exception.Message);
    }

    // ------------------------------------------------------------- customer views

    [Fact]
    public async Task Customer_list_returns_own_requests_most_recent_first()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();

        var first = await harness.SubmitAsync();
        await harness.Service.RejectAsync(
            harness.StaffActor(harness.Staff),
            first.RequestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await harness.SubmitAsync();

        var mine = await harness.Service.ListForCustomerAsync(
            harness.CustomerActor(harness.Customer),
            CancellationToken.None);

        Assert.Equal(2, mine.Count);
        Assert.Equal(second.RequestId, mine[0].RequestId);
        Assert.Equal(first.RequestId, mine[1].RequestId);

        var othersRequests = await harness.Service.ListForCustomerAsync(
            harness.CustomerActor(harness.OtherCustomer),
            CancellationToken.None);
        Assert.Empty(othersRequests);
    }

    [Fact]
    public async Task Customer_get_returns_null_for_another_customers_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var owned = await harness.Service.GetForCustomerAsync(
            harness.CustomerActor(harness.Customer),
            request.RequestId,
            CancellationToken.None);
        var foreign = await harness.Service.GetForCustomerAsync(
            harness.CustomerActor(harness.OtherCustomer),
            request.RequestId,
            CancellationToken.None);

        Assert.NotNull(owned);
        Assert.Equal(request.RequestId, owned.RequestId);
        Assert.Null(foreign);
    }

    // ---------------------------------------------------------------- staff views

    [Fact]
    public async Task Staff_list_scopes_to_assigned_branches_and_rejects_foreign_branch_filter()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var page = await harness.Service.ListForStaffAsync(
            harness.StaffActor(harness.Staff),
            new RefundReplacementListQuery(),
            CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(request.RequestId, page.Items.Single().RequestId);
        Assert.Equal(request.RequestNumber, page.Items.Single().RequestNumber);
        Assert.Equal(harness.Branch.Code, page.Items.Single().BranchCode);

        var foreignBranch = await harness.Service.ListForStaffAsync(
            harness.StaffActor(harness.Staff),
            new RefundReplacementListQuery(BranchId: harness.OtherBranch.Id, Page: 2, PageSize: 5),
            CancellationToken.None);

        Assert.Empty(foreignBranch.Items);
        Assert.Equal(0, foreignBranch.TotalCount);
        Assert.Equal(2, foreignBranch.Page);
        Assert.Equal(5, foreignBranch.PageSize);
    }

    [Fact]
    public async Task Staff_list_supports_global_access_and_status_filter()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        await harness.SubmitAsync();

        var globalActor = new RefundReplacementActor(harness.Staff.Id, new HashSet<long>(), true);

        var all = await harness.Service.ListForStaffAsync(
            globalActor,
            new RefundReplacementListQuery(),
            CancellationToken.None);
        var pending = await harness.Service.ListForStaffAsync(
            globalActor,
            new RefundReplacementListQuery(Status: RefundReplacementStatus.Pending),
            CancellationToken.None);
        var completed = await harness.Service.ListForStaffAsync(
            globalActor,
            new RefundReplacementListQuery(Status: RefundReplacementStatus.Completed),
            CancellationToken.None);

        Assert.Equal(1, all.TotalCount);
        Assert.Equal(1, pending.TotalCount);
        Assert.Equal(0, completed.TotalCount);
    }

    [Fact]
    public async Task Staff_list_clamps_page_and_page_size()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        await harness.SubmitAsync();

        var clamped = await harness.Service.ListForStaffAsync(
            harness.StaffActor(harness.Staff),
            new RefundReplacementListQuery(Page: 0, PageSize: 0),
            CancellationToken.None);
        var capped = await harness.Service.ListForStaffAsync(
            harness.StaffActor(harness.Staff),
            new RefundReplacementListQuery(Page: -3, PageSize: 1000),
            CancellationToken.None);

        Assert.Equal(1, clamped.Page);
        Assert.Equal(20, clamped.PageSize);
        Assert.Equal(1, capped.Page);
        Assert.Equal(100, capped.PageSize);
    }

    [Fact]
    public async Task Staff_get_is_branch_scoped_but_global_access_succeeds()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForStaffAsync(
            harness.StaffActorForBranch(harness.OtherStaff, harness.OtherBranch),
            request.RequestId,
            CancellationToken.None));

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForStaffAsync(
            new RefundReplacementActor(
                harness.Staff.Id,
                new HashSet<long> { harness.OtherBranch.Id },
                false),
            request.RequestId,
            CancellationToken.None));

        var globalResult = await harness.Service.GetForStaffAsync(
            new RefundReplacementActor(harness.Staff.Id, new HashSet<long>(), true),
            request.RequestId,
            CancellationToken.None);

        Assert.Equal(request.RequestId, globalResult.RequestId);
    }

    // ------------------------------------------------------------- decision flow

    [Fact]
    public async Task Approve_moves_pending_to_approved_with_audit_and_notification()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var approved = await harness.Service.ApproveAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            new DecideRefundReplacementRequest("Approved by support"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(harness.Staff.Id, approved.DecidedByUserId);
        Assert.Equal("Approved by support", approved.DecisionRemarks);
        Assert.NotNull(approved.DecidedAt);

        var actions = await AuditActionsAsync(harness, request.RequestId);
        Assert.Equal(["REFUND_REPLACEMENT.APPROVE", "REFUND_REPLACEMENT.SUBMIT"], actions);

        var notificationEvent = await harness.Db.NotificationEvents
            .OrderByDescending(x => x.Id)
            .FirstAsync();
        Assert.Equal(NotificationEventTypes.RefundReplacementDecided, notificationEvent.EventType);
        Assert.Equal(
            $"refund-replacement:{request.RequestId:N}:decided:Approved",
            notificationEvent.EventKey);
        Assert.False(notificationEvent.IsCritical);
        var variables = Variables(notificationEvent);
        Assert.Equal("Approved", variables.GetProperty("status").GetString());
        Assert.Equal(
            "Your refund/replacement request has been approved.",
            variables.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Decision_requires_pending_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        await harness.Service.ApproveAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None);

        var approveAgain = await Assert.ThrowsAsync<ConflictException>(
            () => harness.Service.ApproveAsync(
                harness.StaffActor(harness.Staff),
                request.RequestId,
                new DecideRefundReplacementRequest(null),
                CancellationToken.None));
        var rejectAfter = await Assert.ThrowsAsync<ConflictException>(
            () => harness.Service.RejectAsync(
                harness.StaffActor(harness.Staff),
                request.RequestId,
                new DecideRefundReplacementRequest(null),
                CancellationToken.None));

        Assert.Equal("Only a pending request can be approved.", approveAgain.Message);
        Assert.Equal("Only a pending request can be rejected.", rejectAfter.Message);
    }

    [Fact]
    public async Task Complete_requires_approved_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => harness.Service.CompleteAsync(
                harness.StaffActor(harness.Staff),
                request.RequestId,
                new DecideRefundReplacementRequest(null),
                CancellationToken.None));

        Assert.Equal("Only an approved request can be completed.", exception.Message);
    }

    [Fact]
    public async Task Complete_moves_approved_to_completed_with_notification()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        await harness.Service.ApproveAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None);

        var completed = await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            new DecideRefundReplacementRequest("Refund processed"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(harness.Staff.Id, completed.CompletedByUserId);
        Assert.Equal("Refund processed", completed.CompletionRemarks);
        Assert.NotNull(completed.CompletedAt);

        var actions = await AuditActionsAsync(harness, request.RequestId);
        Assert.Equal(
            [
                "REFUND_REPLACEMENT.APPROVE",
                "REFUND_REPLACEMENT.COMPLETE",
                "REFUND_REPLACEMENT.SUBMIT"
            ],
            actions);

        var notificationEvent = await harness.Db.NotificationEvents
            .OrderByDescending(x => x.Id)
            .FirstAsync();
        Assert.Equal(NotificationEventTypes.RefundReplacementCompleted, notificationEvent.EventType);
        Assert.Equal($"refund-replacement:{request.RequestId:N}:completed", notificationEvent.EventKey);
        Assert.False(notificationEvent.IsCritical);
    }

    // ------------------------------------------------------------- proof image

    [Fact]
    public async Task Upload_stores_private_proof_image_with_metadata_and_audit()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var image = await harness.UploadAsync(request.RequestId);

        Assert.Equal("proof.jpg", image.FileName);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(harness.ImageBytes.Length, image.FileSize);
        Assert.NotEqual(Guid.Empty, image.ImageId);

        var stored = await harness.Db.RefundReplacementImages.SingleAsync();
        Assert.Equal(harness.Customer.Id, stored.UploadedByUserId);
        Assert.Contains($"/{harness.Branch.Id}/{request.RequestId:N}/", stored.StorageKey);
        Assert.EndsWith(".jpg", stored.StorageKey);
        Assert.Equal(harness.ImageBytes, harness.Storage.Files[stored.StorageKey]);

        var actions = await AuditActionsAsync(harness, request.RequestId);
        Assert.Equal(["REFUND_REPLACEMENT.IMAGE_UPLOAD", "REFUND_REPLACEMENT.SUBMIT"], actions);

        var detail = await harness.Service.GetForCustomerAsync(
            harness.CustomerActor(harness.Customer),
            request.RequestId,
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Single(detail.Images);
    }

    [Theory]
    [InlineData("image/gif", 5, "The proof image must be a JPEG, PNG or WebP image.")]
    [InlineData("image/jpeg", 0, "The proof image is empty.")]
    [InlineData("image/jpeg", 10485761, "The proof image cannot exceed 10 MB.")]
    public async Task Upload_validates_image_type_and_size(
        string contentType,
        long fileSize,
        string expectedMessage)
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(
            () => harness.Service.UploadImageAsync(
                harness.CustomerActor(harness.Customer),
                request.RequestId,
                new MemoryStream(harness.ImageBytes, writable: false),
                "proof.jpg",
                contentType,
                fileSize,
                CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(harness.Storage.Files);
    }

    [Fact]
    public async Task Upload_requires_pending_request()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        await harness.Service.ApproveAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => harness.UploadAsync(request.RequestId));

        Assert.Equal("A proof image can only be added while the request is pending.", exception.Message);
        Assert.Empty(harness.Storage.Files);
    }

    [Fact]
    public async Task Upload_is_rejected_for_milk_test_flow()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.CreateRejectedMilkTestOnFailedDeliveryAsync();
        var request = await harness.SubmitAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.UploadAsync(request.RequestId));

        Assert.Equal(
            "A proof image is not required for a request opened from a rejected doorstep milk test.",
            exception.Message);
        Assert.Empty(harness.Storage.Files);
    }

    [Fact]
    public async Task Upload_removes_orphaned_blob_on_size_mismatch()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        harness.Storage.ReportedSizeOffset = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.UploadAsync(request.RequestId));

        Assert.Empty(harness.Storage.Files);
        Assert.Single(harness.Storage.DeletedKeys);
        Assert.Empty(await harness.Db.RefundReplacementImages.ToArrayAsync());
    }

    [Fact]
    public async Task Open_image_is_restricted_to_owner_and_scoped_staff()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        var image = await harness.UploadAsync(request.RequestId);

        await using (var ownerContent = await harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.Customer),
            request.RequestId,
            image.ImageId,
            CancellationToken.None))
        {
            using var buffer = new MemoryStream();
            await ownerContent.Content.CopyToAsync(buffer);
            Assert.Equal(harness.ImageBytes, buffer.ToArray());
            Assert.Equal("image/jpeg", ownerContent.ContentType);
        }

        await using (var staffContent = await harness.Service.OpenImageAsync(
            harness.StaffActor(harness.Staff),
            request.RequestId,
            image.ImageId,
            CancellationToken.None))
        {
            using var buffer = new MemoryStream();
            await staffContent.Content.CopyToAsync(buffer);
            Assert.Equal(harness.ImageBytes, buffer.ToArray());
        }

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            harness.CustomerActor(harness.OtherCustomer),
            request.RequestId,
            image.ImageId,
            CancellationToken.None));

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.OpenImageAsync(
            harness.StaffActorForBranch(harness.OtherStaff, harness.OtherBranch),
            request.RequestId,
            image.ImageId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Open_image_reports_unknown_request_and_image()
    {
        await using var harness = await RefundReplacementHarness.CreateAsync();
        await harness.DeliverAsync();
        var request = await harness.SubmitAsync();
        await harness.UploadAsync(request.RequestId);

        var missingRequest = await Assert.ThrowsAsync<NotFoundException>(
            () => harness.Service.OpenImageAsync(
                harness.CustomerActor(harness.Customer),
                Guid.NewGuid(),
                Guid.NewGuid(),
                CancellationToken.None));
        var missingImage = await Assert.ThrowsAsync<NotFoundException>(
            () => harness.Service.OpenImageAsync(
                harness.CustomerActor(harness.Customer),
                request.RequestId,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal("The refund/replacement request was not found.", missingRequest.Message);
        Assert.Equal("The proof image was not found.", missingImage.Message);
    }

    // ------------------------------------------------------------------- helpers

    private static async Task<string[]> AuditActionsAsync(
        RefundReplacementHarness harness,
        Guid requestId) =>
        await harness.Db.AuditLogs
            .Where(x => x.EntityId == requestId.ToString())
            .Select(x => x.Action)
            .OrderBy(x => x)
            .ToArrayAsync();

    private static JsonElement Payload(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    private static string? DeepLink(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("DeepLink").GetString();

    private sealed class RefundReplacementHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private RefundReplacementHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            CapturingMediaStorage storage,
            RefundReplacementService service,
            RefundReplacementConfigurationService configurationService,
            User customer,
            User otherCustomer,
            User staff,
            User otherStaff,
            Branch branch,
            Branch otherBranch,
            Order order,
            Delivery delivery)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            Storage = storage;
            Service = service;
            ConfigurationService = configurationService;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Staff = staff;
            OtherStaff = otherStaff;
            Branch = branch;
            OtherBranch = otherBranch;
            Order = order;
            Delivery = delivery;
        }

        public byte[] ImageBytes { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];
        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public CapturingMediaStorage Storage { get; }
        public RefundReplacementService Service { get; }
        public RefundReplacementConfigurationService ConfigurationService { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Staff { get; }
        public User OtherStaff { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public Order Order { get; }
        public Delivery Delivery { get; }

        public RefundReplacementActor CustomerActor(User customer) =>
            new(customer.Id, new HashSet<long>(), false);

        public RefundReplacementActor StaffActor(User staff) =>
            new(staff.Id, new HashSet<long> { Branch.Id }, false);

        public RefundReplacementActor StaffActorForBranch(User staff, Branch branch) =>
            new(staff.Id, new HashSet<long> { branch.Id }, false);

        public Task<RefundReplacementEligibilityResult> EligibilityAsync() =>
            Service.GetEligibilityAsync(CustomerActor(Customer), Delivery.PublicId, CancellationToken.None);

        public Task<RefundReplacementRequestResult> SubmitAsync(
            RefundReplacementType type = RefundReplacementType.Refund,
            string reason = "The delivered item was not satisfactory",
            string? remarks = null,
            Guid? milkTestId = null) =>
            Service.SubmitAsync(
                CustomerActor(Customer),
                new SubmitRefundReplacementRequest(Delivery.PublicId, type, reason, remarks, milkTestId),
                CancellationToken.None);

        public Task<RefundReplacementImageResult> UploadAsync(Guid requestId) =>
            Service.UploadImageAsync(
                CustomerActor(Customer),
                requestId,
                new MemoryStream(ImageBytes, writable: false),
                "proof.jpg",
                "image/jpeg",
                ImageBytes.Length,
                CancellationToken.None);

        public async Task AdvanceToArrivedAsync()
        {
            Delivery.PickUp(Staff.Id, Clock.Now, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.Start(Staff.Id, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.Arrive(Staff.Id, Clock.Now);
            await Db.SaveChangesAsync();
        }

        public async Task DeliverAsync()
        {
            await AdvanceToArrivedAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.RecordOtpVerified(Staff.Id, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            Delivery.Complete(Staff.Id, Clock.Now, null);
            await Db.SaveChangesAsync();
        }

        public async Task<MilkTest> CreateCompletedMilkTestAsync()
        {
            var milkTest = new MilkTest(Delivery.Id, Customer.Id, Branch.Id, Staff.Id, Clock.Now);
            Db.MilkTests.Add(milkTest);
            await Db.SaveChangesAsync();

            milkTest.AddParameter("FAT", "Fat", 4.2m, "%");
            await Db.SaveChangesAsync();

            milkTest.AddImage(new MilkTestImage(
                milkTest.Id,
                $"milk-tests/{milkTest.Id}.jpg",
                "proof.jpg",
                "image/jpeg",
                5,
                Staff.Id,
                Clock.Now));
            await Db.SaveChangesAsync();

            Clock.Advance(TimeSpan.FromMinutes(1));
            milkTest.Complete(Staff.Id, Clock.Now, null);
            await Db.SaveChangesAsync();
            return milkTest;
        }

        public async Task<MilkTest> CreateRejectedMilkTestAsync()
        {
            var milkTest = await CreateCompletedMilkTestAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            milkTest.Reject(Clock.Now, "The milk did not meet expectations");
            await Db.SaveChangesAsync();
            return milkTest;
        }

        public async Task<MilkTest> CreateRejectedMilkTestOnFailedDeliveryAsync(
            string remarks = "The milk did not meet expectations")
        {
            var milkTest = await CreateCompletedMilkTestAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            milkTest.Reject(Clock.Now, remarks);
            Delivery.FailForCustomerMilkTest(Clock.Now, milkTest.CustomerRemarks);
            await Db.SaveChangesAsync();
            return milkTest;
        }

        public static async Task<RefundReplacementHarness> CreateAsync()
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

            // Allocation is unscoped, so the request series must exist with the
            // default (global) scope key; otherwise SubmitAsync cannot number the
            // request and fails with NotFoundException.
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode,
                "Customer refund/replacement request numbers",
                "RR/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));
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
                "refund-replacement-order-1",
                "ORD-REF-001",
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
            var configurationService = new RefundReplacementConfigurationService(db, clock);
            var service = new RefundReplacementService(
                db,
                clock,
                configurationService,
                new NumberSeriesService(db, clock),
                storage,
                new TestNotificationEventWriter(db, clock));

            return new RefundReplacementHarness(
                connection,
                db,
                clock,
                storage,
                service,
                configurationService,
                customer,
                otherCustomer,
                staff,
                otherStaff,
                branch,
                otherBranch,
                order,
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
