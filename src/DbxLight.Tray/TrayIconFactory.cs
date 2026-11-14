using System.Drawing.Drawing2D;

namespace DbxLight.Tray;

public static class TrayIconFactory
{
    public static Color ColorFor(TrayState state) => state switch
    {
        TrayState.Running => Color.FromArgb(35, 137, 65),
        TrayState.Uploading => Color.FromArgb(32, 109, 190),
        TrayState.Attention => Color.FromArgb(190, 143, 12),
        TrayState.Failed => Color.FromArgb(192, 48, 48),
        _ => Color.FromArgb(116, 121, 126)
    };
    public static Bitmap Draw(TrayState state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 32f, size / 32f);
        using var background = new SolidBrush(ColorFor(state));
        g.FillEllipse(background, 1, 1, 30, 30);
        g.FillEllipse(Brushes.White, 6, 14, 12, 10);
        g.FillEllipse(Brushes.White, 12, 8, 13, 15);
        g.FillEllipse(Brushes.White, 19, 15, 8, 9);
        g.FillRectangle(Brushes.White, 11, 17, 12, 7);
        return bitmap;
    }
    public static Icon Create(TrayState state)
    {
        byte[][] images = new[] { 16, 32 }.Select(size =>
        {
            using Bitmap bitmap = Draw(state, size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }).ToArray();
        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        int offset = 6 + 16 * 2;
        for (int i = 0; i < 2; i++)
        {
            writer.Write((byte)(i == 0 ? 16 : 32)); writer.Write((byte)(i == 0 ? 16 : 32));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
        }
        foreach (byte[] bytes in images) writer.Write(bytes);
        ico.Position = 0;
        using var icon = new Icon(ico);
        return (Icon)icon.Clone();
    }
}
