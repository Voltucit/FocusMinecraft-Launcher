using System.IO;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FMCore.Versions;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    /// <summary>恢复路径先归一化；只对默认或已失效的目录检查启动器附近位置，不接管用户已有的自定义目录。</summary>
    private async Task ResolveStartupDirectoriesAsync()
    {
        var originalGame = SavedSettings.GameDirectory;
        var originalSelection = SelectedRoot;
        IsLoading = true; Signal();
        try
        {
            var resolved = await Task.Run(() => FindStartupDirectories(originalGame, originalSelection), _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            var gameChanged = string.Equals(SavedSettings.GameDirectory, originalGame, StringComparison.Ordinal)
                && !string.Equals(originalGame, resolved.Game, StringComparison.Ordinal);
            var selectionChanged = string.Equals(SelectedRoot, originalSelection, StringComparison.Ordinal)
                && !string.Equals(originalSelection, resolved.Selected, StringComparison.Ordinal);
            if (gameChanged) SavedSettings.GameDirectory = resolved.Game;
            if (selectionChanged) _selectedRoot = resolved.Selected;
            if (gameChanged || selectionChanged)
            {
                Changed(nameof(SelectedRoot));
                _configuration.ChangeLauncher(document =>
                {
                    if (gameChanged) LauncherConfiguration.Object(document, "global")["gameDirectory"] = resolved.Game;
                    // 首次扫描前保留原来的 instanceId，扫描后仍可恢复上次选择。
                    if (selectionChanged) LauncherConfiguration.Object(document, "selection")["root"] = resolved.Selected;
                });
                Log("启动时确认游戏目录：" + SelectedRoot);
            }
        }
        finally { IsLoading = false; Signal(); }
    }

    private (string Game, string Selected) FindStartupDirectories(string game, string selected)
    {
        string Normalize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) value = fallback;
            if (!Path.IsPathFullyQualified(value)) value = Path.Combine(_configuration.BaseDirectory, value);
            try { return InstanceStore.NormalizeRoot(value); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                Log("保存的游戏目录无效，将使用启动器默认目录：" + RuntimeSupport.Error(error));
                return InstanceStore.NormalizeRoot(fallback);
            }
        }
        bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        bool? HasVersions(string path)
        {
            try { return new MinecraftVersionReader().GetVersionDirectories(path).Length > 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        var defaultRoot = InstanceStore.NormalizeRoot(_configuration.MinecraftDirectory);
        var originalGame = Normalize(game, defaultRoot);
        var originalSelection = Normalize(selected, originalGame);
        var resolvedGame = originalGame;
        if ((Same(originalGame, defaultRoot) || !Directory.Exists(originalGame)) && HasVersions(originalGame) == false)
        {
            foreach (var candidate in new[] { defaultRoot, InstanceStore.NormalizeRoot(_configuration.BaseDirectory) })
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (HasVersions(candidate) != true) continue;
                resolvedGame = candidate;
                break;
            }
        }
        var mayFollowGame = Same(originalSelection, originalGame) || Same(originalSelection, defaultRoot) || !Directory.Exists(originalSelection);
        var resolvedSelection = mayFollowGame && HasVersions(originalSelection) == false ? resolvedGame : originalSelection;
        return (resolvedGame, resolvedSelection);
    }
}
