using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MatchLens;
public static class PerformanceTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var gate=new PriorityGate(1);await gate.WaitAsync(0);
        var background=gate.WaitAsync(2);var core=gate.WaitAsync(0);var core2=gate.WaitAsync(0);
        gate.Release();await core.WaitAsync(TimeSpan.FromSeconds(2));
        check(!background.IsCompleted&&!core2.IsCompleted,"Overview browser requests take priority over queued details");
        gate.Release();await core2.WaitAsync(TimeSpan.FromSeconds(2));check(!background.IsCompleted,"Equal browser priorities preserve FIFO order");
        gate.Release();await background;using var canceled=new CancellationTokenSource();var waiting=gate.WaitAsync(0,canceled.Token);canceled.Cancel();
        try{await waiting;}catch(OperationCanceledException){}
        gate.Release();await gate.WaitAsync(0).WaitAsync(TimeSpan.FromSeconds(2));gate.Release();
        check(waiting.IsCanceled,"Canceled match requests leave no blocked or lost browser permits");

        using(var handler=new DelayedScope())using(var providers=new Providers(handler))
        using(var stop=new CancellationTokenSource())
        {
            var ids=Enumerable.Range(9100,10).Select(i=>(SteamIds.Base+(ulong)i).ToString()).ToArray();
            var observed=new HashSet<string>();var firstStats=new TaskCompletionSource<bool>();
            var loads=ids.Select(id=>providers.Fetch(id,new Settings(),stop.Token,cards=>
            {if(cards.Any(c=>c.Name=="LEETIFY"&&c.Numbers.Any(n=>n.Key=="kd"))){observed.Add(id);if(observed.Count==10)firstStats.TrySetResult(true);}})).ToArray();
            await firstStats.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(observed.Count==10&&loads.All(t=>!t.IsCompleted),"All ten players receive primary statistics while slow SCOPE pages remain pending");
            check(handler.ScopeEntered==2&&handler.MaxActive<=8,"Slow tracker traffic is limited to two per host and eight total HTTP requests");
            stop.Cancel();try{await Task.WhenAll(loads);}catch(OperationCanceledException){}
            check(loads.All(t=>t.IsCanceled),"Changing match cancels every outstanding profile fetch promptly");
        }

        var slowBrowserEntered=new TaskCompletionSource<bool>();var browserPlayers=new HashSet<string>();
        var bothBrowserStats=new TaskCompletionSource<bool>();
        async Task<BrowserPage> Render(string url,Func<string,bool> ready,CancellationToken ct)
        {
            if(url.Contains("app.scope.gg")){slowBrowserEntered.TrySetResult(true);await Task.Delay(Timeout.Infinite,ct);}
            var html=url.Contains("leetify.com")?PublicStatsTests.LeetifyFixture:"<h1>No statistics</h1>";
            return new(url,"Public profile","Public statistics",html,200);
        }
        using(var handler=new ShellHandler())using(var providers=new Providers(handler,Render))using(var stop=new CancellationTokenSource())
        {
            void Partial(string id,List<SourceCard> cards)
            {if(cards.Any(c=>c.Name=="LEETIFY"&&c.Numbers.Count>0)){browserPlayers.Add(id);if(browserPlayers.Count==2)bothBrowserStats.TrySetResult(true);}}
            var first=providers.Fetch("76561198000009201",new Settings(),stop.Token,c=>Partial("1",c));
            await slowBrowserEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second=providers.Fetch("76561198000009202",new Settings(),stop.Token,c=>Partial("2",c));
            await bothBrowserStats.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!first.IsCompleted&&!second.IsCompleted,"A stalled background browser page leaves capacity for the next player's JavaScript statistics");
            stop.Cancel();try{await Task.WhenAll(first,second);}catch(OperationCanceledException){}
        }

        const string cachedId="76561198000009111";
        var saved=new SourceCard{Name="LEETIFY",FetchedAt=DateTimeOffset.UtcNow,Metrics=[new("K/D","1.23")],Numbers=[new("kd","K/D",1.23)]};
        Store.Write("public-profile-v043-"+cachedId+".json",new PublicCache{Id=cachedId,SavedAt=DateTimeOffset.UtcNow,Cards=[saved]});
        using(var handler=new BlockedHandler())using(var providers=new Providers(handler))using(var stop=new CancellationTokenSource())
        {
            List<SourceCard>? first=null;
            var load=providers.Fetch(cachedId,new Settings(),stop.Token,cards=>first??=cards);
            check(first?.Any(c=>c.Numbers.Any(n=>n.Key=="kd"&&n.Value==1.23))==true&&!load.IsCompleted,"Cached statistics appear before any network response is available");
            stop.Cancel();try{await load;}catch(OperationCanceledException){}
            check(providers.Cached(cachedId).Single(c=>c.Name=="LEETIFY").Numbers.Single().Value==1.23,"Canceled refresh preserves the last complete cached profile");
        }

        var now=DateTimeOffset.UtcNow;var tracker=new MatchTracker();
        JsonElement Gsi(string phase,string mode="competitive")=>JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid="76561198000000001"},map=new{name="de_mirage",phase,mode},player=new{steamid="76561198000000001",name="Me",team="CT"}});
        tracker.Apply(Gsi("live"),now);tracker.AddManual([cachedId]);var generation=tracker.State.Generation;
        tracker.Apply(Gsi("live"),now.AddSeconds(1));check(tracker.State.Generation==generation&&tracker.State.Roster.Count==2,"Ordinary same-match GSI updates retain known players");
        tracker.Apply(Gsi("warmup"),now.AddSeconds(2));check(tracker.State.Generation==generation+1&&tracker.State.Roster.Count==1,"A new warmup on the same map clears the previous match roster");
        tracker.AddManual([cachedId]);tracker.Apply(Gsi("live","deathmatch"),now.AddSeconds(3));check(tracker.State.Roster.Count==1,"Switching game mode resets stale players even on the same map");
        var selector=new RecentDiscovery();var previous=new CoplayEntry(cachedId,"Old",now.AddSeconds(-20).ToUnixTimeSeconds());
        selector.Observe(new MatchState(),[previous],now);
        var state=new MatchState{Active=true,Generation=1,StartedAt=now};
        check(selector.Observe(state,[previous],now).Count==0,"Idle Steam history cannot become the next match roster");
        check(selector.Observe(state,[previous with{Timestamp=now.AddSeconds(1).ToUnixTimeSeconds()}],now.AddSeconds(2)).Count==1,"Fresh Steam evidence restores a current player without waiting for profile statistics");

        var stats=new SourceCard{Name="LEETIFY",Numbers=[new("kd","KD",1.31),new("adr","ADR",81),new("headshots","HS",42,"%"),new("leetify_rating","LT",3.21),new("faceit_elo","ELO",1750)]};
        var trust=new SourceCard{Name="CSTRACKER",Numbers=[new("trust","TR",93,"%")]};
        foreach(var count in new[]{10,24,64})
        {
            var players=Enumerable.Range(1,count).Select(i=>new Player{Id=(SteamIds.Base+(ulong)(9200+i)).ToString(),Name=$"Player{i:00} 🖤🎯 <&>",Confirmed=true,Sources=[stats,trust]}).ToList();
            var message=Telegram.Format(state,players);
            check(message.Pages==1&&TelegramMessage.VisibleLength(message.Text)<=4000&&players.All(p=>message.Text.Contains($"/{p.Id}\"")),"Single Telegram message includes every player within the limit: "+count);
            XDocument.Parse("<root>"+message.Text.Replace("&nbsp;"," ")+"</root>");
        }
        var rich=Telegram.Format(state,[new Player{Name="Test",Sources=[stats,trust]}]).Text;
        check(rich.Contains("81")&&rich.Contains("42%")&&rich.Contains("+3.21")&&rich.Contains("1750")&&rich.Contains("93%"),"Telegram aligned summary uses all six real metric keys");
        var noTrust=Telegram.Format(state,[new Player{Name="Test",Sources=[stats,new SourceCard{Name="OTHER",Numbers=[new("trust","Trust",100)]}]}]).Text;
        check(!noTrust.Contains("100%"),"Telegram trust is sourced only from cstracker and is never inferred");
        var strange=Enumerable.Range(1,64).Select(i=>new Player{Name=new string('&',300)+"👨‍👩‍👧‍👦",Sources=[stats,trust]}).ToList();
        check(TelegramMessage.VisibleLength(Telegram.Format(state,strange).Text)<=4000,"Extreme HTML-escaped nicknames still fit the entire Telegram roster");
    }
    sealed class DelayedScope:HttpMessageHandler
    {
        int active;public int MaxActive,ScopeEntered;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            active++;MaxActive=Math.Max(MaxActive,active);
            try
            {
                if(request.RequestUri!.Host=="app.scope.gg"){ScopeEntered++;await Task.Delay(Timeout.Infinite,ct);}
                var html=request.RequestUri.Host=="leetify.com"?PublicStatsTests.LeetifyFixture:"<html><body>No public statistics</body></html>";
                return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(html,Encoding.UTF8,"text/html")};
            }
            finally{active--;}
        }
    }
    sealed class BlockedHandler:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();}
    }
    sealed class ShellHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent("<html><h1>Loading</h1><script>hydrate()</script></html>")});
    }
}
