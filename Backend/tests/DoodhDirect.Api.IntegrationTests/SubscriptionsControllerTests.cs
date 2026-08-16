using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionsControllerTests
{
    [Fact]
    public void Controller_ExposesOnlyExpectedCustomerRoutes()
    {
        var controllerType = typeof(SubscriptionsController);
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/v1/subscriptions", route.Template);

        var expected = new Dictionary<string, (string Method, string? Template)>
        {
            [nameof(SubscriptionsController.Create)] = ("POST", null),
            [nameof(SubscriptionsController.GetMine)] = ("GET", null),
            [nameof(SubscriptionsController.Get)] = ("GET", "{subscriptionId:guid}"),
            [nameof(SubscriptionsController.RetryPayment)] = ("POST", "{subscriptionId:guid}/retry-payment"),
            [nameof(SubscriptionsController.Update)] = ("PATCH", "{subscriptionId:guid}"),
            [nameof(SubscriptionsController.Pause)] = ("POST", "{subscriptionId:guid}/pause"),
            [nameof(SubscriptionsController.Resume)] = ("POST", "{subscriptionId:guid}/resume"),
            [nameof(SubscriptionsController.Cancel)] = ("POST", "{subscriptionId:guid}/cancel"),
            [nameof(SubscriptionsController.Skip)] = ("POST", "{subscriptionId:guid}/skip"),
            [nameof(SubscriptionsController.GetCalendar)] = ("GET", "{subscriptionId:guid}/calendar")
        };
        var actions = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<HttpMethodAttribute>() is not null)
            .ToArray();

        Assert.Equal(expected.Count, actions.Length);
        foreach (var action in actions)
        {
            var attribute = Assert.IsAssignableFrom<HttpMethodAttribute>(
                action.GetCustomAttribute<HttpMethodAttribute>());
            var contract = expected[action.Name];
            Assert.Equal(contract.Method, Assert.Single(attribute.HttpMethods));
            Assert.Equal(contract.Template, attribute.Template);
        }

        Assert.DoesNotContain(actions, action => action.Name is "MarkDeliveryFailed" or "MarkDeliveryDelivered");
    }

    [Theory]
    [InlineData(nameof(SubscriptionsController.Create), AuthorizationCodes.SubscriptionsCreateOwn)]
    [InlineData(nameof(SubscriptionsController.GetMine), AuthorizationCodes.SubscriptionsReadOwn)]
    [InlineData(nameof(SubscriptionsController.Get), AuthorizationCodes.SubscriptionsReadOwn)]
    [InlineData(nameof(SubscriptionsController.RetryPayment), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.Update), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.Pause), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.Resume), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.Cancel), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.Skip), AuthorizationCodes.SubscriptionsManageOwn)]
    [InlineData(nameof(SubscriptionsController.GetCalendar), AuthorizationCodes.SubscriptionsReadOwn)]
    public void Action_RequiresExpectedPermissionAndIsNotAnonymous(string methodName, string permission)
    {
        var method = typeof(SubscriptionsController).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);

        var authorize = Assert.Single(Assert.IsType<AuthorizeAttribute[]>(
            method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)));
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Theory]
    [InlineData(nameof(SubscriptionsController.Create))]
    [InlineData(nameof(SubscriptionsController.RetryPayment))]
    public void PaymentAction_IdempotencyKeyIsRequiredHeaderWithBoundedLength(string methodName)
    {
        var method = typeof(SubscriptionsController).GetMethod(methodName);
        var parameter = Assert.Single(method!.GetParameters(), value => value.Name == "idempotencyKey");
        var header = Assert.IsType<FromHeaderAttribute>(parameter.GetCustomAttribute<FromHeaderAttribute>());

        Assert.Equal("Idempotency-Key", header.Name);
        Assert.NotNull(parameter.GetCustomAttribute<RequiredAttribute>());
        Assert.Equal(100, parameter.GetCustomAttribute<MaxLengthAttribute>()!.Length);
    }

    [Fact]
    public async Task Actions_ForwardAuthenticatedCustomerAndReturnSuccessEnvelopes()
    {
        const long customerId = 73;
        var service = new CapturingSubscriptionService();
        var controller = CreateController(service, customerId.ToString());
        var subscriptionId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var createRequest = new CreateSubscriptionRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1.5m,
            new DateOnly(2026, 8, 17),
            [DayOfWeek.Monday, DayOfWeek.Thursday],
            8,
            PaymentMethod.Wallet);
        var retryRequest = new RetrySubscriptionPaymentRequest(PaymentMethod.Razorpay);
        var updateRequest = new UpdateSubscriptionRequest(2m, Guid.NewGuid(), [DayOfWeek.Tuesday]);
        var skipRequest = new SkipSubscriptionDeliveryRequest(deliveryId);

        var created = AssertSuccess(
            await controller.Create(createRequest, "subscription-73-1", CancellationToken.None),
            StatusCodes.Status201Created);
        Assert.Same(service.CreatedResult, created);
        Assert.Equal((customerId, createRequest, "subscription-73-1"), service.CreateCall);

        Assert.Same(service.Subscriptions, AssertSuccess(
            await controller.GetMine(CancellationToken.None), StatusCodes.Status200OK));
        Assert.Equal(customerId, service.GetMineCustomerId);

        Assert.Same(service.Subscription, AssertSuccess(
            await controller.Get(subscriptionId, CancellationToken.None), StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId), service.GetCall);

        Assert.Same(service.CreatedResult, AssertSuccess(
            await controller.RetryPayment(
                subscriptionId,
                retryRequest,
                "subscription-retry-73-1",
                CancellationToken.None),
            StatusCodes.Status201Created));
        Assert.Equal(
            (customerId, subscriptionId, retryRequest, "subscription-retry-73-1"),
            service.RetryCall);

        Assert.Same(service.Subscription, AssertSuccess(
            await controller.Update(subscriptionId, updateRequest, CancellationToken.None),
            StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId, updateRequest), service.UpdateCall);

        Assert.Same(service.Subscription, AssertSuccess(
            await controller.Pause(subscriptionId, CancellationToken.None), StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId), service.PauseCall);

        Assert.Same(service.Subscription, AssertSuccess(
            await controller.Resume(subscriptionId, CancellationToken.None), StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId), service.ResumeCall);

        Assert.Same(service.Subscription, AssertSuccess(
            await controller.Cancel(subscriptionId, CancellationToken.None), StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId), service.CancelCall);

        Assert.Same(service.Delivery, AssertSuccess(
            await controller.Skip(subscriptionId, skipRequest, CancellationToken.None),
            StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId, skipRequest), service.SkipCall);

        Assert.Same(service.Deliveries, AssertSuccess(
            await controller.GetCalendar(subscriptionId, CancellationToken.None),
            StatusCodes.Status200OK));
        Assert.Equal((customerId, subscriptionId), service.CalendarCall);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task GetMine_WithMissingOrInvalidUserIdClaim_IsUnauthorized(string? userId)
    {
        var service = new CapturingSubscriptionService();
        var controller = CreateController(service, userId);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            controller.GetMine(CancellationToken.None));

        Assert.Null(service.GetMineCustomerId);
    }

    private static T AssertSuccess<T>(ActionResult<ApiResponse<T>> response, int expectedStatusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(expectedStatusCode, objectResult.StatusCode);
        var envelope = Assert.IsType<ApiResponse<T>>(objectResult.Value);
        Assert.True(envelope.Success);
        Assert.Null(envelope.Message);
        Assert.Empty(envelope.Errors);
        return Assert.IsAssignableFrom<T>(envelope.Data);
    }

    private static SubscriptionsController CreateController(
        ISubscriptionService service,
        string? userId)
    {
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };
        return new SubscriptionsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingSubscriptionService : ISubscriptionService
    {
        private static readonly DateTime CreatedAtUtc = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        public SubscriptionResult Subscription { get; } = CreateSubscriptionResult();
        public SubscriptionDeliveryResult Delivery { get; } = CreateDeliveryResult();
        public IReadOnlyList<SubscriptionResult> Subscriptions { get; }
        public IReadOnlyList<SubscriptionDeliveryResult> Deliveries { get; }
        public CreatedSubscriptionResult CreatedResult { get; }
        public (long CustomerId, CreateSubscriptionRequest Request, string IdempotencyKey)? CreateCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId, RetrySubscriptionPaymentRequest Request, string IdempotencyKey)? RetryCall { get; private set; }
        public long? GetMineCustomerId { get; private set; }
        public (long CustomerId, Guid SubscriptionId)? GetCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId, UpdateSubscriptionRequest Request)? UpdateCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId)? PauseCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId)? ResumeCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId)? CancelCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId, SkipSubscriptionDeliveryRequest Request)? SkipCall { get; private set; }
        public (long CustomerId, Guid SubscriptionId)? CalendarCall { get; private set; }

        public CapturingSubscriptionService()
        {
            Subscriptions = [Subscription];
            Deliveries = [Delivery];
            CreatedResult = new CreatedSubscriptionResult(
                Subscription,
                new PaymentResult(
                    Guid.NewGuid(),
                    null,
                    null,
                    PaymentMethod.Wallet,
                    PaymentStatus.Success,
                    Subscription.PayableAmount,
                    0m,
                    "INR",
                    null,
                    "wallet_debited",
                    null,
                    null,
                    null,
                    CreatedAtUtc.AddMinutes(15),
                    CreatedAtUtc,
                    CreatedAtUtc,
                    Subscription.PublicId));
        }

        public Task<CreatedSubscriptionResult> CreateAsync(
            long customerId,
            CreateSubscriptionRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            CreateCall = (customerId, request, idempotencyKey);
            return Task.FromResult(CreatedResult);
        }

        public Task<IReadOnlyList<SubscriptionResult>> GetForCustomerAsync(
            long customerId,
            CancellationToken cancellationToken)
        {
            GetMineCustomerId = customerId;
            return Task.FromResult(Subscriptions);
        }

        public Task<SubscriptionResult> GetAsync(
            long customerId,
            Guid subscriptionId,
            CancellationToken cancellationToken)
        {
            GetCall = (customerId, subscriptionId);
            return Task.FromResult(Subscription);
        }

        public Task<CreatedSubscriptionResult> RetryPaymentAsync(
            long customerId,
            Guid subscriptionId,
            RetrySubscriptionPaymentRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            RetryCall = (customerId, subscriptionId, request, idempotencyKey);
            return Task.FromResult(CreatedResult);
        }

        public Task<SubscriptionResult> UpdateAsync(
            long customerId,
            Guid subscriptionId,
            UpdateSubscriptionRequest request,
            CancellationToken cancellationToken)
        {
            UpdateCall = (customerId, subscriptionId, request);
            return Task.FromResult(Subscription);
        }

        public Task<SubscriptionResult> PauseAsync(
            long customerId,
            Guid subscriptionId,
            CancellationToken cancellationToken)
        {
            PauseCall = (customerId, subscriptionId);
            return Task.FromResult(Subscription);
        }

        public Task<SubscriptionResult> ResumeAsync(
            long customerId,
            Guid subscriptionId,
            CancellationToken cancellationToken)
        {
            ResumeCall = (customerId, subscriptionId);
            return Task.FromResult(Subscription);
        }

        public Task<SubscriptionResult> CancelAsync(
            long customerId,
            Guid subscriptionId,
            CancellationToken cancellationToken)
        {
            CancelCall = (customerId, subscriptionId);
            return Task.FromResult(Subscription);
        }

        public Task<SubscriptionDeliveryResult> SkipAsync(
            long customerId,
            Guid subscriptionId,
            SkipSubscriptionDeliveryRequest request,
            CancellationToken cancellationToken)
        {
            SkipCall = (customerId, subscriptionId, request);
            return Task.FromResult(Delivery);
        }

        public Task<IReadOnlyList<SubscriptionDeliveryResult>> GetCalendarAsync(
            long customerId,
            Guid subscriptionId,
            CancellationToken cancellationToken)
        {
            CalendarCall = (customerId, subscriptionId);
            return Task.FromResult(Deliveries);
        }

        public Task MarkDeliveryFailedAsync(long deliveryId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkDeliveryDeliveredAsync(long deliveryId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static SubscriptionResult CreateSubscriptionResult() => new(
            Guid.NewGuid(),
            SubscriptionStatus.Active,
            Guid.NewGuid(),
            "MILK-1L",
            "Whole Milk",
            "litre",
            1.5m,
            60m,
            720m,
            new DateOnly(2026, 8, 17),
            new DateOnly(2026, 9, 10),
            8,
            0,
            8,
            Guid.NewGuid(),
            "Home, Bengaluru",
            Guid.NewGuid(),
            "BLR-01",
            "Bengaluru Central",
            [new SubscriptionScheduleResult(DayOfWeek.Monday)],
            CreatedAtUtc,
            null,
            null,
            null,
            CreatedAtUtc);

        private static SubscriptionDeliveryResult CreateDeliveryResult() => new(
            Guid.NewGuid(),
            new DateOnly(2026, 8, 17),
            1.5m,
            SubscriptionDeliveryStatus.Scheduled,
            Guid.NewGuid(),
            "BLR-01",
            "Bengaluru Central",
            "Home, Bengaluru",
            null);
    }
}
