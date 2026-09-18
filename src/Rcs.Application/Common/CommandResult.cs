namespace Rcs.Application.Common;

public enum CommandErrorKind
{
    /// <summary>The object does not exist, or is not visible to the actor.</summary>
    NotFound = 1,

    /// <summary>Authorization refused the action (recorded as PERMISSION_DENIED).</summary>
    Forbidden,

    /// <summary>Someone else changed the object since the form was shown (optimistic concurrency, ARCHITECTURE.md §12.4).</summary>
    Conflict,

    /// <summary>A workflow guard or database invariant refused the change.</summary>
    RuleViolation,

    /// <summary>The submitted values are incomplete or invalid.</summary>
    Validation,
}

/// <summary>A refusal with a stable, untranslated message code. <see cref="Field"/> names the offending input when there is one.</summary>
public sealed record CommandError(CommandErrorKind Kind, string Code, string? Field = null);

/// <summary>The result of a command. Commands return the identifier of the primary record they created or changed.</summary>
public sealed record CommandResult<T>
{
    private CommandResult(T? value, CommandError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public CommandError? Error { get; }

    public bool Succeeded => Error is null;

    public static CommandResult<T> Success(T value) => new(value, null);

    public static CommandResult<T> Failure(CommandError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    public static CommandResult<T> Failure(CommandErrorKind kind, string code, string? field = null) => new(default, new CommandError(kind, code, field));

    public CommandResult<TOther> Cast<TOther>() =>
        Succeeded ? throw new InvalidOperationException("Only a failure can be cast.") : CommandResult<TOther>.Failure(Error!);
}

/// <summary>
/// The person on whose behalf the host is acting, as established by the host (authentication in production; the
/// Development-only review actor in the Review build). Roles are NOT carried here: they are read at action time.
/// </summary>
public sealed record ActorContext(Guid UserId, string? ClientHost = null, string? SessionId = null);
