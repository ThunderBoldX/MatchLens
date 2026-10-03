using System.Text.Json;

namespace MatchLens;
public class MatchTracker
{
    public MatchState State {get;private set;}=new();
    // Steam friend history/flags cannot prove current server membership.
    public void Baseline(IEnumerable<CoplayEntry> entries) { }
    public void Coplay(IEnumerable<CoplayEntry> entries,DateTimeOffset now) { }
    public void ServerPlayers(IEnumerable<RosterEntry> entries) { }
    public void Apply(JsonElement data,DateTimeOffset now)
    {
        if(data.At("provider","appid").Int()!=730)return;
        var map=data.At("map");var name=map.At("name").Str();var phase=map.At("phase").Str();
        if(name==""||phase=="gameover"){End(now);return;}
        var mode=map.At("mode").Str();
        var roundValue=map.At("round").Num();
        int? round=roundValue is {} r&&r>=0&&r<=100000&&r==(int)r?(int)r:null;
        var ct=map.At("team_ct","score").Num();var ts=map.At("team_t","score").Num();
        // A same-map rematch can miss both gameover and warmup GSI packets.
        // Require two explicit regressions; a side swap preserves total score.
        var restarted=State.Active&&phase=="live"&&State.Phase is "live" or "intermission"&&
            round is {} currentRound&&State.RoundNumber is {} priorRound&&currentRound<priorRound&&
            ct is >=0&&ts is >=0&&ct+ts<State.CtScore+State.TScore;
        var freshWarmup=phase=="warmup"&&State.Phase is "live" or "intermission";
        var changedMode=State.Active&&State.Mode.Length>0&&mode.Length>0&&State.Mode!=mode;
        if(!State.Active||State.Map!=name||freshWarmup||changedMode||restarted)
            State=new MatchState{Active=true,Map=name,StartedAt=now,Generation=State.Generation+1};
        State.LastGsi=now;State.Active=true;State.Mode=map.At("mode").Str();State.Phase=phase;
        var localId=data.At("provider","steamid").Str();if(SteamIds.Valid(localId))State.LocalId=localId;
        State.CtScore=map.At("team_ct","score").Int(State.CtScore);State.TScore=map.At("team_t","score").Int(State.TScore);
        if(round.HasValue&&ct is >=0&&ts is >=0)State.RoundNumber=round;
        var all=data.At("allplayers");
        var player=data.At("player");var id=player.At("steamid").Str();
        if(all.ValueKind==JsonValueKind.Object)
        {
            var entries=all.EnumerateObject().Select(p=>new {Id=SteamIds.Valid(p.Name)?p.Name:p.Value.At("steamid").Str(),Value=p.Value}).Where(p=>SteamIds.Valid(p.Id)).ToList();
            // Only a fully identified snapshot can replace the roster. Unknown
            // entity slots must not erase known profiles or be matched by name.
            if(entries.Count>0&&entries.Count==all.EnumerateObject().Count())
                State.Roster=entries.Select(p=>new RosterEntry(p.Id,p.Value.At("name").Str(p.Id),p.Value.At("team").Str("?"),true,"GSI · підтверджено")).ToList();
            else foreach(var p in entries)Upsert(new(p.Id,p.Value.At("name").Str(p.Id),p.Value.At("team").Str("?"),true,"GSI · підтверджено"));
            foreach(var p in entries)if(LiveMatchStats.Read(p.Value) is {} stats)State.LivePlayers[p.Id]=stats;
            var current=State.Roster.Select(p=>p.Id).ToHashSet();foreach(var old in State.LivePlayers.Keys.Where(k=>!current.Contains(k)).ToArray())State.LivePlayers.Remove(old);
        }
        // When CS2 supplies the spectated player's identity AND side, use that
        // explicit observation. Never derive a side merely from spectating.
        var team=player.At("team").Str("?");
        if(SteamIds.Valid(id)&&(id==State.LocalId||team is "CT" or "T"))
            Upsert(new(id,player.At("name").Str(id),team,true,"GSI · команда спостереженого гравця"));
        if(SteamIds.Valid(id)&&State.Roster.Any(p=>p.Id==id)&&LiveMatchStats.Read(player) is {} live)State.LivePlayers[id]=live;
    }
    public void AddManual(IEnumerable<string> ids,string team="?")
    {
        foreach(var id in ids.Where(SteamIds.Valid))
            if(!State.Roster.Any(p=>p.Id==id&&p.Confirmed))
                Upsert(new(id,"Гравець "+id[^5..],team is "CT" or "T"?team:"?",false,"Додано вручну"));
    }
    public void SetManualTeam(string id,string team)
    {
        var p=State.Roster.FirstOrDefault(x=>x.Id==id);
        if(p!=null&&!p.Confirmed)Upsert(p with {Team=team is "CT" or "T"?team:"?",Evidence="Команду задано вручну"});
    }
    public void ImportManual(IEnumerable<RosterEntry> players)
    {
        foreach(var p in players.Where(p=>SteamIds.Valid(p.Id)).Take(64))
        {
            var old=State.Roster.FirstOrDefault(x=>x.Id==p.Id);
            if(old?.Confirmed==true)continue;
            Upsert(p with {Confirmed=false,Team=old?.Team??"?",Evidence="Імпорт · вручну"});
        }
    }
    public void ReplaceAutomatic(IEnumerable<RosterEntry> entries)
    {
        State.Roster.RemoveAll(p=>!p.Confirmed&&p.Evidence.StartsWith("Авто ",StringComparison.Ordinal));
        if(!State.Active)return;
        foreach(var p in entries.Where(p=>SteamIds.Valid(p.Id)).Take(64))
            if(!State.Roster.Any(x=>x.Id==p.Id))Upsert(p with{Confirmed=false,Team="?"});
    }
    void Upsert(RosterEntry p){State.Roster.RemoveAll(x=>x.Id==p.Id);State.Roster.Add(p);}
    public void Tick(DateTimeOffset now){if(State.Active&&now-State.LastGsi>TimeSpan.FromSeconds(25))End(now);}
    public void End(DateTimeOffset now){if(State.Active||State.Roster.Count>0)State=new MatchState{Generation=State.Generation+1,LastGsi=now};}
}
