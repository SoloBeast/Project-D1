# DoodhDirect ERD

## Core entities

User, Role, Permission, UserRole, RolePermission, Customer, CustomerAddress, Branch, CustomerBranchAssignment, Product, ProductBranch, Orders, OrderItem, Subscription, SubscriptionSchedule, SubscriptionDelivery, Delivery, DeliveryLocation, DeliveryOTP, Payment, PaymentWebhook, Wallet, WalletTransaction, MilkProduction, MilkBatch, MilkTest, MilkTestParameter, MilkTestImage, MilkTestDevice, Complaint, ComplaintAttachment, Replacement, Coupon, CouponUsage, Referral, Camera, CameraStream, Notification, UserDevice, AuditLog, SystemConfiguration.

## Core relationships

Customer -> Addresses, Orders, Subscriptions, Wallet, Complaints, MilkTests.
Branch -> Orders, Subscriptions, Products, Deliveries, MilkProduction, MilkBatch, MilkTests, Cameras.
Order -> OrderItems, Delivery, Payments, Complaints.
Subscription -> SubscriptionSchedule, SubscriptionDelivery.
Complaint -> Attachments, Replacement.
MilkProduction -> MilkBatch.
Delivery -> zero or one MilkTest.
MilkTest -> one or more MilkTestParameters after completion.
MilkTest -> one or more MilkTestImages after completion; image rows contain metadata and an external storage key, never binary content.
MilkTest -> requesting Customer, Branch, requesting User, and optional completing User.
User -> Roles -> Permissions.
User -> uploaded MilkTestImages and MilkTest audit events.

Use the SQL starter plus this conceptual model as the baseline. The final EF Core model must add explicit foreign keys, indexes, constraints and migration history.
