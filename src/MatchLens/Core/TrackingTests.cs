using System.Net;
using System.Net.Http;
using System.Text;

namespace MatchLens;
public static class TrackingTests
{
    const string id="76561198000000001";
    public static string Fixture(string identity=id)=>$$"""
        <html><meta property="og:image" content="https://cstracker.gg/og/players/{{identity}}"><body>
        <span>latest 34 matches</span><h2>Stats analysis</h2><div>// trust rating</div><span>93.0</span><span>%</span>
        <span>updated</span><time datetime="2026-09-25T10:00:00Z">7d ago</time>
        <div><span>-2.5%</span><span>Wallbang kills</span></div><div><span>-1.9%</span><span>Team damage</span></div>
        <div><span>-1.8%</span><span>Teammates flashed</span></div><div><span>-0.8%</span><span>AFK time</span></div>
        <div data-histogram-config="{&quot;label&quot;:&quot;TTD&quot;,&quot;lowerIsSuspicious&quot;:true,&quot;suspiciousCutoff&quot;:600}"><div>// ttd</div><span>top 33%</span><strong>651ms</strong></div>
        <div data-histogram-config="{&quot;label&quot;:&quot;Accuracy&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:40}"><div>// accuracy</div><span>top 24%</span><strong>24.8%</strong></div>
        <div><strong>5.0%</strong><div>// wallbangs</div><span>10 / 200 kills</span></div>
        <span>168 matches</span><h2>Detailed stats</h2><div>// general</div><span>K/D/A</span><span>240 / 200 / 55</span><span>HS kills</span><span>150 (62.5%)</span>
        <span>Accuracy</span><span>25.5%</span><span>Spray accuracy</span><span>33.1%</span><span>Teamkills / match</span><span>0.14</span>
        <span>Team damage / match</span><span class="text-amber-300">32.2</span><span>Avg teammates flashed</span><span>0.78</span><span>AFK time / match</span><span>22s</span>
        <div style="display:none"><span>Accuracy</span><strong>99%</strong></div><script>trust rating 1%</script>
        <h2>Clutch performance</h2></body></html>
        """;
    public const string FaceitFixture="""
        <h1>TestNick</h1><h2>Recent Performance</h2><span>Last 30 matches</span><span>Win Rate</span><strong>55%</strong><span>Average K/D Ratio</span><strong>1.12</strong>
        <h2>Recent matches</h2><table><tr><th>Date</th><th>Score</th><th>Rating</th><th>K/D/A</th><th>Map</th></tr>
        <tr><td>2026-09-27</td><td>W 13:8</td><td>1750 25</td><td>22/11/4</td><td>Mirage</td></tr>
        <tr><td>2026-09-26</td><td>L 9:13</td><td>1725</td><td>13/19/2</td><td>Nuke</td></tr>
        <tr><td>unknown</td><td>W 13:0</td><td>9999</td><td>100/0/0</td><td>Cache</td></tr></table>
        """;
    public static void Run(Action<bool,string> check)
    {
        var c=TrackingProfiles.CsTracker(id,Fixture());
        check(c.Numbers.Single(n=>n.Key=="trust").Value==93&&c.TrustAdjustments.Count==4&&Math.Abs(c.TrustAdjustments.Sum(a=>a.Delta)+7)<.001,"cstracker published score and signed factors are copied without an invented formula");
        check(c.Numbers.Single(n=>n.Key=="ttd").Value==651&&c.Numbers.Single(n=>n.Key=="accuracy").Value==24.8,"Visible telemetry ignores percentile badges and keeps milliseconds intact");
        check(c.SampleCount==34&&c.Numbers.Single(n=>n.Key=="detail_accuracy").Scope.Contains("168")&&c.Numbers.Single(n=>n.Key=="accuracy").Scope.Contains("34"),"Telemetry and all-match details retain different sample scopes");
        check(c.Numbers.Single(n=>n.Key=="afk").Value==22&&c.Numbers.Single(n=>n.Key=="teamkills").Value==.14,"Behavior statistics parse seconds and fractional teamkills");
        check(c.TrustUpdated==DateTimeOffset.Parse("2026-09-25T10:00:00Z")&&c.ReviewRules.Single(r=>r.Key=="accuracy").Cutoff==40,"Trust age and displayed review thresholds are preserved");
        check(TrackingProfiles.CsTracker(id,Fixture("76561198000000002")).Numbers.Count==0,"A different player's cstracker profile cannot supply stats");
        check(TrackingProfiles.CsTracker(id,"<h1>Sign in</h1><script>trust rating 100</script>").Numbers.Count==0,"Missing or gated cstracker data never becomes a zero or fictional trust score");
        var p=new Player{Id=id,Sources=[c]};var normal=c.Numbers.Single(n=>n.Key=="accuracy");
        check(MetricAppearance.Tile(normal.Key,normal.Label,normal.Display,"",normal,p,source:"CSTRACKER").Tone==MetricTone.Good,"Ordinary cstracker values are green");
        var extreme=normal with{Value=65};check(MetricAppearance.Tile(extreme.Key,extreme.Label,extreme.Display,"",extreme,p,source:"CSTRACKER").Tone==MetricTone.Low,"Extreme accuracy crosses the source's explicit review threshold");
        var friendly=c.Numbers.Single(n=>n.Key=="team_damage");check(MetricAppearance.Tile(friendly.Key,friendly.Label,friendly.Display,"",friendly,p,source:"CSTRACKER").Tone==MetricTone.Low,"A published team-damage penalty colors its behavior metric red");
        var tt=c.Numbers.Single(n=>n.Key=="ttd") with{Value=450};check(MetricAppearance.Tile(tt.Key,tt.Label,tt.Display,"",tt,p,source:"CSTRACKER").Tone==MetricTone.Low,"Unusually low reaction times use the source's inverse threshold");
        check(p.TrustLabel.Contains("93%")&&new Player().TrustLabel.Contains("немає даних"),"Player cards show cstracker trust and retain missing values");
        var f=TrackingProfiles.Faceit(id,"https://www.faceit.com/en/players/TestNick",FaceitFixture);
        check(f.RecentMatches.Count==2&&f.RecentMatches[0].Kills==22&&f.RecentMatches[1].Result=="Поразка","FACEIT recent rows retain dates, maps, results and actual K/D/A");
        check(f.Numbers.Single(n=>n.Key=="recent_kd").Value==35d/30&&f.Numbers.Single(n=>n.Key=="recent_winrate").Value==50,"FACEIT recent aggregates use actual observed matches separately from last-30 summaries");
        check(f.Numbers.All(n=>n.Key!="faceit_elo")&&f.RecentMatches.All(m=>m.Rating==null),"Ambiguous FACEIT rating and Elo-change cells are not guessed");
        check(f.SampleCount==null&&PublicProfileParser.Leetify(id,PublicStatsTests.LeetifyFixture).SampleCount==null,"Last-30 window labels do not fabricate a known sample size");
        var partial=TrackingProfiles.Faceit(id,"https://www.faceit.com/en/players/TestNick",FaceitFixture.Replace("13/19/2","—"));
        check(partial.RecentMatches.Count==2&&partial.Numbers.Single(n=>n.Key=="recent_kd").Scope.Contains("1 із K/D"),"Recent combat statistics disclose how many rows actually contain K/D");
        check(TrackingProfiles.Faceit(id,"https://www.faceit.com/en/players/Other",FaceitFixture).RecentMatches.Count==0,"FACEIT page must match the linked nickname");
        check(TrackingProfiles.FaceitLink("<a href='https://www.faceit.com/en/players/TestNick'>FACEIT</a>")=="https://www.faceit.com/en/players/TestNick"&&TrackingProfiles.FaceitLink("<a href='https://www.faceit.com.evil.test/en/players/TestNick'>FACEIT</a>")==null,"Only one exact public FACEIT player link is accepted");
        var trackerHistory="<table><tr><th>Match</th><th>Rank</th><th>K / D / A</th><th>K/D</th><th>ADR</th><th>Rating</th><th>KAST</th><th>ACC</th><th>Preaim</th><th>TTD</th><th>When</th></tr><tr><td><a>Mirage<span>13–8</span></a><div>FACEIT</div></td><td>10</td><td>22 / 11 / 4</td><td>2</td><td>90</td><td>1.35</td><td>75%</td><td>25%</td><td>3°</td><td>600ms</td><td><span data-time-ago='1787861781'>played 2026-08-27</span></td></tr></table>";
        var rows=TrackingProfiles.MatchTables(trackerHistory,"CSTRACKER");
        check(rows.Count==1&&rows[0].Kills==22&&rows[0].Rating==1.35&&rows[0].Source=="CSTRACKER · FACEIT","Native cstracker history rows supply FACEIT matches with honest attribution");
        check(TrackingProfiles.MatchTables(trackerHistory.Replace("FACEIT","Premier"),"CSTRACKER").Count==0,"Premier match history cannot be mislabeled as FACEIT");
        var bonus=TrackingProfiles.CsTracker(id,Fixture().Replace("-2.5%","+9.6%").Replace("Wallbang kills","FACEIT ELO"));
        check(bonus.Numbers.Single(n=>n.Key=="trust").Value==93&&bonus.TrustAdjustments.Any(r=>r.Delta>0),"Positive FACEIT bonuses preserve the displayed score instead of recalculating it");
    }
}
