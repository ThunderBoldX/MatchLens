namespace MatchLens;

// Failure recovery must not hide a newly joined match for thirty seconds.
public sealed class DiscoverySchedule
{
    int generation=-1,failures;
    DateTimeOffset entered,retryAt;
    public void Session(int value,DateTimeOffset now)
    {if(generation==value)return;generation=value;entered=now;retryAt=default;failures=0;}
    public bool Ready(DateTimeOffset now)=>now>=retryAt;
    public void Success(){failures=0;retryAt=default;}
    public void Failure(DateTimeOffset now)=>retryAt=now.AddSeconds(Math.Min(5,1<<Math.Min(failures++,3)));
    public TimeSpan Interval(bool active,DateTimeOffset now)=>!active?TimeSpan.FromSeconds(2):now-entered<TimeSpan.FromSeconds(20)?TimeSpan.FromMilliseconds(250):TimeSpan.FromMilliseconds(750);
}
