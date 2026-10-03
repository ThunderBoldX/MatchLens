using System.Diagnostics;

namespace MatchLens;
public static class GamePresence
{
    public static bool SteamRunning()
    {var processes=Process.GetProcessesByName("steam");try{return processes.Length>0;}finally{foreach(var p in processes)p.Dispose();}}
    public static bool Running()
    {
        var processes=Process.GetProcessesByName("cs2");
        try{return processes.Length>0;}finally{foreach(var p in processes)p.Dispose();}
    }
}
