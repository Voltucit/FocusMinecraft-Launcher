using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

internal sealed record CurseForgePackMetadata(
    Dictionary<long, (long ProjectId, CurseForgeFile File)> Files,
    Dictionary<long, CurseForgeMod> Projects);

public sealed partial class CurseForgeClient
{
    /// <summary>同一次导入批量预取并复用元数据；未返回的项目继续走原有逐项读取与兜底。</summary>
    internal static async Task<CurseForgePackMetadata> GetPackMetadataAsync(IReadOnlyList<CurseForgePackReference> references, CancellationToken token)
    {
        var files = new Dictionary<long, (long ProjectId, CurseForgeFile File)>();
        var projects = new Dictionary<long, CurseForgeMod>();
        await Task.WhenAll(ReadFiles(), ReadProjects());
        return new(files, projects);

        async Task ReadFiles()
        {
            foreach (var ids in references.Select(item => item.FileId).Distinct().Chunk(50))
            {
                using var document = await PostBatchAsync("mods/files", new { fileIds = ids }, token);
                if (document == null) break;
                foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && ReadFile(item) is { Sha1.Length: 40 } file && ids.Contains(file.Id)
                        && item.TryGetProperty("modId", out var owner) && owner.TryGetInt64(out var projectId) && projectId > 0)
                        files[file.Id] = (projectId, file);
            }
        }

        async Task ReadProjects()
        {
            foreach (var ids in references.Select(item => item.ProjectId).Distinct().Chunk(50))
            {
                using var document = await PostBatchAsync("mods", new { modIds = ids }, token);
                if (document == null) break;
                foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.TryGetInt64(out var projectId) && ids.Contains(projectId))
                        projects[projectId] = ReadMod(item);
            }
        }
    }

    private static async Task<JsonDocument?> PostBatchAsync(string path, object body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await MirrorGate.SendAsync(Http, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                return request;
            }, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return document;
            document.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or System.IO.IOException or JsonException) { return null; }
    }
}
