namespace MatchLens;

// Prefer Steam's current-server set. Coplay is only historical evidence:
// compare against the snapshot BEFORE entry, never against the new roster itself.
public sealed class RecentDiscovery
{
    int generation=-1;
    bool hadSnapshot,boundaryKnown,freshSeen,serverAccepted;
    Dictionary<string,long> previous=[],baseline=[];
    HashSet<string> previousServer=[];
    readonly Dictionary<string,CoplayEntry> accepted=[];
    readonly Dictionary<string,(long Timestamp,DateTimeOffset Seen)> arrivals=[];
    readonly Dictionary<string,long> usedHistory=[];
    long serverRevision,usedServerRevision;
    DateTimeOffset serverChangedAt;
    long boundary;
    int sessionGeneration=-1;
    bool sessionActive;
    readonly HashSet<string> matchMembers=[],retired=[];
    public int RawCount {get;private set;}
    public int ServerCount {get;private set;}
    public int SelectedCount {get;private set;}
    public int WithheldCount {get;private set;}
    public long? NewestTimestamp {get;private set;}
    public string Status {get;private set;}="Очікуємо список Steam";
    // GSI may finish one match and begin another before the next Steam poll.
    // A timestamp written at match end is not proof of joining another match.
    public void Session(MatchState state)
    {
        var active=state.Active&&!state.Demo;
        if(sessionActive&&(!active||sessionGeneration!=state.Generation))
        {
            retired.UnionWith(matchMembers);matchMembers.Clear();SelectedCount=0;WithheldCount=0;
            Status=active?"Очікуємо новий склад Steam":"Очікуємо матч";
        }
        sessionActive=active;sessionGeneration=state.Generation;
        if(!active)return;
        foreach(var p in state.Roster.Where(p=>SteamIds.Valid(p.Id)&&p.Id!=state.LocalId))
        {
            // Explicit current GSI identity can lift the quarantine.
            if(p.Confirmed)retired.Remove(p.Id);
            matchMembers.Add(p.Id);
        }
    }
    List<RosterEntry> Remember(List<RosterEntry> entries)
    {matchMembers.UnionWith(entries.Select(p=>p.Id));return entries;}
    public List<RosterEntry> Observe(MatchState state,IEnumerable<CoplayEntry> snapshot,DateTimeOffset now,IEnumerable<RosterEntry>? currentServer=null)
    {
        Session(state);
        var entries=snapshot.Where(p=>SteamIds.Valid(p.Id)&&p.Id!=state.LocalId).GroupBy(p=>p.Id).Select(g=>g.MaxBy(p=>p.Timestamp)!).ToList();
        var sourceServer=(currentServer??[]).Where(p=>SteamIds.Valid(p.Id)&&p.Id!=state.LocalId).DistinctBy(p=>p.Id).Take(64).ToList();
        var server=sourceServer.Where(p=>!retired.Contains(p.Id)).ToList();
        RawCount=entries.Count;ServerCount=sourceServer.Count;SelectedCount=0;NewestTimestamp=null;
        WithheldCount=state.Active?sourceServer.Where(p=>retired.Contains(p.Id)).Select(p=>p.Id)
            .Concat(entries.Where(p=>retired.Contains(p.Id)&&p.Timestamp>=state.StartedAt.ToUnixTimeSeconds()-120).Select(p=>p.Id)).Distinct().Count():0;
        if(hadSnapshot)foreach(var p in entries)
            if(!previous.TryGetValue(p.Id,out var last)||p.Timestamp>last)arrivals[p.Id]=(p.Timestamp,now);
        // New peers often arrive during loading, BEFORE map data reaches GSI.
        // Remember that arrival across idle snapshots, without reusing a set
        // already accepted for the previous match.
        if(hadSnapshot&&server.Any(p=>!previousServer.Contains(p.Id))){serverRevision++;serverChangedAt=now;}
        if(state.Active&&!state.Demo&&generation!=state.Generation)
        {
            boundaryKnown=hadSnapshot||generation!=-1;generation=state.Generation;boundary=state.StartedAt.ToUnixTimeSeconds();
            baseline=new(previous);accepted.Clear();freshSeen=false;serverAccepted=false;
        }
        previous=entries.ToDictionary(p=>p.Id,p=>p.Timestamp);previousServer=sourceServer.Select(p=>p.Id).ToHashSet();hadSnapshot=true;
        if(!state.Active||state.Demo){Status=$"Steam: {RawCount} записів · очікуємо матч";return [];}
        var valid=entries.Where(p=>!retired.Contains(p.Id)&&p.Timestamp>0&&p.Timestamp<=now.ToUnixTimeSeconds()+5&&now.ToUnixTimeSeconds()-p.Timestamp<=7200)
            .OrderByDescending(p=>p.Timestamp).ThenBy(p=>p.Id).ToList();
        if(valid.Count>0)NewestTimestamp=valid[0].Timestamp;
        bool Changed(CoplayEntry p)=>(!baseline.TryGetValue(p.Id,out var old)||p.Timestamp>old||
            arrivals.TryGetValue(p.Id,out var arrival)&&arrival.Timestamp==p.Timestamp&&arrival.Seen>=state.StartedAt.AddSeconds(-120))&&
            (!usedHistory.TryGetValue(p.Id,out var used)||p.Timestamp>used);
        // Steam records can predate the first GSI packet while the map is loading.
        // An actual identity/timestamp change is required; unchanged old players fail.
        var changed=valid.Where(p=>Changed(p)&&p.Timestamp>=boundary-120).ToList();
        if(server.Count>0&&(!boundaryKnown||serverAccepted||serverRevision>usedServerRevision&&serverChangedAt>=state.StartedAt.AddSeconds(-120)||changed.Any(p=>server.Any(s=>s.Id==p.Id))))
        {
            serverAccepted=true;usedServerRevision=serverRevision;SelectedCount=server.Count;Status=$"Steam: поточний сервер · {server.Count} гравців";
            foreach(var p in valid.Where(p=>server.Any(s=>s.Id==p.Id)))usedHistory[p.Id]=p.Timestamp;
            return Remember(server.Select(p=>p with{Team="?",Confirmed=false,Evidence="Авто · Steam: учасник сервера"}).ToList());
        }
        if(boundaryKnown)
        {
            foreach(var p in changed)accepted[p.Id]=p;
            var selected=valid.Where(p=>accepted.ContainsKey(p.Id)).Take(64).ToList();
            foreach(var p in selected)usedHistory[p.Id]=p.Timestamp;
            SelectedCount=selected.Count;Status=$"Steam: {RawCount} записів → {SelectedCount} нових учасників"+(selected.Count==0?" · очікуємо дані поточної сесії":"");
            return Remember(selected.Select(p=>new RosterEntry(p.Id,p.Name,"?",false,"Авто · новий запис Steam, не підтверджено GSI")).ToList());
        }
        // Startup in a running match has no baseline. Keep this fallback labelled.
        if(valid.Count==0){Status=$"Steam: {RawCount} записів · свіжих даних немає";return [];}
        var newest=valid[0].Timestamp;freshSeen|=newest>=boundary;
        var cutoff=freshSeen?boundary-120:newest-300;
        var cohort=valid.Where(p=>newest-p.Timestamp<=300&&p.Timestamp>=cutoff).Take(64).ToList();
        SelectedCount=cohort.Count;Status=$"Steam: історія · {SelectedCount} непідтверджених профілів";
        foreach(var p in cohort)usedHistory[p.Id]=p.Timestamp;
        return Remember(cohort.Select(p=>new RosterEntry(p.Id,p.Name,"?",false,"Авто · остання група Steam, не підтверджено GSI")).ToList());
    }
    // Retain the baseline across a short helper disconnect.
    public void Reset(){RawCount=0;ServerCount=0;SelectedCount=0;WithheldCount=0;NewestTimestamp=null;Status="Очікуємо список Steam";}
}
