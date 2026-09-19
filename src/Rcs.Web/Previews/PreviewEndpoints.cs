using Microsoft.AspNetCore.Antiforgery;
using Rcs.Application.Previews;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Review;

namespace Rcs.Web.Previews;

public static class PreviewEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        const string route = "/cases/{caseId:guid}/documents/{linkId:guid}/versions/{versionId:guid}/preview";
        endpoints.MapGet(route, async (Guid caseId, Guid linkId, Guid versionId, CurrentActor actor, IDocumentPreviewQueries queries, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!actor.IsAvailable) return Results.NotFound();
            var result = await queries.OpenAsync(actor.Context, caseId, linkId, versionId, context.RequestAborted);
            if (!result.Succeeded) return Results.NotFound();
            var value = result.Value!;
            return Results.Json(new { status = value.Summary.Status.ToCode(), type = value.Summary.Type.ToCode(), value.Title,
                value.Summary.CanRetry, value.Summary.PageCount, value.Summary.PagesRendered,
                artifacts = value.Artifacts.Select(a => new { a.Id, kind = a.Kind.ToCode(), a.PageNumber, a.Width, a.Height }) });
        });
        endpoints.MapGet(route + "/status", async (Guid caseId, Guid linkId, Guid versionId, CurrentActor actor, IDocumentPreviewQueries queries, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!actor.IsAvailable) return Results.NotFound();
            var result = await queries.GetStatusAsync(actor.Context, caseId, linkId, versionId, context.RequestAborted);
            return result.Succeeded ? Results.Json(new { status = result.Value?.Status.ToCode() ?? "PENDING" }) : Results.NotFound();
        });
        endpoints.MapGet(route + "/artifacts/{artifactId:guid}", async (Guid caseId, Guid linkId, Guid versionId, Guid artifactId, CurrentActor actor, IDocumentPreviewQueries queries, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!actor.IsAvailable) return Results.NotFound();
            var result = await queries.OpenArtifactAsync(actor.Context, caseId, linkId, versionId, artifactId, context.RequestAborted);
            if (!result.Succeeded) return Results.NotFound();
            var artifact = result.Value!;
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            context.Response.ContentLength = artifact.SizeBytes;
            return Results.Stream(artifact.Content, artifact.ContentType, enableRangeProcessing: false);
        });
        endpoints.MapPost(route + "/retry", async (Guid caseId, Guid linkId, Guid versionId, CurrentActor actor, IDocumentPreviewQueries queries, IAntiforgery antiforgery, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!actor.IsAvailable) return Results.NotFound();
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var result = await queries.RetryAsync(actor.Context, caseId, linkId, versionId, context.RequestAborted);
            return result.Succeeded ? Results.Ok() : result.Error!.Kind == Rcs.Application.Common.CommandErrorKind.NotFound ? Results.NotFound() : Results.Conflict();
        }).DisableAntiforgery();
    }
}
