# DoodhDirect ERD

## Core entities

User, Role, Permission, UserRole, RolePermission, Customer, CustomerAddress, Branch, CustomerBranchAssignment, Product, ProductBranch, Orders, OrderItem, Subscription, SubscriptionSchedule, SubscriptionDelivery, Delivery, DeliveryLocation, DeliveryOTP, Payment, PaymentWebhook, Wallet, WalletTransaction, MilkProduction, MilkBatch, MilkTest, MilkTestParameter, MilkTestDevice, Complaint, ComplaintAttachment, Replacement, Coupon, CouponUsage, Referral, Camera, CameraStream, Notification, UserDevice, AuditLog, SystemConfiguration.

## Core relationships

Customer -> Addresses, Orders, Subscriptions, Wallet, Complaints.
Branch -> Orders, Subscriptions, Products, Deliveries, MilkProduction, MilkBatch, Cameras.
Order -> OrderItems, Delivery, Payments, Complaints.
Subscription -> SubscriptionSchedule, SubscriptionDelivery.
Complaint -> Attachments, Replacement.
MilkProduction -> MilkBatch.
MilkBatch -> MilkTests.
MilkTest -> TestParameters.
User -> Roles -> Permissions.

Use the SQL starter plus this conceptual model as the baseline. The final EF Core model must add explicit foreign keys, indexes, constraints and migration history.
