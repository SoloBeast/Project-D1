using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Wallets;

namespace DoodhDirect.Domain.Tests;

public sealed class PaymentWalletDomainTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RazorpayPayment_RequiresGatewayReferenceAndSupportsIdempotentSuccess()
    {
        var payment = CreatePayment(PaymentMethod.Razorpay);
        payment.AttachGatewayOrder("order_123", "created");

        payment.Succeed("pay_123", "captured", Now);
        payment.Succeed("pay_123", "captured", Now.AddMinutes(1));

        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal("order_123", payment.GatewayOrderId);
        Assert.Equal("pay_123", payment.GatewayPaymentId);
        Assert.Equal(Now, payment.VerifiedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            payment.Succeed("pay_other", "captured", Now.AddMinutes(2)));
    }

    [Fact]
    public void Payment_TerminalFailureCannotLaterSucceed()
    {
        var payment = CreatePayment(PaymentMethod.Razorpay);
        payment.AttachGatewayOrder("order_123", "created");
        payment.Fail("DECLINED", "Issuer declined", "failed", Now);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("DECLINED", payment.FailureCode);
        Assert.Throws<InvalidOperationException>(() =>
            payment.Succeed("pay_123", "captured", Now.AddMinutes(1)));
    }

    [Fact]
    public void Payment_ExpirationIsIdempotentAndTerminal()
    {
        var payment = CreatePayment(PaymentMethod.Wallet);
        payment.MarkWalletPending();

        payment.Expire(Now);
        payment.Expire(Now.AddMinutes(1));

        Assert.Equal(PaymentStatus.Expired, payment.Status);
        Assert.Equal("PAYMENT_EXPIRED", payment.FailureCode);
        Assert.Equal(Now, payment.FailedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            payment.Fail("LATE_FAILURE", null, null, Now.AddMinutes(2)));
    }

    [Fact]
    public void Payment_PartialAndFullRefundsReconcileExactly()
    {
        var payment = CreateSuccessfulWalletPayment(amount: 100m);

        var first = payment.StartRefund(25m, "Partial", "refund-1", requestedByUserId: 7);
        first.Succeed(Now.AddMinutes(1));
        payment.CompleteRefund(first.Amount);

        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(25m, payment.RefundedAmount);

        var second = payment.StartRefund(75m, "Remainder", "refund-2", requestedByUserId: 7);
        second.Succeed(Now.AddMinutes(2));
        payment.CompleteRefund(second.Amount);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(100m, payment.RefundedAmount);
        Assert.Throws<InvalidOperationException>(() =>
            payment.StartRefund(1m, "Too much", "refund-3", requestedByUserId: 7));
    }

    [Fact]
    public void Payment_FailedRefundRestoresPriorSuccessfulState()
    {
        var payment = CreateSuccessfulWalletPayment(amount: 100m);
        var completed = payment.StartRefund(20m, "First", "refund-1", requestedByUserId: 7);
        completed.Succeed(Now.AddMinutes(1));
        payment.CompleteRefund(completed.Amount);

        var failed = payment.StartRefund(10m, "Second", "refund-2", requestedByUserId: 7);
        failed.Fail("PROVIDER_FAILED", "Rejected", Now.AddMinutes(2));
        payment.FailRefund();

        Assert.Equal(RefundStatus.Failed, failed.Status);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(20m, payment.RefundedAmount);
    }

    [Fact]
    public void Wallet_CreditsDebitsAndRefundsProduceReconcilingLedger()
    {
        var wallet = new Wallet(customerId: 5, currency: "inr");

        var topUp = wallet.Credit(
            WalletTransactionType.TopUp, 150.125m, "topup-1", "Top-up", Now);
        var debit = wallet.DebitOrder(
            60.12m, orderId: 11, paymentId: 12, "debit-1", "Order", Now.AddMinutes(1));
        var refund = wallet.Credit(
            WalletTransactionType.RefundCredit, 10.005m, "refund-1", "Refund",
            Now.AddMinutes(2), paymentId: 12, orderId: 11);

        Assert.Equal(100.02m, wallet.Balance);
        Assert.Equal((0m, 150.13m, 150.13m),
            (topUp.BalanceBefore, topUp.Amount, topUp.BalanceAfter));
        Assert.Equal((150.13m, -60.12m, 90.01m),
            (debit.BalanceBefore, debit.Amount, debit.BalanceAfter));
        Assert.Equal((90.01m, 10.01m, 100.02m),
            (refund.BalanceBefore, refund.Amount, refund.BalanceAfter));
        Assert.All(wallet.Transactions, transaction =>
            Assert.Equal(transaction.BalanceAfter,
                transaction.BalanceBefore + transaction.Amount));
    }

    [Fact]
    public void Wallet_RejectsDebitThatWouldMakeBalanceNegativeWithoutAppendingLedgerEntry()
    {
        var wallet = new Wallet(customerId: 5, currency: "INR");
        wallet.Credit(WalletTransactionType.TopUp, 25m, "topup-1", "Top-up", Now);

        var exception = Assert.Throws<WalletBalanceInsufficientException>(() =>
            wallet.DebitOrder(25.01m, 11, 12, "debit-1", "Order", Now.AddMinutes(1)));

        Assert.Equal(25m, exception.AvailableBalance);
        Assert.Equal(25.01m, exception.RequiredAmount);
        Assert.Equal(0.01m, exception.Shortfall);
        Assert.Equal("INR", exception.Currency);
        Assert.Equal(25m, wallet.Balance);
        Assert.Single(wallet.Transactions);
    }

    [Fact]
    public void Wallet_AdjustmentRecordsAdministratorAndRejectsOverdraw()
    {
        var wallet = new Wallet(customerId: 5, currency: "INR");
        var credit = wallet.Adjust(40m, performedByUserId: 9, "adjust-1", "Correction", Now);

        Assert.Equal(WalletTransactionType.AdminAdjustment, credit.Type);
        Assert.Equal(9, credit.PerformedByUserId);
        Assert.Equal(40m, wallet.Balance);
        Assert.Throws<WalletBalanceInsufficientException>(() =>
            wallet.Adjust(-40.01m, 9, "adjust-2", "Correction", Now.AddMinutes(1)));
    }

    [Fact]
    public void PaymentWebhook_AllowsOneProcessingCompletionOnly()
    {
        var webhook = new PaymentWebhook("Razorpay", "evt_1", "payment.captured", "abc", Now);

        webhook.StartProcessing();
        webhook.Complete(Now.AddMinutes(1));

        Assert.Equal(PaymentWebhookStatus.Processed, webhook.Status);
        Assert.Equal(Now.AddMinutes(1), webhook.ProcessedAtUtc);
        Assert.Throws<InvalidOperationException>(webhook.StartProcessing);
    }

    private static Payment CreatePayment(PaymentMethod method, decimal amount = 100m) =>
        new(
            orderId: 10,
            customerId: 5,
            method,
            amount,
            currency: "inr",
            idempotencyKey: "payment-1",
            expiresAtUtc: Now.AddMinutes(15));

    private static Payment CreateSuccessfulWalletPayment(decimal amount)
    {
        var payment = CreatePayment(PaymentMethod.Wallet, amount);
        payment.MarkWalletPending();
        payment.Succeed(null, "wallet_debited", Now);
        return payment;
    }
}
