using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MatchLens;

public sealed record BrowserPage(string Url,string Title,string Text,string Html,int Status);
public sealed class BrowserPageException(string message) : Exception(message);

// An ordinary browser with its own persistent profile, without extensions or a
// connection to the user's signed-in browser. All operations stay on WPF's STA.
public sealed class BrowserPages : IDisposable
{
    readonly SemaphoreSlim gate=new(1);
    CoreWebView2Controller? controller;
    CoreWebView2? view;
    Task? initializing;
    bool disposed;
    static readonly HashSet<string> sites=new(StringComparer.OrdinalIgnoreCase)
    {"leetify.com","csstats.gg","cstracker.gg","app.scope.gg","scope.gg","www.faceit.com","faceit.com","steamcommunity.com"};
    public static bool Allowed(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.IsDefaultPort&&u.UserInfo.Length==0&&sites.Contains(u.Host);
    public static bool SameProfile(string requested,string final)
    {
        if(!Allowed(requested)||!Allowed(final))return false;
        var a=new Uri(requested);var b=new Uri(final);
        if(!a.Host.TrimStartEquivalent(b.Host))return false;
        if(a.Host=="app.scope.gg")
        {
            var account=Regex.Match(a.AbsolutePath,@"/(?:dashboard|progress)/(\d{1,10})(?:/|$)");
            var target=Regex.Match(b.AbsolutePath,@"/(?:dashboard|progress)/(\d{1,10})(?:/|$)");
            if(account.Success&&target.Success&&account.Groups[1].Value!=target.Groups[1].Value)return false;
        }
        var id=Regex.Match(a.AbsolutePath,@"(?:^|/)(\d{17})(?:/|$)");
        var other=Regex.Match(b.AbsolutePath,@"(?:^|/)(\d{17})(?:/|$)");
        return !id.Success||!other.Success||id.Groups[1].Value==other.Groups[1].Value;
    }
    async Task Initialize()
    {
        if(disposed)throw new ObjectDisposedException(nameof(BrowserPages));
        if(view!=null)return;
        Application.Current.Dispatcher.VerifyAccess();
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Store.Root,"public-browser"),new CoreWebView2EnvironmentOptions{Language="en-US"});
        // Microsoft's HWND_MESSAGE host is supported for invisible WebViews.
        // It never creates an app window, steals focus or affects shutdown.
        var host=await environment.CreateCoreWebView2ControllerAsync(new IntPtr(-3));
        try
        {
            if(disposed)throw new ObjectDisposedException(nameof(BrowserPages));
            host.Bounds=new System.Drawing.Rectangle(0,0,1280,1000);host.IsVisible=false;
            var browser=host.CoreWebView2;
            browser.Settings.AreDevToolsEnabled=false;
            browser.Settings.AreDefaultContextMenusEnabled=false;
            browser.Settings.AreBrowserAcceleratorKeysEnabled=false;
            browser.Settings.IsPasswordAutosaveEnabled=false;
            browser.Settings.IsGeneralAutofillEnabled=false;
            browser.NewWindowRequested+=(_,e)=>e.Handled=true;
            browser.DownloadStarting+=(_,e)=>e.Cancel=true;
            browser.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            view=browser;controller=host;
        }
        catch{host.Close();throw;}
    }
    public async Task<BrowserPage> Read(string url,Func<string,bool> ready,CancellationToken ct=default)
    {
        if(!Allowed(url))throw new ArgumentException("Unsupported public profile URL");
        await gate.WaitAsync(ct);
        try
        {
            await BrowserRuntime.Ensure().WaitAsync(ct);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var token=deadline.Token;
            try{await (initializing??=Initialize()).WaitAsync(token);}
            catch when(initializing?.IsCompleted==true){initializing=null;throw;}
            ct.ThrowIfCancellationRequested();var browser=view!;
            var navigation=new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong navigationId=0;
            int latestStatus=0;
            void Started(object? sender,CoreWebView2NavigationStartingEventArgs e)
            {
                if(!SameProfile(url,e.Uri)){e.Cancel=true;navigation.TrySetException(new BrowserPageException("Unexpected profile redirect"));return;}
                navigationId=e.NavigationId;
            }
            void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)
            {if(e.NavigationId==navigationId){latestStatus=e.HttpStatusCode;navigation.TrySetResult(e);}}
            Exception? processFailure=null;
            void Failed(object? sender,CoreWebView2ProcessFailedEventArgs e)
            {
                // GPU/utility restarts are recovered by WebView2 itself.
                if(e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited))return;
                processFailure=new BrowserPageException("Browser process: "+e.ProcessFailedKind+" / "+e.Reason+" / "+e.ExitCode);
                navigation.TrySetException(processFailure);
            }
            browser.NavigationStarting+=Started;browser.NavigationCompleted+=Completed;browser.ProcessFailed+=Failed;
            BrowserPage? last=null;
            bool confirmed=true;
            try
            {
                browser.Navigate(url);
                var completed=await navigation.Task.WaitAsync(token);
                if(!completed.IsSuccess&&completed.HttpStatusCode<400)throw new BrowserPageException("Browser navigation: "+completed.WebErrorStatus+" (HTTP "+completed.HttpStatusCode+")");
                if(completed.HttpStatusCode is 404 or 429)throw new HttpRequestException("Public page response",null,(HttpStatusCode)completed.HttpStatusCode);
                var previous="";var stable=0;var began=DateTimeOffset.UtcNow;
                while(true)
                {
                    token.ThrowIfCancellationRequested();
                    if(processFailure!=null)throw processFailure;
                    var json=await browser.ExecuteScriptAsync(SnapshotScript).WaitAsync(token);
                    last=JsonSerializer.Deserialize<BrowserPage>(json,Store.Json);
                    if(last==null||!SameProfile(url,last.Url))throw new BrowserPageException("Profile identity mismatch");
                    last=last with{Status=latestStatus};
                    if(last.Html.Length>3*1024*1024)throw new IOException("Page limit");
                    var requestedId=Regex.Match(new Uri(url).AbsolutePath,@"(?:^|/)(\d{17})(?:/|$)").Groups[1].Value;
                    confirmed=true;
                    if(requestedId.Length>0&&!new Uri(last.Url).AbsolutePath.Contains(requestedId))
                    {
                        var content=new PublicHtml(last.Html);
                        confirmed=content.Tokens.Contains(requestedId)||content.Links.Any(l=>l=="https://steamcommunity.com/profiles/"+requestedId||l=="https://steamcommunity.com/profiles/"+requestedId+"/");
                    }
                    var fingerprint=last.Text;
                    stable=fingerprint==previous?stable+1:0;previous=fingerprint;
                    // Waiting for actual parsed statistics avoids returning an SPA shell.
                    if(confirmed&&await Task.Run(()=>ready(last.Html),token)&&stable>=1&&DateTimeOffset.UtcNow-began>TimeSpan.FromMilliseconds(650))return last;
                    if(confirmed&&stable>=5&&Regex.IsMatch(last.Title+" "+new PublicHtml(last.Html).Name,@"player not found|profile not found",RegexOptions.IgnoreCase))return last;
                    if(DateTimeOffset.UtcNow-began>TimeSpan.FromSeconds(25)&&stable>=5)
                    {if(!confirmed)throw new BrowserPageException("Profile identity mismatch after redirect");return last;}
                    await Task.Delay(650,token);
                }
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested&&last!=null&&confirmed){return last;}
            catch(BrowserPageException)when(processFailure!=null)
            {Reset();throw;}
            finally
            {
                try{browser.NavigationStarting-=Started;browser.NavigationCompleted-=Completed;browser.ProcessFailed-=Failed;browser.Stop();}
                catch(InvalidOperationException){}
            }
        }
        catch(InvalidOperationException){Reset();throw;}
        finally{gate.Release();}
    }
    // Preserve public DOM attributes needed for charts; exclude scripts, inputs
    // and CSS-hidden content. Scroll position never excludes below-fold metrics.
    public const string SnapshotScript="""
        (() => {
          const clone=document.documentElement.cloneNode(true);
          const original=[document.documentElement,...document.documentElement.querySelectorAll('*')];
          const copies=[clone,...clone.querySelectorAll('*')];
          for(let i=original.length-1;i>=0;i--){
            const node=original[i], copy=copies[i];
            if(!copy)continue;
            const css=getComputedStyle(node);
            if(['SCRIPT','STYLE','NOSCRIPT','INPUT','TEXTAREA'].includes(node.tagName)||css.display==='none'||css.visibility==='hidden'||node.hidden||node.getAttribute('aria-hidden')==='true')copy.remove();
            else if(node.tagName==='IMG')copy.setAttribute('src',node.currentSrc||node.src);
          }
          return {url:location.href,title:document.title,text:document.body?.innerText||'',html:clone.outerHTML,status:0};
        })()
        """;
    public void Dispose()
    {
        disposed=true;Reset();
    }
    void Reset()
    {
        initializing=null;
        view=null;if(controller!=null){try{controller.Close();}catch(InvalidOperationException){}controller=null;}
    }
}
static class BrowserHostNames
{
    public static bool TrimStartEquivalent(this string a,string b)=>
        (a.StartsWith("www.")?a[4..]:a).Equals(b.StartsWith("www.")?b[4..]:b,StringComparison.OrdinalIgnoreCase);
}
