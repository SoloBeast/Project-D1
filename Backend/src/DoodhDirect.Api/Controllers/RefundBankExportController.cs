using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Stage 3: manual-bank refund export batches for finance staff. Every
/// endpoint requires <c>REFUNDS.BANK_EXPORT</c> (Accountant/Owner); Support
/// and System Administrator are not granted this permission. Lists and
/// results carry masked account details only — full account numbers exist
/// solely inside the downloaded CSV bytes.
/// </summary>
[ApiController]
[Route("api/v1/refund-bank-exports")]
[Tags("Refund bank-transfer exports")]
[Produces("application/json")]
public sealed class RefundBankExportController(
    IRefundBankExportService exportService) : RefundReplacementControllerBase
{
    private const string BankExportPolicy = "permission:" + AuthorizationCodes.RefundBankExport;

    [HttpGet("eligible")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RefundBankExportCandidateResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RefundBankExportCandidateResult>>>> ListEligible(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RefundBankExportCandidateResult>>.Ok(
            await exportService.ListEligibleAsync(RequireRefundReplacementActor(), cancellationToken)));

    [HttpGet("batches")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RefundBankExportBatchResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RefundBankExportBatchResult>>>> ListBatches(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RefundBankExportBatchResult>>.Ok(
            await exportService.ListBatchesAsync(RequireRefundReplacementActor(), cancellationToken)));

    [HttpPost("batches")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportBatchResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<RefundBankExportBatchResult>>> CreateBatch(
        [FromBody] CreateRefundBankExportBatchRequest request,
        CancellationToken cancellationToken) =>
        StatusCode(
            StatusCodes.Status201Created,
            ApiResponse<RefundBankExportBatchResult>.Ok(
                await exportService.CreateBatchAsync(RequireRefundReplacementActor(), request, cancellationToken)));

    [HttpGet("batches/{batchId:guid}")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportBatchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportBatchResult>>> GetBatch(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportBatchResult>.Ok(
            await exportService.GetBatchAsync(RequireRefundReplacementActor(), batchId, cancellationToken)));

    [HttpPost("batches/{batchId:guid}/generate")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportBatchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportBatchResult>>> GenerateBatch(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportBatchResult>.Ok(
            await exportService.GenerateBatchAsync(RequireRefundReplacementActor(), batchId, cancellationToken)));

    [HttpGet("batches/{batchId:guid}/download")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<ActionResult> DownloadBatch(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var result = await exportService.DownloadBatchAsync(
            RequireRefundReplacementActor(), batchId, cancellationToken);
        Response.Headers.Append("X-Export-Disclaimer", result.Disclaimer);
        return File(result.Content, "text/csv; charset=utf-8", result.FileName);
    }

    [HttpPost("batches/{batchId:guid}/submit")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportBatchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportBatchResult>>> SubmitBatch(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportBatchResult>.Ok(
            await exportService.SubmitBatchAsync(RequireRefundReplacementActor(), batchId, cancellationToken)));

    [HttpPost("batches/{batchId:guid}/void")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportBatchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportBatchResult>>> VoidBatch(
        Guid batchId,
        [FromBody] RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportBatchResult>.Ok(
            await exportService.VoidBatchAsync(RequireRefundReplacementActor(), batchId, request, cancellationToken)));

    [HttpPost("items/{itemId:guid}/confirm-paid")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportItemResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportItemResult>>> ConfirmItemPaid(
        Guid itemId,
        [FromBody] ConfirmRefundBankExportItemRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportItemResult>.Ok(
            await exportService.ConfirmItemPaidAsync(RequireRefundReplacementActor(), itemId, request, cancellationToken)));

    [HttpPost("items/{itemId:guid}/fail")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportItemResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportItemResult>>> FailItem(
        Guid itemId,
        [FromBody] RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportItemResult>.Ok(
            await exportService.FailItemAsync(RequireRefundReplacementActor(), itemId, request, cancellationToken)));

    [HttpPost("items/{itemId:guid}/void")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportItemResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportItemResult>>> VoidItem(
        Guid itemId,
        [FromBody] RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportItemResult>.Ok(
            await exportService.VoidItemAsync(RequireRefundReplacementActor(), itemId, request, cancellationToken)));

    [HttpPost("items/{itemId:guid}/release")]
    [Authorize(Policy = BankExportPolicy)]
    [ProducesResponseType(typeof(ApiResponse<RefundBankExportItemResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundBankExportItemResult>>> ReleaseItem(
        Guid itemId,
        [FromBody] RefundBankExportItemDispositionRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundBankExportItemResult>.Ok(
            await exportService.ReleaseItemReservationAsync(RequireRefundReplacementActor(), itemId, request, cancellationToken)));
}
