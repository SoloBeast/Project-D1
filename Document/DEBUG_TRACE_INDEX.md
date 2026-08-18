# DoodhDirect Runtime Debug Trace Index

Documentation-only reverse trace of the current implementation. Source of truth is the code, not PRD/TRD wording.

## Reading Order

1. [DEBUG_TRACE_CROSS_CUTTING.md](DEBUG_TRACE_CROSS_CUTTING.md)
2. [DEBUG_TRACE_API_INDEX.md](DEBUG_TRACE_API_INDEX.md)
3. Role traces: [CUSTOMER](DEBUG_TRACE_CUSTOMER.md), [DELIVERY_STAFF](DEBUG_TRACE_DELIVERY_STAFF.md), [DELIVERY_MANAGER](DEBUG_TRACE_DELIVERY_MANAGER.md), [DAIRY_MANAGER](DEBUG_TRACE_DAIRY_MANAGER.md), [CUSTOMER_SUPPORT](DEBUG_TRACE_CUSTOMER_SUPPORT.md), [ACCOUNTANT](DEBUG_TRACE_ACCOUNTANT.md), [SYSTEM_ADMIN](DEBUG_TRACE_SYSTEM_ADMIN.md), [OWNER](DEBUG_TRACE_OWNER.md)
4. [DEBUG_TRACE_DATABASE.md](DEBUG_TRACE_DATABASE.md)
5. [DEBUG_VALIDATION_MATRIX.md](DEBUG_VALIDATION_MATRIX.md)
6. [DEBUG_PLAYBOOK.md](DEBUG_PLAYBOOK.md)
7. [DEBUG_API_CALL_MAP.md](DEBUG_API_CALL_MAP.md)
8. [DEBUG_FILE_INDEX.md](DEBUG_FILE_INDEX.md)

## Runtime Shape

`Flutter screen -> Riverpod controller -> repository -> ApiClient -> HTTP endpoint -> controller -> application service -> domain entity -> EF Core DbContext -> ApiResponse -> repository model -> state -> navigation/render`.

Authentication is the exception boundary around nearly every path: router restoration occurs before the home screen, bearer authorization is added by the shared client, and 401/403 or mapped application errors become visible state.

## Coverage Status

- Implemented: authentication, customer profile/address, public/admin catalogue, orders, payments, wallet, subscriptions, customer delivery tracking, delivery operations, milk testing, dairy operations, cameras, notifications, reports, and seeded RBAC.
- Shared but role-neutral: transport, exception envelopes, correlation IDs, JWT claims, audit logging, EF Core timestamps.
- `NOT FOUND IN CURRENT IMPLEMENTATION`: dedicated Flutter workflows for Customer Support and Accountant; general user/role administration screens; production push provider configuration; live map/location UI beyond implemented delivery calls; full media CDN.
- `DOCUMENTATION != IMPLEMENTATION`: conceptual `DeliveryOTP` is persisted as `DeliveryOtp`; role names exist in identity seed, but Support and Accountant home screens are placeholders.

## No-Code-Change Rule

This set records current behavior only. It does not propose or apply application changes.
