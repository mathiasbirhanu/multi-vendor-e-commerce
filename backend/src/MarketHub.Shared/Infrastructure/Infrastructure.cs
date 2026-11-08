using MarketHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Shared.Infrastructure;

/// <summary>Base DbContext for module contexts: carries the ambient user for auditing.</summary>
public abstract class MarketHubDbContextBase<TSelf>(DbContextOptions<TSelf> options, ICurrentUser currentUser) : DbContext(options) where TSelf : DbContext
{
    /// <summary>Ambient user resolved from the current HTTP request.</summary>
    public ICurrentUser CurrentUser { get; } = currentUser;

    /// <summary>Applies snake_case naming so PostgreSQL tables/columns match the conventions used in migrations.</summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSnakeCaseNamingConvention();
}

/// <summary>Dedicated outbox store context (schema "outbox") used by <see cref="OutboxProcessor"/>.</summary>
public sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : MarketHubDbContextBase<OutboxDbContext>(options, NullCurrentUser.Instance)
{
    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("outbox");
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.Property(x => x.Type).HasMaxLength(300).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.HasIndex(x => x.ProcessedOnUtc);
            e.HasIndex(x => new { x.Type, x.EventId }).IsUnique();
        });
    }
}

/// <summary>No-op user for background processing.</summary>
public sealed class NullCurrentUser : ICurrentUser
{
    public static readonly NullCurrentUser Instance = new();
    public Guid? UserId => null;
    public string? Email => null;
    public string Role => "System";
    public bool IsAuthenticated => false;
    public bool IsInRole(string role) => false;
    public Guid? VendorId => null;
}

/// <summary>Resolves <see cref="ICurrentUser"/> from HTTP claims (sub, email, role, vendorId).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;
    public string? Email => Principal?.FindFirstValue("email");
    public string Role => Principal?.FindFirstValue("role") ?? "Anonymous";
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
    public bool IsInRole(string role) => string.Equals(Role, role, StringComparison.OrdinalIgnoreCase);
    public Guid? VendorId => Guid.TryParse(Principal?.FindFirstValue("vendorId"), out var id) ? id : null;
}

public static class OutboxExtensions
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>Adds a domain event to the transactional outbox of the given context (camelCase JSON payload).</summary>
    public static void AddToOutbox(this DbContext db, IDomainEvent evt)
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            EventId = evt.EventId,
            Type = evt.GetType().FullName!,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions),
        });
    }

    /// <summary>Deserializes an outbox payload into its event type.</summary>
    public static IDomainEvent? Deserialize(OutboxMessage message)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(message.Type)).FirstOrDefault(t => t is not null);
        if (type is null) return null;
        return System.Text.Json.JsonSerializer.Deserialize(message.PayloadJson, type, JsonOptions) as IDomainEvent;
    }
}

/// <summary>Background service that drains the outbox and publishes events via MediatR.</summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<MediatR.IPublisher>();

                var pending = await db.Messages
                    .Where(m => m.ProcessedOnUtc == null)
                    .OrderBy(m => m.CreatedAtUtc)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var message in pending)
                {
                    try
                    {
                        var evt = OutboxExtensions.Deserialize(message)
                            ?? throw new InvalidOperationException($"Unknown event type '{message.Type}'.");
                        await publisher.Publish(new OutboxEnvelope(evt), stoppingToken);
                        message.ProcessedOnUtc = DateTime.UtcNow;
                        message.Error = null;
                    }
                    catch (Exception ex)
                    {
                        message.Error = ex.Message;
                        logger.LogWarning(ex, "Outbox message {Id} failed; will retry", message.Id);
                    }
                }
                if (pending.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox poll failed");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}

/// <summary>Wraps deserialized events for MediatR publishing.</summary>
public sealed record OutboxEnvelope(IDomainEvent Event) : MediatR.INotification;

/// <summary>
/// Republishes a deserialized outbox envelope to its typed <c>INotificationHandler&lt;TEvent&gt;</c> handlers.
/// Registered once by the composition root; the only bridge between the outbox store and domain handlers.
/// </summary>
public sealed class OutboxEnvelopeRelay(MediatR.IPublisher publisher) : MediatR.INotificationHandler<OutboxEnvelope>
{
    public Task Handle(OutboxEnvelope envelope, CancellationToken ct) => publisher.Publish(envelope.Event, ct);
}
