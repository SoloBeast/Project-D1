# Delivery Staff Runtime Debug Trace

## Entry

`DELIVERY_STAFF` maps to the shared delivery role. [RoleHomeScreen](../mobile/lib/features/home/role_home_screen.dart:209) shows `Today's deliveries` when the user lacks management capability.

## Runtime Chain

`/delivery` -> [StaffDeliveryListScreen](../mobile/lib/features/deliveries/delivery_screens.dart:145) -> delivery controller -> repository `GET /api/v1/delivery/today` -> delivery controller/service -> query assigned deliveries for the actor and current day -> map response -> list state.

Tap delivery -> `/delivery/{deliveryId}` -> operations detail. Available actions call repository POST/PATCH endpoints for pickup, start, arrival, OTP issuance, OTP verification, completion, failure, and location recording. [DeliveryService](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:30) resolves the actor from JWT user/branches/global access, then checks assignment and legal transition before saving and publishing change events.

Milk test click -> `/delivery/{id}/milk-test` -> staff test GET -> multipart image upload -> readings completion. Images pass content/size/signature validation and local media storage. Completion requires evidence and valid parameter readings; customer confirmation/rejection is a later customer action.

## Scope

Required permissions include assigned-delivery operation/tracking and assigned milk-test operation. Matching branch alone is insufficient for staff operations: assignment is checked. Global access can bypass branch restrictions where implemented but does not invent a missing delivery.

## Failures

- Not assigned or wrong branch: forbidden/business authorization failure.
- Wrong delivery state: business-rule failure.
- OTP expired, exhausted, or invalid: validation/business failure; failed attempts persist.
- Completion without required OTP/test conditions: rejected.
- Location outside active tracking state or invalid coordinates: rejected.
- Media invalid/too large/unsupported: validation failure and no completed upload.

## Breakpoints

- [StaffDeliveryDetailScreen](../mobile/lib/features/deliveries/delivery_screens.dart:186)
- [DeliveryService.StartAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:223)
- [DeliveryService.VerifyOtpAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:290)
- [DeliveryService.CompleteAsync()](../Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:337)
- [MilkTestService.UploadImageAsync()](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs:92)
