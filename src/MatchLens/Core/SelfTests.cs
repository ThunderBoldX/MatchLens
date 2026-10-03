using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MatchLens;
public static class SelfTests
{
    static readonly List<string> passed=[];
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);passed.Add("PASS: "+name);}
    static JsonElement Json(string data)=>JsonDocument.Parse(data).RootElement.Clone();
    static JsonElement Gsi(string map="de_mirage",string phase="live",string mode="deathmatch")=>JsonSerializer.SerializeToElement(new {provider=new{appid=730,steamid="76561198000000001"},map=new{name=map,mode,phase,team_ct=new{score=3},team_t=new{score=2}},player=new{steamid="76561198000000001",name="Me",team="CT"}});
    public static async Task Run()
    {
        L.Set("uk");
        await PresentationTests.Run(Check);
        var baseRoot=Store.Root;Store.Root=Path.Combine(baseRoot,"self-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Store.Root);Store.Write("settings.json",new Settings{Language="uk"});
        var now=DateTimeOffset.UtcNow;var old=new CoplayEntry("76561198000000002","Old",now.AddHours(-1).ToUnixTimeSeconds());var current=new CoplayEntry("76561198000000003","Current",now.ToUnixTimeSeconds());
        var discovery=new RecentDiscovery();
        var session=new MatchState{Active=true,Generation=1,StartedAt=now,LocalId="76561198000000001"};
        Check(discovery.Observe(session,[old,current],now).Single().Id==current.Id,"Startup in match immediately shows newest Steam cohort without waiting for timestamp changes");
        Check(discovery.Observe(session,[old,current],now.AddSeconds(3)).Count==1,"Unchanged newest cohort stays visible");
        var newer=current with{Timestamp=now.AddSeconds(4).ToUnixTimeSeconds()};
        var found=discovery.Observe(session,[old,newer],now.AddSeconds(5));
        Check(found.Count==1&&!found[0].Confirmed&&found[0].Team=="?","Fresh Steam change is automatic but not confirmed or assigned a team");
        session.Generation++;session.StartedAt=now.AddSeconds(8);
        Check(discovery.Observe(session,[old,newer],now.AddSeconds(9)).Count==0,"Previous match cohort stays hidden until Steam publishes current-session entries");
        var future=newer with{Timestamp=now.AddHours(1).ToUnixTimeSeconds()};
        Check(discovery.Observe(session,[future],now.AddSeconds(10)).Count==0,"Future Steam timestamps rejected");
        discovery.Reset();Check(discovery.Observe(session,[newer],now.AddSeconds(11)).Count==0,"Reconnect cannot restore players from a previous match");
        Check(discovery.Observe(session,[old with{Timestamp=now.AddHours(-3).ToUnixTimeSeconds()}],now).Count==0,"Records over two hours old are not shown as recent candidates");
        var near=current with{Id="76561198000000004",Timestamp=current.Timestamp-299};
        var far=current with{Id="76561198000000005",Timestamp=current.Timestamp-301};
        Check(new RecentDiscovery().Observe(new MatchState{Active=true,Generation=1,StartedAt=now},[current with{Timestamp=current.Timestamp-60},near with{Timestamp=near.Timestamp-60},far with{Timestamp=far.Timestamp-60},old],now).Count==2,"Newest cohort uses five-minute window and deduplicates instead of choosing nine");
        session.Active=false;Check(discovery.Observe(session,[current],now).Count==0,"Steam candidates disappear outside a match");session.Active=true;
        var autoTracker=new MatchTracker();autoTracker.Apply(Gsi(),now);autoTracker.AddManual([current.Id],"T");
        autoTracker.ReplaceAutomatic([new(current.Id,"Automatic","?",false,"Авто · Steam")]);
        Check(autoTracker.State.Roster.Single(p=>p.Id==current.Id).Team=="T","Automatic candidates preserve explicitly imported profiles");
        autoTracker.ReplaceAutomatic([new(near.Id,"Automatic","?",false,"Авто · Steam")]);
        autoTracker.SetManualTeam(near.Id,"CT");autoTracker.ReplaceAutomatic([new(near.Id,"Automatic","?",false,"Авто · Steam")]);
        Check(autoTracker.State.Roster.Single(p=>p.Id==near.Id).Team=="CT","Manual team on automatic profile survives background refresh");
        var bulk=Enumerable.Range(100,24).Select(i=>new CoplayEntry((SteamIds.Base+(ulong)i).ToString(),"P",now.AddSeconds(15).ToUnixTimeSeconds())).ToList();
        Check(discovery.Observe(session,bulk,now.AddSeconds(16)).Count==24,"Automatic discovery supports larger modes without padding or cap to nine");
        var t=new MatchTracker();t.Baseline([old]);t.Apply(Gsi(),now);t.Coplay([old,current],now);
        Check(t.State.Active&&t.State.Mode=="deathmatch","DM session starts without matchmaking");
        Check(!t.State.Roster.Any(p=>p.Id==old.Id),"Previous-session player excluded");
        Check(!t.State.Roster.Any(p=>p.Id==current.Id),"Recent coplay cannot contaminate match roster");
        t.ServerPlayers([new(current.Id,current.Name,"?",false,"Steam")]);Check(t.State.Roster.Count==1,"Steam server friend flags cannot contaminate roster");
        t.Apply(Json("{\"provider\":{\"appid\":570},\"map\":{\"name\":\"dota\"}}"),now);Check(t.State.Map=="de_mirage","Other app payload ignored");
        t.Apply(Gsi("de_nuke"),now.AddSeconds(5));Check(t.State.Roster.Count==1,"Map change clears prior roster");
        t.Tick(now.AddSeconds(40));Check(!t.State.Active&&t.State.Roster.Count==0,"GSI timeout clears roster and ends session");
        t.Apply(Gsi(),now);t.Apply(Gsi(phase:"gameover"),now);Check(!t.State.Active,"Gameover immediately ends session");
        t.Apply(Gsi(),now);t.Apply(Json("{\"provider\":{\"appid\":730},\"player\":{}}"),now);Check(!t.State.Active,"Menu payload ends session");
        var startup=new MatchTracker();startup.Apply(Gsi(),now);startup.Coplay([current],now);Check(startup.State.Roster.Count==1,"Startup in match does not assume latest coplay players are current");
        t=new();t.Apply(Gsi(),now);var many=Enumerable.Range(50,24).Select(i=>new CoplayEntry((SteamIds.Base+(ulong)i).ToString(),"P"+i,now.ToUnixTimeSeconds())).ToArray();t.Coplay(many,now);Check(t.State.Roster.Count==1,"24 historical players excluded, not arbitrarily capped to ten");
        var roster=many.ToDictionary(p=>p.Id,p=>new{name=p.Name,team="CT"});
        t.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid="76561198000000001"},map=new{name="de_mirage",phase="live",mode="deathmatch"},allplayers=roster}),now);
        Check(t.State.Roster.Count==24&&t.State.Roster.All(p=>p.Confirmed),"Authoritative GSI supports more than ten players");
        roster.Remove(many[0].Id);t.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730},map=new{name="de_mirage",phase="live"},allplayers=roster}),now);Check(t.State.Roster.Count==23,"Fresh roster removes departed players");
        t.Apply(Gsi(),now);Check(t.State.Roster.Count==24,"Explicit GSI observations survive a self-only payload within the same session");
        await PublicStatsTests.Run(Check,now);
        await BrowserFetchTests.Run(Check);
        await PerformanceTests.Run(Check);
        DiscoveryTests.Run(Check);
        t.AddManual([current.Id],"T");Check(t.State.Roster.Single(p=>p.Id==current.Id).Team=="T"&&!t.State.Roster.Single(p=>p.Id==current.Id).Confirmed,"Manual team remains explicitly manual");
        t.SetManualTeam("76561198000000001","T");Check(t.State.Roster.Single(p=>p.Id=="76561198000000001").Team=="CT","Manual assignment cannot override GSI team");
        var snap=new CsStatsSnapshot{Id=current.Id,Url=$"https://csstats.gg/player/{current.Id}",Text="Visible stats",Tables=[new(){Headers=["Map","Wins","Matches"],Rows=[["de_mirage","12","20"],["nuke","30","20"]]}]};
        Check(snap.Validate()&&snap.Maps.Count==1&&snap.Maps[0].Wins==12,"CSStats table parsed; impossible wins rejected");
        Check(CsStatsSnapshot.ParseMaps([new(){Headers=["Map","Win %"],Rows=[["Mirage","60%"]]}]).Count==0,"Map counts never invented from percentage alone");
        snap.Url=$"https://csstats.gg.evil.test/player/{current.Id}";Check(!snap.Validate(),"CSStats source URL validated against Steam identity");
        snap.Url=$"https://csstats.gg/player/{current.Id}";snap.Text="Please login to view player stats";Check(snap.Validate()&&snap.Status.StartsWith("Часткові"),"Blocked CSStats page labeled partial");
        var ids=SteamIds.Parse("STEAM_1:1:123 [U:1:247] https://steamcommunity.com/profiles/76561197960265975 BAD 123");Check(ids.Count==1&&ids[0]=="76561197960265975","Steam ID formats normalize and deduplicate");
        Check(SteamIds.Parse("BOT STEAM_1:1:999999999999999999999").Count==0,"Invalid and bot IDs rejected");
        var imported=RosterImport.Parse("# 2 1 \"Богдан\" STEAM_1:1:123 11:35 14 0 active\nhttps://steamcommunity.com/profiles/76561197960265975\n# 3 \"Bot\" BOT active");
        Check(imported.Count==1&&imported[0].Name=="Богдан"&&!imported[0].Confirmed,"Console import keeps nickname, deduplicates and does not claim confirmation");
        Check(RosterImport.Parse("# 2 1 \"76561198000000003\" STEAM_1:1:123 active").Single().Id=="76561197960265975","A Steam ID inside nickname is not another player");
        Check(RosterImport.Parse("# 2 playername active 42\nplayers: 10 humans").Count==0,"Console names without IDs are not guessed");
        Check(RosterImport.Parse("[Client] 28 00:09 37 0 active 786432 '76561198000000003'\n[Client] 65535 [NoChan] 0 0 challenging 0 ''").Count==0,"Modern CS2 status slots and numeric nicknames never become Steam profiles");
        Check(RosterImport.ServerId("[Client] steamid : [A:1:1562782739:51631] (90293747566652435)")==90293747566652435UL,"Server Steam ID parsed from actual client status format");
        Check(RosterImport.ServerId("[Client] steamid : [U:1:123] (76561198000000003)")==0,"Individual Steam ID cannot be queried as a server");
        Check(RosterImport.ServerId("[Client] 28 00:09 37 0 active 786432 '90293747566652435'")==0,"Nickname cannot impersonate server Steam ID");
        t.ImportManual([new("76561198000000001","Other","T",false,"test")]);Check(t.State.Roster.Single(p=>p.Id=="76561198000000001").Name=="Me","Batch import cannot replace the confirmed local player");
        Check(Avatars.ParseXml("<profile><avatarMedium><![CDATA[https://avatars.steamstatic.com/example_medium.jpg]]></avatarMedium></profile>")=="https://avatars.steamstatic.com/example_medium.jpg","Steam XML avatar CDATA parsed");
        Check(Avatars.TrustedUrl("https://avatars.steamstatic.com/a.jpg")&&!Avatars.TrustedUrl("https://avatars.steamstatic.com.evil.test/a.jpg")&&!Avatars.TrustedUrl("file:///a.jpg"),"Avatars limited to HTTPS Steam CDN hosts");
        var blocked=false;try{Avatars.ParseXml("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///secret'>]><profile>&x;</profile>");}catch(System.Xml.XmlException){blocked=true;}Check(blocked,"Avatar XML external entities prohibited");
        var card=new SourceCard();Providers.ParseLeetify(card,Json("{}"));Check(card.Metrics.Count==0,"Missing stats stay absent, not zero");
        Providers.ParseLeetify(card,Json("{\"ranks\":{\"premier\":14000},\"rating\":{\"aim\":77},\"winrate\":0.56}"));Check(card.Metrics.Any(m=>m.Label=="Перемоги"&&m.Value=="56%"),"Leetify fractional winrate converted");
        var secret=Secrets.Protect("test-secret");Check(secret!="test-secret"&&Secrets.Unprotect(secret)=="test-secret","Windows secret encryption roundtrip");
        var p=new Player{Id=current.Id,Name="<script>&\"",Sources=Providers.Empty(current.Id)};
        foreach(var s in p.Sources){s.Status=new string('x',100);s.Metrics=Enumerable.Range(0,8).Select(i=>new Metric(new string('x',100),new string('y',100))).ToList();}
        var active=new MatchState{Active=true,Map="<map>",Mode="deathmatch",Generation=1};
        var formatted=Telegram.Format(active,Enumerable.Repeat(p,64).ToArray(),999);
        Check(formatted.Pages==1&&formatted.Text.Contains("64"),"Telegram fits all 64 players in a single message without pagination");
        Check(!formatted.Text.Contains("<script>")&&formatted.Text.Contains("&lt;script&gt;"),"Telegram escapes player-controlled HTML");
        Check(TelegramMessage.VisibleLength(formatted.Text)<4096,"Telegram text stays within length limit");
        Check(formatted.Text.Contains("cstracker")&&formatted.Text.Contains("Leetify"),"Telegram legend attributes tracker metrics");
        Check(Telegram.Format(new MatchState(),[p],0).Text=="Ви не в грі❤️⚡","Idle message matches requested text");
        var settings=new Settings{TelegramEnabled=true,ChatId="12345",BotToken="123:test"};
        var h=new FakeTelegram();using(var tg=new Telegram(h))
        {
            await tg.Update(settings,active,[p]);Check(h.Sent==1&&tg.MessageId==700&&h.Calls==1,"First Telegram update creates one message without callback polling");
            await tg.Update(settings,active,[p]);Check(h.Calls==1,"Unchanged Telegram content causes no network traffic");
            await tg.Update(settings,new MatchState(),[]);Check(h.Sent==1&&h.Edited==1,"Leaving match edits instead of sending");
        }
        using(var tg=new Telegram(h)) {await tg.Update(settings,active,[p]);Check(h.Sent==1&&h.Edited==2,"Restart reuses persisted message ID");}
        Store.Root=Path.Combine(baseRoot,"test-deleted-"+Guid.NewGuid().ToString("N"));
        var h2=new FakeTelegram();using(var tg=new Telegram(h2)){await tg.Update(settings,active,[p]);h2.Missing=true;await tg.Update(settings,new MatchState(),[]);Check(h2.Sent==2,"Deleted Telegram message recreated once");}
        Store.Root=Path.Combine(baseRoot,"test-ambiguous-"+Guid.NewGuid().ToString("N"));
        var h3=new FakeTelegram{Timeout=true};using(var tg=new Telegram(h3)){await tg.Update(settings,active,[p]);await tg.Update(settings,active,[p],true);Check(h3.Sent==1,"Ambiguous send timeout does not duplicate");}
        using(var tg=new Telegram(h3)){await tg.Update(settings,active,[p],true);Check(h3.Sent==1,"Pending-send protection survives restart");}
        Store.Root=Path.Combine(baseRoot,"test-demo-"+Guid.NewGuid().ToString("N"));
        var h4=new FakeTelegram();using(var tg=new Telegram(h4)){active.Demo=true;await tg.Update(settings,active,[p]);Check(h4.Calls==0,"Demo does not contact Telegram");}
        LocalizationTests.Run(Check);
        Store.Root=baseRoot;Directory.CreateDirectory(Store.Root);File.WriteAllText(Path.Combine(Store.Root,"test-results.txt"),string.Join(Environment.NewLine,passed)+$"\n\n{passed.Count} tests passed.\n");
    }
    sealed class FakeTelegram : HttpMessageHandler
    {
        public int Sent,Edited,Calls;public bool Missing,Timeout;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;var method=request.RequestUri!.Segments.Last();var body=await request.Content!.ReadAsStringAsync(ct);
            string response;
            if(method=="sendMessage"){Sent++;if(Timeout)throw new TaskCanceledException();response="{\"ok\":true,\"result\":{\"message_id\":700}}";}
            else if(method=="editMessageText"){Edited++;response=Missing?"{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message to edit not found\"}":"{\"ok\":true,\"result\":{}}";Missing=false;}
            else response="{\"ok\":true,\"result\":[]}";
            return new(HttpStatusCode.OK){Content=new StringContent(response,Encoding.UTF8,"application/json")};
        }
        protected override void Dispose(bool disposing){} // shared fake across restart simulation
    }
}
