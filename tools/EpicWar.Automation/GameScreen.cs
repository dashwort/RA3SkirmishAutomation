using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace EpicWar.Automation;

internal enum GameScreenKind { Unknown, Setup, Gameplay }

internal static class GameScreen
{
    private static readonly Rectangle Title = new(180, 20, 175, 55);
    private static readonly Rectangle Begin = new(812, 660, 70, 28);
    private static readonly Rectangle Controls = new(1065, 208, 205, 62);
    private static readonly Bitmap TitleTemplate = Load("setup-title");
    private static readonly Bitmap BeginTemplate = Load("setup-begin");
    private static readonly Bitmap ControlsTemplate = Load("game-controls");
    private static readonly Bitmap AlliesControlsTemplate = Load("game-controls-allies");
    private static readonly Bitmap BeginNormal = Load("begin-normal");
    private static readonly Bitmap BeginHover = Load("begin-hover");

    public static Bitmap Normalize(Bitmap bitmap)
    {
        var result = new Bitmap(1280, 720);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, new Rectangle(0, 0, 1280, 720));
        return result;
    }

    public static GameScreenKind Classify(Bitmap frame)
    {
        using var image = Normalize(frame);
        if (GoldMatch(image, Title, TitleTemplate) > .78 &&
            (GoldMatch(image, Begin, BeginTemplate) > .78 || ButtonMatch(image)>.90))
            return GameScreenKind.Setup;
        // Require a detailed HUD at its known client-relative position. A loading
        // progress bar, surviving process or world pointer cannot satisfy this.
        if (ControlMatch(image) > .86 && GoldMatch(image, Title, TitleTemplate) < .45)
            return GameScreenKind.Gameplay;
        return GameScreenKind.Unknown;
    }
    private static double ButtonMatch(Bitmap image)
    {
        double best=0;
        foreach(var template in new[]{BeginNormal,BeginHover})
        for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)
        {
            int count=0,matched=0;
            for(int y=0;y<55;y+=2)for(int x=0;x<155;x+=2)
            {
                var a=image.GetPixel(770+x+dx,650+y+dy);var b=template.GetPixel(x,y);
                if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)<60)matched++;
                count++;
            }
            best=Math.Max(best,(double)matched/count);
        }
        return best;
    }

    internal static object Scores(Bitmap frame)
    {
        using var image=Normalize(frame);
        return new{title=GoldMatch(image,Title,TitleTemplate),begin=GoldMatch(image,Begin,BeginTemplate),controls=ControlMatch(image)};
    }

    private static Bitmap Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EpicWar.Automation.Vision.{name}.png")
            ?? throw new InvalidOperationException($"Missing visual anchor: {name}");
        using var image = new Bitmap(stream);
        return new Bitmap(image);
    }

    private static bool Gold(Color p) => p.R > 135 && p.G > 95 &&
        ((p.B < 155 && p.R > p.B * 1.25) || (p.R>195 && p.G>195 && p.B>195));

    private static double GoldMatch(Bitmap image, Rectangle rectangle, Bitmap template)
    {
        double best=0;
        // Fractional Windows display scaling can shift a resampled client frame
        // by one pixel. Search a bounded alignment window, not a screen-wide click target.
        for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)
            best=Math.Max(best,GoldMatchAt(image,rectangle,template,dx,dy));
        return best;
    }

    private static double GoldMatchAt(Bitmap image, Rectangle rectangle, Bitmap template,int dx,int dy)
    {
        int intersection = 0, union = 0;
        for (var y = 0; y < rectangle.Height; y += 2)
        for (var x = 0; x < rectangle.Width; x += 2)
        {
            var expected = Gold(template.GetPixel(x, y));
            var actual = Gold(image.GetPixel(x + rectangle.X+dx, y + rectangle.Y+dy));
            if (expected || actual) union++;
            if (expected && actual) intersection++;
        }
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double ControlMatch(Bitmap image)
    {
        double best=0;
        foreach (var template in new[] { ControlsTemplate, AlliesControlsTemplate })
        for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)best=Math.Max(best,ControlMatchAt(image,template,dx,dy));
        return best;
    }
    private static double ControlMatchAt(Bitmap image,Bitmap template,int dx,int dy)
    {
        int matched = 0, count = 0;
        for (var y = 0; y < Controls.Height; y += 2)
        for (var x = 0; x < Controls.Width; x += 2)
        {
            // The money amount changes as soon as a match starts.
            if (x > 110 && y < 30) continue;
            var a = image.GetPixel(Controls.X + x+dx, Controls.Y + y+dy);
            var b = template.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 100) matched++;
            count++;
        }
        return (double)matched / count;
    }
}
