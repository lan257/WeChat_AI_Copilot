using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Skills;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace WeChatAiCopilot.AgentTests;

public sealed class V6SkillsPolicyTests
{
    private static readonly AuthorizedContextScopeV1 Scope = new(Guid.NewGuid(), Guid.NewGuid());
    private static SkillWriteRequest Valid => new("Persona", "克制的温柔", "清楚有礼", "保持温柔，避免替本人许诺。",
        ["reply", "consult"], "Conversation", Scope.AccountId, Scope.ConversationId);

    [Fact]
    public void ScopeCannotBeGrantedByRequestIdsOrAllScopeWithForeignIds()
    {
        Assert.Equal("SKILL_SCOPE_INVALID", Assert.Throws<SkillException>(() => SkillPolicy.Validate(
            Valid with { AccountId = Guid.NewGuid() }, Scope)).Code);
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { ConversationId = Guid.NewGuid() }, Scope));
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { ScopeType = "AllAuthorizedAccounts" }, Scope));
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { ScopeType = "Account" }, Scope));
        Assert.Equal("AllAuthorizedAccounts", SkillPolicy.Validate(Valid with
        { ScopeType = "AllAuthorizedAccounts", AccountId = null, ConversationId = null }, Scope).ScopeType);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("beforeSend")]
    [InlineData("Reply")]
    public void ArbitraryHooksCannotBeAdded(string stage) =>
        Assert.Equal("SKILL_STAGES_INVALID", Assert.Throws<SkillException>(() => SkillPolicy.Validate(
            Valid with { Stages = ["reply", stage] }, Scope)).Code);

    [Fact]
    public void PersonaRequiresReplyAndWorkflowKeepsOnlyFixedStageOrder()
    {
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { Stages = ["consult"] }, Scope));
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { Stages = ["reply", "reply"] }, Scope));
        Assert.Equal(new[] { "context", "review", "memory" }, SkillPolicy.Validate(Valid with
        { Type = "Workflow", Stages = ["memory", "review", "context"] }, Scope).Stages);
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { Name = new string('a', 35) }, Scope));
        Assert.Throws<SkillException>(() => SkillPolicy.Validate(Valid with { Instruction = "short" }, Scope));
    }

    [Fact]
    public void DraftDeletedDisabledAndForeignWorkflowNeverInfluenceExecution()
    {
        var persona = Skill("natural", "Persona", ["reply"]);
        var flow = Skill("flow", "Workflow", ["review"]);
        var excluded = new[]
        {
            flow with { SkillId = "draft", ApprovalStatus = "Draft" },
            flow with { SkillId = "deleted", Deleted = true },
            flow with { SkillId = "disabled", Enabled = false },
            flow with { SkillId = "foreign", ScopeType = "Conversation", AccountId = Scope.AccountId, ConversationId = Guid.NewGuid() },
            flow with { SkillId = "foreign-account", ScopeType = "Account", AccountId = Guid.NewGuid() }
        };
        var resolved = SkillPolicy.Resolve(new[] { persona, flow }.Concat(excluded), Scope, "natural");
        Assert.Single(resolved.WorkflowInstructions["review"]);
        Assert.Single(resolved.WorkflowRevisions);
        Assert.Equal("flow", resolved.WorkflowRevisions.Keys.Single());
        Assert.Empty(resolved.WorkflowInstructions["message"]);
        Assert.Throws<SkillException>(() => SkillPolicy.Resolve([persona with { ApprovalStatus = "Draft" }], Scope, "natural"));
    }

    [Fact]
    public void RevisionDigestChangesForPersonaChoiceWorkflowRevisionAndDisableButNotListOrder()
    {
        var persona = Skill("natural", "Persona", ["reply"]);
        var other = persona with { SkillId = "warm" };
        var flow = Skill("flow", "Workflow", ["review"]);
        var initial = SkillPolicy.Resolve([persona, other, flow], Scope, "natural");
        Assert.Equal(initial.RevisionDigest, SkillPolicy.Resolve([flow, other, persona], Scope, "natural").RevisionDigest);
        Assert.NotEqual(initial.RevisionDigest, SkillPolicy.Resolve([persona, other, flow], Scope, "warm").RevisionDigest);
        Assert.NotEqual(initial.RevisionDigest, SkillPolicy.Resolve([persona, other, flow with { Revision = 2 }], Scope, "natural").RevisionDigest);
        Assert.NotEqual(initial.RevisionDigest, SkillPolicy.Resolve([persona, other, flow with { Enabled = false }], Scope, "natural").RevisionDigest);
    }

    [Fact]
    public async Task SkillsEndpointsRejectMissingCredentialBeforeResolvingDatabaseOrScope()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
        { ["Authentication:WebBearerToken"] = "synthetic-skill-token" });
        await using var app = builder.Build();
        app.MapV6SkillEndpoints(builder.Configuration);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>().First(e => e.RoutePattern.RawText == "/api/skills/");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.Path = "/api/skills";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(401, context.Response.StatusCode);
    }

    private static SkillDto Skill(string id, string type, string[] stages) => new(id, type, "规则", "",
        "仅使用当前授权会话资料", stages, "AllAuthorizedAccounts", null, null, true, false, "Manual", 1,
        "Approved", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
