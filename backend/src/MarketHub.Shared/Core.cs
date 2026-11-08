namespace MarketHub.Shared;

/// <summary>Base entity for all modules: GUID key + audit timestamps.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAtUtc { get; set; }
}

/// <summary>Entities whose creator/modifier is stamped automatically from <see cref="ICurrentUser"/>.</summary>
public interface IAuditable
{
    string? CreatedBy { get; set; }
    string? ModifiedBy { get; set; }
}

/// <summary>Structured error code + message.</summary>
public sealed record Error(string Code, string Message);

/// <summary>Result type used by all application handlers instead of exceptions for expected failures.</summary>
public sealed class Result
{
    public bool IsSuccess { get; }
    public Error[] Errors { get; }
    public bool IsFailed => !IsSuccess;

    private Result(bool success, Error[] errors) { IsSuccess = success; Errors = errors; }

    public static Result Success() => new(true, Array.Empty<Error>());
    public static Result Failure(params Error[] errors) => new(false, errors);
    public static Result Failure(string code, string message) => new(false, new[] { new Error(code, message) });

    public static Result<T> Ok<T>(T value) => new(value, true, Array.Empty<Error>());
    public static Result<T> Fail<T>(params Error[] errors) => new(default, false, errors);
    public static Result<T> Fail<T>(string code, string message) => new(default, false, new[] { new Error(code, message) });
}

/// <summary>Typed result.</summary>
public sealed class Result<T>(T? value, bool success, Error[] errors) : Result(success, errors)
{
    public T Value => IsSuccess ? value! : throw new InvalidOperationException("Cannot read Value of a failed result.");
}

/// <summary>Standard pagination envelope.</summary>
public sealed class PagedResult<T>(IReadOnlyList<T> items, int page, int pageSize, long totalCount)
{
    public IReadOnlyList<T> Items { get; } = items;
    public int Page { get; } = page;
    public int PageSize { get; } = pageSize;
    public long TotalCount { get; } = totalCount;
    public long TotalPages => (long)Math.Ceiling(totalCount / (double)Math.Max(1, pageSize));

    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, long totalCount) => new(items, page, pageSize, totalCount);
}

/// <summary>Marker for integration events dispatched through the outbox.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredOnUtc { get; }
}

/// <summary>Base record for domain/integration events (MediatR notifications dispatched through the outbox).</summary>
public abstract record DomainEvent : IDomainEvent, MediatR.INotification
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Transactional outbox row — persisted in the same transaction as the state change.</summary>
public sealed class OutboxMessage : BaseEntity
{
    public Guid EventId { get; set; }
    public string Type { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    public DateTime? ProcessedOnUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>Ambient user context resolved from the current HTTP request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    string Role { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
    Guid? VendorId { get; }
}

/// <summary>Domain rule violation.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>Entity lookup failure.</summary>
public sealed class NotFoundException(string entity, object key) : DomainException($"{entity} '{key}' was not found.");

/// <summary>A payment gateway is enabled but its credentials/configuration are missing.</summary>
public sealed class GatewayNotConfiguredException(string provider) : DomainException($"Payment gateway '{provider}' is not configured. Add its credentials to the environment configuration.");
