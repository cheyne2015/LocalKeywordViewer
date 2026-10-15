using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        using var input = File.OpenRead(args[0]);
        var dictionary = (ResourceDictionary)XamlReader.Load(input);
        var logo = (DrawingImage)dictionary["BrandLogo"];
        var frames = new List<(int Size, byte[] Data)>();
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawImage(logo, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var bytes = new MemoryStream(); png.Save(bytes);
            frames.Add((size, bytes.ToArray()));
        }
        using var output = new BinaryWriter(File.Create(args[1])); output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var frame in frames) { output.Write((byte)(frame.Size == 256 ? 0 : frame.Size)); output.Write((byte)(frame.Size == 256 ? 0 : frame.Size)); output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)32); output.Write(frame.Data.Length); output.Write(offset); offset += frame.Data.Length; }
        foreach (var frame in frames) output.Write(frame.Data);
    }
}
