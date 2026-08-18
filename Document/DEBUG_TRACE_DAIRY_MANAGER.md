# Dairy Manager Runtime Debug Trace

## Entry

`DAIRY_MANAGER` maps to `UserRole.dairy`. The home reads the first JWT branch; no branch shows an unavailable state. `/dairy/dashboard` initializes the branch from session or route context.

## Runtime Chains

| Action | Chain and persistence |
|---|---|
| Dashboard | GET branch dashboard -> [DairyService.GetDashboardAsync()](../Backend/src/DoodhDirect.Infrastructure/Dairy/DairyService.cs:15) -> aggregate production/batch/usage data |
| Record production | form/date/quantity -> POST production -> branch check -> create `MilkProduction`, save, create linked `MilkBatch`, save/commit -> navigate/reload |
| Production history | GET filtered branch history -> no-tracking query -> list |
| Batches | GET branch batches; tap opens `/dairy/batches/{id}` -> batch detail |
| Availability | GET availability -> aggregate available/remaining quantities |
| Record usage | batch detail/usage form -> POST usage -> branch/batch/status/remaining checks -> `MilkUsage` plus batch quantity transition in transaction |
| Usage history | GET branch or batch usage -> list |

## Scope and Validation

`DAIRY.READ` permits reads and `DAIRY.MANAGE` permits writes. [DairyService](../Backend/src/DoodhDirect.Infrastructure/Dairy/DairyService.cs:14) requires branch membership unless actor has global access. Quantities must be positive; production dates and duplicate daily/product constraints are enforced by service/domain/database behavior; usage cannot exceed remaining available quantity or target a non-available batch.

## Breakpoints

- [DairyDashboardScreen](../mobile/lib/features/dairy/dairy_screens.dart:10)
- [DairyProductionEntryScreen](../mobile/lib/features/dairy/dairy_screens.dart:171)
- [DairyUsageEntryScreen](../mobile/lib/features/dairy/dairy_screens.dart:687)
- [DairyService.RecordProductionAsync()](../Backend/src/DoodhDirect.Infrastructure/Dairy/DairyService.cs:42)
- [DairyService.RecordUsageAsync()](../Backend/src/DoodhDirect.Infrastructure/Dairy/DairyService.cs:170)
