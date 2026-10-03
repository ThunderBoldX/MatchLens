using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MatchLens;
public static class PublicStatsTests
{
    const string id="76561198000000001";
    public const string LeetifyFixture="""
        <html><body><h1>Test player</h1><img src="https://avatars.steamstatic.com/example.jpg">
        <h2>Ranks</h2><div>Premier</div><div>19,876</div><div>FACEIT</div><img alt="FACEIT Level 8"><div>1,750</div><h2>Platforms</h2>
        <h3>Steam</h3><div>Account Level</div><div>28</div><div>Account Age</div><div>12 years</div><button>Show More</button>
        <h3>Last 30 Matches</h3><span>All 5v5 sources</span>
        <div>Leetify Rating</div><strong>+3.21</strong><div>Win Rate</div><span>56%</span><div>K/D</div><span>1.31</span><div>KAST</div><span>75%</span>
        <div>Aim</div><strong>82</strong><span>Better than 92% of players</span><span>Rating Breakdown</span><span>71</span>
        <div>Utility</div><strong>53</strong><div>Time to Damage</div><strong>553ms</strong><div>Crosshair Placement</div><strong>9.2°</strong>
        <div>Kills per Match</div><strong>22.5</strong><div>Trade Kill Success</div><strong>62%</strong>
        <div>Accuracy (Enemy Spotted)</div><strong>43%</strong><h3>Top Highlights</h3>
        <div style="display:none">ADR<span>999</span></div><script>Win Rate 100 ADR 999</script>
        </body></html>
        """;
    const string CsStatsFixture="<h1>Test</h1><div>Premier</div><div>Wins</div><div>11</div><div>Current</div><div>22,157</div><div>Best</div><div>26,979</div><h3>FACEIT</h3><h3>Please login to view player stats</h3>";
    static string SteamFixture(string identity)=>$"<profile><steamID64>{identity}</steamID64><steamID><![CDATA[A & B]]></steamID><privacyState>public</privacyState><avatarFull>https://avatars.steamstatic.com/example.jpg</avatarFull><vacBanned>0</vacBanned><tradeBanState>None</tradeBanState><memberSince>March 18, 2019</memberSince></profile>";
    public static async Task Run(Action<bool,string> check,DateTimeOffset now)
    {
        var l=PublicProfileParser.Leetify(id,LeetifyFixture);
        check(l.Numbers.Single(n=>n.Key=="kd").Value==1.31&&l.Numbers.Single(n=>n.Key=="winrate").Value==56,"Public Leetify labels and percentages parse without credentials");
        check(l.Numbers.Single(n=>n.Key=="aim").Value==82,"Tooltip and average values cannot replace the player's aim rating");
        check(l.Numbers.All(n=>n.Key!="adr"),"Hidden markup and scripts are excluded from metric sources");
        check(l.Numbers.Single(n=>n.Key=="faceit_elo").Value==1750&&l.Numbers.Single(n=>n.Key=="faceit_level").Value==8,"Public FACEIT rank parses with attribution to Leetify");
        check(l.AvatarUrl.Contains("steamstatic.com")&&l.Scope.Contains("30"),"Public profile preserves avatar source and comparison period");
        var cs=PublicProfileParser.CsStats(id,CsStatsFixture);
        check(cs.Numbers.Single(n=>n.Key=="premier").Value==22157&&cs.Numbers.All(n=>n.Key!="kd"),"Gated CSStats profile provides public Premier but never invented K/D");
        check(PublicProfileParser.Number("22,157")==22157&&PublicProfileParser.Number("1,31")==1.31&&PublicProfileParser.Number("Better than 99% of players")==null,"Numeric parsing separates thousands, decimals and explanatory text");
        check(PublicProfileParser.Leetify(id,"<h1>Sign in</h1>").Numbers.Count==0,"Login shell remains empty instead of zero statistics");
        check(PublicProfileParser.Leetify(id,LeetifyFixture.Replace("All 5v5 sources","Premier only")).Scope=="","Unrecognized game filter cannot claim a comparable scope");
        var steam=PublicProfileParser.Steam(id,SteamFixture(id));
        check(steam.Metrics.Any(m=>m.Label=="Нік Steam"&&m.Value=="A & B")&&steam.Metrics.Any(m=>m.Label=="VAC-бан"&&m.Value=="Немає позначки"),"Public Steam XML parses CDATA and explicit ban state");
        check(PublicProfileParser.Steam(id,SteamFixture("76561198000000002")).Metrics.Count==0,"Steam profile must match the requested identity");
        check(PublicProfileParser.Steam(id,"<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///secret'>]><profile>&x;</profile>").Metrics.Count==0,"Statistics XML external entities are prohibited");
        var maps=PublicProfileParser.MapTables("<table><tr><th>Map</th><th>Wins</th><th>Matches</th></tr><tr><td>de_mirage</td><td>12</td><td>20</td></tr></table>");
        check(maps.Count==1&&maps[0].Wins==12,"Public table map chart uses explicit wins and match counts");
        var detail=$"<span>{id}</span><h1>Map Stats</h1><span>Last 100 Matches</span><div>Mirage</div><span>Win Rate</span><strong>69%</strong><span>Matches</span><span>13</span>";
        var history=$"<span>{id}</span><table><tr><th>Map</th><th>Date</th><th>Leetify Rating</th><th>Source</th></tr><tr><td>Mirage</td><td>2026-09-02</td><td>+3.14</td><td>FACEIT</td></tr><tr><td>Nuke</td><td>2026-09-01</td><td>-1.25</td><td>Premier</td></tr></table>";
        PublicProfileParser.LeetifyDetails(l,id,detail,history);
        check(l.MapRates.Single().WinRate==69&&l.MapRates.Single().Matches==13&&l.Maps.Count==0,"Map percentages retain displayed sample count without deriving fictional wins");
        check(l.History.Count==2&&l.History[0].Value==-1.25&&l.History[1].Scope=="FACEIT","Rating history preserves signed values, chronological order and source");
        var mismatch=new SourceCard();PublicProfileParser.LeetifyDetails(mismatch,"76561198000000002",detail,history);
        check(mismatch.MapRates.Count==0&&mismatch.History.Count==0,"Linked detail pages must confirm the original Steam identity");
        check(PublicProfileParser.LeetifySubpage("<a href='/@test/maps'>Maps</a>","maps")=="https://leetify.com/@test/maps"&&PublicProfileParser.LeetifySubpage("<a href='https://leetify.com.evil.test/@test/maps'>Maps</a>","maps")==null,"Only public same-site Leetify detail links are followed");
        var self=new Player{Id=id,Sources=[l]};l.FetchedAt=now;
        var other=PublicProfileParser.Leetify("76561198000000002",LeetifyFixture.Replace("1.31","1.61").Replace("+3.21","+5.21").Replace(">82<",">92<"));other.FetchedAt=now;
        other.SampleCount=30;var player=new Player{Id="76561198000000002",Sources=[other]};
        var comparison=PlayerComparison.Read(player,self);
        check(comparison.Available&&comparison.Index>0&&comparison.Metrics.Count>=3,"Comparison uses shared recent metrics rather than Premier or account age");
        var numeric=other.Numbers.First(n=>n.Key=="aim");
        var green=MetricAppearance.Tile("aim","Aim",numeric.Display,"LEETIFY",numeric,player,self,"LEETIFY");
        check(green.Tone==MetricTone.Good,"Color comparison uses the same observed Leetify scope as the difficulty index");
        numeric=other.Numbers.First(n=>n.Key=="ttd");var inverseSelf=new Player{Id=id,Sources=[PublicProfileParser.Leetify(id,LeetifyFixture.Replace("553ms","653ms"))]};inverseSelf.Sources[0].FetchedAt=now;
        check(MetricAppearance.Tile("ttd","TTD",numeric.Display,"LEETIFY",numeric,player,inverseSelf,"LEETIFY").Tone==MetricTone.Good,"Lower damage latency is favorable rather than red");
        var uncertain=other.Scope;other.Scope="Other period";
        check(MetricAppearance.Tile("aim","Aim","92","LEETIFY",other.Numbers.First(n=>n.Key=="aim"),player,self,"LEETIFY").Tone==MetricTone.Good,"Normal aim stays green independently of the comparison period");other.Scope=uncertain;
        check(MetricAppearance.Tile("kd","K/D","-","LEETIFY").Tone==MetricTone.Neutral,"Missing statistics stay neutral instead of poor performance");
        check(MetricAppearance.Tile("headshots","HS","99%","LEETIFY",new("headshots","HS",99,"%"),player,self,"LEETIFY").Tone==MetricTone.Low,"Extreme headshot rate gets a local review marker, not a cheating verdict");
        check(MetricAppearance.Tile("winrate","Wins","50%","LEETIFY",new("winrate","Wins",50,"%")).Tone==MetricTone.Good,"An ordinary win rate is green");
        check(MetricAppearance.Tile("leetify_rating","Rating","-2","LEETIFY",new("leetify_rating","Rating",-2)).Tone==MetricTone.Good,"Weak performance is ordinary, not a red suspicion marker");
        check(MetricAppearance.Tile("aim","Aim","99","LEETIFY",new("aim","Aim",99," / 100"),player,self,"LEETIFY").Tone==MetricTone.Good,"Normalized aim score never uses a raw shot accuracy threshold");
        check(MetricAppearance.Tile("kd","K/D","0.5","LEETIFY",new("kd","K/D",.5)).Tone==MetricTone.Good,"Low K/D is ordinary performance rather than suspicion");
        check(MetricAppearance.Tile("headshots","HS","99%","LEETIFY",new("headshots","HS",99,"%")).Tone==MetricTone.Good,"Tiny or unknown samples do not trigger a local extreme-rate marker");
        TrackingTests.Run(check);
        check(!PlayerComparison.Read(self,self).Available,"The local profile is a baseline, not its own opponent");
        other.Scope="Lifetime";check(!PlayerComparison.Read(player,self).Available,"Different source periods are not compared");other.Scope=l.Scope;
        other.FetchedAt=now.AddDays(-2);check(!PlayerComparison.Read(player,self).Available,"Old profile observations do not produce fresh difficulty rankings");other.FetchedAt=now;
        other.Numbers=[new("kd","K/D",1.9)];check(!PlayerComparison.Read(player,self).Available,"One shared metric is insufficient for difficulty ranking");
        var tracker=new MatchTracker();tracker.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=id},map=new{name="de_mirage",phase="live"},player=new{steamid=id,name="Me",team="CT",match_stats=new{kills=12,deaths=8,assists=3}}}),now);
        check(tracker.State.LivePlayers[id].Kills==12&&tracker.State.LivePlayers[id].Mvps==null,"Current GSI stats preserve missing fields as missing");
        tracker.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=id},map=new{name="de_mirage",phase="live"},player=new{steamid=id,name="Me",team="CT"}}),now);
        check(tracker.State.LivePlayers[id].Kills==12,"A partial GSI packet does not erase observed match stats");
        tracker.End(now);check(tracker.State.LivePlayers.Count==0,"Ending the session clears current-match statistics");
        var fit=WindowAppearance.InitialSize(1093,584);check(fit.Width<=1093&&fit.Height<=584,"Initial window fits a laptop work area at scaled DPI");
        var noTeams=Telegram.Format(new(){Active=true},[new(){Id=id,Name="Me",Team="CT",Sources=[]}],0).Text;
        check(!noTeams.Contains(" · CT")&&!noTeams.Contains(">CT<")&&!noTeams.Contains(">T<"),"Telegram output no longer groups players by CT and T");
        var h=new PublicHandler();using(var provider=new Providers(h))
        {
            var updates=0;var cards=await provider.Fetch(id,new Settings{SteamKey="NOT_SENT",FaceitKey="NOT_SENT",LeetifyKey="NOT_SENT"},partial:_=>updates++);
            check(cards.Count(c=>c.Metrics.Count>0)>=4&&updates>=5,"Automatic public fetch updates sources independently with no Bridge");
            check(!h.Seen.Any(r=>r.Contains("NOT_SENT")||r.Contains("key=")||r.Contains("api-public")||r.Contains("api.steampowered")),"Provider requests never use stored API credentials");
            var count=h.Seen.Count;await provider.Fetch(id,new Settings());check(h.Seen.Count==count,"Profile cache prevents repeated requests within the same match");
        }
        var linked=new PublicHandler{LinkedFaceit=true};using(var provider=new Providers(linked))
        {
            var cards=await provider.Fetch("76561198000000012",new Settings());var faceit=cards.Single(c=>c.Name=="FACEIT");
            check(faceit.RecentMatches.Count==2&&faceit.Numbers.Any(n=>n.Key=="recent_kd")&&linked.Seen.Any(u=>u=="https://www.faceit.com/en/players/TestNick"),"Confirmed profile link fetches public FACEIT history automatically");
            check(faceit.Numbers.Single(n=>n.Key=="faceit_elo").Scope.Contains("LEETIFY"),"Fallback FACEIT rank keeps its original source after direct history is merged");
        }
        var limited=new PublicHandler{LimitLeetify=true};using(var provider=new Providers(limited))
        {
            await provider.Fetch("76561198000000010",new Settings());await provider.Fetch("76561198000000011",new Settings());
            check(limited.Seen.Count(u=>u.Contains("leetify.com"))==1,"HTTP 429 pauses later profiles for that source rather than retrying aggressively");
        }
    }
    sealed class PublicHandler : HttpMessageHandler
    {
        public List<string> Seen {get;}=[];public bool LimitLeetify;public bool LinkedFaceit;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Seen.Add(request.RequestUri!.ToString());
            if(request.Headers.Authorization!=null)throw new Exception("Credentials were sent");
            var host=request.RequestUri.Host;
            if(host=="leetify.com"&&LimitLeetify)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests){RequestMessage=request});
            var requested=SteamIds.Parse(request.RequestUri.ToString()).FirstOrDefault()??id;
            var content=host switch{"leetify.com"=>LeetifyFixture+(LinkedFaceit?"<a href='https://www.faceit.com/en/players/TestNick'>FACEIT</a>":""),"csstats.gg"=>CsStatsFixture,"steamcommunity.com"=>SteamFixture(requested),"cstracker.gg"=>TrackingTests.Fixture(requested),"www.faceit.com"=>TrackingTests.FaceitFixture,_=>"<html><h1>Sign in</h1></html>"};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(content,Encoding.UTF8,"text/html")});
        }
    }
}
