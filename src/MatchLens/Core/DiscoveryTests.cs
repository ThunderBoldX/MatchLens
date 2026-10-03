using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace MatchLens;
public static class DiscoveryTests
{
    static CoplayEntry Entry(int n,DateTimeOffset stamp)=>new((SteamIds.Base+(ulong)n).ToString(),"P"+n,stamp.ToUnixTimeSeconds());
    static RosterEntry Member(int n)=>new((SteamIds.Base+(ulong)n).ToString(),"P"+n,"?",false,"Steam");
    public static void Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;
        var state=new MatchState{Active=true,Generation=1,StartedAt=now,LocalId=(SteamIds.Base+20000).ToString()};
        var peers=Enumerable.Range(20001,9).Select(Member).ToList();
        var old=Enumerable.Range(20100,9).Select(n=>Entry(n,now.AddMinutes(-30))).ToList();
        var selector=new RecentDiscovery();selector.Observe(new(),old,now.AddSeconds(-5));
        var watch=Stopwatch.StartNew();var result=selector.Observe(state,old,now.AddMilliseconds(250),peers);watch.Stop();
        check(result.Count==9&&result.All(p=>!p.Confirmed&&p.Team=="?"),"Current-server IDs appear on the first snapshot without waiting for coplay or statistics");
        check(watch.Elapsed<TimeSpan.FromMilliseconds(500),"Roster selection itself finishes well within one poll interval");
        check(selector.Observe(state,old,now.AddSeconds(1),peers.Take(8)).Count==8,"Current-server snapshot removes a departed player immediately");
        state.Generation=2;state.StartedAt=now.AddSeconds(2);
        check(selector.Observe(state,old,now.AddSeconds(2),peers.Take(8)).Count==0,"An unchanged server set from the previous match is not automatically reused");
        var next=Enumerable.Range(20200,9).Select(Member).ToList();
        check(selector.Observe(state,old,now.AddSeconds(2.25),next).Count==9,"New server membership replaces the old roster on the next poll");

        selector=new();selector.Observe(new(),old,now.AddSeconds(-20));
        var loading=Enumerable.Range(20300,9).Select(n=>Entry(n,now.AddSeconds(-4))).ToList();
        // The game may tell Steam about new peers before the map reaches GSI.
        selector.Observe(new(),old.Concat(loading),now.AddSeconds(-3),peers);
        var first=new MatchState{Active=true,Generation=1,StartedAt=now};
        check(selector.Observe(first,old.Concat(loading),now,peers).Count==9,"Peers seen while loading survive the first GSI packet instead of becoming an old baseline");
        selector=new();selector.Observe(new(),old,now.AddSeconds(-20));
        selector.Observe(new(),old.Concat(loading),now.AddSeconds(-3));
        var found=selector.Observe(first,old.Concat(loading),now);
        check(found.Count==9&&found.All(p=>loading.Any(x=>x.Id==p.Id)),"New coplay IDs timestamped before GSI are accepted without another timestamp update");
        first.Generation++;first.StartedAt=now.AddSeconds(10);
        check(selector.Observe(first,old.Concat(loading),now.AddSeconds(10)).Count==0,"Pre-entry arrival evidence cannot be consumed again in the next match");
        var reconnect=loading.Select(p=>p with{Timestamp=now.AddSeconds(8).ToUnixTimeSeconds()}).ToList();
        check(selector.Observe(first,old.Concat(reconnect),now.AddSeconds(10.25)).Count==0,"A late completion timestamp cannot readmit participants from the previous match");
        first.Roster=loading.Select(p=>new RosterEntry(p.Id,p.Name,"CT",true,"GSI")).ToList();
        check(selector.Observe(first,old.Concat(reconnect),now.AddSeconds(10.5)).Count==9,"Explicit current GSI identities can readmit genuinely repeated participants");
        check(selector.Observe(new(),old.Concat(reconnect),now.AddSeconds(11),peers).Count==0,"Current-server data never displays players outside a GSI match");

        var large=new RecentDiscovery().Observe(new(){Active=true,Generation=1,StartedAt=now},[],now,
            Enumerable.Range(20400,24).Select(Member).Concat([Member(20400),new("BOT","Bot","?",false,"Steam")]));
        check(large.Count==24,"Fast discovery supports large modes, deduplicates IDs and rejects bots");
        var schedule=new DiscoverySchedule();schedule.Session(1,now);schedule.Failure(now);
        check(!schedule.Ready(now.AddMilliseconds(500))&&schedule.Ready(now.AddSeconds(1)),"First helper failure recovers after one second, not thirty");
        for(var i=0;i<8;i++)schedule.Failure(now);
        check(schedule.Ready(now.AddSeconds(5)),"Helper recovery backoff never exceeds five seconds");
        schedule.Session(2,now);check(schedule.Ready(now),"Entering a new match bypasses the previous recovery delay");
        check(schedule.Interval(true,now.AddSeconds(2))==TimeSpan.FromMilliseconds(250)&&schedule.Interval(true,now.AddSeconds(21))==TimeSpan.FromMilliseconds(750),"Discovery polls at 250 ms after entry and keeps updating during the match");
        check(schedule.Interval(false,now)==TimeSpan.FromSeconds(2),"Idle prewarming avoids excessive polling");
        check(Marshal.SizeOf<PassiveSteamClient.FriendGameInfo>()==24,"Steam FriendGameInfo ABI uses the documented 24-byte layout");
        var wire=JsonSerializer.Serialize(new SteamSnapshot(true,"test",old,peers,now));
        check(JsonSerializer.Deserialize<SteamSnapshot>(wire)?.ServerPlayers?.Count==9,"Worker IPC transfers server membership independently of history");
        check(JsonSerializer.Deserialize<SteamSnapshot>("{\"Available\":true,\"Status\":\"legacy\",\"Players\":[]}")?.ServerPlayers==null,"Legacy worker snapshots remain readable");

        // Exercise the actual WPF roster binding with no shown window/network.
        var main=new MainWindow(true);
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var tracker=(MatchTracker)typeof(MainWindow).GetField("tracker",flags)!.GetValue(main)!;
        tracker.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=state.LocalId},map=new{name="de_mirage",phase="live",mode="competitive"},
            player=new{steamid=state.LocalId,name="Me",team="CT"}}),now);
        tracker.ReplaceAutomatic(peers.Select(p=>p with{Evidence="Авто • Steam: учасник сервера"}));
        typeof(MainWindow).GetMethod("Sync",flags)!.Invoke(main,null);
        var content=(FrameworkElement)main.Content;content.Measure(new Size(1280,850));content.Arrange(new Rect(0,0,1280,850));content.UpdateLayout();
        var players=main.PlayerSelector.Items.Cast<Player>().ToList();
        check(players.Count==10&&main.RosterList.Items.Count==10&&players.All(p=>p.Sources.All(s=>s.Metrics.Count==0)),"Actual dropdown and player cards show all ten identities before any statistic is loaded");
        main.OpenPreviewProfile();
        tracker.End(now);typeof(MainWindow).GetMethod("Sync",flags)!.Invoke(main,null);
        check(main.PlayerSelector.Items.Count==0&&!main.PlayerSelector.IsEnabled&&main.RosterList.Items.Count==0,"Leaving a match clears both the actual player selector and cards immediately");
        check(typeof(MainWindow).GetField("selected",flags)!.GetValue(main)==null,"Leaving a match clears the selected old player profile");
        // No Steam poll occurs between ending and entering the next match.
        tracker.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=state.LocalId},map=new{name="de_mirage",phase="live",mode="competitive"},
            player=new{steamid=state.LocalId,name="Me",team="CT"}}),now.AddSeconds(2));
        typeof(MainWindow).GetMethod("Sync",flags)!.Invoke(main,null);
        var discovery=(RecentDiscovery)typeof(MainWindow).GetField("recentDiscovery",flags)!.GetValue(main)!;
        var delayed=peers.Select(p=>new CoplayEntry(p.Id,p.Name,now.AddSeconds(3).ToUnixTimeSeconds())).ToList();
        tracker.ReplaceAutomatic(discovery.Observe(tracker.State,delayed,now.AddSeconds(3),peers));
        typeof(MainWindow).GetMethod("Sync",flags)!.Invoke(main,null);
        check(main.PlayerSelector.Items.Cast<Player>().All(p=>p.IsLocal),"Actual UI rejects delayed previous-match participants even when no idle Steam poll occurred");
        var freshPeers=Enumerable.Range(30301,9).Select(Member).ToList();
        var freshHistory=freshPeers.Select(p=>new CoplayEntry(p.Id,p.Name,now.AddSeconds(4).ToUnixTimeSeconds()));
        tracker.ReplaceAutomatic(discovery.Observe(tracker.State,delayed.Concat(freshHistory),now.AddSeconds(4),peers.Concat(freshPeers)));
        typeof(MainWindow).GetMethod("Sync",flags)!.Invoke(main,null);
        var current=main.PlayerSelector.Items.Cast<Player>().ToList();
        check(current.Count==10&&main.RosterList.Items.Count==10&&current.All(p=>p.IsLocal||freshPeers.Any(f=>f.Id==p.Id)),"Actual second-match dropdown and cards contain the new ten profiles with none from the first match");
        content.Measure(new Size(1440,1030));content.Arrange(new Rect(0,0,1440,1030));content.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1440,1030,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);
        var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var output=System.IO.File.Create(System.IO.Path.Combine(Store.Root,"transition-overview.png")))png.Save(output);
        main.Close();
        MatchTransitions(check,now);
    }
    static void MatchTransitions(Action<bool,string> check,DateTimeOffset now)
    {
        var local=(SteamIds.Base+30000).ToString();
        MatchState State(int gen,int seconds,bool active=true)=>new(){Active=active,Generation=gen,StartedAt=now.AddSeconds(seconds),LocalId=local};
        var first=Enumerable.Range(30001,9).Select(Member).ToList();
        var next=Enumerable.Range(30101,9).Select(Member).ToList();
        var selector=new RecentDiscovery();
        check(selector.Observe(State(1,0),[],now,first).Count==9,"Transition fixture accepts the first match's server group");
        selector.Session(State(2,1,false));
        var late=first.Select(p=>new CoplayEntry(p.Id,p.Name,now.AddSeconds(3).ToUnixTimeSeconds())).ToList();
        check(selector.Observe(State(3,2),late,now.AddSeconds(3),first).Count==0,"Gameover followed by delayed completion records cannot restore the previous match");
        selector.Observe(State(3,2),late,now.AddSeconds(3.5),[]);
        check(selector.Observe(State(3,2),late,now.AddSeconds(4),first).Count==0,"An old server group cannot return after a temporary empty Steam response");
        var fresh=next.Select(p=>new CoplayEntry(p.Id,p.Name,now.AddSeconds(4).ToUnixTimeSeconds())).ToList();
        var result=selector.Observe(State(3,2),late.Concat(fresh),now.AddSeconds(4),first.Concat(next));
        check(result.Count==9&&result.All(p=>next.Any(n=>n.Id==p.Id)),"A mixed Steam response shows only new participants, never the old group");
        selector.Session(State(4,5,false));
        var third=Member(30200);
        var older=late.Concat(fresh).Select(p=>p with{Timestamp=now.AddSeconds(7).ToUnixTimeSeconds()});
        result=selector.Observe(State(5,6),older,now.AddSeconds(7),first.Concat(next).Append(third));
        check(result.Count==1&&result[0].Id==third.Id,"Old participants stay excluded across a third match and another timestamp refresh");
        selector.Reset();
        check(selector.Observe(State(5,6),older,now.AddSeconds(8),first.Concat(next).Append(third)).All(p=>p.Id==third.Id),"Helper reconnect preserves exclusions for completed matches");

        selector=new();selector.Observe(State(1,0),[],now,first);
        check(selector.Observe(State(2,1),late,now.AddSeconds(3),first).Count==0,"A direct generation change retires the old group without an idle snapshot");
        selector=new();selector.Observe(State(0,-10,false),late,now.AddSeconds(3),first);
        check(selector.Observe(State(1,4),late,now.AddSeconds(4),first).Count==0,"The initial idle server group is baseline data, not a new arrival during loading");

        var tracker=new MatchTracker();
        tracker.Apply(JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=local},map=new{name="de_mirage",phase="live"},player=new{steamid=local,name="Me",team="CT"}}),now);
        tracker.ReplaceAutomatic(first.Select(p=>p with{Evidence="Авто • Steam"}));
        tracker.ReplaceAutomatic(next.Select(p=>p with{Evidence="Авто · Steam"}));
        check(tracker.State.Roster.Count==10&&tracker.State.Roster.All(p=>p.Id==local||next.Any(n=>n.Id==p.Id)),"Replacing automatic profiles removes old entries with either bullet format");
        tracker.ReplaceAutomatic([]);
        check(tracker.State.Roster.Count==1&&tracker.State.Roster.Single().Confirmed,"Empty automatic snapshot clears Steam entries and preserves explicit GSI identity");
        JsonElement Round(int index,int ct,int t,bool scores=true)=>scores?
            JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=local},map=new{name="de_mirage",phase="live",mode="competitive",round=index,team_ct=new{score=ct},team_t=new{score=t}},player=new{steamid=local,name="Me",team="CT"}}):
            JsonSerializer.SerializeToElement(new{provider=new{appid=730,steamid=local},map=new{name="de_mirage",phase="live",mode="competitive",round=index},player=new{steamid=local,name="Me",team="CT"}});
        tracker=new();tracker.Apply(Round(20,11,9),now);tracker.ReplaceAutomatic(first.Select(p=>p with{Evidence="Авто · Steam"}));
        var generation=tracker.State.Generation;selector=new();selector.Session(tracker.State);
        tracker.Apply(Round(0,0,0),now.AddSeconds(1));selector.Session(tracker.State);
        check(tracker.State.Generation==generation+1&&tracker.State.Roster.Count==1,"Same-map rematch clears the old roster even when gameover and warmup were missed");
        check(selector.Observe(tracker.State,late,now.AddSeconds(3),first).Count==0,"Same-map restart quarantine blocks delayed completion records from the old match");
        tracker=new();tracker.Apply(Round(12,8,4),now);generation=tracker.State.Generation;
        tracker.Apply(Round(12,4,8),now.AddSeconds(1));
        check(tracker.State.Generation==generation,"Halftime side swap does not look like a new match");
        tracker.Apply(Round(24,12,12),now.AddSeconds(2));tracker.Apply(Round(25,13,12),now.AddSeconds(3));
        check(tracker.State.Generation==generation,"Overtime round progression preserves the current roster generation");
        tracker.Apply(Round(0,0,0,false),now.AddSeconds(4));
        check(tracker.State.Generation==generation,"Missing scoreboard fields cannot manufacture a same-map restart");
        tracker.Apply(Round(0,0,0),now.AddSeconds(5));
        check(tracker.State.Generation==generation+1,"Partial GSI packet preserves the baseline for the next explicit restart snapshot");
    }
}
