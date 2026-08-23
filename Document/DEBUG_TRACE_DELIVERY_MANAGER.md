# Delivery Manager Runtime Debug Trace

## Entry and Scope

`DELIVERY_MANAGER` maps to the shared delivery workspace. Management UI appears when the role is present or permission `DELIVERIES.READ_BRANCH` exists. It uses the first JWT branch ID; no branch produces a `No branch assigned` panel.

## Runtime Chains

- Branch queue: `/delivery-management/branch/{branchId}` -> date/status/source/slot filters -> branch deliveries and eligible employees -> controller/service actor branch check -> `Delivery`, `DeliveryAssignment`, identity role data -> management state.
- Automatic one-time creation: successful wallet, Razorpay, development-payment, webhook, retry, or replay confirmation -> idempotent order delivery check inside the payment transaction -> one `OneTimeOrder` delivery in `ReadyForAssignment` -> queue refresh. Failed or cancelled payments create no row.
- Generate subscriptions: `Generate Subscription Deliveries` -> subscription-only generation endpoint -> configured inclusive operational-window validation -> eligible scheduled subscription occurrences through the selected date -> duplicate prevention -> new delivery rows -> reload. Requests beyond the window fail without mutation.
- Single assign/reassign: detail employee selector -> assignment endpoint -> branch and employee eligibility checks -> `DeliveryAssignment` update/create -> audit/event save -> detail reload.
- Bulk assign: row checkboxes, Select All, or Clear Selection -> employee selection and confirmation -> one bulk endpoint -> validate all selected rows, branch scope, current state, and employee eligibility -> atomic assignment transaction -> audit/notification/realtime effects -> server reload and selected-ID reconciliation.
- Monitor: tap delivery -> `/delivery-management/{deliveryId}` -> operations detail including source type, subscription slot when applicable, status, assignee, OTP/tracking/test state.

The detail screen also renders operational buttons supported by shared delivery methods. Actual success still depends on endpoint permission, branch scope, assignment/state rules; visibility alone is not authorization.

## Failures

Wrong route branch, actor with no matching branch/global claim, employee outside branch or without delivery role, an ineligible or stale selected delivery, an out-of-window generation request, duplicate generation, or illegal transition is rejected. Bulk prevalidation failure leaves every selected delivery unchanged. Permission middleware failures return audited 401/403 envelopes.

## Breakpoints

- [DeliveryManagementScreen](../mobile/lib/features/deliveries/delivery_screens.dart:267)
- [DeliveryManagementDetailScreen](../mobile/lib/features/deliveries/delivery_screens.dart:363)
- [DeliveryService.MaterializeEligibleAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:53)
- [DeliveryService.FetchSubscriptionDeliveriesAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:124)
- [DeliveryService.GetForBranchAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:203)
- [DeliveryService.AssignAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:275)
- [DeliveryService.BulkAssignAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:305)
