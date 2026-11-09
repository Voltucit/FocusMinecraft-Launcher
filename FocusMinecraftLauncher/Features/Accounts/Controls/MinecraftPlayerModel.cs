using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FocusMinecraftLauncher.Features.Accounts.Controls;

/// <summary>Minecraft 方块模型、像素贴图和原地行走动作，全部由 WPF 渲染。</summary>
internal sealed class MinecraftPlayerModel
{
    public Model3DGroup Root { get; } = new();
    public AxisAngleRotation3D Yaw { get; } = new(new Vector3D(0, 1, 0), -25);
    public AxisAngleRotation3D Pitch { get; } = new(new Vector3D(1, 0, 0), 10);
    private readonly List<(AxisAngleRotation3D Joint, double Neutral, double From, double To, double Seconds)> _joints = [];
    private const double WalkHalfPeriod = .7;
    private bool _walking;

    public MinecraftPlayerModel(byte[]? skinPng, bool slim, byte[]? capePng)
    {
        var skin = skinPng == null ? Atlas.Steve() : Atlas.Decode(skinPng, false);
        var modern = skin.Height == skin.Width;
        var armWidth = slim && skinPng != null && modern ? 3 : 4;
        var armX = 4 + armWidth / 2.0;
        var parts = new Model3DGroup();
        var head = Part(skin, 0, 28, 0, 8, 8, 8, 0, 0, 32, 0, .5, true);
        parts.Children.Add(head);
        parts.Children.Add(Part(skin, 0, 18, 0, 8, 12, 4, 16, 16, 16, 32, .25, modern));
        var rightArm = Part(skin, -armX, 18, 0, armWidth, 12, 4, 40, 16, 40, 32, .25, modern);
        rightArm.Transform = ArmJoints(-armX);
        parts.Children.Add(rightArm);
        var leftArm = Part(skin, armX, 18, 0, armWidth, 12, 4, modern ? 32 : 40, modern ? 48 : 16, 48, 48, .25, modern, !modern);
        leftArm.Transform = ArmJoints(armX);
        parts.Children.Add(leftArm);
        var rightLeg = Part(skin, -2, 6, 0, 4, 12, 4, 0, 16, 0, 32, .25, modern);
        rightLeg.Transform = Joint(1, 0, 0, -2, 12, 0, -28, 28, WalkHalfPeriod);
        parts.Children.Add(rightLeg);
        var leftLeg = Part(skin, 2, 6, 0, 4, 12, 4, modern ? 16 : 0, modern ? 48 : 16, 0, 48, .25, modern, !modern);
        leftLeg.Transform = Joint(1, 0, 0, 2, 12, 0, 28, -28, WalkHalfPeriod);
        parts.Children.Add(leftLeg);
        if (capePng != null)
        {
            var cape = Atlas.Decode(capePng, true);
            var flipped = new Model3DGroup
            {
                Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180), 0, 16, -2.9)
            };
            flipped.Children.Add(Box(cape, 0, 16, -2.9, 10, 16, 1, 0, 0, 0, true));
            var movingCape = new Model3DGroup { Transform = Joint(1, 0, 0, 0, 24, -2.4, 8, 16, WalkHalfPeriod, 12) };
            movingCape.Children.Add(flipped);
            parts.Children.Add(movingCape);
        }
        var view = new Transform3DGroup();
        view.Children.Add(new TranslateTransform3D(0, -16, 0));
        view.Children.Add(new RotateTransform3D(Yaw));
        view.Children.Add(new RotateTransform3D(Pitch));
        parts.Transform = view;
        Root.Children.Add(parts);
    }

    private Transform3D ArmJoints(double x)
    {
        var transforms = new Transform3DGroup();
        var direction = Math.Sign(x);
        transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), direction * 3), x, 22, 0));
        // 同侧手臂与腿反向摆动，左右两侧保持相反相位。
        transforms.Children.Add(Joint(1, 0, 0, x, 22, 0, -25 * direction, 25 * direction, WalkHalfPeriod));
        return transforms;
    }

    private RotateTransform3D Joint(double ax, double ay, double az, double x, double y, double z, double from, double to, double seconds, double rest = 0)
    {
        var joint = new AxisAngleRotation3D(new Vector3D(ax, ay, az), rest);
        _joints.Add((joint, rest, from, to, seconds));
        return new RotateTransform3D(joint, x, y, z);
    }

    public void SetWalking(bool enabled)
    {
        if (_walking == enabled) return;
        _walking = enabled;
        foreach (var (joint, rest, from, to, seconds) in _joints)
        {
            DoubleAnimationUsingKeyFrames? animation = null;
            if (enabled)
            {
                animation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(seconds * 2), RepeatBehavior = RepeatBehavior.Forever
                };
                // 从静止姿态开始并回到同一姿态，不在开关或循环边界突然跳到摆动端点。
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                AddFrame(to, seconds * .5, EasingMode.EaseOut);
                AddFrame(from, seconds * 1.5, EasingMode.EaseInOut);
                AddFrame(rest, seconds * 2, EasingMode.EaseIn);

                void AddFrame(double value, double time, EasingMode easing) => animation.KeyFrames.Add(new EasingDoubleKeyFrame(value,
                    KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time)), new SineEase { EasingMode = easing }));
            }
            joint.BeginAnimation(AxisAngleRotation3D.AngleProperty, animation);
        }
    }

    private static Model3DGroup Part(Atlas skin, double x, double y, double z, int width, int height, int depth,
        int u, int v, int outerU, int outerV, double inflation, bool outer, bool mirror = false)
    {
        var part = new Model3DGroup();
        part.Children.Add(Box(skin, x, y, z, width, height, depth, u, v, 0, false, mirror));
        // 旧皮肤的整块不透明未使用区域不是帽子；与游戏的旧格式语义一致。
        if (outer && !(skin.Height * 2 == skin.Width && skin.IsOpaque(32, 0, 32, 32)))
            part.Children.Add(Box(skin, x, y, z, width, height, depth, outerU, outerV, inflation, true));
        return part;
    }

    private static Model3DGroup Box(Atlas atlas, double x, double y, double z, int width, int height, int depth,
        int u, int v, double inflation, bool transparent, bool mirror = false)
    {
        var box = new Model3DGroup();
        var left = x - width / 2.0 - inflation; var right = x + width / 2.0 + inflation;
        var bottom = y - height / 2.0 - inflation; var top = y + height / 2.0 + inflation;
        var back = z - depth / 2.0 - inflation; var front = z + depth / 2.0 + inflation;
        // 每个面的点均按从外侧看的左上、左下、右下、右上排列，保持正面绕序。
        Face(u + depth, v + depth, width, height, new(left, top, front), new(left, bottom, front), new(right, bottom, front), new(right, top, front));
        Face(u + depth * 2 + width, v + depth, width, height, new(right, top, back), new(right, bottom, back), new(left, bottom, back), new(left, top, back));
        Face(mirror ? u + depth + width : u, v + depth, depth, height, new(left, top, back), new(left, bottom, back), new(left, bottom, front), new(left, top, front));
        Face(mirror ? u : u + depth + width, v + depth, depth, height, new(right, top, front), new(right, bottom, front), new(right, bottom, back), new(right, top, back));
        Face(u + depth, v, width, depth, new(left, top, back), new(left, top, front), new(right, top, front), new(right, top, back));
        Face(u + depth + width, v, width, depth, new(left, bottom, front), new(left, bottom, back), new(right, bottom, back), new(right, bottom, front));
        box.Freeze();
        return box;

        void Face(int faceU, int faceV, int faceWidth, int faceHeight, Point3D p0, Point3D p1, Point3D p2, Point3D p3)
        {
            var pixelWidth = faceWidth * atlas.Scale; var pixelHeight = faceHeight * atlas.Scale;
            var pixels = atlas.Crop(faceU, faceV, faceWidth, faceHeight, mirror, !transparent);
            var mesh = new MeshGeometry3D();
            void Quad(double x0, double y0, double x1, double y1)
            {
                var horizontal = p3 - p0; var vertical = p1 - p0;
                var start = mesh.Positions.Count;
                mesh.Positions.Add(p0 + horizontal * x0 + vertical * y0);
                mesh.Positions.Add(p0 + horizontal * x0 + vertical * y1);
                mesh.Positions.Add(p0 + horizontal * x1 + vertical * y1);
                mesh.Positions.Add(p0 + horizontal * x1 + vertical * y0);
                mesh.TextureCoordinates.Add(new(x0, y0)); mesh.TextureCoordinates.Add(new(x0, y1));
                mesh.TextureCoordinates.Add(new(x1, y1)); mesh.TextureCoordinates.Add(new(x1, y0));
                foreach (var index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(start + index);
            }
            if (!transparent) Quad(0, 0, 1, 1);
            else
            {
                // 透明像素不生成几何面，避免 WPF 的透明区域写入深度后遮住皮肤或其他外层。
                for (var row = 0; row < pixelHeight; row++)
                {
                    var column = 0;
                    while (column < pixelWidth)
                    {
                        while (column < pixelWidth && pixels[(row * pixelWidth + column) * 4 + 3] == 0) column++;
                        var start = column;
                        while (column < pixelWidth && pixels[(row * pixelWidth + column) * 4 + 3] != 0) column++;
                        if (column > start) Quad((double)start / pixelWidth, (double)row / pixelHeight, (double)column / pixelWidth, (double)(row + 1) / pixelHeight);
                    }
                }
            }
            if (mesh.Positions.Count == 0) return;
            mesh.Freeze();
            // 3D 纹理会经过采样，先按整数最近邻放大每个独立面，保持像素边缘且不串到相邻面。
            var factor = Math.Max(1, 16 / atlas.Scale);
            var enlargedWidth = pixelWidth * factor; var enlargedHeight = pixelHeight * factor;
            var enlarged = new byte[enlargedWidth * enlargedHeight * 4];
            for (var row = 0; row < enlargedHeight; row++)
                for (var column = 0; column < enlargedWidth; column++)
                    Buffer.BlockCopy(pixels, ((row / factor) * pixelWidth + column / factor) * 4, enlarged, (row * enlargedWidth + column) * 4, 4);
            var image = BitmapSource.Create(enlargedWidth, enlargedHeight, 96, 96, PixelFormats.Bgra32, null, enlarged, enlargedWidth * 4);
            image.Freeze();
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill, TileMode = TileMode.None };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            brush.Freeze();
            var material = new DiffuseMaterial(brush); material.Freeze();
            var model = new GeometryModel3D(mesh, material); model.Freeze();
            box.Children.Add(model);
        }
    }

    private sealed class Atlas(int width, int height, byte[] pixels)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Scale => Width / 64;

        public static Atlas Decode(byte[] png, bool cape)
        {
            if (png.Length > 1024 * 1024) throw new InvalidDataException("玩家纹理过大。");
            using var stream = new MemoryStream(png, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var w = frame.PixelWidth; var h = frame.PixelHeight;
            if (decoder is not PngBitmapDecoder || w < 64 || w > 1024 || w % 64 != 0 || (cape ? h * 2 != w : h != w && h * 2 != w))
                throw new InvalidDataException("玩家纹理尺寸或格式无效。");
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var data = new byte[w * h * 4]; bitmap.CopyPixels(data, w * 4, 0);
            return new(w, h, data);
        }

        public byte[] Crop(int u, int v, int width, int height, bool mirror, bool opaque)
        {
            var w = width * Scale; var h = height * Scale;
            var result = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var target = (y * w + x) * 4;
                    Buffer.BlockCopy(pixels, ((v * Scale + y) * Width + u * Scale + (mirror ? w - 1 - x : x)) * 4, result, target, 4);
                    if (opaque) result[target + 3] = 255;
                }
            return result;
        }

        public bool IsOpaque(int u, int v, int width, int height)
        {
            for (var y = v * Scale; y < (v + height) * Scale; y++)
                for (var x = u * Scale; x < (u + width) * Scale; x++)
                    if (pixels[(y * Width + x) * 4 + 3] != 255) return false;
            return true;
        }

        public static Atlas Steve()
        {
            // 使用随程序嵌入的原版 Steve 皮肤，沿用旧版 64×32 皮肤的镜像映射。
            var uri = new Uri("/FocusMinecraftLauncher;component/Assets/Skins/Steve.png", UriKind.Relative);
            using var stream = Application.GetResourceStream(uri)?.Stream
                ?? throw new InvalidDataException("内置 Steve 皮肤资源缺失。");
            using var data = new MemoryStream();
            stream.CopyTo(data);
            return Decode(data.ToArray(), false);
        }
    }
}
