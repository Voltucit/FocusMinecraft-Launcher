using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>只交换账号页已生成的 8×8 头像像素，不传输皮肤地址、账号凭据或完整皮肤文件。</summary>
internal static class MemberAvatarCodec
{
    private const int Side = 8;
    private const int ByteCount = Side * Side * 4;

    public static string Encode(ImageSource? image)
    {
        if (image is not BitmapSource { PixelWidth: Side, PixelHeight: Side } bitmap) return "";
        var pixels = new byte[ByteCount];
        var source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        source.CopyPixels(Int32Rect.Empty, pixels, Side * 4, 0);
        return Convert.ToBase64String(pixels);
    }

    /// <summary>严格限制头像尺寸；无效或旧版客户端未提供头像时回退到统一默认头像。</summary>
    public static string Normalize(string? pixels)
    {
        Span<byte> buffer = stackalloc byte[ByteCount];
        return pixels is { Length: 344 } && Convert.TryFromBase64String(pixels, buffer, out var length) && length == ByteCount
            ? Convert.ToBase64String(buffer) : "";
    }

    public static BitmapSource? Decode(string? pixels)
    {
        var valid = Normalize(pixels);
        if (valid.Length == 0) return null;
        var head = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Bgra32, null, Convert.FromBase64String(valid), Side * 4);
        head.Freeze();
        return head;
    }
}
