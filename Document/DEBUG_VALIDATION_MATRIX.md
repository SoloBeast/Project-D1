# Validation and Business Condition Matrix

| Path | Client validation | Service/domain validation | Persistence/effect | Failure clues |
|---|---|---|---|---|
| Login/register | required form fields, password format | duplicate identity, credential/password checks | user/session/refresh/audit | 400/401 auth code |
| OTP | mobile/code required | purpose, expiry, attempts, code hash | challenge/audit/session | invalid or expired OTP |
| Refresh | stored refresh expiry | token hash, session/revocation/device rules | rotate refresh/session audit | 401 then clear session |
| Address | required label/contact/location | ownership, default handling, active state | address save; clear other defaults | validation/not found |
| Catalogue | positive quantity | active product/category/branch availability | read projections; admin updates | unavailable/not found |
| Checkout/order | address/product/quantity | allocation, stock/availability, order state | order/items/events | business rule |
| Payment | target and gateway form | amount/target/state/expiry/idempotency | payment, gateway order, events | gateway/business error |
| Webhook | raw payload/signature | signature, duplicate webhook, payment state | webhook and payment transition | rejected or processed once |
| Wallet top-up | positive amount | development-only/provider rules | wallet ledger and balance | forbidden/business error |
| Refund | valid amount/reason | successful payment, duplicate/idempotency, gateway | refund/payment/wallet/order events | refund failure |
| Subscription | plan/date/quantity | schedule, balance/payment, updateable state | subscription/schedule/deliveries | invalid state |
| Delivery materialize | branch/date request | eligible orders/occurrences and duplicate prevention | delivery rows/assignments/events | conflict/business |
| Delivery actions | OTP/code/location fields | assignment, branch, state transition, OTP limits | delivery/OTP/location/audit | 403/validation/business |
| Milk test | image/readings form | ownership/assignment, eligible delivery, image validator, readings/state | test/parameters/images/media/events | validation/business |
| Dairy production | positive quantities/date | branch, batch/product/day rules | production and batch transaction | branch/business |
| Dairy usage | positive used quantity | branch, batch active, remaining quantity | usage and batch quantity transaction | insufficient quantity |
| Camera stream | camera route ID | active/public availability, gateway descriptor/expiry | no stream bytes persisted; camera metadata read | unavailable/expired |
| Notifications | page/filter/device fields | actor ownership, token/device uniqueness, preference validity | inbox read flags/device/preferences | 403/not found |
| Reports | page/filter/date/export format | permission, branch/global actor scope, normalized filters | read-only projections/export bytes | forbidden/validation |

All backend failures pass through [ExceptionHandlingMiddleware](../Backend/src/DoodhDirect.Api/Middleware/ExceptionHandlingMiddleware.cs:25). UI state should be checked for whether the error originated before HTTP, in transport decoding, authorization, service validation, or persistence.
