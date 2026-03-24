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
    // These boxes must stay aligned with the same top-row cards that the runtime
    // capture plan clicks via select_agent_1/2/3.
    private static readonly (int AgentSlotIndex, double X, double Y, double Width, double Height)[] VisibleRosterSlotBoxes =
    [
        (1, 0.532, 0.015, 0.135, 0.235),
        (2, 0.635, 0.045, 0.120, 0.245),
        (3, 0.747, 0.000, 0.130, 0.235),
    ];
    private const double RosterSlotCenterInsetFraction = 0.20;
    private const double RosterSlotColorPixelSaturationThreshold = 12.0;
    private const double RosterSlotUnavailableMaxColorPixelFraction = 0.05;
    private const double RosterSlotUnavailableMaxMeanSaturation = 6.0;
    private const double RosterSlotUnavailableMinMeanLuma = 60.0;
    private const double RosterSlotLockBadgeMaxColorPixelFraction = 0.20;
    private const double RosterSlotLockBadgeMaxMeanSaturation = 28.0;
    private const double RosterSlotLockExpandLeftFraction = 0.25;
    private const double RosterSlotLockExpandRightFraction = 0.25;
    private const double RosterSlotLockExpandTopFraction = 0.10;
    private const double RosterSlotLockExpandBottomFraction = 0.35;
    private const double RosterSlotLockSearchLeftFraction = 0.00;
    private const double RosterSlotLockSearchTopFraction = 0.55;
    private const double RosterSlotLockSearchWidthFraction = 0.55;
    private const double RosterSlotLockSearchHeightFraction = 0.40;
    private const byte RosterSlotLockDarkThreshold = 60;
    private const double RosterSlotLockMinAreaFraction = 0.008;
    private const double RosterSlotLockMaxAreaFraction = 0.080;
    private const double RosterSlotLockMinFillRatio = 0.55;
    private const double RosterSlotLockMinAspectRatio = 0.65;
    private const double RosterSlotLockMaxAspectRatio = 1.35;
    private const double RosterSlotLockMinCenterXFraction = 0.25;
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

    public static IReadOnlyList<RosterSlotInspection> InspectVisibleRosterSlots(string screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
        {
            return [];
        }

        using var screenshot = new Bitmap(screenshotPath);
        if (screenshot.Width < 300 || screenshot.Height < 200)
        {
            return [];
        }

        var inspections = new List<RosterSlotInspection>(VisibleRosterSlotBoxes.Length);
        foreach (var (agentSlotIndex, x, y, width, height) in VisibleRosterSlotBoxes)
        {
            var slotRect = FractionalRectangle(screenshot, x, y, width, height);
            if (slotRect.Width <= 0 || slotRect.Height <= 0)
            {
                continue;
            }

            var centerRect = InsetRectangle(slotRect, RosterSlotCenterInsetFraction);
            var metrics = MeasureSaturation(screenshot, centerRect);
            var lockBadgeDetected = DetectLockBadge(screenshot, slotRect);
            var looksUnavailableVisualStyle = LooksUnavailableVisualStyle(metrics);
            var looksUnavailableByLockBadge = LooksUnavailableByLockBadge(lockBadgeDetected, metrics);
            inspections.Add(
                new RosterSlotInspection(
                    agentSlotIndex,
                    !looksUnavailableVisualStyle && !looksUnavailableByLockBadge,
                    lockBadgeDetected,
                    looksUnavailableVisualStyle,
                    looksUnavailableByLockBadge,
                    Math.Round(metrics.MeanSaturation, 4),
                    Math.Round(metrics.ColorPixelFraction, 4),
                    Math.Round(metrics.MeanLuma, 4)
                )
            );
        }

        return inspections;
    }

    public static bool LooksLikeAgentRosterScreen(string screenshotPath)
    {
        // Home/menu can light up the top-card heuristics, but it is never a safe
        // substitute for the actual agent roster.
        if (LooksLikeHomeScreen(screenshotPath))
        {
            return false;
        }

        var inspections = InspectVisibleRosterSlots(screenshotPath);
        if (inspections.Count == 0)
        {
            return false;
        }

        var brightSlots = inspections.Count(inspection => inspection.MeanCenterLuma >= 80.0);
        var unavailableSlots = inspections.Count(inspection =>
            inspection.LooksUnavailableVisualStyle || inspection.LooksUnavailableByLockBadge
        );
        return brightSlots >= 2 || (brightSlots >= 1 && unavailableSlots >= 1);
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

    private static Rectangle FractionalRectangle(
        Bitmap source,
        double x,
        double y,
        double width,
        double height
    )
    {
        var left = (int)Math.Round(source.Width * x);
        var top = (int)Math.Round(source.Height * y);
        var right = (int)Math.Round(source.Width * (x + width));
        var bottom = (int)Math.Round(source.Height * (y + height));
        var rect = Rectangle.FromLTRB(left, top, right, bottom);
        return Rectangle.Intersect(rect, new Rectangle(0, 0, source.Width, source.Height));
    }

    private static Rectangle InsetRectangle(Rectangle rect, double fraction)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var insetX = (int)Math.Round(rect.Width * fraction);
        var insetY = (int)Math.Round(rect.Height * fraction);
        var width = Math.Max(1, rect.Width - (insetX * 2));
        var height = Math.Max(1, rect.Height - (insetY * 2));
        return new Rectangle(rect.X + insetX, rect.Y + insetY, width, height);
    }

    private static Rectangle RelativeRectangle(Rectangle rect, double x, double y, double width, double height)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var left = rect.Left + (int)Math.Round(rect.Width * x);
        var top = rect.Top + (int)Math.Round(rect.Height * y);
        var right = rect.Left + (int)Math.Round(rect.Width * (x + width));
        var bottom = rect.Top + (int)Math.Round(rect.Height * (y + height));
        return Rectangle.Intersect(
            Rectangle.FromLTRB(left, top, right, bottom),
            rect
        );
    }

    private static Rectangle ExpandRectangle(
        Bitmap source,
        Rectangle rect,
        double leftFraction,
        double topFraction,
        double rightFraction,
        double bottomFraction
    )
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var left = rect.Left - (int)Math.Round(rect.Width * leftFraction);
        var top = rect.Top - (int)Math.Round(rect.Height * topFraction);
        var right = rect.Right + (int)Math.Round(rect.Width * rightFraction);
        var bottom = rect.Bottom + (int)Math.Round(rect.Height * bottomFraction);
        return Rectangle.Intersect(
            Rectangle.FromLTRB(left, top, right, bottom),
            new Rectangle(0, 0, source.Width, source.Height)
        );
    }

    private static CenterMetrics MeasureSaturation(Bitmap bitmap, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return new CenterMetrics(0.0, 0.0, 0.0);
        }

        var saturationSum = 0.0;
        var colorPixels = 0;
        var lumaSum = 0.0;
        var totalPixels = rect.Width * rect.Height;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var saturation = ComputeSaturation(pixel);
                saturationSum += saturation;
                lumaSum += (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                if (saturation >= RosterSlotColorPixelSaturationThreshold)
                {
                    colorPixels += 1;
                }
            }
        }

        return totalPixels <= 0
            ? new CenterMetrics(0.0, 0.0, 0.0)
            : new CenterMetrics(
                saturationSum / totalPixels,
                (double)colorPixels / totalPixels,
                lumaSum / totalPixels
            );
    }

    private static double ComputeSaturation(Color pixel)
    {
        var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
        if (max <= 0)
        {
            return 0.0;
        }

        var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
        return 255.0 * (max - min) / max;
    }

    private static bool LooksUnavailableVisualStyle(CenterMetrics metrics)
    {
        return metrics.ColorPixelFraction <= RosterSlotUnavailableMaxColorPixelFraction &&
               metrics.MeanSaturation <= RosterSlotUnavailableMaxMeanSaturation &&
               metrics.MeanLuma >= RosterSlotUnavailableMinMeanLuma;
    }

    private static bool LooksUnavailableByLockBadge(bool lockBadgeDetected, CenterMetrics metrics)
    {
        return lockBadgeDetected &&
               metrics.ColorPixelFraction <= RosterSlotLockBadgeMaxColorPixelFraction &&
               metrics.MeanSaturation <= RosterSlotLockBadgeMaxMeanSaturation;
    }

    private static bool DetectLockBadge(Bitmap bitmap, Rectangle slotRect)
    {
        var expandedRect = ExpandRectangle(
            bitmap,
            slotRect,
            RosterSlotLockExpandLeftFraction,
            RosterSlotLockExpandTopFraction,
            RosterSlotLockExpandRightFraction,
            RosterSlotLockExpandBottomFraction
        );
        var searchRect = RelativeRectangle(
            expandedRect,
            RosterSlotLockSearchLeftFraction,
            RosterSlotLockSearchTopFraction,
            RosterSlotLockSearchWidthFraction,
            RosterSlotLockSearchHeightFraction
        );
        if (searchRect.Width <= 0 || searchRect.Height <= 0)
        {
            return false;
        }

        var width = searchRect.Width;
        var height = searchRect.Height;
        var dark = new bool[height, width];
        var visited = new bool[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(searchRect.Left + x, searchRect.Top + y);
                var luma = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                dark[y, x] = luma < RosterSlotLockDarkThreshold;
            }
        }

        var minArea = Math.Max(16, (int)Math.Round(width * height * RosterSlotLockMinAreaFraction));
        var maxArea = Math.Max(minArea, (int)Math.Round(width * height * RosterSlotLockMaxAreaFraction));
        var offsets = new (int X, int Y)[]
        {
            (-1, -1), (0, -1), (1, -1),
            (-1, 0),            (1, 0),
            (-1, 1),  (0, 1),  (1, 1),
        };

        for (var startY = 0; startY < height; startY++)
        {
            for (var startX = 0; startX < width; startX++)
            {
                if (!dark[startY, startX] || visited[startY, startX])
                {
                    continue;
                }

                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((startX, startY));
                visited[startY, startX] = true;

                var area = 0;
                var minX = startX;
                var maxX = startX;
                var minY = startY;
                var maxY = startY;
                var touchesEdge = false;

                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    area += 1;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                    {
                        touchesEdge = true;
                    }

                    foreach (var (offsetX, offsetY) in offsets)
                    {
                        var nextX = x + offsetX;
                        var nextY = y + offsetY;
                        if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                        {
                            continue;
                        }
                        if (!dark[nextY, nextX] || visited[nextY, nextX])
                        {
                            continue;
                        }

                        visited[nextY, nextX] = true;
                        queue.Enqueue((nextX, nextY));
                    }
                }

                if (touchesEdge || area < minArea || area > maxArea)
                {
                    continue;
                }

                var componentWidth = maxX - minX + 1;
                var componentHeight = maxY - minY + 1;
                if (componentWidth <= 0 || componentHeight <= 0)
                {
                    continue;
                }

                var fillRatio = (double)area / (componentWidth * componentHeight);
                var aspectRatio = (double)componentWidth / componentHeight;
                var centerXFraction = (minX + (componentWidth / 2.0)) / width;
                if (fillRatio >= RosterSlotLockMinFillRatio &&
                    aspectRatio >= RosterSlotLockMinAspectRatio &&
                    aspectRatio <= RosterSlotLockMaxAspectRatio &&
                    centerXFraction >= RosterSlotLockMinCenterXFraction)
                {
                    return true;
                }
            }
        }

        return false;
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

    private sealed record CenterMetrics(double MeanSaturation, double ColorPixelFraction, double MeanLuma);
}

internal sealed record RosterSlotInspection(
    int AgentSlotIndex,
    bool LooksOwned,
    bool LockBadgeDetected,
    bool LooksUnavailableVisualStyle,
    bool LooksUnavailableByLockBadge,
    double MeanCenterSaturation,
    double CenterColorPixelFraction,
    double MeanCenterLuma
);
