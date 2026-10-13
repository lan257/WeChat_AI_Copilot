using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WeChatAiCopilot.Api.Modules.Operations;
using WeChatAiCopilot.Api.Platform.Migrations;

namespace WeChatAiCopilot.DomainTests;

public sealed class V6WebSessionTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080",true)]
    [InlineData("http://127.0.0.1:8080",false)]
    [InlineData("https://127.0.0.1:5080",false)]
    [InlineData("https://attacker.example",false)]
    [InlineData("null",false)]
    [InlineData("",false)]
    public void SessionMutationRequiresExactSchemeHostAndPort(string origin,bool expected)
    {
        var context=new DefaultHttpContext();context.Request.Scheme="http";context.Request.Host=new("127.0.0.1",5080);
        context.Request.Headers.Origin=origin;Assert.Equal(expected,WebSessionServiceV1.SameOrigin(context));
    }

    [Fact]
    public void PairedDeviceCannotUseAnotherConversationOrAccount()
    {
        var account=Guid.NewGuid();var conversation=Guid.NewGuid();
        var identity=new WebSessionIdentityV1(Guid.NewGuid(),false,[account],[conversation],false,DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(identity.Allows(account,conversation));Assert.False(identity.Allows(account,Guid.NewGuid()));
        Assert.False(identity.Allows(Guid.NewGuid(),conversation));Assert.False(identity.AllowsAccount(Guid.NewGuid()));
        Assert.True((identity with {AllImported=true,IsLocal=true}).Allows(Guid.NewGuid(),Guid.NewGuid()));
    }

    [Fact]
    public void MobileAccessRequiresExplicitTrustedHttpsConfiguration()
    {
        IConfiguration Config(params (string,string)[] pairs)=>new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(pair=>new KeyValuePair<string,string?>(pair.Item1,pair.Item2))).Build();
        Assert.False(SessionEndpointsV1.MobileConfigured(Config()));
        Assert.False(SessionEndpointsV1.MobileConfigured(Config(("Web:TrustedHttpsEnabled","true"),("Web:MobileOrigin","http://192.168.1.2:5443"))));
        Assert.False(SessionEndpointsV1.MobileConfigured(Config(("Web:TrustedHttpsEnabled","false"),("Web:MobileOrigin","https://192.168.1.2:5443"))));
        Assert.True(SessionEndpointsV1.MobileConfigured(Config(("Web:TrustedHttpsEnabled","true"),("Web:MobileOrigin","https://192.168.1.2:5443"))));
    }

    [V6SessionsPostgresFact]
    public async Task StoredSessionEnforcesOriginCsrfExpiryRevocationAndHttps()
    {
        var builder=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES"));
        Assert.Contains("test",builder.Database!,StringComparison.OrdinalIgnoreCase);
        await using var db=NpgsqlDataSource.Create(builder.ConnectionString);await MigrationRunner.ApplyPendingAsync(db);
        var sessions=new WebSessionServiceV1(db);var token=WebSessionServiceV1.NewToken();var id=Guid.NewGuid();
        await using(var insert=db.CreateCommand("INSERT INTO copilot.web_sessions(session_id,token_hash,device_name,all_imported,is_local,expires_at) VALUES(@id,@hash,'synthetic',true,true,now()+interval '1 hour');")){
            insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("hash",WebSessionServiceV1.Hash(token));await insert.ExecuteNonQueryAsync();}
        var context=new DefaultHttpContext();context.Connection.RemoteIpAddress=IPAddress.Loopback;context.Request.Scheme="http";context.Request.Host=new("127.0.0.1",5080);
        context.Request.Headers.Cookie=WebSessionServiceV1.CookieName+"="+token;
        Assert.NotNull(await sessions.ValidateAsync(context,false));
        Assert.Null(await sessions.ValidateAsync(context,true));
        context.Request.Headers.Origin="http://127.0.0.1:5080";context.Request.Headers[WebSessionServiceV1.CsrfHeader]=WebSessionServiceV1.Csrf(token);
        Assert.NotNull(await sessions.ValidateAsync(context,true));
        context.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.10");Assert.Null(await sessions.ValidateAsync(context,false));
        context.Connection.RemoteIpAddress=IPAddress.Loopback;
        await using(var revoke=db.CreateCommand("UPDATE copilot.web_sessions SET revoked_at=now() WHERE session_id=@id;")){revoke.Parameters.AddWithValue("id",id);await revoke.ExecuteNonQueryAsync();}
        Assert.Null(await sessions.ValidateAsync(context,false));
    }
}

public sealed class V6SessionsPostgresFactAttribute:FactAttribute
{
    public V6SessionsPostgresFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WECHAT_AI_COPILOT_V6_TEST_POSTGRES")))Skip="Requires a disposable test PostgreSQL database; never reads Formal data.";}
}
