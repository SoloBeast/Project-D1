using DoodhDirect.Domain.MilkTesting;

namespace DoodhDirect.Domain.Tests;

public sealed class MilkTestDomainTests
{
    private static readonly DateTime RequestedAt = new(2026, 8, 17, 10, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Constructor_InitializesRequestedTestAndRejectsInvalidIdentityOrTimestamp()
    {
        var test = CreateTest();

        Assert.Equal(MilkTestStatus.Requested, test.Status);
        Assert.Equal(MilkTestCustomerDecision.Pending, test.CustomerDecision);
        Assert.Empty(test.Parameters);
        Assert.Empty(test.Images);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MilkTest(0, 2, 3, 2, RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new MilkTest(1, 2, 3, 2, DateTime.SpecifyKind(RequestedAt, DateTimeKind.Local)));
    }

    [Fact]
    public void Complete_RequiresAtLeastOneReadingAndImage()
    {
        var withoutEvidence = CreateTest();
        var withoutImage = CreateTest();
        withoutImage.AddParameter("fat", "Fat", 4.25m, "%");

        Assert.Throws<InvalidOperationException>(() =>
            withoutEvidence.Complete(4, RequestedAt.AddMinutes(5), null));
        Assert.Throws<InvalidOperationException>(() =>
            withoutImage.Complete(4, RequestedAt.AddMinutes(5), null));
    }

    [Fact]
    public void Readings_NormalizeCodesAndRejectDuplicatesIgnoringCase()
    {
        var test = CreateTest();

        test.AddParameter(" fat ", " Milk Fat ", 4.25m, " % ");

        var reading = Assert.Single(test.Parameters);
        Assert.Equal("FAT", reading.Code);
        Assert.Equal("Milk Fat", reading.Name);
        Assert.Equal("%", reading.Unit);
        Assert.Throws<InvalidOperationException>(() =>
            test.AddParameter("FAT", "Fat duplicate", 4.30m, "%"));
    }

    [Fact]
    public void Complete_RecordsUploaderEvidenceReadingsAndNormalizedRemarks()
    {
        var test = CreateReadyTest();
        var completedAt = RequestedAt.AddMinutes(5);

        test.Complete(4, completedAt, " Tested at doorstep ");

        Assert.Equal(MilkTestStatus.Completed, test.Status);
        Assert.Equal(4, test.CompletedByUserId);
        Assert.Equal(completedAt, test.CompletedAt);
        Assert.Equal("Tested at doorstep", test.StaffRemarks);
        Assert.Equal(4, Assert.Single(test.Images).UploadedByUserId);
    }

    [Fact]
    public void Complete_RejectsTimestampBeforeRequestAndFurtherMutation()
    {
        var test = CreateReadyTest();

        Assert.Throws<ArgumentException>(() =>
            test.Complete(4, RequestedAt.AddSeconds(-1), null));

        test.Complete(4, RequestedAt.AddMinutes(5), null);
        Assert.Throws<InvalidOperationException>(() =>
            test.AddParameter("SNF", "Solids not fat", 8.5m, "%"));
        Assert.Throws<InvalidOperationException>(() =>
            test.AddImage(CreateImage()));
        Assert.Throws<InvalidOperationException>(() =>
            test.Complete(4, RequestedAt.AddMinutes(6), null));
    }

    [Fact]
    public void CustomerDecision_RequiresCompletionAndIsTerminal()
    {
        var pending = CreateReadyTest();
        Assert.Throws<InvalidOperationException>(() =>
            pending.Confirm(RequestedAt.AddMinutes(1), null));

        var confirmed = CreateReadyTest();
        confirmed.Complete(4, RequestedAt.AddMinutes(5), null);
        confirmed.Confirm(RequestedAt.AddMinutes(6), " Looks good ");

        Assert.Equal(MilkTestCustomerDecision.Confirmed, confirmed.CustomerDecision);
        Assert.Equal(RequestedAt.AddMinutes(6), confirmed.ConfirmedAt);
        Assert.Equal("Looks good", confirmed.CustomerRemarks);
        Assert.Throws<InvalidOperationException>(() =>
            confirmed.Reject(RequestedAt.AddMinutes(7), "Changed mind"));
    }

    [Fact]
    public void Reject_RecordsTerminalDecisionAndEnforcesTimestampOrdering()
    {
        var test = CreateReadyTest();
        test.Complete(4, RequestedAt.AddMinutes(5), null);

        Assert.Throws<ArgumentException>(() =>
            test.Reject(RequestedAt.AddMinutes(4), null));

        test.Reject(RequestedAt.AddMinutes(6), " Image does not match ");
        Assert.Equal(MilkTestCustomerDecision.Rejected, test.CustomerDecision);
        Assert.Equal(RequestedAt.AddMinutes(6), test.RejectedAt);
        Assert.Equal("Image does not match", test.CustomerRemarks);
        Assert.Throws<InvalidOperationException>(() =>
            test.Confirm(RequestedAt.AddMinutes(7), null));
    }

    [Fact]
    public void RemoveImage_WhileRequested_RemovesOnlyTheTargetImage()
    {
        var test = CreateTest();
        var keep = CreateImage();
        var remove = CreateImage();
        test.AddImage(keep);
        test.AddImage(remove);

        test.RemoveImage(remove.PublicId);

        var remaining = Assert.Single(test.Images);
        Assert.Equal(keep.PublicId, remaining.PublicId);
    }

    [Fact]
    public void RemoveImage_AfterCompletion_Throws()
    {
        var test = CreateReadyTest();
        var image = Assert.Single(test.Images);
        test.Complete(4, RequestedAt.AddMinutes(5), null);

        Assert.Throws<InvalidOperationException>(() =>
            test.RemoveImage(image.PublicId));
    }

    [Fact]
    public void RemoveImage_WithUnknownImageId_Throws()
    {
        var test = CreateReadyTest();

        Assert.Throws<InvalidOperationException>(() =>
            test.RemoveImage(Guid.NewGuid()));
    }

    [Fact]
    public void RemoveImage_OnlyRemovesImageBelongingToThisTest()
    {
        var first = CreateReadyTest();
        var other = CreateReadyTest();
        var otherImage = Assert.Single(other.Images);

        Assert.Throws<InvalidOperationException>(() =>
            first.RemoveImage(otherImage.PublicId));
        Assert.Single(first.Images);
    }

    [Fact]
    public void AddImageDuringReview_WhileCompletedAndPending_Appends()
    {
        var test = CreateReadyTest();
        test.Complete(4, RequestedAt.AddMinutes(5), null);
        var reviewImage = CreateImage();

        test.AddImageDuringReview(reviewImage);

        Assert.Equal(2, test.Images.Count);
        Assert.Contains(test.Images, x => x.PublicId == reviewImage.PublicId);
    }

    [Fact]
    public void AddImageDuringReview_WhileRequested_Throws()
    {
        var test = CreateTest();

        Assert.Throws<InvalidOperationException>(() =>
            test.AddImageDuringReview(CreateImage()));
    }

    [Fact]
    public void RemoveImageDuringReview_WhileCompletedAndPending_RemovesTarget()
    {
        var test = CreateReadyTest();
        test.Complete(4, RequestedAt.AddMinutes(5), null);
        var original = Assert.Single(test.Images);
        var replacement = CreateImage();
        test.AddImageDuringReview(replacement);

        test.RemoveImageDuringReview(original.PublicId);

        var remaining = Assert.Single(test.Images);
        Assert.Equal(replacement.PublicId, remaining.PublicId);
    }

    [Fact]
    public void RemoveImageDuringReview_AfterConfirmation_Throws()
    {
        var test = CreateReadyTest();
        var image = Assert.Single(test.Images);
        test.Complete(4, RequestedAt.AddMinutes(5), null);
        test.Confirm(RequestedAt.AddMinutes(6), null);

        Assert.Throws<InvalidOperationException>(() =>
            test.RemoveImageDuringReview(image.PublicId));
    }

    [Fact]
    public void AddImageDuringReview_AfterConfirmation_Throws()
    {
        var test = CreateReadyTest();
        test.Complete(4, RequestedAt.AddMinutes(5), null);
        test.Confirm(RequestedAt.AddMinutes(6), null);

        Assert.Throws<InvalidOperationException>(() =>
            test.AddImageDuringReview(CreateImage()));
    }

    [Fact]
    public void RemoveImageDuringReview_AfterRejection_Throws()
    {
        var test = CreateReadyTest();
        var image = Assert.Single(test.Images);
        test.Complete(4, RequestedAt.AddMinutes(5), null);
        test.Reject(RequestedAt.AddMinutes(6), null);

        Assert.Throws<InvalidOperationException>(() =>
            test.RemoveImageDuringReview(image.PublicId));
    }

    private static MilkTest CreateTest() => new(
        deliveryId: 1,
        customerId: 2,
        branchId: 3,
        requestedByUserId: 2,
        requestedAt: RequestedAt);

    private static MilkTest CreateReadyTest()
    {
        var test = CreateTest();
        test.AddParameter("FAT", "Fat", 4.25m, "%");
        test.AddImage(CreateImage());
        return test;
    }

    private static MilkTestImage CreateImage() => new(
        milkTestId: 0,
        storageKey: "2026/08/3/test/image.jpg",
        fileName: "sample.jpg",
        contentType: "image/jpeg",
        fileSize: 3,
        uploadedByUserId: 4,
        uploadedAt: RequestedAt.AddMinutes(2));
}
