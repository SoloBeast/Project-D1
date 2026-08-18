# System Administrator Runtime Debug Trace

## Entry

`SYSTEM_ADMIN` maps to `UserRole.admin`. Seeded permissions include broad administration and `ACCESS.GLOBAL`. The home conditionally shows reports and cameras by permission, always shows catalogue management/preview, and shows branch dairy/delivery cards only when branch IDs are present.

Important mismatch: global access does not create a branch ID in the Flutter session. Therefore globally authorized admin operations may exist in the API while branch-specific home cards remain hidden when `branchIds` is empty.

## Runtime Chains

- `/admin` -> report module discovery from local permission mapping -> dashboard GET -> branch-aware/global [ReportService](../Backend/src/DoodhDirect.Infrastructure/Reports/ReportService.cs:22).
- Report module -> filters/search/date/status/page -> module GET -> page state; export -> byte endpoint -> report saver.
- `/admin/catalogue` -> admin products/categories/branches -> dialogs create/update/activate/availability -> catalogue service and catalogue tables.
- `/admin/cameras` -> managed list/create/update -> actor/global branch checks -> `Camera`, `CameraStream`, audit.
- `/dairy/dashboard` and delivery management are visible only with a session branch ID, despite API global access.

`NOT FOUND IN CURRENT IMPLEMENTATION`: Flutter user/role assignment administration, audit-specific detail UI, and admin order list navigation from home.

## Breakpoints

- [AdminDashboardScreen](../mobile/lib/features/admin_reports/admin_report_screens.dart:13)
- [AdminReportScreen](../mobile/lib/features/admin_reports/admin_report_screens.dart:105)
- [AdminCatalogueScreen](../mobile/lib/features/catalogue/catalogue_screens.dart:276)
- [AdminCameraListScreen](../mobile/lib/features/cameras/camera_screens.dart:311)
- [ReportService.ExportAsync()](../Backend/src/DoodhDirect.Infrastructure/Reports/ReportService.cs:89)
