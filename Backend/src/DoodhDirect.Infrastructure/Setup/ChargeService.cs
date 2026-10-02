using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Admin CRUD for the Setup → Tax &amp; Charges master, mirroring the number-series
/// service conventions: normalize/validate inputs, guard delete/edit once referenced,
/// and write an audit log entry for every mutation.
/// </summary>
public sealed class ChargeService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : IChargeService
{
    public const string ActionCreated = "CHARGE.CREATED";
    public const string ActionUpdated = "CHARGE.UPDATED";
    public const string ActionActivated = "CHARGE.ACTIVATED";
    public const string ActionDeactivated = "CHARGE.DEACTIVATED";
    public const string ActionDeleted = "CHARGE.DELETED";

    public async Task<IReadOnlyList<ChargeResult>> ListAsync(CancellationToken cancellationToken)
    {
        var charges = await dbContext.Charges
            .AsNoTracking()
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToListAsync(cancellationToken);
        // One grouped query (not N+1): how many products each charge is
        // assigned to, so the admin UI can lock the applicability toggle.
        var counts = await dbContext.ProductCharges
            .AsNoTracking()
            .GroupBy(link => link.ChargeId)
            .Select(group => new { ChargeId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.ChargeId, entry => entry.Count, cancellationToken);
        return charges
            .Select(charge => ToResult(
                charge,
                counts.TryGetValue(charge.Id, out var count) ? count : 0))
            .ToArray();
    }

    public async Task<ChargeResult> GetAsync(Guid publicId, CancellationToken cancellationToken)
    {
        var charge = await FindAsync(publicId, cancellationToken);
        return await ToResultAsync(charge, cancellationToken);
    }

    public async Task<ChargeResult> CreateAsync(
        CreateChargeRequest request,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var chargeType = Required(request.ChargeType, "ChargeType", 40);
        var chargeCode = Required(request.ChargeCode, "ChargeCode", 20);
        var description = Optional(request.Description, "Description", 200);
        ValidatePercentage(request.Percentage);

        // Case-insensitive uniqueness on the business key (the DB index catches
        // exact duplicates; the master is a small config table so the case
        // comparison is done in memory).
        var duplicate = await dbContext.Charges
            .AnyAsync(charge => charge.ChargeCode.ToUpper() == chargeCode.ToUpper(), cancellationToken);
        if (duplicate)
        {
            throw new BusinessRuleException(
                $"A charge with code '{chargeCode}' already exists.");
        }

        var now = timeProvider.Now;
        var charge = new Charge(chargeType, chargeCode, description, request.Percentage, request.ApplicableOnAll);
        dbContext.Charges.Add(charge);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionCreated,
            "Charge",
            charge.PublicId.ToString(),
            null,
            ToSnapshotJson(charge),
            null,
            null,
            $"Charge '{charge.ChargeCode}' created.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(charge, productCount: 0);
    }

    public async Task<ChargeResult> UpdateAsync(
        Guid publicId,
        UpdateChargeRequest request,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var charge = await FindAsync(publicId, cancellationToken);
        var chargeType = Required(request.ChargeType, "ChargeType", 40);
        var description = Optional(request.Description, "Description", 200);
        ValidatePercentage(request.Percentage);

        if (charge.IsUsed && charge.ChargeType != chargeType)
        {
            throw new BusinessRuleException(
                $"Charge '{charge.ChargeCode}' has been applied to orders; its type can no longer change.");
        }

        var now = timeProvider.Now;
        var oldSnapshot = ToSnapshotJson(charge);
        charge.Update(chargeType, description, request.Percentage);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionUpdated,
            "Charge",
            charge.PublicId.ToString(),
            oldSnapshot,
            ToSnapshotJson(charge),
            null,
            null,
            $"Charge '{charge.ChargeCode}' updated.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToResultAsync(charge, cancellationToken);
    }

    public async Task<ChargeResult> SetActiveAsync(
        Guid publicId,
        bool isActive,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var charge = await FindAsync(publicId, cancellationToken);
        if (charge.IsActive == isActive)
        {
            return await ToResultAsync(charge, cancellationToken);
        }

        var now = timeProvider.Now;
        var oldSnapshot = ToSnapshotJson(charge);
        if (isActive)
        {
            charge.Activate();
        }
        else
        {
            charge.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            isActive ? ActionActivated : ActionDeactivated,
            "Charge",
            charge.PublicId.ToString(),
            oldSnapshot,
            ToSnapshotJson(charge),
            null,
            null,
            $"Charge '{charge.ChargeCode}' {(isActive ? "activated" : "deactivated")}.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToResultAsync(charge, cancellationToken);
    }

    /// <summary>
    /// Dedicated applicability toggle (mirrors the activate/deactivate convention).
    /// Re-enabling the global mode is refused while any product assignment exists;
    /// the mapping check runs INSIDE the authoritative serializable transaction so a
    /// concurrent product edit cannot slip a mapping past the invariant.
    /// </summary>
    public async Task<ChargeResult> SetApplicableOnAllAsync(
        Guid publicId,
        bool applicableOnAll,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var charge = await FindAsync(publicId, cancellationToken);
        if (charge.ApplicableOnAll == applicableOnAll)
        {
            return await ToResultAsync(charge, cancellationToken);
        }

        var now = timeProvider.Now;
        var oldSnapshot = ToSnapshotJson(charge);
        var changed = false;

        await ExecuteSerializableAsync(
            async () =>
            {
                await dbContext.Entry(charge).ReloadAsync(cancellationToken);
                if (charge.ApplicableOnAll == applicableOnAll)
                {
                    // A concurrent toggle already applied the requested mode.
                    return;
                }

                if (applicableOnAll)
                {
                    var mapped = await dbContext.ProductCharges
                        .AnyAsync(link => link.ChargeId == charge.Id, cancellationToken);
                    if (mapped)
                    {
                        throw new BusinessRuleException(
                            "Cannot enable Applicable on All because this charge is assigned to one or more products. "
                            + "Remove the charge from those products first.");
                    }
                }

                charge.SetApplicability(applicableOnAll);
                await dbContext.SaveChangesAsync(cancellationToken);
                changed = true;
            },
            cancellationToken);

        if (!changed)
        {
            // Nothing was written (a concurrent toggle won the race).
            return await ToResultAsync(charge, cancellationToken);
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionUpdated,
            "Charge",
            charge.PublicId.ToString(),
            oldSnapshot,
            ToSnapshotJson(charge),
            null,
            null,
            $"Charge '{charge.ChargeCode}' {(applicableOnAll ? "is now applicable on all products" : "is now product-assigned")}.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToResultAsync(charge, cancellationToken);
    }

    public async Task DeleteAsync(Guid publicId, long actorUserId, CancellationToken cancellationToken)
    {
        var charge = await FindAsync(publicId, cancellationToken);
        var now = timeProvider.Now;

        var productMapped = await dbContext.ProductCharges
            .AnyAsync(link => link.ChargeId == charge.Id, cancellationToken);
        if (productMapped)
        {
            throw new BusinessRuleException(
                $"This charge is assigned to one or more products. Remove the product assignments before deleting it.");
        }

        if (charge.IsUsed)
        {
            throw new BusinessRuleException(
                $"Charge '{charge.ChargeCode}' has been applied to orders and can only be deactivated, not deleted.");
        }

        var referenced = await dbContext.Set<OrderCharge>()
            .AnyAsync(orderCharge => orderCharge.ChargeCode == charge.ChargeCode, cancellationToken);
        if (referenced)
        {
            throw new BusinessRuleException(
                $"Charge '{charge.ChargeCode}' is referenced by historical orders and can only be deactivated, not deleted.");
        }

        var oldSnapshot = ToSnapshotJson(charge);
        dbContext.Charges.Remove(charge);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionDeleted,
            "Charge",
            charge.PublicId.ToString(),
            oldSnapshot,
            null,
            null,
            null,
            $"Charge '{charge.ChargeCode}' deleted.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Charge> FindAsync(Guid publicId, CancellationToken cancellationToken) =>
        await dbContext.Charges
            .SingleOrDefaultAsync(charge => charge.PublicId == publicId, cancellationToken)
            ?? throw new NotFoundException("Charge was not found.");

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static ChargeResult ToResult(Charge charge, int productCount) => new(
        charge.PublicId,
        charge.ChargeType,
        charge.ChargeCode,
        charge.Description,
        charge.Percentage,
        charge.IsActive,
        charge.ApplicableOnAll,
        charge.IsUsed,
        charge.CreatedAt,
        charge.UpdatedAt,
        productCount);

    private async Task<ChargeResult> ToResultAsync(Charge charge, CancellationToken cancellationToken) =>
        ToResult(
            charge,
            await dbContext.ProductCharges.CountAsync(
                link => link.ChargeId == charge.Id,
                cancellationToken));

    private static string ToSnapshotJson(Charge charge) => JsonSerializer.Serialize(new
    {
        charge.PublicId,
        charge.ChargeType,
        charge.ChargeCode,
        charge.Description,
        charge.Percentage,
        charge.IsActive,
        charge.ApplicableOnAll,
        charge.IsUsed
    });

    private static string Required(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new ValidationAppException(
                $"{field} is required and must be at most {maxLength} characters.",
                field);
        }

        return trimmed;
    }

    private static string? Optional(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new ValidationAppException(
                $"{field} must be at most {maxLength} characters.",
                field);
        }

        return trimmed;
    }

    private static void ValidatePercentage(decimal percentage)
    {
        if (percentage <= 0 || percentage > 100)
        {
            throw new ValidationAppException(
                "Percentage must be greater than 0 and at most 100.",
                "Percentage");
        }

        if (decimal.Round(percentage, 2) != percentage)
        {
            throw new ValidationAppException(
                "Percentage supports at most two decimal places.",
                "Percentage");
        }
    }
}
