using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MatchLens;
public sealed class GsiServer : IAsyncDisposable
{
    WebApplication? app;
    public async Task Start(string token,Action<JsonElement> receive,int port=37931)
    {
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o=>{o.ListenLocalhost(port);o.Limits.MaxRequestBodySize=1048576;});
        app=builder.Build();
        app.MapPost("/gsi",async (HttpContext ctx)=>
        {
            try
            {
                using var doc=await JsonDocument.ParseAsync(ctx.Request.Body,cancellationToken:ctx.RequestAborted);
                if(doc.RootElement.At("auth","token").Str()!=token){ctx.Response.StatusCode=403;return;}
                receive(doc.RootElement.Clone());ctx.Response.StatusCode=200;
            }
            catch(JsonException){ctx.Response.StatusCode=400;}
        });
        await app.StartAsync();
    }
    public async ValueTask DisposeAsync(){if(app!=null){using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));await app.StopAsync(stop.Token);await app.DisposeAsync();}}
}
