using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MatchLens;
public static class TelegramMessage
{
    static string H(string s)=>WebUtility.HtmlEncode(s);
    static string Short(string s,int length)
    {
        s=Regex.Replace(s,@"[\p{Cc}\p{Zl}\p{Zp}]"," ").Trim();
        var indices=StringInfo.ParseCombiningCharacters(s);
        return indices.Length<=length?s:s[..indices[length]]+"…";
    }
    public static int VisibleLength(string html)=>WebUtility.HtmlDecode(Regex.Replace(html,@"</?(?:b|i|pre|a)(?:\s[^>]*)?>","")).Length;
    static NumericMetric? Metric(Player p,string key)=>p.Sources
        .Where(s=>key!="trust"||s.Name=="CSTRACKER")
        .OrderBy(s=>s.Name=="LEETIFY"?0:s.Name=="CSSTATS"?1:2)
        .SelectMany(s=>s.Numbers).FirstOrDefault(n=>n.Key==key&&double.IsFinite(n.Value));
    static string Value(Player p,string key)
    {
        var m=Metric(p,key);if(m==null)return "—";
        var v=m.Value.ToString(key=="leetify_rating"?"+0.00;-0.00;0.00":key is "faceit_elo" or "adr"?"0":"0.##",CultureInfo.InvariantCulture);
        if(key is "headshots" or "trust")v+="%";
        return v.Length<=8?v:"—";
    }
    static string HS(Player p)=>Value(p,"headshots");
    public static string Format(MatchState state,IReadOnlyList<Player> players)
    {
        if(!state.Active)return L.T("Ви не в грі❤️⚡");
        // Compact layouts preserve every player, even in modes with 64 slots.
        foreach(var layout in new[]{0,1,2})
        {
            var b=new StringBuilder($"🎯 <b>MATCHLENS</b>{(state.Demo?" · ДЕМО":"")}\n<b>{H(Short(state.Map,40))}</b> · {H(Short(state.ModeLabel,35))}\n");
            b.Append(L.F($"Рахунок <b>{state.CtScore} : {state.TScore}</b> · Гравців <b>{players.Count}</b>\n📊 Статистика {players.Count(p=>p.Sources.Any(s=>s.Numbers.Count>0))}/{players.Count}\n"));
            if(players.Count==0)b.Append(L.T("\nОчікуємо склад матчу…\n"));
            for(var i=0;i<players.Count;i++)
            {
                var p=players[i];var name=H(Short(p.Name,layout==0?26:layout==1?16:10));
                // Bound extreme Unicode grapheme sequences without splitting them.
                if(WebUtility.HtmlDecode(name).Length>48)name=H(L.T("Гравець ")+(i+1));
                var title=$"{i+1:00} · {name}";
                b.Append('\n').Append(SteamIds.Valid(p.Id)?$"<b><a href=\"https://steamcommunity.com/profiles/{p.Id}\">{title}</a></b>":$"<b>{title}</b>");
                if(p.IsLocal)b.Append(L.T(" · ТИ"));if(!p.Confirmed)b.Append(" · ?");b.Append('\n');
                if(!p.Sources.Any(s=>s.Numbers.Count>0)){b.Append(L.T("Очікуємо статистику…\n"));continue;}
                if(layout==0)
                {
                    var row1=$"KD {Value(p,"kd"),5}  ADR {Value(p,"adr"),5}  HS {HS(p),6}";
                    var row2=$"LT {Value(p,"leetify_rating"),5}  ELO {Value(p,"faceit_elo"),5}  TR {Value(p,"trust"),6}";
                    b.Append("<pre>").Append(H(row1)).Append('\n').Append(H(row2)).Append("</pre>\n");
                }
                else b.Append("<pre>").Append(H($"KD {Value(p,"kd")} · TR {Value(p,"trust")}"+(layout==1?$" · ELO {Value(p,"faceit_elo")}":""))).Append("</pre>\n");
            }
            b.Append(L.T("\n<i>LT — Leetify · ELO — FACEIT · TR — cstracker\n— немає даних · ? склад не підтверджено GSI</i>"));
            if(VisibleLength(b.ToString())<=4000)return b.ToString();
        }
        // The app caps rosters at 64; reject unsupported caller input explicitly.
        throw new ArgumentOutOfRangeException(nameof(players),"Telegram roster exceeds the supported message size.");
    }
}
