# Delivery Manager Runtime Debug Trace

## Entry and Scope

`DELIVERY_MANAGER` maps to the shared delivery workspace. Management UI appears when the role is present or permission `DELIVERIES.READ_BRANCH` exists. It uses the first JWT branch ID; no branch produces a `No branch assigned` panel.

## Runtime Chains

- Branch dashboard: `/delivery-management/branch/{branchId}` -> branch deliveries and employee repository calls -> controller/service actor branch check -> `Delivery`, `DeliveryAssignment`, identity role data -> management state.
- Materialize: sync button -> materialization endpoint -> eligible confirmed orders and subscription occurrences -> new delivery rows -> save -> reload.
- Assign/reassign: detail employee selector -> assignment endpoint -> branch and employee eligibility checks -> `DeliveryAssignment` update/create -> audit/event save -> detail reload.
- Monitor: tap delivery -> `/delivery-management/{deliveryId}` -> operations detail including status, assignee, OTP/tracking/test state.

The detail screen also renders operational buttons supported by shared delivery methods. Actual success still depends on endpoint permission, branch scope, assignment/state rules; visibility alone is not authorization.

## Failures

Wrong route branch, actor with no matching branch/global claim, employee outside branch or without delivery role, ineligible source order/occurrence, duplicate materialization, or illegal transition are rejected. Permission middleware failures return audited 401/403 envelopes.

## Breakpoints

- [DeliveryManagementScreen](../mobile/lib/features/deliveries/delivery_screens.dart:267)
- [DeliveryManagementDetailScreen](../mobile/lib/features/deliveries/delivery_screens.dart:363)
- [DeliveryService.MaterializeEligibleAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:31)
- [DeliveryService.AssignAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:186)
- [DeliveryService.GetForBranchAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:123)
