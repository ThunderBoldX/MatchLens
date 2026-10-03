using System.IO;
using System.Runtime.InteropServices;

namespace MatchLens;

// Uses Valve's exported Steamworks functions in the locally installed CS2
// redistributable. No process handle, injection, offsets or memory scanning.
public sealed class SteamReader : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFlatFn(IntPtr errorMessage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr InterfaceFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong IdFn(IntPtr self,int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TimeFn(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint GameFn(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NameFn(IntPtr self,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FlagCountFn(IntPtr self,int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong FlagIdFn(IntPtr self,int index,int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SourceCountFn(IntPtr self,ulong source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong SourceIdFn(IntPtr self,ulong source,int index);
    IntPtr library, friends;
    bool initialized;
    DateTimeOffset historyAt;List<CoplayEntry> history=[];
    CountFn? count;IdFn? idAt;TimeFn? timeAt;GameFn? gameAt;NameFn? nameAt;VoidFn? callbacks,shutdown;
    FlagCountFn? serverCount;FlagIdFn? serverId;
    public string Status {get;private set;}="Steam ще не підключений";
    T Fn<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));
    public bool Connect(string cs2Path)
    {
        if(initialized)return true;
        try
        {
            var path=Path.Combine(cs2Path,"game","bin","win64","steam_api64.dll");
            if(!File.Exists(path)){Status="Не знайдено локальний Steamworks DLL. Вкажіть папку CS2.";return false;}
            Environment.SetEnvironmentVariable("SteamAppId","730");
            Environment.SetEnvironmentVariable("SteamGameId","730");
            library=NativeLibrary.Load(Path.GetFullPath(path),typeof(SteamReader).Assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
            shutdown=Fn<VoidFn>("SteamAPI_Shutdown");
            // Require every export before calling Init to avoid leaving a partial session.
            count=Fn<CountFn>("SteamAPI_ISteamFriends_GetCoplayFriendCount");
            idAt=Fn<IdFn>("SteamAPI_ISteamFriends_GetCoplayFriend");
            timeAt=Fn<TimeFn>("SteamAPI_ISteamFriends_GetFriendCoplayTime");
            gameAt=Fn<GameFn>("SteamAPI_ISteamFriends_GetFriendCoplayGame");
            nameAt=Fn<NameFn>("SteamAPI_ISteamFriends_GetFriendPersonaName");
            callbacks=Fn<VoidFn>("SteamAPI_RunCallbacks");
            serverCount=Fn<FlagCountFn>("SteamAPI_ISteamFriends_GetFriendCount");serverId=Fn<FlagIdFn>("SteamAPI_ISteamFriends_GetFriendByIndex");
            var version=NativeLibrary.TryGetExport(library,"SteamAPI_SteamFriends_v018",out _)?"SteamAPI_SteamFriends_v018":"SteamAPI_SteamFriends_v017";
            var getFriends=Fn<InterfaceFn>(version);
            var ok=false;var initCode=-1;
            if(NativeLibrary.TryGetExport(library,"SteamAPI_InitFlat",out var flat))
            {
                var buffer=Marshal.AllocHGlobal(1024);try{initCode=Marshal.GetDelegateForFunctionPointer<InitFlatFn>(flat)(buffer);ok=initCode==0;}finally{Marshal.FreeHGlobal(buffer);}
            }
            else ok=Fn<InitFn>("SteamAPI_Init")();
            if(!ok){Status=$"Steam API не ініціалізувався (код {initCode}). MatchLens і Steam мають працювати під одним користувачем Windows.";Cleanup();return false;}
            initialized=true;friends=getFriends();
            if(friends==IntPtr.Zero){Status="SteamFriends недоступний";Cleanup();return false;}
            Status="Steam підключений · список недавніх гравців";return true;
        }
        catch(EntryPointNotFoundException ex) {Status="Steamworks: "+ex.Message;Cleanup();return false;}
        catch {Status="Не вдалося завантажити Steamworks. Перевірте Steam і папку CS2.";Cleanup();return false;}
    }
    public List<CoplayEntry> Read()
    {
        if(!initialized||friends==IntPtr.Zero)return [];
        callbacks!();if(DateTimeOffset.UtcNow<historyAt)return history;var result=new List<CoplayEntry>();
        for(var i=0;i<Math.Clamp(count!(friends),0,512);i++)
        {
            var id=idAt!(friends,i);if(gameAt!(friends,id)!=730)continue;
            var str=id.ToString();if(!SteamIds.Valid(str))continue;
            result.Add(new(str,Marshal.PtrToStringUTF8(nameAt!(friends,id))??str,timeAt!(friends,id)));
        }
        historyAt=DateTimeOffset.UtcNow.AddSeconds(1);return history=result;
    }
    void Cleanup(){if(initialized){shutdown?.Invoke();initialized=false;}friends=IntPtr.Zero;history=[];historyAt=default;if(library!=IntPtr.Zero){NativeLibrary.Free(library);library=IntPtr.Zero;}}
    public List<RosterEntry> ReadServer()
    {
        if(!initialized||friends==IntPtr.Zero)return [];
        callbacks!();const int sameServer=0x10;var result=new List<RosterEntry>();
        for(int i=0;i<Math.Clamp(serverCount!(friends,sameServer),0,64);i++)
        {var id=serverId!(friends,i,sameServer);if(SteamIds.Valid(id.ToString()))result.Add(new(id.ToString(),Marshal.PtrToStringUTF8(nameAt!(friends,id))??id.ToString(),"?",false,"Steam · список сервера"));}
        return result;
    }
    public void Dispose()=>Cleanup();
    public List<RosterEntry> ReadSource(ulong source)
    {
        if(!initialized||friends==IntPtr.Zero)return [];
        callbacks!();
        var getCount=Fn<SourceCountFn>("SteamAPI_ISteamFriends_GetFriendCountFromSource");
        var getId=Fn<SourceIdFn>("SteamAPI_ISteamFriends_GetFriendFromSourceByIndex");
        var count=getCount(friends,source);
        if(count<0||count>64){Status="Steam повернув непідтримуваний розмір джерела";return [];}
        var result=new List<RosterEntry>();
        for(var i=0;i<count;i++)
        {
            var id=getId(friends,source,i).ToString();if(!SteamIds.Valid(id))continue;
            var name=Marshal.PtrToStringUTF8(nameAt!(friends,ulong.Parse(id)))??id;
            result.Add(new(id,name,"?",false,"Steam · вибраний сервер"));
        }
        Status=result.Count==0?"Steam не має доступного списку для цього сервера":$"Steam повернув {result.Count} профілів для вибраного сервера";
        return result.DistinctBy(p=>p.Id).ToList();
    }
}
