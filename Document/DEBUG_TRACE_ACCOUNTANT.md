# Accountant Runtime Debug Trace

## Implemented Identity Surface

`ACCOUNTANT` is a seeded, branch-assigned role. Its permissions include read-oriented identity/customer/order/report access, financial reports and export, payment refund, and wallet adjustment. Authorization is claim-driven.

## Flutter Runtime

The role maps to `UserRole.accountant`, but [RoleHomeScreen](../mobile/lib/features/home/role_home_screen.dart:56) renders an accounting placeholder. Common notifications and sign-out remain available.

`NOT FOUND IN CURRENT IMPLEMENTATION`: dedicated accounting dashboard, refund UI, wallet-adjustment UI, reconciliation, settlement, ledger export workflow, or role-home report links. Backend refund, wallet adjustment, financial reports, and export endpoints are implemented and permission protected.

`DOCUMENTATION != IMPLEMENTATION`: accounting authorization capability exists without a dedicated Flutter workflow.

## Backend Trace

Authorized client -> refund endpoint -> [PaymentService.RefundAsync()](../Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:455) -> load successful owned target without customer ownership restriction for administrator -> amount/status/idempotency/gateway checks -> `Refund`, `Payment`, optional wallet/order updates, audit and notification events -> save/transaction -> response.

Wallet adjustment -> administrator/customer route -> [WalletService.AdjustAsync()](../Backend/src/DoodhDirect.Infrastructure/Wallets/WalletService.cs:82) -> customer/wallet lookup -> signed adjustment validation -> ledger transaction and balance mutation -> audit/event save.

## Breakpoints

- [RoleHomeScreen.build()](../mobile/lib/features/home/role_home_screen.dart:14)
- [PaymentService.RefundAsync()](../Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:455)
- [WalletService.AdjustAsync()](../Backend/src/DoodhDirect.Infrastructure/Wallets/WalletService.cs:82)
- [ReportService.ExportAsync()](../Backend/src/DoodhDirect.Infrastructure/Reports/ReportService.cs:89)
