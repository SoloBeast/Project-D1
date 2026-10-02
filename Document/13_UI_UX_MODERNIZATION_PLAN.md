# DoodhDirect — UI/UX Audit & Modernization Plan

> Status: **PLAN ONLY — no production code was modified.**
> Scope: `mobile/` Flutter application (customer + staff/admin surfaces).
> Deliverable type: design & implementation plan document (non-production artifact).
> Source of truth for business behavior: existing backend contracts, route guards, and RBAC — **all preserved**.

---

## 1. Overall UX Assessment

DoodhDirect is a functionally complete, role-aware dairy commerce app with a **better-than-average engineering baseline for UI reuse that is only partially exploited**. Three facts define the current state:

1. **A real design foundation exists but is too thin and is bypassed by most screens.**
   [`doodh_theme.dart`](../mobile/lib/core/theme/doodh_theme.dart:1) defines 9 colors, 5 spacing steps (`xs 4 / sm 8 / md 16 / lg 24 / xl 32`), and 3 radii (`sm 8 / md 14 / lg 20`), plus a Material 3 theme with 48px-minimum buttons. This is a solid seed. However, the audit found **195 raw `EdgeInsets` literals** across `mobile/lib` versus a small number of token usages — screens routinely hard-code `16`, `12`, `14`, `20`, `24` rather than referencing `DoodhSpacing`. Spacing therefore drifts page-to-page and blocks any global rhythm change.

2. **A shared component layer exists and works, but covers only a fraction of patterns.**
   The reusable layer is: [`DoodhPage`](../mobile/lib/core/widgets/customer_widgets.dart:8), [`DoodhSectionHeader`](../mobile/lib/core/widgets/customer_widgets.dart:34), [`DoodhActionTile`](../mobile/lib/core/widgets/customer_widgets.dart:51), [`DoodhStatusPill`/`DoodhStatusTone`](../mobile/lib/core/widgets/customer_widgets.dart:104), [`CustomerShell`](../mobile/lib/core/widgets/customer_widgets.dart:156), [`DoodhHeroCard`](../mobile/lib/core/widgets/customer_widgets.dart:291), and the [`state_panel.dart`](../mobile/lib/core/widgets/state_panel.dart:1) family (`StatePanel`, `Loading/Empty/Error/Unauthorized/Offline`). Total shared-widget references: ~49. Meanwhile the same private widgets are **copy-pasted per feature**:
   - `_SavedBanner` defined **5 times** (employees, number-series, otp-config, integrations, refund-replacement-config)
   - `_ErrorBanner` defined **5 times** (same five files)
   - `_SectionCard`, `_InfoTile`, `_MetricTile`, `_StatusChip` each re-implemented independently
   Conclusion: the *intent* to standardize is proven; the *coverage* is not.

3. **States are already standardized; visuals are not.**
   The `StatePanel` family is the single best-coverage pattern in the app (loading/empty/error/unauthorized/offline are consistently applied across ~139 call sites). This is the template to follow for the rest of the design system: **centralize once, apply everywhere, keep screens thin.**

**Customer experience gap (the strategic problem):** the customer journey is structurally a *forms-and-lists* app. The product has **no imagery anywhere**:
- [`CatalogueProduct`](../mobile/lib/features/catalogue/catalogue_models.dart:1) has no image, offer, discount, rating, or "freshness" field.
- [`_ProductTile`](../mobile/lib/features/catalogue/catalogue_screens.dart:133) is a `Card` + `ListTile` + `CircleAvatar(Icon)`.
- [`ProductDetailScreen`](../mobile/lib/features/catalogue/catalogue_screens.dart:190+) leads with a 72px Material icon.
- [`pubspec.yaml`](../mobile/pubspec.yaml:1) declares **no `assets:` section, no fonts, and no image-loading package** (`cached_network_image` absent).
- Hero/CTA surface is a single static [`DoodhHeroCard`](../mobile/lib/core/widgets/customer_widgets.dart:291) repeated identically in both guest and customer home.

So while the app is trustworthy *operationally*, it does not **merchandise**. A dairy consumer decides with their eyes ("is this fresh? is this real milk? what does the pack look like?"), and today the UI answers those questions with a teal circle icon.

**Staff experience:** staff screens are functional and permission-correct (e.g. [`_AdminTileGrid`](../mobile/lib/features/home/role_home_screen.dart:761), [`AdminDashboardScreen`](../mobile/lib/features/admin_reports/admin_report_screens.dart:14), [`EmployeeListScreen`](../mobile/lib/features/employees/employee_screens.dart:26)), but dense screens like [`delivery_screens.dart`](../mobile/lib/features/deliveries/delivery_screens.dart:1) (1,773 lines) and [`milk_test_screens.dart`](../mobile/lib/features/milk_testing/milk_test_screens.dart:1) (1,543 lines) mix UI chrome with heavy operational logic. The delivery module already contains **the best trust UI in the app** — [`_DeliveryProgress`/`_ProgressStage`](../mobile/lib/features/deliveries/delivery_screens.dart:946), [`_CustomerOtpCard`](../mobile/lib/features/deliveries/delivery_screens.dart:982), and [`_LiveLocationCard`](../mobile/lib/features/deliveries/delivery_screens.dart:1036) — which should be *promoted*, not rewritten.

**Bottom line:** the modernization is **not a rewrite**. It is (a) *finishing* the design system, (b) *enforcing* it over ad-hoc spacing/duplication, and (c) *adding a product-merchandising layer* (imagery, cards, pricing hierarchy, trust signals) that does not exist today — all without touching business rules.

---

## 2. Design Principles and UX Goals

**Principles**

1. **Fresh & trustworthy first.** Every screen that concerns milk should answer "is this fresh, pure, traceable, and safe?" visually — not only in text.
2. **Show, don't list.** Products get images, cards, and pricing hierarchy; statuses get color-coded pills and timelines.
3. **One design language, two densities.** Shared tokens/components; customer = spacious, image-led, 1 primary action; staff = compact, data-led, multi-action. Never force one onto the other.
4. **Preserve, don't rewrite.** Business rules, API contracts, RBAC gating, routes, and state machines are untouched. UI changes are presentational except for additive fields.
5. **Mobile-first, then genuinely responsive.** Design portrait-mobile first; tablet/desktop get real layouts (grids, master–detail), not stretched mobile columns.
6. **Reuse over rebuild.** Promote existing good patterns (`StatePanel`, delivery timeline, OTP card, status pill) instead of inventing new ones.
7. **Transparency as a feature.** Payment, delivery, milk-test, batch, and refund states are visible and explainable in the UI.

**Measurable goals**

| Goal | Current | Target |
|---|---|---|
| Screens using design tokens for spacing | low (195 raw EdgeInsets) | >90% via tokens |
| Shared components vs. one-off private widgets | duplicated (`_SavedBanner` ×5 etc.) | 0 duplicated banner/section/tile widgets |
| Product imagery coverage | 0% | 100% of sellable SKUs |
| Customer primary flow tap count (browse → pay) | baseline | −25% |
| Responsive breakpoints formalized | ad-hoc (<600) | 4 named breakpoints app-wide |
| Tablet/desktop product columns | 1 | 2 (tablet) / 3–4 (desktop) |

---

## 3. Current Design System Audit

### 3.1 What exists (baseline to build on)

| Token group | Location | Values |
|---|---|---|
| Colors | [`DoodhColors`](../mobile/lib/core/theme/doodh_theme.dart:4) | `ink #172321`, `muted #667572`, `teal #087F8C`, `tealDark #075E67`, `mint #E5F3EF`, `cream #FFFBF5`, `amber #F4B942`, `coral #D8664D`, `line #DDE7E3` |
| Spacing | [`DoodhSpacing`](../mobile/lib/core/theme/doodh_theme.dart:14) | `xs 4, sm 8, md 16, lg 24, xl 32` |
| Radii | [`DoodhRadii`](../mobile/lib/core/theme/doodh_theme.dart:22) | `sm 8, md 14, lg 20` |
| Theme | [`buildDoodhTheme()`](../mobile/lib/core/theme/doodh_theme.dart:30) | M3 seeded from teal, cream scaffold, 48px min buttons |

### 3.2 Gaps identified

1. **No semantic status colors.** Status colors are chosen ad hoc per screen (e.g. tone mapping inside [`_DeliveryHeader`](../mobile/lib/features/deliveries/delivery_screens.dart:900+)); there is no central `success/warning/danger/info/neutral` semantic set even though [`DoodhStatusTone`](../mobile/lib/core/widgets/customer_widgets.dart:104) already defines the *enum*.
2. **No full type scale.** Theme sets `textTheme` from seed but there is no explicit, documented scale (display/title/body/label sizes, weights, line-heights). Many widgets hand-pick `fontSize: 20/28` and `FontWeight.w800`.
3. **No elevation/press-token vocabulary.** Cards rely on default M3 elevation; some banners use tinted cards (`DoodhColors.mint`) as a de facto "success" surface.
4. **No iconography guidance** beyond Material Icons; icon sizes vary (`14, 18, 24, 28, 72`).
5. **No assets/fonts declared.** [`pubspec.yaml`](../mobile/pubspec.yaml:1) has no `assets:` block and no custom font family.
6. **Spacing is not enforced.** 195 `EdgeInsets` literals; common rogue values include `12, 14, 18, 20, 22, 28`.
7. **No skeleton/loading shimmer.** Loading is `CircularProgressIndicator` inside [`LoadingStatePanel`](../mobile/lib/core/widgets/state_panel.dart:1); lists "pop".
8. **No image treatment spec** (aspect ratio, placeholder, error, CDN sizing) — because no images exist yet.

---

## 4. Proposed DoodhDirect Design System

> Naming convention: extend the existing `Doodh*` namespace. Suggestions below are **proposals**; final names/values to be approved before implementation.

### 4.1 Color tokens (extend `DoodhColors`)

**Keep** the 9 existing brand colors as the identity. **Add**:

- Semantic: `success #1F9D6B`, `successSurface #E6F5EE`, `warning #E8A020`, `warningSurface #FDF3DF`, `danger #C7442F`, `dangerSurface #FBEAE7`, `info #2A7DBF`, `infoSurface #E9F2FA`.
- Surface/elevation: `surface`, `surfaceMuted`, `surfaceRaised`, `scrim`.
- Text: `textPrimary (ink)`, `textSecondary (muted)`, `textOnBrand (white)`.
- Data-zone accents for staff (subtle, hue-shifted but harmonized): `dairy #2F6F9F`, `delivery #7A5CC0` used only for module identity chips.

**Rules:** brand teal remains the single primary CTA color; amber for "attention/limited", coral for danger, mint for positive surfaces. No new brand hues.

### 4.2 Typography scale (proposed `DoodhType`)

| Token | Size / Weight / Line-height | Usage |
|---|---|---|
| `displayS` | 28 / w800 / 1.15 | Hero headline, big balance |
| `titleL` | 22 / w700 / 1.2 | Screen titles within `DoodhPage` |
| `titleM` | 17 / w700 / 1.25 | Section headers, product names |
| `bodyL` | 16 / w500 / 1.4 | Primary body |
| `bodyM` | 14 / w400 / 1.45 | Secondary body, list subtitles |
| `labelM` | 13 / w600 / 1.2 | Chips, pill labels, buttons |
| `labelS` | 11 / w600 / 1.2 (letterSpacing .3) | Overlines, SKU/unit meta |

Font decision: Phase 1 ships with the system font (`Roboto`/platform) to avoid risk; Phase 2 optionally bundles **one** geometric-humanist family (e.g. Manrope/Inter) for brand voice. Bundling a font requires a `pubspec` `fonts:` block and is additive/low-risk.

### 4.3 Spacing & layout

- Keep `DoodhSpacing` as the only spacing source. Add `xxs 2` and `xxl 48` plus semantic aliases: `screenInset 16`, `cardPadding 16`, `gutter 12`.
- Layout primitives: `DoodhGrid` (responsive column count) and `DoodhContentMax` (max content width, replacing the magic `1180` in [`DoodhPage`](../mobile/lib/core/widgets/customer_widgets.dart:8)).
- **Migration rule:** raw `EdgeInsets` values must map to tokens; a lint/review checklist enforces it. Rogue values resolve as: `12→sm+xxs(=10)` is **not** allowed — instead pick nearest token (`12→md-? ` → use `sm` or `md`).

### 4.4 Shape, elevation, motion

- Radii: keep `sm 8 / md 14 / lg 20`; add `pill 999`, `sheet 24` (top corners).
- Elevation: `flat 0`, `card 1`, `raised 2`, `overlay 3`; cards use border (`line`) + `card 1` rather than heavy shadows.
- Motion tokens: `fast 150ms`, `base 220ms`, `slow 320ms`, standard `Curves.easeOutCubic`; skeleton shimmer 1.2s loop. Respect `MediaQuery.disableAnimations`.

### 4.5 Iconography

- Material Icons only; sizes `16 / 20 / 24 / 32 / 56 (empty-state) / 72 (product fallback)`. Every interactive icon gets a semantics label; decorative icons excluded from semantics.
- Standard glyphs: freshness = `Icons.spa`/`water_drop`, purity = `Icons.verified`, traceability = `Icons.qr_code_2`, batch = `Icons.inventory_2_outlined`, route = `Icons.route`, OTP = `Icons.password`.

### 4.6 Component inventory (proposed additions)

**Primitives (new, ~15):**
`DoodhButton` (primary/secondary/tertiary/destructive/loading variants), `DoodhCard` (+ `interactive`), `DoodhChip`/`DoodhFilterChip`, `DoodhBadge` (count/dot), `DoodhField` (label/helper/error wrapper), `DoodhDropdown`, `DoodhDialog`, `DoodhBottomSheet`, `DoodhTabs`, `DoodhInfoBanner` (replaces all `_SavedBanner`/`_ErrorBanner`), `DoodhEmptyIllustration`, `DoodhSkeleton` (+ `DoodhSkeletonList`, `DoodhSkeletonCard`), `DoodhKeyValueRow` (replaces `_ProfileRow`/`_InfoTile`), `DoodhMetricTile` (replaces `_MetricTile`), `DoodhSectionCard` (replaces `_SectionCard`).

**Domain components (new, ~14):**
`DoodhProductCard`, `DoodhProductImage`, `DoodhPriceTag`, `DoodhQuantityStepper`, `DoodhCategoryRail`, `DoodhSearchBar`, `DoodhStatusTimeline` (generalized from delivery `_DeliveryProgress`), `DoodhOtpCard` (from `_CustomerOtpCard`), `DoodhTrustStrip`, `DoodhBatchTraceCard`, `DoodhMilkTestCard`, `DoodhDeliveryCard`, `DoodhWalletSummary`, `DoodhRoleScaffold` (staff chrome).

**Existing (keep, extend, migrate callers):**
[`DoodhPage`](../mobile/lib/core/widgets/customer_widgets.dart:8), [`DoodhSectionHeader`](../mobile/lib/core/widgets/customer_widgets.dart:34), [`DoodhActionTile`](../mobile/lib/core/widgets/customer_widgets.dart:51), [`DoodhStatusPill`](../mobile/lib/core/widgets/customer_widgets.dart:104), [`DoodhHeroCard`](../mobile/lib/core/widgets/customer_widgets.dart:291), [`CustomerShell`](../mobile/lib/core/widgets/customer_widgets.dart:156), [`state_panel.dart`](../mobile/lib/core/widgets/state_panel.dart:1) family.

---

## 5. Global Navigation & Information Architecture

**Current:** [`app.dart`](../mobile/lib/app/app.dart:1) registers a large flat GoRouter map; customers use [`CustomerShell`](../mobile/lib/core/widgets/customer_widgets.dart:156) with bottom nav **Home / Orders / Subscribe / Wallet / Profile** (`/home`, `/orders`, `/subscriptions`, `/wallet`, `/customer/account`). Staff use plain `Scaffold` + `AppBar` with per-role action clusters ([`RoleHomeScreen`](../mobile/lib/features/home/role_home_screen.dart:1)).

**Assessment:** the 5-tab customer bar is correct and conventional — keep it. Issues: (a) deep, inconsistent pushes (e.g. `/customer/addresses/new`, `/security/mobile`) with no breadcrumb/sub-header; (b) guest and authenticated home diverge structurally; (c) staff navigation is action-icon-heavy in the app bar (security, notifications, sign-out, role-specific) which crowds on small screens.

**Recommendations**
1. **Keep the 5 customer tabs**; add a persistent but subtle sub-header (`DoodhSubHeader`) on nested pages showing parent context + back affordance.
2. **Unify guest vs. customer Home** into one `HomeScreen` that renders a *guest variant* (login prompt + browse) vs *authenticated variant* — same layout skeleton, no divergent code paths.
3. **Staff: move to `DoodhRoleScaffold`** with a role-aware drawer/rail; keep only 1–2 primary actions in the app bar and consolidate the rest into the drawer. Preserve every existing route target and permission gate.
4. **Do not change route paths or guards** in the first phase — navigation work is presentational (chrome, back behavior, discoverability) so deep links and RBAC remain intact.
5. **Deep-link clarity:** ensure notification deep links land on a screen whose app bar title matches the notification topic (e.g. milk-test result, refund status), via `DoodhSubHeader`.

---

## 6. Customer Navigation

- **Bottom nav labels/icons:** keep; ensure selected/unselected icon pairs (`outlined` → `filled`) and 3-line-safe labels; badge on Orders for active deliveries.
- **Cart:** currently a FAB "Cart (n)" on catalogue ([`_ProductTile` screen](../mobile/lib/features/catalogue/catalogue_screens.dart:60+)). Promote to a **persistent cart affordance** in the catalogue app bar + a `DoodhBadge` count on the bottom-nav "Orders" or a dedicated cart sheet. Preserve the existing `guest_cart_storage` behavior.
- **Sub-navigation depth:** catalogue → product detail → cart → checkout → payment is 4 levels with no visual continuity. Introduce a **checkout progress indicator** (already conceptually present as `_CheckoutProgress` in [`order_screens.dart`](../mobile/lib/features/orders/order_screens.dart:1)) shown as a sticky stepper across cart/checkout/payment.
- **Return-intent:** the router already supports return-intent; surface it in UI with a "Return to checkout" affordance after auth, without altering the mechanism.

---

## 7. Home Screen (Customer & Guest) — redesign

**Current:** [`_CustomerHomeActions`](../mobile/lib/features/home/role_home_screen.dart:189) → `DoodhPage` → `RefreshIndicator`/`ListView` of `_GreetingHeader`, one [`DoodhHeroCard`](../mobile/lib/core/widgets/customer_widgets.dart:291), a 2–4 column "Your shortcuts" grid of `_QuickAction`, and `_HomeContextCards` (latest order + address + wallet tiles). Guest home ([`GuestHomeScreen`](../mobile/lib/features/home/role_home_screen.dart:896)) mirrors this with a login prompt card.

**Problems:** no freshness/trust signal above the fold; no product discovery (no featured/shortlist products — only 4 nav shortcuts); hero is identical every visit; no "next delivery" module even though subscriptions are the app's core recurring value.

**Proposed layout (top → bottom):**
1. `DoodhGreetingHeader` — name + delivery branch/address context, compact.
2. **Next delivery card** — from subscription/delivery data: date, product, quantity, live status pill, "Track" CTA. (Read-only reuse of existing data; no new business logic.)
3. **Trust strip** (`DoodhTrustStrip`) — 3–4 compact chips: "Lab-tested", "Farm fresh", "On-time tracked", "Pay via wallet/UPI". Static copy in Phase 1; can bind to milk-test/batch data later.
4. **Featured products rail** (`DoodhCategoryRail` variant) — horizontal `DoodhProductCard`s (image, name, ₹/unit, availability badge). Data source: existing catalogue list filtered to active/available; **no new API**.
5. **Reorders / "Your usual"** — derived from user's order history (existing order list).
6. **Shortcuts grid** — keep the existing `_QuickAction` items (Shop, My orders, Subscribe, Wallet), restyled.
7. **Context cards** — keep latest-order/address/wallet `DoodhActionTile`s.
8. Guest variant: same skeleton with a soft `DoodhInfoBanner` login prompt replacing next-delivery/reorders; preserves guest browsing.

---

## 8. Catalogue Screen — redesign

**Current:** [`ProductCatalogueScreen`](../mobile/lib/features/catalogue/catalogue_screens.dart:1) = `CustomerShell` + FAB "Cart (n)" + a `DropdownButtonFormField` category selector + `SliverList.separated` of [`_ProductTile`](../mobile/lib/features/catalogue/catalogue_screens.dart:133) (`Card` + `ListTile` + `CircleAvatar` icon).

**Problems:** no images; category via dropdown is a poor mobile pattern; no search; no sort/filter; list-only (no grid) so no visual merchandising; no availability-at-a-glance beyond detail screen.

**Proposed:**
1. **Search bar** (`DoodhSearchBar`) pinned at top — client-side filter first (existing in-memory data), server search later if contract allows.
2. **Category rail** (`DoodhCategoryRail`) — horizontal chips replacing the dropdown; "All" default. Preserve current category source.
3. **Availability filter chip** — "Available near me" using existing `branchAvailability`.
4. **Responsive product grid** (`DoodhGrid`): **1 col** phone portrait *or* 2-col compact cards on larger phones; **2 cols** tablet; **3–4 cols** desktop.
5. **`DoodhProductCard`** — image (primary), name, category/unit meta, `DoodhPriceTag` (`₹{price} / {unit}` from existing `formattedPrice`), availability badge, quantity stepper or "Add".
6. Keep the cart FAB but restyle as a `DoodhBadge`-driven bottom bar on mobile.
7. **Sort** (price/name) as a bottom sheet — client-side, additive.

---

## 9. Product Card & Product Details — redesign

**ProductCard (new):** fixed-aspect image (`1:1` or `4:3`), placeholder + error treatment, optional corner badge (e.g. "New"/"Popular" — only if backed by data), name (2-line clamp), unit + price hierarchy (`₹ price` large, `/unit` muted small), inline stepper or Add button, tap → detail. Must degrade gracefully when `imageUrl == null` (icon fallback with brand gradient) so it can ship **before** the backend adds image fields.

**Product Details — current:** [`ProductDetailScreen`](../mobile/lib/features/catalogue/catalogue_screens.dart:190+) shows a 72px icon, name, description, price, a `TextFormField` quantity (validator, ≤3 decimals), Add-to-cart `FilledButton.icon` + snackbar, then per-branch availability `ListTile`s.

**Proposed:**
1. **Hero image area** (image or branded fallback) with freshness/availability pill overlay.
2. **Sticky bottom action bar** — price + quantity stepper + primary "Add to cart" (thumb-reachable), replacing the mid-scroll button + snackbar. Preserve validator behavior and decimal rule.
3. **Trust panel** (`DoodhTrustStrip` + `DoodhBatchTraceCard`) — purity/testing/traceability teasers, linking into existing milk-test/batch screens where relevant.
4. **Branch availability** — convert `ListTile` list into `DoodhKeyValueRow`/chips showing per-branch availability + `maxDailyQuantity` (existing fields).
5. **Description** — collapsible, with unit spec table (`unitOfMeasure`, `sku`).
6. **Related products** rail (same category, client-side).

> **Backend dependency (flagged, additive only):** to realize real imagery, product APIs need an optional `imageUrl`(s) field on the catalogue product contract, plus asset hosting. Until then, the UI must render the branded fallback. This is a **contract addition**, not a change to existing fields or behavior.

---

## 10. Cart & Checkout — redesign

**Current:** [`CheckoutScreen`](../mobile/lib/features/orders/order_screens.dart:1) — `EmptyStatePanel` when empty; `_CheckoutProgress`; `OutlinedButton.icon` continue shopping; `_CheckoutSectionLabel(step,title,subtitle)`; cart items as `Card`+`ListTile` with `IconButton` +/- and centered quantity; address via `DropdownButtonFormField` + `_ManualAddressCard`.

**Proposed:**
1. **Cart sheet/screen** with `DoodhProductCard`-style line items (thumbnail, name, unit price, `DoodhQuantityStepper`), swipe-to-remove + explicit remove (accessibility), and a **sticky summary** (subtotal, delivery, total) — all values already computed by existing controllers.
2. **Free-delivery / minimum-order progress** — if business rules define thresholds, visualize via a progress bar (copy mirrored from backend rules; **no rule changes**).
3. **Checkout stepper** — promote `_CheckoutProgress` to a `DoodhStepHeader` used consistently across Cart → Address → Payment → Confirm.
4. **Address step** — replace dropdown with selectable `DoodhCard` address list + "Add new" (routes unchanged: `/customer/addresses/new`); show map-pin preview thumbnail (existing coordinate data).
5. **Payment step** — see §11.
6. **Review step** — explicit order summary before pay (order items, address, ETA, payment method), reducing accidental submissions.
7. Preserve: guest cart, validators, decimal quantity rule, address selection semantics, and all existing route targets.

---

## 11. Payment — redesign

**Current:** [`PaymentMethodScreen`](../mobile/lib/features/payments/payment_screens.dart:14) — dark `tealDark` "Ready to pay" banner (lock icon, order number, amount), wallet balance card when wallet selected, `RadioGroup<PaymentMethod>` of capability-derived `_PaymentMethodTile`s, error text, single `FilledButton.icon` ("Pay ₹…", spinner while processing), then navigates to `/payments/{id}/result`.

**Assessment:** functionally sound and already transparent. Gaps: capability tiles are plain radios; no fee/availability explanation; no explicit "what happens next"; result screen (`_PaymentResult`) is minimal.

**Proposed:**
1. **Amount summary card** — keep dark banner but add order number copy, ETA, and method-agnostic security note.
2. **Method tiles** → `DoodhCard` with brand mark/icon, label, availability state, and wallet balance inline; disabled states explained (not just hidden) — **only for capabilities the backend already reports as unavailable**.
3. **Wallet insufficient-balance** handling — show shortfall and offer top-up route (existing wallet flow), no rule change.
4. **Processing state** — full-width button with inline spinner + a `DoodhInfoBanner` "Do not close this screen"; keep `openRazorpayAndVerify()` untouched.
5. **Result screen** — success/failure `DoodhStatusTimeline`-style confirmation with order number, amount, method, next steps, and CTAs (View order / Retry if failure). Preserve the existing verification flow and status semantics exactly.

---

## 12. Orders & Order Details — redesign

**Current:** orders list is a `ListView.builder` of order cards; [`OrderDetailsScreen`](../mobile/lib/features/orders/order_screens.dart:777+) uses `DoodhSectionHeader` for **Your items / Payment / Delivery tracking / Support**, with delivery tracking and support as `Card`s. This is already close to target.

**Proposed:**
1. **Orders list** — `DoodhOrderCard`: status pill (tone from existing status mapping), order # copy, item thumbnails row (image fallback), total, date, primary contextual CTA ("Track" if in delivery, "Reorder" if delivered).
2. **Order details** — restructure into `DoodhSectionCard`s: *Status timeline* (reuse delivery timeline component at order granularity), *Items* (product rows w/ thumbnails), *Payment* (method, status, amount, transaction ref), *Delivery* (address, slot, OTP entry where applicable), *Support* (refund/replacement eligibility entry point — existing routes).
3. **Reorder** — one-tap "Reorder" that rebuilds the cart from the order's items (**additive client-side convenience using existing data**; confirm with backend that no rule prohibits).
4. Keep the existing refund/replacement entry point and eligibility display verbatim ([`CustomerRefundReplacementScreen`](../mobile/lib/features/refund_replacement/refund_replacement_screens.dart:160)) — only visual shell changes.

---

## 13. Subscription Screen — redesign

**Current:** [`subscription_screens.dart`](../mobile/lib/features/subscriptions/subscription_screens.dart:1) (1,043 lines) — setup + list + detail + delivery calendar. Heavy reuse of `DoodhSectionHeader`/`DoodhStatusPill`; dedup helpers for products/addresses already exist.

**Proposed:**
1. **List** — `DoodhSubscriptionCard`: plan summary (product, qty, frequency, next delivery date), status pill, pause/resume affordance (respecting existing state machine), tap → detail.
2. **Detail ("Your plan")** — keep `DoodhSectionHeader(title: 'Your plan')`; add a **delivery calendar** as a month grid with per-day status dots (data already in `state.calendar`), replacing the current flat list.
3. **Setup** — stepper (Product → Frequency → Quantity → Address → Schedule → Review) using `DoodhStepHeader`; preserve all validation and time-window rules.
4. **Empty state** — illustrated `EmptyStatePanel` with "Start a subscription" leading to catalogue.
5. **No change** to frequency/time-window/auto-consumption logic.

---

## 14. Wallet & Profile — redesign

**Wallet (current:** [`wallet_screens.dart`](../mobile/lib/features/wallet/wallet_screens.dart:1)) — `_BalancePanel` (₹ balance, 20px inner padding), `_TransactionTile` list with credit/debit `CircleAvatar`, top-up `AlertDialog`.
**Proposed:** `DoodhWalletSummary` (big balance, `displayS`, top-up primary CTA, "Add money" and "Auto-pay" explanation), transaction list with grouped-by-date headers, credit/debit semantic colors from the new token set, filter chips (All/Credits/Debits). Preserve top-up flow and Razorpay path exactly.

**Profile (current:** [`CustomerOverviewScreen`](../mobile/lib/features/customer/customer_screens.dart:21)) — `_ProfileSection` on mint card with `_ProfileRow`s; mobile/email rows link to `/security/*`; `_AddressSection` with `EmptyStatePanel` + add.
**Proposed:** header avatar block, `DoodhKeyValueRow`s for profile, address cards with map thumbnail + default badge, clear sections (Account / Addresses / Security / Preferences), notification preferences entry. Preserve all `/security/*` and address routes and their OTP flows verbatim.

---

## 15. Milk Test & Refund/Replacement UI — redesign

**Milk test (current:** [`milk_test_screens.dart`](../mobile/lib/features/milk_testing/milk_test_screens.dart:1), 1,543 lines) — image picking mixin, camera/gallery bottom sheet, preview dialog, decision pills (`DoodhStatusPill` at [line ~1139](../mobile/lib/features/milk_testing/milk_test_screens.dart:1139)).
**Proposed:** promote to `DoodhMilkTestCard` + `DoodhBatchTraceCard`; present test results as a **readable certificate-style card** (fat/SNF/parameters, decision, performer, timestamp, proof photo thumbnail) — strongly reinforces the "trust-first" goal. Extract the image-pick flow into a shared `DoodhImageCaptureSheet` (currently duplicated conceptually with refund/replacement's mixin in [`refund_replacement_screens.dart`](../mobile/lib/features/refund_replacement/refund_replacement_screens.dart:42)). Preserve `ApiByteUnauthenticatedException` handling and the protected-image auth rule exactly (never request protected images unauthenticated).

**Refund/Replacement (current:** [`refund_replacement_screens.dart`](../mobile/lib/features/refund_replacement/refund_replacement_screens.dart:1), 1,479 lines) — eligibility is server-authoritative; UI renders the answer and disables submission out of window; milk-test flow pre-linked and proof-optional.
**Proposed:** `DoodhStatusTimeline` for request lifecycle (requested → under review → approved/rejected → processed), `DoodhStatusPill` for state, `DoodhInfoBanner` for the window/eligibility explanation, proof image card. **Do not** move eligibility logic client-side; keep "server-authoritative" behavior and all disabled states.

---

## 16. Staff & Admin Screens

**Guiding rule:** professional, dense, status-driven. Reuse the *same tokens/primitives* with a compact density variant; do **not** apply the image-led customer aesthetic.

- **Role home ([`RoleHomeScreen`](../mobile/lib/features/home/role_home_screen.dart:1)):** introduce `DoodhRoleScaffold` (drawer/rail). Keep permission-gated tiles ([`_AdminTileGrid`](../mobile/lib/features/home/role_home_screen.dart:761)); add a compact branch switcher and "today at a glance" metrics.
- **Owner/Admin ([`AdminDashboardScreen`](../mobile/lib/features/admin_reports/admin_report_screens.dart:14)):** keep `_MetricGrid`/`_ModuleGrid`; make metrics `DoodhMetricTile`s with trend deltas; reports table → responsive (sticky header, horizontal scroll on compact — logic already exists at [line ~541](../mobile/lib/features/admin_reports/admin_report_screens.dart:541)); export action unchanged.
- **Employees ([`EmployeeListScreen`](../mobile/lib/features/employees/employee_screens.dart:26)) / Branches ([`BranchListScreen`](../mobile/lib/features/branches/branch_screens.dart:22)) / Number series ([`NumberSeriesListScreen`](../mobile/lib/features/setup/number_series_screens.dart:15)) / OTP config / Integrations / Refund config:** replace per-file `_SavedBanner`+`_ErrorBanner`+`_SectionCard` duplicates with shared `DoodhInfoBanner` + `DoodhSectionCard`; convert `Card`+`ListTile` rows to `DoodhListRow` with status `DoodhStatusPill`. **Zero permission-logic changes.**
- **Dairy ([`dairy_screens.dart`](../mobile/lib/features/dairy/dairy_screens.dart:1)):** keep `_MetricGrid`; promote `_MetricTile` to shared `DoodhMetricTile`; batch/usage tabs → `DoodhTabs`.
- **Delivery ([`delivery_screens.dart`](../mobile/lib/features/deliveries/delivery_screens.dart:1)):** this module holds the app's best components. Extract `_DeliveryProgress`/`_ProgressStage` → `DoodhStatusTimeline`, `_CustomerOtpCard` → `DoodhOtpCard`, `_LiveLocationCard` → `DoodhInfoCard`, `_InfoTile` → `DoodhKeyValueRow`. Preserve delivery OTP, failed-state handling, and location logic exactly.
- **Milk test (staff) / linked order-delivery-milk-test inspection:** keep dense; use `DoodhSectionCard` + timeline.
- **Setup / Cameras / Security:** design for consistency (same banners, cards, states), no logic change.

---

## 17. Responsive Strategy

**Current:** ad-hoc. [`DoodhPage`](../mobile/lib/core/widgets/customer_widgets.dart:8) uses `constraints.maxWidth < 600 ? 16 : 28` padding and `maxWidth: 1180`; a few screens branch at `720`.

**Proposed named breakpoints (`DoodhBreakpoints`):**

| Name | Range | Layout |
|---|---|---|
| `compact` | 0–599 | 1 col (or 2 compact product cols on ≥480), bottom nav, full-bleed sheets |
| `medium` | 600–899 | 2-col product grid, 2-col forms, navigation rail **or** bottom nav |
| `expanded` | 900–1279 | 2–3 col grids, master–detail (list + detail pane) |
| `large` | ≥1280 | 3–4 col grids, content max-width 1180–1280, persistent nav rail |

**Rules**
1. Implement `DoodhGrid` (childAspectRatio + column count per breakpoint) and `DoodhContentMax`; migrate `DoodhPage` internals to them.
2. **Tables (staff reports)** become card lists on `compact`, real tables on `medium+`.
3. **Master–detail** for catalogue, orders, deliveries, milk tests on `expanded+` (list + right pane) — using `DoodhPage` split; keeps route targets for deep links (selecting sets the detail route).
4. Forms cap at `DoodhContentMax` (~560–680) and center on wide screens.
5. Test matrix in §17 conclusion: portrait phone, landscape phone, tablet portrait/landscape, desktop/web 1280+.

---

## 18. Accessibility, Loading/Empty/Error/Offline, Consistency

**Accessibility**
- Enforce min touch target **48×48** (theme already sets button min-height 48; extend to icon buttons, steppers, chips, list rows).
- Provide `Semantics` labels for icons, images (`imageUrl` alt text), quantity steppers ("Increase quantity of X"), status pills ("Status: Out for delivery"), and timelines.
- Contrast: verify `muted` on `mint`/`cream` and white-on-`teal` meet WCAG AA; add `textOnBrand` and darkened `muted` if needed.
- Dynamic type: cap layouts at ~1.3× scale without overflow (test with `textScaler`); avoid fixed-height text containers.
- Color-independence: never convey status by color alone — pill text always present (already true for `DoodhStatusPill`).
- Reduced motion: honor `MediaQuery.disableAnimations` for shimmer/transitions.

**States**
- **Loading:** introduce skeletons (`DoodhSkeletonList`/`DoodhSkeletonCard`) for list/detail first-loads; keep `LoadingStatePanel` for blocking actions. This reduces layout shift vs. current spinner-only approach.
- **Empty:** keep `EmptyStatePanel`; add lightweight illustration + a single clear action; ensure every list has a tailored empty copy (several admin lists already do).
- **Error:** keep `ErrorStatePanel` with retry; add error codes/traceability hint where the API provides them.
- **Offline:** keep `OfflineStatePanel`; consider a global connectivity banner for staff.
- **Unauthorized:** keep `UnauthorizedStatePanel`; ensure every permission-gated screen renders it rather than an empty screen (some already do, e.g. [`AdminDashboardScreen`](../mobile/lib/features/admin_reports/admin_report_screens.dart:53)).
- **Success:** standardize to a `DoodhInfoBanner(success)` / snackbar pattern (replacing per-feature `_SavedBanner`).

**Consistency**
- Single source for banners, section cards, key-value rows, metric tiles, status pills, dialogs, sheets, steppers.
- Consistent currency format: reuse existing `formattedPrice` (`₹{price} / {unit}`) and `formattedTotal` everywhere; do not introduce a second formatter.
- Consistent date/time: keep using [`india_time.dart`](../mobile/lib/core/time/india_time.dart:1) helpers.

---

## 19. Recommended Implementation Phases

> All phases are **UI-only or additive**; business rules, API contracts (except additive `imageUrl`), RBAC, routes, and state machines remain unchanged. Each phase should compile and be shippable independently.

**Phase 0 — Foundations (no visible change)**
- Add semantic colors, `DoodhType` scale, extended spacing aliases, elevation/motion tokens, `DoodhBreakpoints`, `DoodhGrid`, `DoodhContentMax`.
- Add shared primitives: `DoodhInfoBanner`, `DoodhSectionCard`, `DoodhKeyValueRow`, `DoodhMetricTile`, `DoodhButton`, `DoodhCard`, `DoodhChip`, `DoodhSkeleton*`.
- Delete duplicated `_SavedBanner`/`_ErrorBanner`/`_SectionCard`/`_InfoTile`/`_MetricTile` in favor of shared ones. **Pure refactor, behavior-identical.**

**Phase 1 — Customer product experience (highest business impact)**
- `DoodhProductCard` + branded fallback image; catalogue search + category rail + availability filter + responsive grid.
- Product details: hero, sticky add-to-cart, trust panel, branch availability chips.
- Home: next-delivery card, trust strip, featured rail, reorders.
- **Requires** additive backend `imageUrl` field for full effect; ships with fallback until then.

**Phase 2 — Cart → Payment → Order result**
- Cart sheet with stepper + sticky summary; checkout stepper; address cards; payment card redesign + result screen; order details restructure; one-tap reorder.

**Phase 3 — Trust & traceability surfaces**
- Extract `DoodhStatusTimeline`, `DoodhOtpCard`, `DoodhBatchTraceCard`, `DoodhMilkTestCard`, `DoodhImageCaptureSheet`; apply to delivery, milk test, refund/replacement, batch views.
- Subscription calendar grid + subscription card; wallet summary + grouped transactions; profile redesign.

**Phase 4 — Staff/admin consolidation & responsive**
- `DoodhRoleScaffold`; migrate employees/branches/series/otp/integrations/refund-config to shared banners/cards; dairy/delivery/admin metrics to shared tiles; responsive tables + master–detail on expanded+.

**Phase 5 — Polish & a11y**
- Accessibility pass (touch targets, semantics, contrast, dynamic type, reduced motion); skeleton coverage; optional brand font; motion polish; empty-state illustrations.

**Per-phase acceptance criteria:** `flutter analyze` clean; existing tests ([`mobile/test`](../mobile/test)) still pass; all permission-gated screens still render correct authorized/unauthorized states; all deep links still resolve; no API contract change except the flagged additive image field.

---

## P0–P3 Screen Priority Matrix

### P0 — Critical usability / navigation

| Screen | Current problem | Proposed solution | UX benefit | Complexity | Reusable component |
|---|---|---|---|---|---|
| Catalogue | No search; category dropdown; list-only | Search + category rail + availability filter + responsive grid | Finds product fast; scalable | Med | `DoodhSearchBar`, `DoodhCategoryRail`, `DoodhGrid` |
| Product Details | No image; add-to-cart mid-scroll + snackbar | Hero image, sticky action bar, trust panel | Trust + fewer missed taps | Med | `DoodhProductImage`, `DoodhQuantityStepper`, `DoodhTrustStrip` |
| Cart/Checkout | Single long form; dropdown address | Stepper (Cart→Address→Pay→Review), address cards, summary | Fewer errors/abandonment | Med-High | `DoodhStepHeader`, `DoodhKeyValueRow` |
| Customer Home | No next-delivery; no discovery | Next-delivery card, trust strip, featured rail | Core value visible immediately | Med | `DoodhTrustStrip`, `DoodhProductCard` |
| Payment (insufficient wallet) | Hidden methods; no shortfall guidance | Explain unavailable methods; top-up path | Transparency; fewer failures | Low-Med | `DoodhInfoBanner` |

### P1 — High-impact visual/UX

| Screen | Current problem | Proposed solution | UX benefit | Complexity | Reusable component |
|---|---|---|---|---|---|
| Orders list/details | Flat, no thumbnails/reorder | `DoodhOrderCard` + section cards + reorder | Repeat purchase faster | Med | `DoodhOrderCard`, `DoodhSectionCard` |
| Payment result | Minimal confirmation | Status timeline + next steps | Reduces "did it work?" support | Low | `DoodhStatusTimeline` |
| Delivery | Best-in-app timeline but private | Extract & reuse; polish cards | Consistency + trust | Low-Med | `DoodhStatusTimeline`, `DoodhOtpCard` |
| Milk test | Dense, no certificate view | Certificate card + trace card | Purity transparency | Med | `DoodhMilkTestCard`, `DoodhBatchTraceCard` |
| Refund/Replacement | Text-heavy lifecycle | Status timeline + banners | Clarity of outcome | Low-Med | `DoodhStatusTimeline`, `DoodhInfoBanner` |
| Wallet | Plain balance + list | Summary + grouped transactions | Comprehension of money | Low | `DoodhWalletSummary` |
| Profile | Dense key-values | Sectioned profile + address cards | Easier management | Low | `DoodhKeyValueRow`, `DoodhSectionCard` |
| Subscription | Flat calendar list | Month-grid calendar + card | Predictable plan view | Med | `DoodhSubscriptionCard` |
| Guest home | Divergent layout | Unify with customer skeleton | Consistent first impression | Low | shared Home skeleton |
| Staff/admin lists | 5× duplicated banners/cards | Shared banner/section/list rows | Consistency + maintainability | Low | `DoodhInfoBanner`, `DoodhSectionCard` |

### P2 — Worthwhile refinements

- Skeleton loading across all lists/details.
- Sort bottom sheet on catalogue; related products on detail.
- Free-delivery/ minimum-order progress on cart (mirrors backend rule).
- Notification inbox: category chips + grouped read/unread.
- Admin report tables: sticky headers + density toggle.
- Empty-state illustrations per feature.
- Guest→login conversion banner with benefit copy (no flow change).

### P3 — Optional polish

- Optional bundled brand font; subtle motion (hero parallax, card press).
- Micro-animations on status changes (delivery stage advance).
- Dark mode variant using the same tokens (not requested; future option).
- Home personalization (frequently ordered rail).
- Haptics on critical actions (payment success, OTP confirm).

---

## Guardrails (explicitly preserved — unchanged by this plan)

- **Auth/OTP:** OTP-first login, [`otp_screen.dart`](../mobile/lib/features/auth/otp_screen.dart:1), onboarding, platform launchers, retries.
- **Payments:** Razorpay open+verify flow ([`payment_screens.dart`](../mobile/lib/features/payments/payment_screens.dart:189)), payment status semantics, `usesRazorpay`/pending logic.
- **Wallet:** balances, top-up, credit/debit semantics.
- **Orders/subscriptions:** creation, frequency, time windows, auto-consumption.
- **Branch isolation & RBAC:** every permission gate (e.g. `EMPLOYEES.MANAGE`, `BRANCHES.MANAGE`, `SETUP.NUMBER_SERIES.MANAGE`) and branch-scoping preserved.
- **Delivery:** OTP, proof-of-delivery, live location, failed-delivery handling.
- **Milk test:** decision logic, batch allocation, protected-image authenticated access (`ApiByteUnauthenticatedException`).
- **Refund/Replacement:** server-authoritative eligibility window, milk-test-linked flow.
- **API contracts:** no breaking changes; only the flagged **additive** `imageUrl` field is proposed, and the UI ships with a fallback until it exists.
- **Routes/deep links:** [`app.dart`](../mobile/lib/app/app.dart:1) paths and guards untouched in all phases.

---

## Recommendation

Proceed with **Phase 0 + Phase 1 first**: foundation tokens/components plus the customer product experience. That sequence delivers the largest visible modernization (imagery, product cards, home discovery, details, checkout) while remaining a *presentational* change set that cannot regress business behavior. Phase 0 also removes the duplicated banner/section widgets, which shrinks every later phase.

**STOP — awaiting approval before any implementation.**
