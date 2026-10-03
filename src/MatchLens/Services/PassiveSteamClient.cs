using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MatchLens;

// Connect to the existing Steam client, without SteamAPI_Init or an AppID.
// ISteamClient021 ABI checked against the public SDK header. Runs only in
// the isolated helper; no game process handles, memory reads or injected code.
public sealed class PassiveSteamClient : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Factory([MarshalAs(UnmanagedType.LPStr)] string version,IntPtr code);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int CreatePipe(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int ConnectUser(IntPtr self,int pipe);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void ReleaseUser(IntPtr self,int pipe,int user);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return:MarshalAs(UnmanagedType.I1)] delegate bool ReleasePipe(IntPtr self,int pipe);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr GetFriends(IntPtr self,int user,int pipe,[MarshalAs(UnmanagedType.LPStr)] string version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Count(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong IdAt(IntPtr self,int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TimeAt(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint GameAt(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NameAt(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FlagCount(IntPtr self,int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong FlagId(IntPtr self,int index,int flags);
    [StructLayout(LayoutKind.Sequential)] public struct FriendGameInfo
    {public ulong GameId;public uint Ip;public ushort GamePort,QueryPort;public ulong LobbyId;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool GameInfo(IntPtr self,ulong id,out FriendGameInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool RequestInfo(IntPtr self,ulong id,[MarshalAs(UnmanagedType.I1)] bool namesOnly);
    IntPtr clientDll,apiDll,client,friends;
    int pipe,user;
    Count? count;IdAt? idAt;TimeAt? timeAt;GameAt? gameAt;NameAt? nameAt;
    FlagCount? serverCount;FlagId? serverId;GameInfo? gameInfo;RequestInfo? requestInfo;
    readonly Dictionary<ulong,(DateTimeOffset Time,string Name)> names=[];
    readonly Dictionary<ulong,(DateTimeOffset Time,uint App)> games=[];
    DateTimeOffset historyAt;
    List<CoplayEntry> history=[];
    public string Status {get;private set;}="Очікування Steam";
    static T Export<T>(IntPtr dll,string name) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll,name));
    T Method<T>(int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(client),slot*IntPtr.Size));
    public bool Connect(string cs2Path)
    {
        if(friends!=IntPtr.Zero)return true;
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var path=key?.GetValue("SteamClientDll64") as string;
            if(string.IsNullOrEmpty(path)||!File.Exists(path))
            {
                // Some Steam installs do not publish ActiveProcess's DLL path.
                // Only locate the installed DLL; no alternate user hive/session.
                using var steamKey=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var home=steamKey?.GetValue("SteamPath") as string;
                var fallback=string.IsNullOrEmpty(home)?"":Path.Combine(home,"steamclient64.dll");
                if(File.Exists(fallback))path=fallback;
                else
                {
                    var standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam","steamclient64.dll");
                    if(File.Exists(standard))path=standard;
                }
            }
            if(string.IsNullOrEmpty(path)||!Path.IsPathFullyQualified(path)||!File.Exists(path)||!Path.GetFileName(path).Equals("steamclient64.dll",StringComparison.OrdinalIgnoreCase))
            {Status="Steam цього користувача Windows недоступний";return false;}
            var api=Path.GetFullPath(Path.Combine(cs2Path,"game","bin","win64","steam_api64.dll"));
            if(!File.Exists(api)){Status="Вкажіть папку CS2 у налаштуваннях";return false;}
            clientDll=NativeLibrary.Load(path,typeof(PassiveSteamClient).Assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
            client=Export<Factory>(clientDll,"CreateInterface")("SteamClient021",IntPtr.Zero);
            if(client==IntPtr.Zero)throw new InvalidOperationException("SteamClient021");
            pipe=Method<CreatePipe>(0)(client);if(pipe==0)throw new InvalidOperationException("Steam pipe");
            user=Method<ConnectUser>(2)(client,pipe);if(user==0)throw new InvalidOperationException("Steam user");
            apiDll=NativeLibrary.Load(api,typeof(PassiveSteamClient).Assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
            // The flat wrappers must receive their own SDK interface version.
            // CS2's newer DLL can target v018; passing v017 to those wrappers
            // dispatches some methods to the wrong virtual table slots.
            var version=NativeLibrary.TryGetExport(apiDll,"SteamAPI_SteamFriends_v018",out _)?"SteamFriends018":"SteamFriends017";
            friends=Method<GetFriends>(8)(client,user,pipe,version);
            if(friends==IntPtr.Zero)throw new InvalidOperationException(version);
            // Flat SDK wrappers accept the existing interface pointer; no Init.
            count=Export<Count>(apiDll,"SteamAPI_ISteamFriends_GetCoplayFriendCount");
            idAt=Export<IdAt>(apiDll,"SteamAPI_ISteamFriends_GetCoplayFriend");
            timeAt=Export<TimeAt>(apiDll,"SteamAPI_ISteamFriends_GetFriendCoplayTime");
            gameAt=Export<GameAt>(apiDll,"SteamAPI_ISteamFriends_GetFriendCoplayGame");
            nameAt=Export<NameAt>(apiDll,"SteamAPI_ISteamFriends_GetFriendPersonaName");
            serverCount=Export<FlagCount>(apiDll,"SteamAPI_ISteamFriends_GetFriendCount");
            serverId=Export<FlagId>(apiDll,"SteamAPI_ISteamFriends_GetFriendByIndex");
            if(NativeLibrary.TryGetExport(apiDll,"SteamAPI_ISteamFriends_GetFriendGamePlayed",out var gamePtr))gameInfo=Marshal.GetDelegateForFunctionPointer<GameInfo>(gamePtr);
            if(NativeLibrary.TryGetExport(apiDll,"SteamAPI_ISteamFriends_RequestUserInformation",out var infoPtr))requestInfo=Marshal.GetDelegateForFunctionPointer<RequestInfo>(infoPtr);
            Status="Steam Client · "+version;return true;
        }
        catch(Exception ex){Status="Steam недоступний · "+(ex is InvalidOperationException?ex.Message:ex.GetType().Name);Dispose();return false;}
    }
    public List<CoplayEntry> Read()
    {
        if(friends==IntPtr.Zero)return [];
        if(DateTimeOffset.UtcNow<historyAt)return history;
        var result=new List<CoplayEntry>();var n=count!(friends);
        if(n<0||n>4096)throw new InvalidOperationException("Steam count "+n);
        for(var i=0;i<n;i++)
        {
            var id=idAt!(friends,i);if(!SteamIds.Valid(id.ToString()))continue;
            var now=DateTimeOffset.UtcNow;
            if(!games.TryGetValue(id,out var game)||now-game.Time>TimeSpan.FromSeconds(5))games[id]=game=(now,gameAt!(friends,id));
            if(game.App!=730)continue;
            result.Add(new(id.ToString(),Name(id),timeAt!(friends,id)));
        }
        historyAt=DateTimeOffset.UtcNow.AddSeconds(1);return history=result.DistinctBy(x=>x.Id).ToList();
    }
    string Name(ulong id)
    {
        var now=DateTimeOffset.UtcNow;
        if(names.TryGetValue(id,out var saved)&&now-saved.Time<TimeSpan.FromSeconds(3))return saved.Name;
        var name=Marshal.PtrToStringUTF8(nameAt!(friends,id))??"";
        if(string.IsNullOrWhiteSpace(name)||name=="[unknown]"){requestInfo?.Invoke(friends,id,true);name="Гравець "+id.ToString()[^5..];}
        names[id]=(now,name[..Math.Min(name.Length,128)]);return names[id].Name;
    }
    public List<RosterEntry> ReadServer()
    {
        if(friends==IntPtr.Zero||serverCount==null||serverId==null)return [];
        const int onServer=0x10;var count=serverCount(friends,onServer);
        if(count<0||count>256)return [];
        var result=new List<RosterEntry>();
        for(var i=0;i<count&&result.Count<64;i++)
        {
            var id=serverId(friends,i,onServer);if(!SteamIds.Valid(id.ToString()))continue;
            // Explicit information about another running game excludes that ID.
            if(gameInfo?.Invoke(friends,id,out var info)==true&&info.GameId!=730)continue;
            result.Add(new(id.ToString(),Name(id),"?",false,"Авто • Steam: учасник сервера"));
        }
        return result.DistinctBy(p=>p.Id).ToList();
    }
    public void Dispose()
    {
        friends=IntPtr.Zero;
        names.Clear();games.Clear();history=[];historyAt=default;
        if(client!=IntPtr.Zero&&user!=0){Method<ReleaseUser>(4)(client,pipe,user);user=0;}
        if(client!=IntPtr.Zero&&pipe!=0){Method<ReleasePipe>(1)(client,pipe);pipe=0;}
        client=IntPtr.Zero;
        if(apiDll!=IntPtr.Zero){NativeLibrary.Free(apiDll);apiDll=IntPtr.Zero;}
        if(clientDll!=IntPtr.Zero){NativeLibrary.Free(clientDll);clientDll=IntPtr.Zero;}
    }
}
