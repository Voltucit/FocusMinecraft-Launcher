using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FocusMinecraftLauncher.Infrastructure.Content;

public sealed record HomeContentEntry(string Title,string Summary,string Date,Uri? Target,string? Image,Uri? Detail);

/// <summary>只读公开发布公告和 Mojang 官方 Java 更新内容，不检查或安装启动器升级。</summary>
public static class HomeContentService
{
    private const string RepositoryPath="/Voltucit/FocusMinecraft-Launcher";
    private static readonly Uri Mojang=new("https://launchercontent.mojang.com/v2/");
    private static readonly HttpClient Client=CreateClient();
    private static HttpClient CreateClient()
    {
        var client=new HttpClient{Timeout=TimeSpan.FromSeconds(25),MaxResponseContentBufferSize=8*1024*1024};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FocusMinecraftLauncher/2.0 (+https://github.com/Voltucit/FocusMinecraft-Launcher)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");return client;
    }
    public static async Task<IReadOnlyList<HomeContentEntry>> ReleasesAsync(CancellationToken token)
    {
        var json=JsonNode.Parse(await Client.GetStringAsync("https://api.github.com/repos"+RepositoryPath+"/releases?per_page=5",token)) as JsonArray
            ??throw new System.IO.InvalidDataException("发布公告格式无效。");
        return json.Where(item=>item?["draft"]?.GetValue<bool>()!=true).OrderByDescending(item=>Text(item,"published_at")).Take(3)
            .Select(item=>new HomeContentEntry(Text(item,"name") is {Length:>0} name?name:Text(item,"tag_name"),PlainText(Text(item,"body")),Date(Text(item,"published_at")),
                AllowedTarget(Text(item,"html_url")),null,null)).ToArray();
    }
    public static async Task<IReadOnlyList<HomeContentEntry>> GameUpdatesAsync(CancellationToken token)
    {
        var json=JsonNode.Parse(await Client.GetStringAsync(new Uri(Mojang,"javaPatchNotes.json"),token));
        var entries=json?["entries"] as JsonArray??throw new System.IO.InvalidDataException("游戏更新列表格式无效。");
        return entries.Where(item=>Text(item,"type") is "release" or "snapshot")
            .OrderByDescending(item=>Text(item,"date")).Take(8).Select(item=>
            {
                var image=Text(item?["image"],"url");
                var detail=DetailAddress(Text(item,"contentPath"));
                return new HomeContentEntry(Text(item,"title"),PlainText(Text(item,"shortText")),Date(Text(item,"date")),
                    AllowedTarget(Text(item,"readMoreLink")),image.Length>0?new Uri(Mojang,image).AbsoluteUri:null,detail);
            }).ToArray();
    }
    public static async Task<string> UpdateBodyAsync(Uri detail,CancellationToken token)
    {
        if(detail.Scheme!="https"||detail.Host!=Mojang.Host||!detail.AbsolutePath.StartsWith("/v2/javaPatchNotes/",StringComparison.Ordinal))
            throw new System.IO.InvalidDataException("游戏更新详情地址无效。");
        var json=JsonNode.Parse(await Client.GetStringAsync(detail,token));
        return PlainText(Text(json,"body"));
    }
    private static Uri? DetailAddress(string path)
    {
        if(!Uri.TryCreate(Mojang,path,out var uri)||uri.Scheme!="https"||uri.Host!=Mojang.Host||uri.UserInfo.Length>0
            ||!uri.AbsolutePath.StartsWith("/v2/javaPatchNotes/",StringComparison.Ordinal)||!uri.AbsolutePath.EndsWith(".json",StringComparison.Ordinal))return null;
        return uri;
    }
    public static Uri? AllowedTarget(string text)
    {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0)return null;
        return uri.Host is "minecraft.net" or "www.minecraft.net"||uri.Host=="github.com"&&uri.AbsolutePath.StartsWith(RepositoryPath+"/releases/",StringComparison.OrdinalIgnoreCase)?uri:null;
    }
    private static string Text(JsonNode? node,string field)=>node?[field] is JsonValue value&&value.TryGetValue<string>(out var text)?text??"":"";
    private static string Date(string text)=>DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date.ToString("yyyy-MM-dd"):"";
    private static string PlainText(string text)
    {
        text=Regex.Replace(text,@"<(script|style)\b[^>]*>.*?</\1>","",RegexOptions.Singleline|RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"</(?:p|div|h[1-6]|ul|ol)>|<br\s*/?>","\n\n",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"<li\b[^>]*>","\n- ",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"<[^>]+>","");text=Regex.Replace(text,@"(?m)^\s*#{1,6}\s+","");
        text=Regex.Replace(text,@"!\[[^\]]*\]\([^)]*\)","");text=Regex.Replace(text,@"\[([^\]]+)\]\([^)]*\)","$1");
        return Regex.Replace(WebUtility.HtmlDecode(text.Replace("**","").Replace("`","")),@"\n\s*\n(?:\s*\n)+","\n\n").Trim();
    }
}
