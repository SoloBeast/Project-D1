using DoodhDirect.Application.Payments;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class PaymentReconciliationCoordinator(
    PaymentService paymentService) : IPaymentReconciliationCoordinator
{
    public Task<int> ProcessBatchAsync(
        int batchSize,
        int candidateAgeMinutes,
        CancellationToken cancellationToken) =>
        paymentService.ProcessReconciliationBatchAsync(
            batchSize,
            candidateAgeMinutes,
            cancellationToken);
}
