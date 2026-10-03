using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MatchLens;
public class TelegramState
{
    public string Destination {get;set;}="";
    public long MessageId {get;set;}
    public long Offset {get;set;}
    public bool PendingSend {get;set;}
}
public class TelegramException(string description,int code,int retry=0):Exception(description)
{public int Code {get;}=code;public int Retry {get;}=retry;}
public sealed class Telegram : IDisposable
{
    readonly HttpClient http;
    readonly SemaphoreSlim gate=new(1);
    TelegramState saved;
    string lastText="";string lastKeyboard="";int generation=-1;bool lastActive;
    DateTimeOffset retryAt;DateTimeOffset editAt;
    public string Status {get;private set;}="Telegram вимкнений";
    public Telegram(HttpMessageHandler? handler=null)
    {http=handler==null?new():new(handler);http.Timeout=TimeSpan.FromSeconds(10);saved=new();}
    void Persist()=>Store.Write("telegram-state-"+saved.Destination+".json",saved);
    static string H(string s)=>WebUtility.HtmlEncode(s);
    static string Limit(string s,int n)=>s.Length<=n?s:s[..n]+"…";
    public static (string Text,object Keyboard,int Pages) Format(MatchState state,IReadOnlyList<Player> players,int page=0)
        =>(TelegramMessage.Format(state,players),new{inline_keyboard=Array.Empty<object>()},1);
    async Task<JsonElement> Call(string token,string method,object body)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,$"https://api.telegram.org/bot{token}/{method}");
        req.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
        using var res=await http.SendAsync(req);var str=await res.Content.ReadAsStringAsync();
        using var doc=JsonDocument.Parse(str);var root=doc.RootElement;
        if(root.At("ok").ValueKind!=JsonValueKind.True)throw new TelegramException(root.At("description").Str("Помилка Telegram"),root.At("error_code").Int((int)res.StatusCode),root.At("parameters","retry_after").Int());
        return root.At("result").Clone();
    }
    static string Destination(Settings s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.BotToken+"\n"+s.ChatId)));
    public async Task Update(Settings settings,MatchState state,IReadOnlyList<Player> players,bool force=false)
    {
        if(state.Demo){Status="Демонстрація · Telegram не надсилає дані";return;}
        if(!settings.TelegramEnabled){Status="Telegram вимкнений";return;}
        if(settings.BotToken==""||settings.ChatId==""){Status="Вкажіть токен бота й Chat ID";return;}
        if(!force&&DateTimeOffset.UtcNow<retryAt)return;
        if(!await gate.WaitAsync(0))return;
        try
        {
            var dest=Destination(settings);
            if(saved.Destination!=dest){saved=Store.Read<TelegramState>("telegram-state-"+dest+".json");saved.Destination=dest;lastText="";lastKeyboard="";Persist();}
            if(!force&&generation==state.Generation&&lastActive==state.Active&&DateTimeOffset.UtcNow<editAt)return;
            if(saved.PendingSend){Status="Невідомий результат надсилання. Вкажіть ID наявного повідомлення в налаштуваннях.";return;}
            var result=Format(state,players);
            var keyboard=JsonSerializer.Serialize(result.Keyboard);
            if(saved.MessageId>0&&lastText==result.Text&&lastKeyboard==keyboard&&!force){Status="Одне повідомлення · синхронізовано";return;}
            if(saved.MessageId>0)
            {
                try
                {await Call(settings.BotToken,"editMessageText",new{chat_id=settings.ChatId,message_id=saved.MessageId,text=result.Text,parse_mode="HTML",link_preview_options=new{is_disabled=true},reply_markup=result.Keyboard});}
                catch(TelegramException ex)when(ex.Code==400&&ex.Message.Contains("message is not modified",StringComparison.OrdinalIgnoreCase)){}
                catch(TelegramException ex)when(ex.Code==400&&ex.Message.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)){saved.MessageId=0;Persist();}
            }
            if(saved.MessageId==0)
            {
                saved.PendingSend=true;Persist();
                try
                {
                    var sent=await Call(settings.BotToken,"sendMessage",new{chat_id=settings.ChatId,text=result.Text,parse_mode="HTML",disable_notification=true,link_preview_options=new{is_disabled=true},reply_markup=result.Keyboard});
                    saved.MessageId=(long)(sent.At("message_id").Num()??throw new IOException("Telegram не повернув ID"));saved.PendingSend=false;Persist();
                }
                catch(TelegramException){saved.PendingSend=false;Persist();throw;}
                // A network timeout may happen AFTER Telegram accepted sendMessage.
                // Keep PendingSend on disk to avoid a duplicate on retry/restart.
            }
            lastText=result.Text;lastKeyboard=keyboard;generation=state.Generation;lastActive=state.Active;editAt=DateTimeOffset.UtcNow.AddSeconds(3);Status="Одне повідомлення · синхронізовано";
        }
        catch(TelegramException ex)
        {
            retryAt=DateTimeOffset.UtcNow.AddSeconds(Math.Max(ex.Retry,30));
            Status=ex.Code switch{401=>"Невірний токен бота",403=>"Бот заблокований або немає доступу до чату",409=>"Цей бот уже використовує webhook / інший getUpdates",429=>"Telegram: очікування після ліміту",_=>"Telegram: "+Limit(ex.Message,130)};
        }
        catch{retryAt=DateTimeOffset.UtcNow.AddSeconds(30);Status=saved.PendingSend?"Невідомий результат надсилання. Перевірте чат; повторне надсилання зупинено.":"Telegram недоступний. Повторимо пізніше.";}
        finally{gate.Release();}
    }
    public void Recover(Settings settings,long messageId)
    {
        if(messageId<=0)throw new ArgumentException("ID має бути додатним числом.");
        if(gate.CurrentCount==0)throw new InvalidOperationException("Зачекайте завершення поточного запиту.");
        saved=new TelegramState{Destination=Destination(settings),MessageId=messageId};Persist();lastText="";lastKeyboard="";retryAt=default;editAt=default;generation=-1;
    }
    public long MessageId=>saved.MessageId;
    public void Dispose()=>http.Dispose();
}
