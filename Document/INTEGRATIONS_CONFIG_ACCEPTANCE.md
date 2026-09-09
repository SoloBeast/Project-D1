# INTEGRATIONS Configuration — Acceptance & UAT Verification

Status: **Ready for UAT**
Feature: System Setup → **INTEGRATIONS** tab

---

## 1. What this feature delivers

The Add Employee page generates a single-use invitation link for onboarding, but
there was no way to configure the mail ID the system sends mail from, nor where
the Razorpay / Google Maps runtime credentials live.

Under **System Setup** a new **INTEGRATIONS** tab now maintains:

| Section        | Fields                                                                 |
| -------------- | ---------------------------------------------------------------------- |
| Email (SMTP)   | From address, From name, SMTP host, SMTP port, SMTP user name, SMTP password (write-only), Use SSL/TLS, Invitation link base URL |
| Razorpay       | Key ID, Key secret (write-only), Webhook secret (write-only)           |
| Google Maps    | Server API key (write-only), Base URL                                  |

The backend picks these up **at runtime whenever needed**:

- SMTP credentials → emailing the employee invitation link (in addition to the
  copyable link). Email is silently skipped (logged + audited) when SMTP is not
  fully configured or the invitee has no email.
- Razorpay → collecting payments (`PaymentOptions` runtime values).
- Google Maps → backend address lookups (`GoogleAddressLocationLookup`).

### Key rules

- **Database overrides win**: a non-empty saved value overrides
  appsettings/environment at call time. Clearing a field removes the override so
  the fallback applies again.
- **Secrets are write-only**: the backend never returns them. The screen shows a
  blank field with a `••••••••••••` hint. Leaving a secret blank preserves the
  stored value; typing a new one replaces it.
- **Google Maps**: the INTEGRATION section stores the **backend/server key only**.
  The Flutter **web client key stays a build-time** `--dart-define`.
- **SMTP transport**: built-in `System.Net.Mail.SmtpClient` (no NuGet/network).
- **Permissions**:
  - `SETUP.INTEGRATIONS.READ` — view the tab/screen (read-only).
  - `SETUP.INTEGRATIONS.MANAGE` — edit + "Send test email".

---

## 2. Build & verification status

### Backend

| Check | Result |
| ----- | ------ |
| `dotnet test DoodhDirect.slnx` | 80 domain + 895 integration = **975 passed, 0 failed** |
| Integrations filter (`~Integrations`) | 9 passed |
| PaymentsWalletControllerTests + EmployeeService | 38 passed |

New/changed backend units:

- `IntegrationsController` — `GET/PUT /api/v1/admin/setup/integrations`,
  `POST .../test`. Class-level READ policy, per-method MANAGE policy.
- `IntegrationConfigurationService` — load/update/test, DB-over-IConfiguration
  resolution, write-only secret protection (`IntegrationSecretProtector`).
- `IntegrationSettingsProvider` — effective runtime values from DB first, then
  environment/appsettings.
- `SmtpEmailSender` — `System.Net.Mail` delivery; unconfigured → silently
  skipped; unreachable → surfaced as a test/audit error.
- `EmployeeService` — emails the invitation link via SMTP when configured.
- `PaymentOptions` + `PaymentGateways` — Razorpay credentials relaxed to
  **per-call runtime readiness** (no boot-time fail-closed).

### Flutter

| Check | Result |
| ----- | ------ |
| `flutter analyze` | No issues found |
| `flutter test` (full suite) | **554 passed, 0 failed** |
| `flutter build web --release` | Built successfully |
| New tests | `test/integrations_models_repository_test.dart` + `test/integrations_screen_test.dart` = 30 passed |

New/changed Flutter units:

- `lib/features/integrations/integrations_models.dart` — `IntegrationConfiguration`,
  `UpdateIntegrationConfigurationRequest` (null = keep, `''` = clear override),
  `IntegrationTestResult`.
- `lib/features/integrations/integrations_repository.dart` —
  `GET/PUT /api/v1/admin/setup/integrations` + `POST .../test`.
- `lib/features/integrations/integrations_controller.dart` — Riverpod notifier
  (`integrationsControllerProvider`), session-reset listener, API error mapping.
- `lib/features/integrations/integrations_screen.dart` — the INTEGRATIONS screen.
- `lib/app/app.dart` — route `/admin/setup/integrations`.
- `lib/features/home/role_home_screen.dart` — "Integrations" tile in System Setup.

---

## 3. UAT verification steps

### 3.1 Restart the backend (grants the new permissions)

`IdentitySeedService` idempotently adds the missing role-permission rows on
startup, so existing `OWNER` / `SYSTEM_ADMIN` rows receive
`SETUP.INTEGRATIONS.READ/MANAGE` automatically. Restart the API, then log in as
an OWNER (or SYSTEM_ADMIN).

### 3.2 Open the INTEGRATIONS tab

1. Home → **System Setup** → **INTEGRATIONS** (tile subtitle: "SMTP, Razorpay &
   Maps keys").
2. The screen shows three cards: **Email (SMTP) delivery**, **Razorpay**, and
   **Google Maps**, each with a Configured / Not-configured chip, plus the
   "What this screen controls" info card.
3. Without `SETUP.INTEGRATIONS.READ` a user sees the Access-denied panel; with
   READ only they see a "Read-only view" banner, disabled fields and no buttons.

### 3.3 Configure SMTP and send a test email

1. Enter a From address (e.g. `no-reply@yourdomain.com`), SMTP host, port
   (587 TLS default, or 465), user name + password (or leave user/password blank
   for an unauthenticated relay), and an **Invitation link base URL**
   (e.g. `https://your-app.example.com`).
2. **Send test email** — expect "Configuration verified. A test email was sent
   to …".
   - Wrong host/port → friendly SMTP error banner; fields keep their edits.
   - Not fully configured → silent skip/audit path (see audit log).
3. **Save settings** → green "Integration settings saved." banner; the form
   re-hydrates; secret fields clear back to `••••••••••••`.
4. Reload the tab → saved non-secret values persist; secrets never reappear.

### 3.4 Verify SMTP invitation emailing

1. Add Employee → generate the single-use onboarding link for an invitee **with
   an email address**.
2. Expect the invite email (from the configured From address) containing the
   **absolute** invitation link built from the configured base URL.
3. Clearing the SMTP settings (or an invitee with no email) does not break
   Add Employee — the link stays copyable and the email is skipped + audited.

### 3.5 Razorpay & Google Maps runtime pick-up

1. Configure Razorpay Key ID/Secret/Webhook in the tab; place an order to
   confirm payment initiation uses the runtime credentials.
2. Configure the Google Maps **server** API key + base URL; run an address lookup
   (customer address form) to confirm the backend uses the runtime values.
3. Clearing a field (e.g. Razorpay Key ID) sends `''`, which removes the DB
   override so the appsettings/environment fallback applies again.

### 3.6 Permission / role matrix

| Identity | Sees tile | Edits | Sends test email |
| -------- | :-------: | :---: | :--------------: |
| OWNER | Yes | Yes | Yes |
| SYSTEM_ADMIN | Yes | Yes | Yes |
| Role with READ only | Yes | No | No |
| Role with neither | No (Access denied) | No | No |

---

## 4. API reference (admin, authorized)

| Method | Path | Policy | Purpose |
| ------ | ---- | ------ | ------- |
| GET    | `/api/v1/admin/setup/integrations` | `SETUP.INTEGRATIONS.READ`   | Current effective configuration + configured flags |
| PUT    | `/api/v1/admin/setup/integrations` | `SETUP.INTEGRATIONS.MANAGE` | Save overrides (null keeps; `''` clears) |
| POST   | `/api/v1/admin/setup/integrations/test` | `SETUP.INTEGRATIONS.MANAGE` | Send a test email through the configured relay |

All requests/responses use the standard `ApiResponse` envelope
(`{ success, data, errors }`).
