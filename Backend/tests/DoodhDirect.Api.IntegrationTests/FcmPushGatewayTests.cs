using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 1 push tests. No real Firebase project, service account, or customer
/// notification is involved: OAuth and FCM endpoints are fully scripted, and
/// the service-account key is generated in memory per test run.
/// </summary>
public sealed class FcmPushGatewayTests
{
    [Fact]
    public async Task SendAsync_success_posts_notification_plus_data_and_returns_delivered()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/msg-1"}""");
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.Delivered, result.Outcome);
        Assert.Equal("projects/test-project/messages/msg-1", result.ProviderMessageId);
        Assert.False(result.InvalidateDestination);

        Assert.Equal(2, fixture.Requests.Count);
        var send = fixture.Requests[1];
        Assert.Equal(
            "https://fcm.googleapis.com/v1/projects/test-project/messages:send",
            send.Url);
        Assert.Equal("Bearer test-access-token", send.Authorization);

        using var payload = JsonDocument.Parse(send.Body);
        var message = payload.RootElement.GetProperty("message");
        Assert.Equal("fcm-device-token", message.GetProperty("token").GetString());
        Assert.Equal("Order ready", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Your order is on its way.", message.GetProperty("notification").GetProperty("body").GetString());
        var data = message.GetProperty("data");
        Assert.Equal(fixture.NotificationId.ToString(), data.GetProperty("notificationId").GetString());
        Assert.Equal("ORDER_CREATED", data.GetProperty("eventType").GetString());
        Assert.Equal("/orders/100", data.GetProperty("deepLink").GetString());
        Assert.Equal("/orders/100", data.GetProperty("deep_link").GetString());
        Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task SendAsync_caches_access_token_across_sends()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/1"}""");
        fixture.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/2"}""");
        var gateway = fixture.Gateway();

        await gateway.SendAsync(fixture.Message(), default);
        await gateway.SendAsync(fixture.Message(), default);

        Assert.Single(fixture.Requests, x => x.Url.Contains("oauth2", StringComparison.Ordinal));
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"message":"Quota exceeded.","status":"RESOURCE_EXHAUSTED"}}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"code":500,"message":"Internal error.","status":"INTERNAL"}}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"Unavailable.","status":"UNAVAILABLE"}}""")]
    public async Task SendAsync_retryable_statuses_return_retryable_failure(
        HttpStatusCode status,
        string body)
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(status, body);
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.RetryableFailure, result.Outcome);
        Assert.False(result.InvalidateDestination);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureCode));
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    [Fact]
    public async Task SendAsync_transport_error_returns_retryable_failure()
    {
        await using var fixture = await FcmFixture.CreateAsync(
            sendBehavior: _ => throw new HttpRequestException("connection reset"));
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.RetryableFailure, result.Outcome);
        Assert.Equal("FCM_TRANSPORT_ERROR", result.FailureCode);
        Assert.False(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_unregistered_token_returns_permanent_failure_and_invalidates_destination()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(
            HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}""");
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("FCM_UNREGISTERED", result.FailureCode);
        Assert.True(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_invalid_token_returns_permanent_failure_and_invalidates_destination()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(
            HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"The registration token is not a valid FCM registration token.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}""");
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("FCM_INVALID_TOKEN", result.FailureCode);
        Assert.True(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_unauthorized_returns_permanent_actionable_failure_without_invalidation()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(
            HttpStatusCode.Unauthorized,
            """{"error":{"code":401,"message":"Request had invalid authentication credentials.","status":"UNAUTHENTICATED"}}""");
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("FCM_UNAUTHORIZED", result.FailureCode);
        Assert.Contains("service-account", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_oauth_failure_returns_permanent_failure()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""",
            tokenEndpoint: true);
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("FCM_AUTHENTICATION_FAILED", result.FailureCode);
        Assert.False(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_oauth_server_error_returns_retryable_failure()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(HttpStatusCode.ServiceUnavailable, "try again", tokenEndpoint: true);
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.RetryableFailure, result.Outcome);
        Assert.False(result.InvalidateDestination);
    }

    [Fact]
    public async Task SendAsync_missing_configuration_returns_permanent_failure_without_http_calls()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        var gateway = fixture.Gateway(new FcmPushOptions
        {
            ProjectId = string.Empty,
            ServiceAccountFile = string.Empty
        });

        var result = await gateway.SendAsync(fixture.Message(), default);

        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("FCM_NOT_CONFIGURED", result.FailureCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task SendAsync_drops_unsafe_deep_links_and_tolerates_missing_title()
    {
        await using var fixture = await FcmFixture.CreateAsync();
        fixture.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/9"}""");
        var gateway = fixture.Gateway();

        var message = fixture.Message() with
        {
            Title = null,
            DeepLink = "https://evil.example/orders/100"
        };
        var result = await gateway.SendAsync(message, default);

        Assert.Equal(NotificationAttemptOutcome.Delivered, result.Outcome);
        using var payload = JsonDocument.Parse(fixture.Requests[1].Body);
        var fcmMessage = payload.RootElement.GetProperty("message");
        Assert.False(fcmMessage.GetProperty("notification").TryGetProperty("title", out _));
        Assert.Equal(
            "Your order is on its way.",
            fcmMessage.GetProperty("notification").GetProperty("body").GetString());
        var data = fcmMessage.GetProperty("data");
        Assert.False(data.TryGetProperty("deepLink", out _));
        Assert.False(data.TryGetProperty("deep_link", out _));
    }

    [Fact]
    public async Task Processor_marks_fcm_delivery_delivered_with_provider_message_id()
    {
        await using var pipeline = await FcmPipeline.CreateAsync();
        pipeline.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/p-1"}""");

        Assert.Equal(1, await pipeline.Processor.ProcessDueDeliveriesAsync(default));

        var delivery = await pipeline.DeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("projects/test-project/messages/p-1", delivery.ProviderMessageId);
        Assert.Equal(1, delivery.AttemptCount);
        var attempt = await pipeline.LastAttemptAsync();
        Assert.Equal(NotificationAttemptOutcome.Delivered, attempt.Outcome);
        Assert.True((await pipeline.DeviceAsync()).IsActive);
    }

    [Fact]
    public async Task Processor_invalidates_device_on_unregistered_token()
    {
        await using var pipeline = await FcmPipeline.CreateAsync();
        pipeline.EnqueueJson(
            HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}""");

        Assert.Equal(1, await pipeline.Processor.ProcessDueDeliveriesAsync(default));

        var delivery = await pipeline.DeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal("FCM_UNREGISTERED", delivery.FailureCode);
        Assert.False((await pipeline.DeviceAsync()).IsActive);
    }

    [Fact]
    public async Task Processor_schedules_retry_on_fcm_server_error()
    {
        await using var pipeline = await FcmPipeline.CreateAsync();
        pipeline.EnqueueJson(
            HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":503,"message":"Unavailable.","status":"UNAVAILABLE"}}""");

        Assert.Equal(1, await pipeline.Processor.ProcessDueDeliveriesAsync(default));

        var delivery = await pipeline.DeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.NotNull(delivery.NextAttemptAt);
        Assert.True((await pipeline.DeviceAsync()).IsActive);
    }

    [Fact]
    public void OptionsValidator_allows_missing_fcm_values_when_provider_is_unconfigured()
    {
        var validator = new FcmPushOptionsValidator(
            Options.Create(new NotificationOptions { PushProvider = "Unconfigured" }));

        var result = validator.Validate(null, new FcmPushOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsValidator_rejects_missing_values_when_fcm_is_selected()
    {
        var validator = new FcmPushOptionsValidator(
            Options.Create(new NotificationOptions { PushProvider = "Fcm" }));

        var missing = validator.Validate(null, new FcmPushOptions());
        Assert.False(missing.Succeeded);

        var noFile = validator.Validate(null, new FcmPushOptions
        {
            ProjectId = "test-project",
            ServiceAccountFile = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")
        });
        Assert.False(noFile.Succeeded);
    }

    private sealed record CapturedRequest(string Url, string? Authorization, string Body);

    private sealed class FcmFixture : IAsyncDisposable
    {
        private readonly string _serviceAccountFile;
        private readonly ScriptedHandler _handler;
        private readonly FcmPushOptions _options;

        public Guid NotificationId { get; } = Guid.NewGuid();
        public List<CapturedRequest> Requests => _handler.Requests;

        private FcmFixture(string serviceAccountFile, ScriptedHandler handler, FcmPushOptions options)
        {
            _serviceAccountFile = serviceAccountFile;
            _handler = handler;
            _options = options;
        }

        public static async Task<FcmFixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? sendBehavior = null)
        {
            using var rsa = RSA.Create(2048);
            var serviceAccount = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "sa-project",
                private_key_id = "test-key-id",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                client_email = "test@sa-project.iam.gserviceaccount.com",
                token_uri = "https://oauth2.example.test/token"
            });
            var file = Path.Combine(Path.GetTempPath(), $"fcm-test-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, serviceAccount);
            var options = new FcmPushOptions
            {
                ProjectId = "test-project",
                ServiceAccountFile = file,
                TimeoutSeconds = 30
            };
            return new FcmFixture(file, new ScriptedHandler(sendBehavior), options);
        }

        public void EnqueueJson(HttpStatusCode status, string body, bool tokenEndpoint = false) =>
            _handler.Enqueue(status, body, tokenEndpoint);

        public INotificationChannelGateway Gateway(FcmPushOptions? options = null) =>
            new FcmPushGateway(new ScriptedClientFactory(_handler), Options.Create(options ?? _options), NullLogger<FcmPushGateway>.Instance);

        public NotificationProviderMessage Message() =>
            new(
                Guid.NewGuid(),
                NotificationChannel.Push,
                "fcm-device-token",
                "Order ready",
                "Your order is on its way.",
                "/orders/100",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["notificationId"] = NotificationId.ToString(),
                    ["eventType"] = NotificationEventTypes.OrderCreated
                });

        public async ValueTask DisposeAsync()
        {
            _handler.Dispose();
            if (File.Exists(_serviceAccountFile))
            {
                File.Delete(_serviceAccountFile);
            }

            await ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage>? sendBehavior) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _sendResponses = new();
        private readonly Queue<(HttpStatusCode Status, string Body)> _tokenResponses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body, bool tokenEndpoint) =>
            (tokenEndpoint ? _tokenResponses : _sendResponses).Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                body));

            if (request.RequestUri?.AbsoluteUri.Contains("oauth2", StringComparison.Ordinal) == true)
            {
                if (_tokenResponses.Count > 0)
                {
                    var (status, responseBody) = _tokenResponses.Dequeue();
                    return JsonResponse(status, responseBody);
                }

                return JsonResponse(
                    HttpStatusCode.OK,
                    """{"access_token":"test-access-token","expires_in":3600,"token_type":"Bearer"}""");
            }

            if (sendBehavior is not null)
            {
                return sendBehavior(request);
            }

            if (_sendResponses.Count > 0)
            {
                var (status, responseBody) = _sendResponses.Dequeue();
                return JsonResponse(status, responseBody);
            }

            return JsonResponse(HttpStatusCode.OK, """{"name":"projects/test-project/messages/default"}""");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class ScriptedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Minimal real-pipeline wiring: the genuine <see cref="NotificationProcessor"/>
    /// serves a pending Push delivery through the genuine <see cref="FcmPushGateway"/>
    /// with scripted HTTP, proving delivery status, retry, and device
    /// invalidation end to end without touching other channels or records.
    /// </summary>
    private sealed class FcmPipeline : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ScriptedHandler _handler;
        private readonly string _serviceAccountFile;

        public DoodhDirectDbContext Db { get; }
        public INotificationProcessor Processor { get; }

        private FcmPipeline(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            INotificationProcessor processor,
            ScriptedHandler handler,
            string serviceAccountFile)
        {
            _connection = connection;
            Db = db;
            Processor = processor;
            _handler = handler;
            _serviceAccountFile = serviceAccountFile;
        }

        public static async Task<FcmPipeline> CreateAsync()
        {
            using var rsa = RSA.Create(2048);
            var serviceAccount = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "sa-project",
                private_key_id = "test-key-id",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                client_email = "test@sa-project.iam.gserviceaccount.com",
                token_uri = "https://oauth2.example.test/token"
            });
            var file = Path.Combine(Path.GetTempPath(), $"fcm-pipe-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, serviceAccount);

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new DoodhDirectDbContext(
                new DbContextOptionsBuilder<DoodhDirectDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified);
            var user = new User(UserType.Customer);
            user.SetProfile("Customer");
            user.SetContact("9999999999", "customer@example.com");
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var dataProtectionProvider = new EphemeralDataProtectionProvider();
            var tokenProtector = new NotificationTokenProtector(dataProtectionProvider);
            var tokenGenerator = new SecureTokenGenerator();
            const string pushToken = "fcm-device-token";
            var device = new UserDevice(
                user.Id,
                tokenGenerator.Hash("device-1"),
                tokenGenerator.Hash(pushToken),
                tokenProtector.Protect(pushToken),
                "android",
                "Device device-1",
                now);
            db.UserDevices.Add(device);

            var notificationEvent = new NotificationEvent(
                user.Id,
                NotificationEventTypes.OrderCreated,
                "order:1:created",
                "{}",
                false,
                now);
            db.NotificationEvents.Add(notificationEvent);
            await db.SaveChangesAsync();

            var notification = new Notification(
                notificationEvent.Id,
                user.Id,
                NotificationEventTypes.OrderCreated,
                "Order ready",
                "Your order is on its way.",
                "/orders/100",
                now);
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();

            db.NotificationDeliveries.Add(new NotificationDelivery(
                notification.Id,
                NotificationChannel.Push,
                "FCM",
                device.TokenHash,
                device.Id,
                now));
            await db.SaveChangesAsync();

            var handler = new ScriptedHandler(null);
            var gateway = new FcmPushGateway(
                new ScriptedClientFactory(handler),
                Options.Create(new FcmPushOptions
                {
                    ProjectId = "test-project",
                    ServiceAccountFile = file,
                    TimeoutSeconds = 30
                }),
                NullLogger<FcmPushGateway>.Instance);
            var clock = new TestClock(now);
            var processor = new NotificationProcessor(
                db,
                [gateway],
                tokenProtector,
                new DeliveryOtpHandoffProtector(dataProtectionProvider),
                Options.Create(new NotificationOptions
                {
                    BatchSize = 25,
                    MaxAttempts = 4,
                    InitialRetryDelayMinutes = 2,
                    PollIntervalSeconds = 10
                }),
                new TestIndiaTimeProvider(clock));
            return new FcmPipeline(connection, db, processor, handler, file);
        }

        public void EnqueueJson(HttpStatusCode status, string body) =>
            _handler.Enqueue(status, body, tokenEndpoint: false);

        public async Task<NotificationDelivery> DeliveryAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.NotificationDeliveries.AsNoTracking().SingleAsync();
        }

        public async Task<NotificationAttempt> LastAttemptAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.NotificationAttempts.AsNoTracking().SingleAsync();
        }

        public async Task<UserDevice> DeviceAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.UserDevices.AsNoTracking().SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            _handler.Dispose();
            if (File.Exists(_serviceAccountFile))
            {
                File.Delete(_serviceAccountFile);
            }
        }
    }
}
