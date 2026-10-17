using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.BuildingBlocks.Endpoints;

/// <summary>TK-343: a store action Talia may propose, owned by the module whose endpoint runs it (docs/talia-acciones.md §3). The preview is built from
/// this store's own data; the person's click then calls that endpoint, with their own session, carrying <see cref="AllowAssistantExtensions.Header"/>.</summary>
public interface IAssistantAction
{
    /// <summary>A type of storeActions.json (Abastalia.Sync.Contracts); its endpoint carries <c>AllowAssistant(Type)</c>.</summary>
    string Type { get; }

    /// <summary>Resolves the (schema-valid) arguments with local data and writes the summary the card shows; can refuse ("ya existe").</summary>
    Task<Result<AssistantPreview>> PreviewAsync(JsonObject args, CancellationToken ct);
}

/// <summary>What the card shows and the exact request its Confirmar sends.</summary>
/// <param name="ResourceId">What a supervisor approval would be bound to (<c>IRequiresApproval.ResourceId</c>); empty when the command never asks.</param>
public sealed record AssistantPreview(string Summary, string Method, string Path, JsonObject Body, Guid ResourceId = default);

/// <summary>Set by the filter while a proposal runs: the audit log adds <c>"via": "talia"</c> and the proposal id to what the command records.</summary>
public sealed class AssistantRun
{
    public Guid? ProposalId { get; set; }
}

public enum AssistantOutcome { Done, Retry, Failed }

/// <summary>TK-343: the store's "done once" table (Sync module).</summary>
public interface IAssistantProposals
{
    /// <summary>Moves the proposal to running when it is this user's, not expired or closed, of a type this endpoint allows, and exactly this request
    /// (method, path and body, a supervisor approval id aside).</summary>
    /// <returns>Null to run it; otherwise the answer instead: the stored result of a proposal already done, or the refusal.</returns>
    Task<IResult?> ClaimAsync(Guid id, IReadOnlyList<string> types, string method, string path, JsonNode? body, CancellationToken ct);

    /// <param name="result">The success's JSON or the failure's text.</param>
    Task FinishAsync(Guid id, AssistantOutcome outcome, int status, string? result, CancellationToken ct);
}

public sealed record AllowAssistantMetadata(IReadOnlyList<string> Types);

public static class AllowAssistantExtensions
{
    /// <summary>The proposal a request runs. Only endpoints marked with <see cref="AllowAssistant{TBuilder}"/> accept it.</summary>
    public const string Header = "X-Talia-Proposal";

    /// <summary>The body field a supervisor approval travels in on a retry (<c>IRequiresApproval.SupervisorApprovalId</c>); not part of the proposal.</summary>
    public const string ApprovalField = "supervisorApprovalId";

    public static readonly Error NotAllowed = Error.Forbidden("Talia.EndpointNotAllowed", "Talia no puede hacer esto. Hágalo desde su pantalla.");

    /// <summary>Talia may drive this endpoint for these store action types: a request with <see cref="Header"/> runs the proposal at most once, and its
    /// audit records say "via talia". The endpoint's own permission, validation and supervisor approval apply as always.</summary>
    public static TBuilder AllowAssistant<TBuilder>(this TBuilder builder, params string[] types) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowAssistantMetadata(types)).AddEndpointFilter(Filter(types));

    internal static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Filter(IReadOnlyList<string> types) =>
        async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.Headers.TryGetValue(Header, out var value)) return await next(context);
            if (!Guid.TryParse(value, out var id)) return EndpointExtensions.Problem(NotAllowed);

            var ct = http.RequestAborted;
            var proposals = http.RequestServices.GetRequiredService<IAssistantProposals>();
            http.Request.Body.Position = 0; // buffered by UseAssistantGuard; model binding already read it
            JsonNode? body = null;
            try { if (http.Request.ContentLength is not 0) body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct); }
            catch (JsonException) { }
            if (body is JsonObject o) o.Remove(ApprovalField); // the store app sends it camelCase, like every body
            if (await proposals.ClaimAsync(id, types, http.Request.Method, http.Request.Path, body, ct) is { } answer) return answer;

            http.RequestServices.GetRequiredService<AssistantRun>().ProposalId = id;
            object? result;
            try { result = await next(context); }
            catch
            {
                await proposals.FinishAsync(id, AssistantOutcome.Failed, 500, "No se pudo completar. Inténtelo de nuevo desde su pantalla.", CancellationToken.None);
                throw;
            }
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            var problem = (result as ProblemHttpResult)?.ProblemDetails;
            var (outcome, stored) = status < 400
                ? (AssistantOutcome.Done, result is IValueHttpResult { Value: { } v } ? JsonSerializer.Serialize(v, Json.Options) : null)
                // A supervisor must allow it: the card asks for one and sends it again, so it stays open.
                : problem?.Extensions.TryGetValue("code", out var code) == true && code as string == "Approval.Required" ? (AssistantOutcome.Retry, null)
                : (AssistantOutcome.Failed, problem?.Detail ?? "No se pudo hacer.");
            await proposals.FinishAsync(id, outcome, status, stored, CancellationToken.None);
            return result;
        };

    /// <summary>Refuses a request carrying <see cref="Header"/> on any endpoint not marked <see cref="AllowAssistant{TBuilder}"/>, and buffers the body
    /// of the marked ones so the filter can compare it with the proposal.</summary>
    public static IApplicationBuilder UseAssistantGuard(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.Request.Headers.ContainsKey(Header))
        {
            if (http.GetEndpoint()?.Metadata.GetMetadata<AllowAssistantMetadata>() is null)
            {
                await EndpointExtensions.Problem(NotAllowed).ExecuteAsync(http);
                return;
            }
            http.Request.EnableBuffering();
        }
        await next(http);
    });
}
