using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Company bank details for manual bank-transfer refund payouts. Read and
/// Manage are restricted via SetupCompanyBankRead / SetupCompanyBankManage,
/// granted to System Administrator (and Owner). Reads are masked; the
/// decrypted account number is available only to the audited export
/// workflow, which has no settings endpoint here.
/// </summary>
[ApiController]
[Route("api/v1/admin/setup/company-bank-detail")]
[Tags("Company bank setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupCompanyBankRead)]
public sealed class CompanyBankController(ICompanyBankService companyBankService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<CompanyBankDetailResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CompanyBankDetailResult?>>> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<CompanyBankDetailResult?>.Ok(await companyBankService.GetAsync(cancellationToken)));

    [HttpPut]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupCompanyBankManage)]
    [ProducesResponseType(typeof(ApiResponse<CompanyBankDetailResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CompanyBankDetailResult>>> Update(
        [FromBody] CompanyBankDetailApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CompanyBankDetailResult>.Ok(await companyBankService.UpdateAsync(
            new UpsertCompanyBankDetailRequest(
                request.AccountHolderName,
                request.BankName,
                request.AccountNumber,
                request.Ifsc),
            RequireUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

public sealed record CompanyBankDetailApiRequest(
    [Required, MaxLength(100)] string AccountHolderName,
    [Required, MaxLength(100)] string BankName,
    [MaxLength(18)] string? AccountNumber,
    [Required, MaxLength(11)] string Ifsc);
