using System.Text.RegularExpressions;

namespace MatchLens;
public static class RosterImport
{
    public static List<RosterEntry> Parse(string text)
    {
        if(text.Length>128000)throw new ArgumentException("Список завеликий. Вставте лише актуальний склад.");
        var players=new Dictionary<string,RosterEntry>();
        foreach(var line in text.Split('\n'))
        {
            // Modern CS2 client status uses slot/time/ping/loss/state/rate/name,
            // with no account ID. A numeric nickname must not become a profile.
            if(Regex.IsMatch(line,@"^\s*\[Client\]\s+\d+\s+\S+\s+\d+\s+\d+\s+\w+\s+\d+\s+'.*'\s*$"))continue;
            var ids=SteamIds.Parse(line);
            var name="";
            var quoted=Regex.Match(line,"\"([^\"]{1,128})\"");
            if(quoted.Success)
            {
                // A nickname itself can contain digits resembling a Steam ID.
                // Only identifiers outside the quoted nickname identify players.
                var outside=line.Remove(quoted.Index,quoted.Length);
                ids=SteamIds.Parse(outside);
                if(ids.Count==1)name=quoted.Groups[1].Value.Trim();
            }
            foreach(var id in ids)
            {
                if(players.Count>=64&&!players.ContainsKey(id))continue;
                var label=ids.Count==1&&name!=""?name:"Гравець "+id[^5..];
                if(!players.TryGetValue(id,out var existing)||existing.Name.StartsWith("Гравець "))
                    players[id]=new(id,label,"?",false,"Імпорт · вручну");
            }
        }
        return players.Values.ToList();
    }
    public static ulong ServerId(string text)
    {
        var matches=Regex.Matches(text,@"(?im)^\s*\[Client\]\s+steamid\s*:\s*\[[AG]:[^\]\r\n]+\]\s*\((\d{17,20})\)\s*$");
        if(matches.Count==0||!ulong.TryParse(matches[^1].Groups[1].Value,out var id))return 0;
        var type=(id>>52)&15;var universe=id>>56;
        return universe==1&&(type==3||type==4)?id:0;
    }
}
