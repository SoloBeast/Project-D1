# Runtime Debugging Playbook

## Start at the User Action

1. Identify screen and exact callback.
2. Set a breakpoint in the controller/notifier method.
3. Confirm loading/saving state and serialized request body.
4. Step into repository and [ApiClient](../mobile/lib/core/network/api_client.dart:1).
5. Capture HTTP method/path/status/response envelope and correlation ID.

## Classify the Failure

- No HTTP request: widget guard, form validation, disabled saving state, or controller exception.
- 401: absent/expired bearer or router/session restoration failure.
- 403: permission claim, branch claim, assignment, or audit authorization result handler.
- 400/409/422: DTO validation, service business rule, state transition, duplicate/idempotency, or conflict.
- 500: inspect correlation ID and middleware development detail; then service transaction and inner exception.
- Correct response but wrong UI: model factory, controller state assignment, notifier invalidation, or route target.

## Auth Checklist

Inspect secure storage, refresh-token expiry, device metadata, JWT `user_id`/`session_id`, permissions, branch IDs, and `/auth/me`. Confirm restore calls refresh before home. Logout clears storage on both success and failure.

## Scope Checklist

Compare requested `branchId` with JWT `branch_id`. Check `ACCESS.GLOBAL`. For own resources compare JWT user to entity owner. For delivery/milk tests inspect assignment. Remember policy support and actual controller attributes may differ; actor-aware services are authoritative for branch/ownership checks.

## Payment Checklist

Trace idempotency key, target order/subscription, payment status, wallet path versus gateway path, gateway identifiers, webhook signature, duplicate webhook row, refund state, and notification event writes.

## Delivery/Milk Test Checklist

Confirm delivery state, assignment, OTP expiry/attempt count, location active state, test status, image validation/storage key, reading values, and customer decision transition.

## Database Checklist

Inspect numeric key/public GUID conversion, active/status predicates, branch joins, transaction boundaries, timestamps, and event rows. Verify `SaveChangesAsync` was reached and committed.

## Reproducibility

Use integration tests under `Backend/tests/DoodhDirect.Api.IntegrationTests` and domain tests under `Backend/tests/DoodhDirect.Domain.Tests` as behavioral references. This document does not claim a test was run.
