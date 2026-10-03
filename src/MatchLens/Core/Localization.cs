using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;

namespace MatchLens;

/// <summary>Presentation-only translation. Source data and discovery evidence stay unchanged.</summary>
public static class L
{
    static readonly Dictionary<string,string> english=Load();
    static readonly ConcurrentDictionary<string,string> cache=new();
    static readonly Regex slots=new(@"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})",RegexOptions.CultureInvariant);
    static readonly Lazy<List<(Regex Pattern,string Target)>> patterns=new(()=>english
        .Where(p=>slots.IsMatch(p.Key)).OrderByDescending(p=>p.Key.Length)
        .Select(p=>(new Regex("^"+slots.Replace(Regex.Escape(p.Key).Replace(@"\{","{").Replace(@"\}","}"),m=>"(?<p"+m.Groups[1].Value+">.+?)")+"$",RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(30)),p.Value)).ToList());
    public static string Language {get;private set;}="en";
    public static string? OverrideLanguage {get;set;}
    public static event Action? Changed;
    public static string Normalize(string? language)=>language is "uk" or "uk-UA"?"uk":"en";
    public static void Set(string? language)
    {
        var value=Normalize(language);var changed=value!=Language;Language=value;if(changed)cache.Clear();
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MatchLens.Localization.ui.json")!;
        var resources=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
        if(Application.Current is {} app)foreach(var item in resources)app.Resources[item.Key]=T(item.Value);
        if(changed)Changed?.Invoke();
    }
    static Dictionary<string,string> Load()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MatchLens.Localization.en.json")
            ??throw new InvalidOperationException("Translation catalog missing.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??[];
    }
    public static string T(string? text)
    {
        if(string.IsNullOrEmpty(text))return text??"";
        if(Language=="uk"||!text.Any(c=>c>='\u0400'&&c<='\u04ff'))return text;
        return cache.GetOrAdd(text,Translate);
    }
    static string Translate(string text)
    {
        if(english.TryGetValue(text,out var exact))return exact;
        foreach(var (pattern,target) in patterns.Value)
        {
            var match=pattern.Match(text);if(match.Success)return slots.Replace(target,m=>T(match.Groups["p"+m.Groups[1].Value].Value));
        }
        // Source notes are composed of independent phrases. Do not translate player identities.
        if(text.Contains('\n'))return string.Join("\n",text.Split('\n').Select(T));
        if(text.Contains(" · "))return string.Join(" · ",text.Split(" · ").Select(part=>T(part)));
        return text;
    }
    public static string F(FormattableString text)=>string.Format(CultureInfo.InvariantCulture,T(text.Format),text.GetArguments());
}

public sealed class LocalizedConverter : IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>value is string s?L.T(s):value;
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
