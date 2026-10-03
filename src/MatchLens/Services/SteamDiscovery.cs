using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MatchLens;
public record SteamSnapshot(bool Available,string Status,List<CoplayEntry> Players,List<RosterEntry>? ServerPlayers=null,DateTimeOffset CapturedAt=default);
public sealed class SteamDiscovery : IDisposable
{
    public const string WirePrefix="MATCHLENS_SNAPSHOT_V1:";
    Process? worker;
    readonly DiscoverySchedule recovery=new();
    public string Status {get;private set;}="Очікуємо Steam";
    public async Task<SteamSnapshot?> Poll(string path,int generation=-1)
    {
        var now=DateTimeOffset.UtcNow;recovery.Session(generation,now);
        if(!recovery.Ready(now))return null;
        // Warm the passive pipe while Steam is open. It never starts a game.
        if(!GamePresence.SteamRunning()&&!GamePresence.Running()){Dispose();Status="Очікуємо Steam";return null;}
        try
        {
            var starting=worker==null||worker.HasExited;
            if(starting)
            {
                Dispose();
                var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
                start.ArgumentList.Add("--passive-steam-worker");worker=Process.Start(start)??throw new IOException("Helper did not start");
            }
            await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(path));await worker.StandardInput.FlushAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(starting?5:2));
            SteamSnapshot? result=null;
            for(var i=0;i<32;i++)
            {
                var line=await worker.StandardOutput.ReadLineAsync(timeout.Token);
                if(line==null||line.Length>=1048576)break;
                if(!line.StartsWith(WirePrefix,StringComparison.Ordinal))continue;
                result=JsonSerializer.Deserialize<SteamSnapshot>(line[WirePrefix.Length..]);break;
            }
            if(result==null)throw new IOException("Helper response missing");
            Status=result.Status;
            if(result.Available)recovery.Success();else{recovery.Failure(DateTimeOffset.UtcNow);Dispose();}
            return result;
        }
        catch(Exception ex)
        {Dispose();recovery.Failure(DateTimeOffset.UtcNow);Status="Steam: повторне підключення · "+ex.GetType().Name;return null;}
    }
    public static async Task RunWorker()
    {
        using var client=new PassiveSteamClient();using var sdk=new SteamReader();
        var sdkConnected=false;DateTimeOffset? emptySince=null;DateTimeOffset sdkRetry;
        sdkRetry=default;string? line;
        while((line=await Console.In.ReadLineAsync())!=null)
        {
            SteamSnapshot result;
            try
            {
                var path=JsonSerializer.Deserialize<string>(line)??"";
                var running=GamePresence.Running();var now=DateTimeOffset.UtcNow;
                if(!running&&sdkConnected){sdk.Dispose();sdkConnected=false;}
                if(!GamePresence.SteamRunning()&&!running)
                {client.Dispose();emptySince=null;result=new(false,"Steam закрито",[],[],now);}
                else
                {
                    var connected=client.Connect(path);var status=client.Status;
                    var history=new List<CoplayEntry>();var server=new List<RosterEntry>();
                    if(connected)
                    {
                        // Current-server enumeration comes first; no profile requests.
                        server=client.ReadServer();history=client.Read();
                    }
                    if(server.Count>0)emptySince=null;else emptySince??=now;
                    // Preserve the SDK fallback for installations where the passive
                    // pipe has no current-server data. Historical entries must
                    // not suppress the app-specific check and callback pump.
                    // Never initialize app 730 without real CS2.
                    if(running&&!sdkConnected&&now>=sdkRetry&&(!connected||now-emptySince>=TimeSpan.FromSeconds(3)))
                    {sdkConnected=sdk.Connect(path);sdkRetry=now.AddSeconds(5);}
                    if(sdkConnected&&running)
                    {
                        var live=sdk.ReadServer();var recent=sdk.Read();
                        if(live.Count>0)server=live;if(recent.Count>0)history=recent;
                        status=server.Count>0?"Steam SDK · поточний сервер":connected?client.Status:"Steam SDK · історія";
                    }
                    result=new(connected||sdkConnected,status,history,server,DateTimeOffset.UtcNow);
                }
            }
            catch(Exception ex){result=new(false,"Помилка Steam · "+ex.GetType().Name,[],[],DateTimeOffset.UtcNow);client.Dispose();sdk.Dispose();sdkConnected=false;}
            await Console.Out.WriteLineAsync(WirePrefix+JsonSerializer.Serialize(result));await Console.Out.FlushAsync();
        }
    }
    public void Dispose()
    {
        if(worker==null)return;
        try{worker.StandardInput.Close();if(!worker.HasExited)worker.Kill();}catch{}
        worker.Dispose();worker=null;
    }
}
