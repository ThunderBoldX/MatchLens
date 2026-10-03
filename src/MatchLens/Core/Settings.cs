using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MatchLens;
public class Settings
{
    public string Language { get; set; } = "en";
    public string Cs2Path { get; set; } = "";
    public bool TelegramEnabled { get; set; }
    public string ChatId { get; set; } = "";
    public string SteamKeyCipher { get; set; } = "";
    public string FaceitKeyCipher { get; set; } = "";
    public string LeetifyKeyCipher { get; set; } = "";
    public string BotTokenCipher { get; set; } = "";
    public string GsiToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    [JsonIgnore] public string SteamKey { get => Secrets.Unprotect(SteamKeyCipher); set => SteamKeyCipher=Secrets.Protect(value); }
    [JsonIgnore] public string FaceitKey { get => Secrets.Unprotect(FaceitKeyCipher); set => FaceitKeyCipher=Secrets.Protect(value); }
    [JsonIgnore] public string LeetifyKey { get => Secrets.Unprotect(LeetifyKeyCipher); set => LeetifyKeyCipher=Secrets.Protect(value); }
    [JsonIgnore] public string BotToken { get => Secrets.Unprotect(BotTokenCipher); set => BotTokenCipher=Secrets.Protect(value); }
}
public static class Store
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MatchLens");
    public static string Warning {get;private set;}="";
    public static readonly JsonSerializerOptions Json = new() {WriteIndented=true, PropertyNameCaseInsensitive=true};
    public static T Read<T>(string name) where T:new()
    {
        try {var p=Path.Combine(Root,name);return File.Exists(p)?JsonSerializer.Deserialize<T>(File.ReadAllText(p),Json)??new():new();}
        catch { Warning="Не вдалося прочитати збережені налаштування. Перевірте їх перед збереженням.";return new(); }
    }
    public static void Write<T>(string name,T data)
    {
        Directory.CreateDirectory(Root); var p=Path.Combine(Root,name);var temp=p+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(data,Json),Encoding.UTF8);File.Move(temp,p,true);
    }
}
public static class Secrets
{
    [StructLayout(LayoutKind.Sequential)] struct Blob {public int Length;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptProtectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};
        try {Marshal.Copy(bytes,0,input.Data,bytes.Length);Blob output;bool ok=protect?CryptProtectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new CryptographicException("Windows не зміг захистити ключ.");try{var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;}finally{LocalFree(output.Data);}}
        finally {Marshal.FreeHGlobal(input.Data);}
    }
    public static string Protect(string value)=>string.IsNullOrWhiteSpace(value)?"":Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value.Trim()),true));
    public static string Unprotect(string value){if(value=="")return "";try{return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value),false));}catch{return "";}}
}
public static class Installation
{
    public static string FindCs2()
    {
        var steam=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null)?.ToString();
        var roots=new List<string>{steam??@"C:\Program Files (x86)\Steam"};
        try {var vdf=File.ReadAllText(Path.Combine(roots[0],"steamapps","libraryfolders.vdf"));foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(vdf,"\"path\"\\s+\"([^\"]+)\""))roots.Add(m.Groups[1].Value.Replace(@"\\",@"\"));}catch{}
        return roots.Select(r=>Path.Combine(r,"steamapps","common","Counter-Strike Global Offensive")).FirstOrDefault(p=>File.Exists(Path.Combine(p,"game","bin","win64","cs2.exe")))??"";
    }
    public static string GsiConfig(string token)=>"\"MatchLens\"\n{\n  \"uri\" \"http://127.0.0.1:37931/gsi\"\n  \"timeout\" \"5.0\"\n  \"buffer\" \"0.5\"\n  \"throttle\" \"1.0\"\n  \"heartbeat\" \"5.0\"\n  \"auth\" { \"token\" \""+token+"\" }\n  \"data\"\n  {\n    \"provider\" \"1\"\n    \"map\" \"1\"\n    \"round\" \"1\"\n    \"player_id\" \"1\"\n    \"player_match_stats\" \"1\"\n    \"allplayers_id\" \"1\"\n  }\n}\n";
    public static string Install(Settings settings)
    {
        if(!File.Exists(Path.Combine(settings.Cs2Path,"game","bin","win64","cs2.exe")))throw new IOException("Оберіть папку Counter-Strike Global Offensive з game\\bin\\win64\\cs2.exe.");
        var path=Path.Combine(settings.Cs2Path,"game","csgo","cfg","gamestate_integration_matchlens.cfg");
        File.WriteAllText(path,GsiConfig(settings.GsiToken),new UTF8Encoding(false));return path;
    }
}
