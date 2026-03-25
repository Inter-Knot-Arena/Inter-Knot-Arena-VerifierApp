namespace VerifierApp.Core.Services;

internal enum LayoutProfileKind
{
    Unknown = 0,
    Wide16x9 = 1,
    Wide16x10 = 2,
}

internal readonly record struct LayoutPoint(double X, double Y);

internal readonly record struct LayoutRect(double X, double Y, double Width, double Height);

internal readonly record struct LayoutBounds(double Left, double Top, double Right, double Bottom);

internal readonly record struct AgentGridPoint(int AgentSlotIndex, double X, double Y);

internal readonly record struct DiskSlotPoint(int SlotIndex, double X, double Y);

internal readonly record struct RosterSlotBox(int AgentSlotIndex, double X, double Y, double Width, double Height);

internal sealed record LayoutProfile(
    LayoutProfileKind Kind,
    LayoutPoint HomeAgentsClickPoint,
    LayoutPoint BaseButtonPoint,
    LayoutPoint EquipmentButtonPoint,
    LayoutPoint AmplifierClickPoint,
    LayoutRect HomeAgentsTemplateSize,
    LayoutBounds HomeAgentsSearchBounds,
    LayoutRect BaseStatsTabBox,
    LayoutRect EquipmentTabBox,
    IReadOnlyList<AgentGridPoint> VisibleAgentGridPoints,
    IReadOnlyList<DiskSlotPoint> DiskSlotPoints,
    IReadOnlyList<RosterSlotBox> VisibleRosterSlotBoxes
);

internal static class LiveLayoutProfiles
{
    private const double Aspect16x9 = 16.0 / 9.0;
    private const double Aspect16x10 = 16.0 / 10.0;
    private const double AspectTolerance = 0.07;
    private const double ReferenceHeight16x9 = 1440.0;
    private const double ReferenceHeight16x10 = 1600.0;
    private static readonly double VerticalScale16x10 = ReferenceHeight16x9 / ReferenceHeight16x10;

    private static readonly LayoutProfile Wide16x9 = new(
        Kind: LayoutProfileKind.Wide16x9,
        HomeAgentsClickPoint: new LayoutPoint(0.660, 0.905),
        BaseButtonPoint: new LayoutPoint(0.606, 0.776),
        EquipmentButtonPoint: new LayoutPoint(0.823, 0.907),
        AmplifierClickPoint: new LayoutPoint(0.694, 0.496),
        HomeAgentsTemplateSize: new LayoutRect(0.0, 0.0, 280.0 / 2560.0, 240.0 / 1440.0),
        HomeAgentsSearchBounds: new LayoutBounds(0.53, 0.78, 0.77, 0.98),
        BaseStatsTabBox: new LayoutRect(0.53, 0.88, 0.17, 0.10),
        EquipmentTabBox: new LayoutRect(0.79, 0.88, 0.17, 0.10),
        VisibleAgentGridPoints:
        [
            new AgentGridPoint(1, 0.597, 0.135),
            new AgentGridPoint(2, 0.688, 0.197),
            new AgentGridPoint(3, 0.814, 0.124),
        ],
        DiskSlotPoints:
        [
            new DiskSlotPoint(1, 0.632, 0.284),
            new DiskSlotPoint(2, 0.571, 0.487),
            new DiskSlotPoint(3, 0.632, 0.691),
            new DiskSlotPoint(4, 0.825, 0.691),
            new DiskSlotPoint(5, 0.866, 0.487),
            new DiskSlotPoint(6, 0.825, 0.284),
        ],
        VisibleRosterSlotBoxes:
        [
            new RosterSlotBox(1, 0.532, 0.015, 0.135, 0.235),
            new RosterSlotBox(2, 0.635, 0.045, 0.120, 0.245),
            new RosterSlotBox(3, 0.747, 0.000, 0.130, 0.235),
        ]
    );

    private static readonly LayoutProfile Wide16x10 = new(
        Kind: LayoutProfileKind.Wide16x10,
        HomeAgentsClickPoint: ConvertPoint(Wide16x9.HomeAgentsClickPoint, VerticalAnchor.Bottom),
        BaseButtonPoint: ConvertPoint(Wide16x9.BaseButtonPoint, VerticalAnchor.Bottom),
        EquipmentButtonPoint: ConvertPoint(Wide16x9.EquipmentButtonPoint, VerticalAnchor.Bottom),
        AmplifierClickPoint: ConvertPoint(Wide16x9.AmplifierClickPoint, VerticalAnchor.Center),
        HomeAgentsTemplateSize: ConvertRect(
            Wide16x9.HomeAgentsTemplateSize,
            VerticalAnchor.Bottom
        ),
        HomeAgentsSearchBounds: ConvertBounds(
            Wide16x9.HomeAgentsSearchBounds,
            VerticalAnchor.Bottom
        ),
        BaseStatsTabBox: ConvertRect(Wide16x9.BaseStatsTabBox, VerticalAnchor.Bottom),
        EquipmentTabBox: ConvertRect(Wide16x9.EquipmentTabBox, VerticalAnchor.Bottom),
        VisibleAgentGridPoints: Wide16x9.VisibleAgentGridPoints
            .Select(point => new AgentGridPoint(
                point.AgentSlotIndex,
                point.X,
                ConvertY(point.Y, VerticalAnchor.Top)
            ))
            .ToArray(),
        DiskSlotPoints: Wide16x9.DiskSlotPoints
            .Select(point => new DiskSlotPoint(
                point.SlotIndex,
                point.X,
                ConvertY(point.Y, VerticalAnchor.Center)
            ))
            .ToArray(),
        VisibleRosterSlotBoxes: Wide16x9.VisibleRosterSlotBoxes
            .Select(box =>
            {
                var rect = ConvertRect(
                    new LayoutRect(box.X, box.Y, box.Width, box.Height),
                    VerticalAnchor.Top
                );
                return new RosterSlotBox(box.AgentSlotIndex, rect.X, rect.Y, rect.Width, rect.Height);
            })
            .ToArray()
    );

    public static LayoutInspection Inspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return new LayoutInspection(0, 0, 0.0, false, LayoutProfileKind.Unknown);
        }

        var aspectRatio = width / (double)height;
        var diff16x9 = Math.Abs(aspectRatio - Aspect16x9);
        var diff16x10 = Math.Abs(aspectRatio - Aspect16x10);
        var profileKind = diff16x9 <= diff16x10 ? LayoutProfileKind.Wide16x9 : LayoutProfileKind.Wide16x10;
        var supported = Math.Min(diff16x9, diff16x10) <= AspectTolerance;
        if (!supported)
        {
            profileKind = LayoutProfileKind.Unknown;
        }

        return new LayoutInspection(
            width,
            height,
            Math.Round(aspectRatio, 4),
            supported,
            profileKind
        );
    }

    public static LayoutProfile Get(LayoutProfileKind kind) =>
        kind switch
        {
            LayoutProfileKind.Wide16x10 => Wide16x10,
            _ => Wide16x9,
        };

    private static LayoutPoint ConvertPoint(LayoutPoint point, VerticalAnchor anchor) =>
        new(point.X, ConvertY(point.Y, anchor));

    private static LayoutRect ConvertRect(LayoutRect rect, VerticalAnchor anchor)
    {
        var scaledHeight = rect.Height * VerticalScale16x10;
        double top = anchor switch
        {
            VerticalAnchor.Top => ConvertTopEdge(rect.Y),
            VerticalAnchor.Bottom => ConvertBottomEdge(rect.Y + rect.Height) - scaledHeight,
            VerticalAnchor.Center => ConvertCenter(rect.Y + (rect.Height / 2.0)) - (scaledHeight / 2.0),
            _ => rect.Y,
        };
        return new LayoutRect(rect.X, top, rect.Width, scaledHeight);
    }

    private static LayoutBounds ConvertBounds(LayoutBounds bounds, VerticalAnchor anchor)
    {
        var rect = ConvertRect(
            new LayoutRect(
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top
            ),
            anchor
        );
        return new LayoutBounds(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
    }

    private static double ConvertY(double value, VerticalAnchor anchor) =>
        anchor switch
        {
            VerticalAnchor.Top => ConvertTopEdge(value),
            VerticalAnchor.Bottom => ConvertBottomEdge(value),
            VerticalAnchor.Center => ConvertCenter(value),
            _ => value,
        };

    private static double ConvertTopEdge(double value) => (value * ReferenceHeight16x9) / ReferenceHeight16x10;

    private static double ConvertBottomEdge(double value)
    {
        var bottomGapPixels = (1.0 - value) * ReferenceHeight16x9;
        return (ReferenceHeight16x10 - bottomGapPixels) / ReferenceHeight16x10;
    }

    private static double ConvertCenter(double value)
    {
        var offsetPixels = (value - 0.5) * ReferenceHeight16x9;
        return ((ReferenceHeight16x10 / 2.0) + offsetPixels) / ReferenceHeight16x10;
    }

    private enum VerticalAnchor
    {
        Top,
        Center,
        Bottom,
    }
}
