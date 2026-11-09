# 将确定性的矢量图形绘制成 PNG 和多尺寸 ICO；与 app-icon.svg 保持相同几何与配色。
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
// 图标绘制器使用超采样保留透明边缘，覆盖 Windows 托盘至高清资源管理器尺寸。
public static class LanShareIconBuilder
{
    // 构建圆角矩形路径。
    private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+w-d,y,d,d,270,90);
        p.AddArc(x+w-d,y+h-d,d,d,0,90); p.AddArc(x,y+h-d,d,d,90,90); p.CloseFigure(); return p;
    }
    // 大图保持两张卡片与双向箭头；缩小使用高质量采样。
    private static Bitmap Draw(int size)
    {
        using (var large = new Bitmap(1024,1024,PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(large))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(4,4);
                using (var tile = Rounded(8,8,240,240,52))
                using (var gradient = new LinearGradientBrush(new Point(8,8), new Point(248,248), ColorTranslator.FromHtml("#20B99F"), ColorTranslator.FromHtml("#08675F"))) g.FillPath(gradient,tile);
                using (var back = Rounded(50,52,110,105,19))
                using (var mint = new SolidBrush(ColorTranslator.FromHtml("#ACF3DD"))) g.FillPath(mint,back);
                using (var front = Rounded(96,99,110,105,19)) g.FillPath(Brushes.White,front);
                using (var pen = new Pen(ColorTranslator.FromHtml("#097B70"),9))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLine(pen,75,85,133,85); g.DrawLines(pen,new Point[] {new Point(120,72),new Point(133,85),new Point(120,98)});
                    g.DrawLine(pen,181,168,123,168); g.DrawLines(pen,new Point[] {new Point(136,155),new Point(123,168),new Point(136,181)});
                }
            }
            var result = new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(large, new Rectangle(0,0,size,size));
            }
            return result;
        }
    }
    // ICO 使用八个真实尺寸条目，避免系统将小图标放大导致模糊。
    public static void Build(string directory)
    {
        int[] sizes = {16,20,24,32,48,64,128,256}; byte[][] data = new byte[sizes.Length][];
        for (int i=0;i<sizes.Length;i++) using(var bitmap=Draw(sizes[i])) using(var stream=new MemoryStream())
        { bitmap.Save(stream,ImageFormat.Png); data[i]=stream.ToArray(); }
        using (var preview=Draw(512)) preview.Save(Path.Combine(directory,"app-icon.png"),ImageFormat.Png);
        using (var stream=File.Create(Path.Combine(directory,"app.ico"))) using(var writer=new BinaryWriter(stream))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset=6+sizes.Length*16;
            for(int i=0;i<sizes.Length;i++)
            {
                writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(data[i].Length); writer.Write(offset); offset+=data[i].Length;
            }
            foreach(byte[] png in data) writer.Write(png);
        }
    }
}
"@
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LanShare.App\Assets'
[LanShareIconBuilder]::Build($assets)
