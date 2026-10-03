using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace MatchLens;
public sealed class Avatars : IDisposable
{
    readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(8)};
    readonly SemaphoreSlim gate=new(3);
    readonly Dictionary<string,(DateTimeOffset Time,ImageSource? Image)> cache=[];
    public Avatars()=>http.DefaultRequestHeaders.UserAgent.ParseAdd("MatchLens/0.1");
    public void RetryFailed(){foreach(var id in cache.Where(x=>x.Value.Image==null).Select(x=>x.Key).ToArray())cache.Remove(id);}
    public static bool TrustedUrl(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&new[]{"avatars.steamstatic.com","avatars.akamai.steamstatic.com","steamcdn-a.akamaihd.net","steamuserimages-a.akamaihd.net","cdn.akamai.steamstatic.com"}.Contains(u.Host.ToLowerInvariant());
    public static string ParseXml(string xml)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});
        var d=XDocument.Load(reader);return d.Root?.Element("avatarMedium")?.Value??d.Root?.Element("avatarFull")?.Value??"";
    }
    public async Task<ImageSource?> Get(string id,string? knownUrl=null,CancellationToken ct=default)
    {
        if(!SteamIds.Valid(id))return null;
        if(cache.TryGetValue(id,out var old)&&DateTimeOffset.UtcNow-old.Time<TimeSpan.FromMinutes(old.Image==null?2:60)&&(old.Image!=null||!TrustedUrl(knownUrl??"")))return old.Image;
        await gate.WaitAsync(ct);
        try
        {
            var url=knownUrl;
            if(!TrustedUrl(url??""))
            {
                // Public avatar fallback when no Steam Web API key is configured.
                var xml=await http.GetStringAsync($"https://steamcommunity.com/profiles/{id}/?xml=1",ct);url=ParseXml(xml);
            }
            if(!TrustedUrl(url??""))return null;
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength>2*1024*1024)return null;
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[16384];int read;
            while((read=await stream.ReadAsync(buffer,ct))>0){if(output.Length+read>2*1024*1024)return null;output.Write(buffer,0,read);}
            output.Position=0;var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=160;image.StreamSource=output;image.EndInit();image.Freeze();
            cache[id]=(DateTimeOffset.UtcNow,image);return image;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{cache[id]=(DateTimeOffset.UtcNow,null);return null;}
        finally{gate.Release();}
    }
    public void Dispose()=>http.Dispose();
}
