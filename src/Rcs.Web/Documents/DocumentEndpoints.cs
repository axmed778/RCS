using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Idempotency;
using Rcs.Domain.Documents;
using Rcs.Infrastructure.Configuration;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Documents;

/// <summary>A document target in a URL or form field: <c>kind:id</c>, e.g. <c>correspondence:0199…</c>.</summary>
public static class DocumentTargetCodec
{
    public static string Format(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{target.Kind.ToString().ToLowerInvariant()}:{target.Id:D}";
    }

    public static DocumentTarget? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(':', 2);
        return parts.Length == 2
               && Enum.TryParse<DocumentTargetKind>(parts[0], ignoreCase: true, out var kind)
               && Enum.IsDefined(kind)
               && Guid.TryParse(parts[1], out var id)
            ? new DocumentTarget(kind, id)
            : null;
    }
}

/// <summary>
/// The two endpoints that move bytes. Both stream: an upload is read section by section from the multipart body and
/// written straight into the object store's temporary area, never buffered whole (ARCHITECTURE.md §12.3); a download is
/// copied from the object to the response. Neither ever names a path or a hash (DOCUMENT_MODEL.md §7.7).
/// </summary>
public static class DocumentEndpoints
{
    /// <summary>Room for the multipart framing and the small form fields around the file.</summary>
    private const long EnvelopeAllowance = 1024 * 1024;

    private const int MaxFieldLength = 16 * 1024;

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/cases/{caseId:guid}/documents/upload", UploadAsync).DisableAntiforgery();
        endpoints.MapPost("/cases/{caseId:guid}/documents/{linkId:guid}/versions", UploadVersionAsync).DisableAntiforgery();
        endpoints.MapGet("/cases/{caseId:guid}/documents/{linkId:guid}/versions/{versionId:guid}/download", DownloadAsync);
    }

    // ------------------------------------------------------------------ download

    private static async Task<IResult> DownloadAsync(
        Guid caseId, Guid linkId, Guid versionId, HttpContext context, CurrentActor actor, IDocumentQueries documents, UiText text)
    {
        if (!actor.IsAvailable)
        {
            return Results.NotFound();
        }

        var result = await documents.OpenDownloadAsync(actor.Context, caseId, linkId, versionId, context.RequestAborted);
        if (!result.Succeeded)
        {
            return result.Error!.Kind == CommandErrorKind.NotFound
                ? Results.NotFound()
                : Results.Text(text.Error(result.Error), "text/plain; charset=utf-8", statusCode: StatusCodes.Status500InternalServerError);
        }

        var download = result.Value!;

        // Always a download, never rendered: a neutral type, nosniff (set globally), no caching of authorized bytes,
        // and a sandbox should a browser try anyway (SECURITY.md §10.2, §10.3). No range requests: every read of the
        // bytes is this one authorized, audited response.
        var headers = context.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength = download.ByteSize;
        return Results.Stream(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }

    // -------------------------------------------------------------------- upload

    private static Task<IResult> UploadAsync(Guid caseId, HttpContext context, CurrentActor actor, IDocumentService documents, IAntiforgery antiforgery, IOptions<StorageOptions> storage, UiText text) =>
        ReceiveAsync(context, actor, antiforgery, storage.Value, text, async (fields, content) =>
        {
            var target = DocumentTargetCodec.Parse(fields.GetValueOrDefault("target"));
            if (target is null || !Guid.TryParse(fields.GetValueOrDefault("operationId"), out var operation) || operation == Guid.Empty)
            {
                return CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "validation.required");
            }

            return await documents.UploadDocumentAsync(actor.Context, new UploadDocumentCommand(
                new OperationId(operation),
                caseId,
                target,
                fields.GetValueOrDefault("role") ?? string.Empty,
                fields.GetValueOrDefault("title") ?? string.Empty,
                fields.GetValueOrDefault("kind") ?? string.Empty,
                fields.GetValueOrDefault("description"),
                ParseDate(fields.GetValueOrDefault("documentDate")),
                fields.GetValueOrDefault("reference"),
                Float: fields.GetValueOrDefault("float") == "true",
                Note: fields.GetValueOrDefault("note")), content, context.RequestAborted);
        });

    private static Task<IResult> UploadVersionAsync(Guid caseId, Guid linkId, HttpContext context, CurrentActor actor, IDocumentService documents, IAntiforgery antiforgery, IOptions<StorageOptions> storage, UiText text) =>
        ReceiveAsync(context, actor, antiforgery, storage.Value, text, async (fields, content) =>
        {
            if (!Guid.TryParse(fields.GetValueOrDefault("operationId"), out var operation) || operation == Guid.Empty)
            {
                return CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "validation.required");
            }

            var placement = DocumentTargetCodec.Parse(fields.GetValueOrDefault("placement"));
            return await documents.UploadVersionAsync(actor.Context, new UploadVersionCommand(
                new OperationId(operation),
                caseId,
                linkId,
                ParseDate(fields.GetValueOrDefault("documentDate")),
                placement,
                placement is null ? null : fields.GetValueOrDefault("placementRole")), content, context.RequestAborted);
        });

    /// <summary>
    /// Reads the multipart body in order: the small fields first, then exactly one file section, which is handed to the
    /// command as a stream — so the command authorizes before a byte is stored. The antiforgery token comes from the
    /// request header (script) or the first form field (plain form); either way it is validated before the file is read.
    /// </summary>
    private static async Task<IResult> ReceiveAsync(
        HttpContext context,
        CurrentActor actor,
        IAntiforgery antiforgery,
        StorageOptions storage,
        UiText text,
        Func<Dictionary<string, string>, UploadedContent, Task<CommandResult<UploadOutcome>>> command)
    {
        if (!actor.IsAvailable)
        {
            return Results.NotFound();
        }

        // The application enforces the 500 MB limit itself; Kestrel's default body limit is lifted for this request only.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = storage.MaxUploadBytes + EnvelopeAllowance;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
        {
            return Respond(context, text, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.no_file"), fields: null);
        }

        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = 16 * 1024 };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(context.RequestAborted)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || (!disposition.IsFormDisposition() && !disposition.IsFileDisposition()))
                {
                    continue;
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;
                if (!disposition.IsFileDisposition())
                {
                    using var streamReader = new StreamReader(section.Body);
                    var buffer = new char[MaxFieldLength + 1];
                    var length = await streamReader.ReadBlockAsync(buffer, context.RequestAborted);
                    if (length > MaxFieldLength)
                    {
                        return Respond(context, text, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "validation.too_long"), fields);
                    }

                    fields[name] = new string(buffer, 0, length);
                    continue;
                }

                if (!await AntiforgeryValidAsync(context, antiforgery, fields))
                {
                    return Results.BadRequest();
                }

                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                    ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                if (string.IsNullOrEmpty(fileName))
                {
                    return Respond(context, text, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.no_file"), fields);
                }

                var result = await command(fields, new UploadedContent(section.Body, fileName, section.ContentType));
                return Respond(context, text, result, fields);
            }
        }
        catch (Exception exception) when (exception is IOException or BadHttpRequestException or InvalidDataException)
        {
            // An interrupted or malformed body: the store discards the partial temporary file; nothing is recorded.
            return Respond(context, text, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.upload_incomplete"), fields);
        }

        return Respond(context, text, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.no_file"), fields);
    }

    private static async Task<bool> AntiforgeryValidAsync(HttpContext context, IAntiforgery antiforgery, Dictionary<string, string> fields)
    {
        // The default header name. Setting it from the form field lets validation run without reading the (already
        // consumed) form, so the file itself is never buffered to validate a token.
        const string header = "RequestVerificationToken";
        if (!context.Request.Headers.ContainsKey(header) && fields.TryGetValue("__RequestVerificationToken", out var token))
        {
            context.Request.Headers[header] = token;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>JSON for the script-driven form (progress bar); a redirect for a plain form post.</summary>
    private static IResult Respond(HttpContext context, UiText text, CommandResult<UploadOutcome> result, Dictionary<string, string>? fields)
    {
        var wantsJson = context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
        var returnUrl = LocalPath(fields?.GetValueOrDefault("returnUrl"));
        var formUrl = LocalPath(fields?.GetValueOrDefault("formUrl"));

        if (result.Succeeded)
        {
            var target = returnUrl ?? "/cases";
            return wantsJson ? Results.Json(new { ok = true, redirect = target, converged = result.Value!.ConvergedOnExistingVersion }) : Results.Redirect(target);
        }

        var message = text.Error(result.Error!);
        if (wantsJson)
        {
            var status = result.Error!.Kind switch
            {
                CommandErrorKind.NotFound => StatusCodes.Status404NotFound,
                CommandErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                CommandErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity,
            };
            return Results.Json(new { ok = false, message }, statusCode: status);
        }

        return formUrl is null
            ? Results.Text(message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status422UnprocessableEntity)
            : Results.Redirect(QueryHelpers.AddQueryString(formUrl, "error", result.Error!.Code));
    }

    /// <summary>Only same-site paths are followed after an upload; anything else is ignored.</summary>
    private static string? LocalPath(string? url) =>
        url is { Length: > 1 } && url[0] == '/' && url[1] != '/' && url[1] != '\\' ? url : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
