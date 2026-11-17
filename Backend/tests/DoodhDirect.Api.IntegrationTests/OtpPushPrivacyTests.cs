using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
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
/// OTP lock-screen privacy: the six-digit code stays in the authenticated
/// inbox, while the FCM push title/body/data carry only generic text.
/// No real Firebase project, service account, or customer notification is
/// involved; HTTP is fully scripted and the service-account key is generated
/// per test run.
/// </summary>
public sealed class OtpPushPrivacyTests
{
    private const string OtpCode = "731946";

    [Fact]
    public void PushPresentation_otp_event_returns_generic_text()
    {
        var (title, body) = DeliveryOtpPushPrivacy.PushPresentation(
            NotificationEventTypes.DeliveryOtpIssued,
            $"Your delivery OTP is {OtpCode}.",
            $"Your delivery OTP is {OtpCode}.");

        Assert.Equal(DeliveryOtpPushPrivacy.GenericTitle, title);
        Assert.Equal(DeliveryOtpPushPrivacy.GenericBody, body);
        Assert.DoesNotContain(OtpCode, title, StringComparison.Ordinal);
        Assert.DoesNotContain(OtpCode, body, StringComparison.Ordinal);
    }

    [Fact]
    public void PushPresentation_non_otp_event_passes_through()
    {
        var (title, body) = DeliveryOtpPushPrivacy.PushPresentation(
            NotificationEventTypes.OrderCreated,
            "Order ready",
            "Your order is on its way.");

        Assert.Equal("Order ready", title);
        Assert.Equal("Your order is on its way.", body);
    }

    [Fact]
    public async Task Fcm_gateway_otp_event_sends_generic_payload_without_otp()
    {
        await using var fixture = await OtpFcmFixture.CreateAsync();
        fixture.EnqueueJson(HttpStatusCode.OK, """{"name":"projects/test-project/messages/otp-1"}""");
        var gateway = fixture.Gateway();

        var result = await gateway.SendAsync(fixture.OtpMessage(), default);

        Assert.Equal(NotificationAttemptOutcome.Delivered, result.Outcome);
        Assert.Equal(2, fixture.Requests.Count);
        var send = fixture.Requests[1];
        using var payload = JsonDocument.Parse(send.Body);
        var message = payload.RootElement.GetProperty("message");
        var notification = message.GetProperty("notification");
        Assert.Equal(
            DeliveryOtpPushPrivacy.GenericTitle,
            notification.GetProperty("title").GetString());
        Assert.Equal(
            DeliveryOtpPushPrivacy.GenericBody,
            notification.GetProperty("body").GetString());
        Assert.DoesNotContain(OtpCode, send.Body, StringComparison.Ordinal);
        var data = message.GetProperty("data");
        Assert.Equal(
            fixture.NotificationId.ToString(),
            data.GetProperty("notificationId").GetString());
        Assert.Equal(
            NotificationEventTypes.DeliveryOtpIssued,
            data.GetProperty("eventType").GetString());
        Assert.False(TryGetProperty(data, "otp", out _));
    }

    [Fact]
    public async Task Processor_keeps_otp_in_inbox_but_sends_generic_push()
    {
        await using var harness = await OtpPipelineHarness.CreateAsync();
        await harness.RegisterDeviceAsync("otp-device-1", "fcm-device-token-otp");
        await harness.AddTemplatesAsync();
        await harness.AddOtpEventAsync();

        Assert.Equal(1, await harness.Processor.ProcessPendingEventsAsync(default));
        Assert.Equal(4, await harness.Processor.ProcessDueDeliveriesAsync(default));

        var notification = await harness.InboxAsync();
        Assert.Contains(OtpCode, notification.Body, StringComparison.Ordinal);

        var push = Assert.Single(harness.Push.Messages);
        Assert.Equal(DeliveryOtpPushPrivacy.GenericTitle, push.Title);
        Assert.Equal(DeliveryOtpPushPrivacy.GenericBody, push.Body);
        Assert.DoesNotContain(OtpCode, push.Title ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(OtpCode, push.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtpCode, string.Join(";", push.Data.Select(x => x.Key + "=" + x.Value)), StringComparison.Ordinal);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record CapturedRequest(string Url, string? Authorization, string Body);

    private sealed class OtpFcmFixture : IAsyncDisposable
    {
        private readonly string _serviceAccountFile;
        private readonly ScriptedHandler _handler;
        private readonly FcmPushOptions _options;

        public Guid NotificationId { get; } = Guid.NewGuid();
        public List<CapturedRequest> Requests => _handler.Requests;

        private OtpFcmFixture(string serviceAccountFile, ScriptedHandler handler, FcmPushOptions options)
        {
            _serviceAccountFile = serviceAccountFile;
            _handler = handler;
            _options = options;
        }

        public static async Task<OtpFcmFixture> CreateAsync()
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
            var file = Path.Combine(Path.GetTempPath(), $"fcm-otp-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, serviceAccount);
            var options = new FcmPushOptions
            {
                ProjectId = "test-project",
                ServiceAccountFile = file,
                TimeoutSeconds = 30
            };
            return new OtpFcmFixture(file, new ScriptedHandler(), options);
        }

        public void EnqueueJson(HttpStatusCode status, string body) =>
            _handler.Enqueue(status, body);

        public INotificationChannelGateway Gateway() =>
            new FcmPushGateway(new ScriptedClientFactory(_handler), Options.Create(_options), NullLogger<FcmPushGateway>.Instance);

        public NotificationProviderMessage OtpMessage() =>
            new(
                Guid.NewGuid(),
                NotificationChannel.Push,
                "fcm-device-token",
                $"Your delivery OTP is {OtpCode}.",
                $"Your delivery OTP is {OtpCode}. Share it only with the assigned delivery staff.",
                "/deliveries/test",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["notificationId"] = NotificationId.ToString(),
                    ["eventType"] = NotificationEventTypes.DeliveryOtpIssued,
                    ["deepLink"] = "/deliveries/test",
                    ["otp"] = OtpCode
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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _sendResponses = new();
        private readonly Queue<(HttpStatusCode Status, string Body)> _tokenResponses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _sendResponses.Enqueue((status, body));

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

    private sealed class OtpPipelineHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private OtpPipelineHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            User customer,
            SecureTokenGenerator tokenGenerator,
            NotificationTokenProtector tokenProtector,
            DeliveryOtpHandoffProtector otpProtector,
            CapturingPushGateway push,
            NotificationProcessor processor,
            NotificationService service)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Customer = customer;
            TokenGenerator = tokenGenerator;
            TokenProtector = tokenProtector;
            OtpProtector = otpProtector;
            Push = push;
            Processor = processor;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public User Customer { get; }
        public SecureTokenGenerator TokenGenerator { get; }
        public NotificationTokenProtector TokenProtector { get; }
        public DeliveryOtpHandoffProtector OtpProtector { get; }
        public CapturingPushGateway Push { get; }
        public NotificationProcessor Processor { get; }
        public NotificationService Service { get; }

        public async Task RegisterDeviceAsync(string identifier, string token) =>
            await Service.RegisterDeviceAsync(
                new NotificationActor(Customer.Id),
                new RegisterDeviceRequest(identifier, token, "android", $"Device {identifier}"),
                default);

        public async Task AddTemplatesAsync()
        {
            Db.NotificationTemplates.AddRange(
                Enum.GetValues<NotificationChannel>().Select(channel =>
                    new NotificationTemplate(
                        NotificationEventTypes.DeliveryOtpIssued,
                        channel,
                        "en",
                        "Delivery Otp Issued",
                        "Your delivery OTP is {{otp}}. Share it only with the assigned delivery staff.")));
            await Db.SaveChangesAsync();
        }

        public async Task AddOtpEventAsync()
        {
            var writer = new NotificationEventWriter(Db, Clock);
            writer.Add(new NotificationEventRequest(
                Customer.Id,
                NotificationEventTypes.DeliveryOtpIssued,
                $"delivery:test:otp-privacy:{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["message"] = "Your delivery OTP is ready." },
                "/deliveries/test",
                Clock.Now,
                new Dictionary<string, string> { ["otp"] = OtpProtector.Protect(OtpCode) }));
            await Db.SaveChangesAsync();
        }

        public async Task<Notification> InboxAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.Notifications.AsNoTracking().SingleAsync();
        }

        public static async Task<OtpPipelineHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new DoodhDirectDbContext(
                new DbContextOptionsBuilder<DoodhDirectDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", "customer@example.com");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified));
            var tokenGenerator = new SecureTokenGenerator();
            var dataProtectionProvider = new EphemeralDataProtectionProvider();
            var tokenProtector = new NotificationTokenProtector(dataProtectionProvider);
            var otpProtector = new DeliveryOtpHandoffProtector(dataProtectionProvider);
            var gateways = Enum.GetValues<NotificationChannel>()
                .ToDictionary(channel => channel, channel => new CapturingPushGateway(channel));
            var push = gateways[NotificationChannel.Push];
            var processor = new NotificationProcessor(
                db,
                gateways.Values,
                tokenProtector,
                otpProtector,
                Options.Create(new NotificationOptions
                {
                    BatchSize = 25,
                    MaxAttempts = 4,
                    InitialRetryDelayMinutes = 2,
                    PollIntervalSeconds = 10
                }),
                clock);
            var service = new NotificationService(db, tokenGenerator, tokenProtector, clock);
            return new OtpPipelineHarness(
                connection, db, clock, customer, tokenGenerator, tokenProtector, otpProtector, push, processor, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingPushGateway(NotificationChannel channel) : INotificationChannelGateway
    {
        public NotificationChannel Channel { get; } = channel;
        public string ProviderCode => $"TEST_{Channel.ToString().ToUpperInvariant()}";
        public List<NotificationProviderMessage> Messages { get; } = [];

        public Task<NotificationProviderResult> SendAsync(
            NotificationProviderMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return Task.FromResult(new NotificationProviderResult(
                NotificationAttemptOutcome.Delivered,
                $"test-{message.DeliveryId:N}"));
        }
    }
}
