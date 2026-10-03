using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace MatchLens;
static class BrowserRuntime
{
    static Task? install;
    public static async Task Ensure()
    {
        try{CoreWebView2Environment.GetAvailableBrowserVersionString();return;}
        catch(WebView2RuntimeNotFoundException){}
        try{await (install??=Install());}
        catch{install=null;throw;}
    }
    static async Task Install()
    {
        var setup=Path.Combine(AppContext.BaseDirectory,"Dependencies","MicrosoftEdgeWebview2Setup.exe");
        if(!File.Exists(setup))throw new WebView2RuntimeNotFoundException();
        // Official Evergreen installer; per-user operation without elevation.
        // This runs only when Windows has no installed WebView2 Runtime.
        using var process=Process.Start(new ProcessStartInfo(setup){Arguments="/silent /install",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})??throw new BrowserPageException("WebView2 installer could not start");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        try{CoreWebView2Environment.GetAvailableBrowserVersionString();}
        catch(WebView2RuntimeNotFoundException){throw new BrowserPageException("WebView2 runtime installation failed (exit "+process.ExitCode+")");}
    }
}
