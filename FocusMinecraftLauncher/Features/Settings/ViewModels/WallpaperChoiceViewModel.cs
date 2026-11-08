using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>本地 Wallpaper Engine 壁纸的列表项，缩略图只在显示时解码。</summary>
public sealed class WallpaperChoiceViewModel : ObservableObject
{
    public WallpaperOption Option { get; }
    public string Title => Option.Title;
    public string KindText => Option.Kind switch
    {
        WallpaperKind.Video => "视频",
        WallpaperKind.Web => "Web",
        WallpaperKind.Scene => "场景",
        _ => "图片"
    };
    public string ProjectPath => Option.ProjectPath;
    private ImageSource? _preview;
    private readonly string _previewPath;
    private bool _previewRequested;
    public ImageSource? Preview
    {
        get
        {
            if (!_previewRequested)
            {
                _previewRequested = true;
                _ = LoadPreviewAsync(_previewPath);
            }
            return _preview;
        }
    }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public WallpaperChoiceViewModel(WallpaperOption option)
    {
        Option = option;
        string preview = option.PreviewPath;
        if (preview.Length == 0 && option.Kind == WallpaperKind.Scene)
            preview = WallpaperFrameCache.FindUsable(option.ProjectPath) ?? "";
        _previewPath = preview;
    }

    private async Task LoadPreviewAsync(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try
        {
            var image = await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 192;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            });
            _preview = image;
            Changed(nameof(Preview));
        }
        catch (Exception)
        {
            // 缩略图失败不影响选择壁纸。
        }
    }
}
