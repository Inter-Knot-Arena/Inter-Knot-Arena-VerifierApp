using System.Drawing;
using System.Drawing.Drawing2D;

namespace VerifierApp.Core.Services;

internal static class LiveUiDetector
{
    private const string HomeAgentsTemplateEnvVar = "IKA_HOME_AGENTS_TEMPLATE_PATH";
    private const string HomeAgentsTemplateRelativePath = @"assets\live_templates\home_agents_icon_template.png";
    private const double HomeAgentsCenterX = 0.660;
    private const double HomeAgentsCenterY = 0.905;
    private const double HomeAgentsWidthFraction = 280.0 / 2560.0;
    private const double HomeAgentsHeightFraction = 240.0 / 1440.0;
    private const double SearchLeftFraction = 0.53;
    private const double SearchTopFraction = 0.78;
    private const double SearchRightFraction = 0.77;
    private const double SearchBottomFraction = 0.98;
    private const double MatchThreshold = 0.30;
    private static readonly object TemplateLock = new();
    private static TemplateData? _homeAgentsTemplate;

    public static bool LooksLikeHomeScreen(string screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
        {
            return false;
        }

        var template = LoadHomeAgentsTemplate();
        if (template is null)
        {
            return false;
        }

        using var screenshot = new Bitmap(screenshotPath);
        if (screenshot.Width < 300 || screenshot.Height < 200)
        {
            return false;
        }

        var targetWidth = Math.Max(32, (int)Math.Round(screenshot.Width * HomeAgentsWidthFraction));
        var targetHeight = Math.Max(24, (int)Math.Round(screenshot.Height * HomeAgentsHeightFraction));
        var searchLeft = (int)Math.Round(screenshot.Width * SearchLeftFraction);
        var searchTop = (int)Math.Round(screenshot.Height * SearchTopFraction);
        var searchRight = (int)Math.Round(screenshot.Width * SearchRightFraction);
        var searchBottom = (int)Math.Round(screenshot.Height * SearchBottomFraction);
        var maxLeft = Math.Max(searchLeft, searchRight - targetWidth);
        var maxTop = Math.Max(searchTop, searchBottom - targetHeight);
        var stepX = Math.Max(8, targetWidth / 10);
        var stepY = Math.Max(8, targetHeight / 10);
        var bestScore = double.NegativeInfinity;

        for (var top = searchTop; top <= maxTop; top += stepY)
        {
            for (var left = searchLeft; left <= maxLeft; left += stepX)
            {
                var cropRect = Rectangle.Intersect(
                    new Rectangle(left, top, targetWidth, targetHeight),
                    new Rectangle(0, 0, screenshot.Width, screenshot.Height)
                );
                if (cropRect.Width <= 0 || cropRect.Height <= 0)
                {
                    continue;
                }

                using var resized = ResizeCrop(screenshot, cropRect, template.Width, template.Height);
                var score = ComputeNormalizedCorrelation(resized, template);
                if (score > bestScore)
                {
                    bestScore = score;
                }
            }
        }

        return bestScore >= MatchThreshold;
    }

    private static TemplateData? LoadHomeAgentsTemplate()
    {
        lock (TemplateLock)
        {
            if (_homeAgentsTemplate is not null)
            {
                return _homeAgentsTemplate;
            }

            var templatePath = ResolveHomeAgentsTemplatePath();
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                return null;
            }

            using var bitmap = new Bitmap(templatePath);
            _homeAgentsTemplate = TemplateData.FromBitmap(bitmap);
            return _homeAgentsTemplate;
        }
    }

    private static string ResolveHomeAgentsTemplatePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(HomeAgentsTemplateEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VerifierApp.sln")))
            {
                var candidate = Path.Combine(current.FullName, HomeAgentsTemplateRelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            current = current.Parent;
        }

        var workingDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(workingDirectory, "VerifierApp.sln")))
        {
            var candidate = Path.Combine(workingDirectory, HomeAgentsTemplateRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static Bitmap ResizeCrop(Bitmap source, Rectangle cropRect, int width, int height)
    {
        var resized = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), cropRect, GraphicsUnit.Pixel);
        return resized;
    }

    private static double ComputeNormalizedCorrelation(Bitmap candidate, TemplateData template)
    {
        var gray = new double[template.Width * template.Height];
        var sum = 0.0;
        var index = 0;
        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                var pixel = candidate.GetPixel(x, y);
                var value = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                gray[index++] = value;
                sum += value;
            }
        }

        var mean = sum / gray.Length;
        var numerator = 0.0;
        var candidateNorm = 0.0;
        for (var i = 0; i < gray.Length; i++)
        {
            var centered = gray[i] - mean;
            numerator += centered * template.CenteredGray[i];
            candidateNorm += centered * centered;
        }

        if (candidateNorm <= 0.0 || template.CenteredNorm <= 0.0)
        {
            return double.NegativeInfinity;
        }

        return numerator / (Math.Sqrt(candidateNorm) * template.CenteredNorm);
    }

    private sealed record TemplateData(int Width, int Height, double[] CenteredGray, double CenteredNorm)
    {
        public static TemplateData FromBitmap(Bitmap bitmap)
        {
            var gray = new double[bitmap.Width * bitmap.Height];
            var sum = 0.0;
            var index = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var value = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                    gray[index++] = value;
                    sum += value;
                }
            }

            var mean = sum / gray.Length;
            var centered = new double[gray.Length];
            var norm = 0.0;
            for (var i = 0; i < gray.Length; i++)
            {
                centered[i] = gray[i] - mean;
                norm += centered[i] * centered[i];
            }

            return new TemplateData(bitmap.Width, bitmap.Height, centered, Math.Sqrt(norm));
        }
    }
}
