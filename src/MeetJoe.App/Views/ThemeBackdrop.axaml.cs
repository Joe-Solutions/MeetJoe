using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MeetJoe.App.Views;

public partial class ThemeBackdrop : UserControl
{
    private const int GrainSize = 128;

    public ThemeBackdrop()
    {
        InitializeComponent();
        Grain.Background = new ImageBrush(CreateGrain())
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, GrainSize, GrainSize, RelativeUnit.Absolute),
        };
    }

    private static WriteableBitmap CreateGrain()
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(GrainSize, GrainSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        var pixels = new byte[GrainSize * GrainSize * 4];
        var random = new Random(11);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = (byte)random.Next(0, 7);
            var value = random.Next(2) == 0 ? (byte)0 : alpha;
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = alpha;
        }

        using var buffer = bitmap.Lock();
        for (var row = 0; row < GrainSize; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                pixels, row * GrainSize * 4, buffer.Address + row * buffer.RowBytes, GrainSize * 4);
        }

        return bitmap;
    }
}
