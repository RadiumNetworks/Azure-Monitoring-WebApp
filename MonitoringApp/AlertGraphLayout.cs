namespace MonitoringApp;

public static class AlertGraphLayout
{
    public const double NodeWidth = 190;
    public const double NodeHeight = 50;

    private const double MinimumInnerRadius = 220;
    private const double RingSpacing = 220;
    private const double NodeArcSpacing = 230;
    private const double ClusterPadding = 70;
    private const double PreferredViewportWidth = 1200;
    private const double PreferredViewportHeight = 650;

    public static AlertGraphLayoutResult Build(IReadOnlyList<AlertGraphHierarchyNode> roots)
    {
        if (roots.Count == 0)
        {
            return new AlertGraphLayoutResult(960, 560, [], []);
        }

        var clusters = roots
            .Select((root, index) => BuildCluster(root, index))
            .ToArray();
        var cellWidth = clusters.Max(cluster => cluster.Width) + 2 * ClusterPadding;
        var cellHeight = clusters.Max(cluster => cluster.Height) + 2 * ClusterPadding;
        var columnCount = GetColumnCount(clusters.Length, cellWidth, cellHeight);
        var rowCount = (int)Math.Ceiling((double)roots.Count / columnCount);
        var nodes = new List<AlertGraphNode>();
        var edges = new List<AlertGraphEdge>();

        for (var clusterIndex = 0; clusterIndex < clusters.Length; clusterIndex++)
        {
            var cluster = clusters[clusterIndex];
            var centerX = cellWidth * (clusterIndex % columnCount + 0.5);
            var centerY = cellHeight * (clusterIndex / columnCount + 0.5);
            var offsetX = centerX - (cluster.MinimumX + cluster.MaximumX) / 2;
            var offsetY = centerY - (cluster.MinimumY + cluster.MaximumY) / 2;
            nodes.AddRange(cluster.Nodes.Select(node => node with { X = node.X + offsetX, Y = node.Y + offsetY }));
            edges.AddRange(cluster.Edges.Select(edge => edge with
            {
                X1 = edge.X1 + offsetX,
                Y1 = edge.Y1 + offsetY,
                X2 = edge.X2 + offsetX,
                Y2 = edge.Y2 + offsetY
            }));
        }

        return new AlertGraphLayoutResult(
            Math.Max(960, columnCount * cellWidth),
            Math.Max(560, rowCount * cellHeight),
            nodes,
            edges);
    }

    private static GraphCluster BuildCluster(
        AlertGraphHierarchyNode root,
        int rootIndex)
    {
        var nodes = new List<AlertGraphNode>();
        var edges = new List<AlertGraphEdge>();
        var rootId = $"root-{rootIndex}";
        var rootNode = new AlertGraphNode(
            rootId,
            root.Name,
            root.Layer,
            root.Count,
            root.HistoryCount,
            0,
            0);
        nodes.Add(rootNode);

        var leafCount = root.Children.Sum(middle => middle.Children.Count);
        var middleCount = root.Children.Count;
        var innerRadius = Math.Max(MinimumInnerRadius, RadiusForNodeCount(middleCount));
        var outerRadius = Math.Max(innerRadius + RingSpacing, RadiusForNodeCount(leafCount));
        var leafIndex = 0;

        for (var middleIndex = 0; middleIndex < middleCount; middleIndex++)
        {
            var middle = root.Children[middleIndex];
            var middleLeafCount = middle.Children.Count;
            var middleAngle = AngleForSlot(leafIndex + middleLeafCount / 2d, leafCount);
            var middleNode = CreateNode(
                $"{rootId}-middle-{middleIndex}",
                middle.Name,
                middle.Layer,
                middle.Count,
                middle.HistoryCount,
                0,
                0,
                innerRadius,
                middleAngle);
            nodes.Add(middleNode);
            edges.Add(AlertGraphEdge.Between(rootNode, middleNode));

            for (var localLeafIndex = 0; localLeafIndex < middleLeafCount; localLeafIndex++)
            {
                var leaf = middle.Children[localLeafIndex];
                var leafAngle = AngleForSlot(leafIndex + localLeafIndex + 0.5, leafCount);
                var leafNode = CreateNode(
                    $"{middleNode.Id}-leaf-{localLeafIndex}",
                    leaf.Name,
                    leaf.Layer,
                    leaf.Count,
                    leaf.HistoryCount,
                    0,
                    0,
                    outerRadius,
                    leafAngle);
                nodes.Add(leafNode);
                edges.Add(AlertGraphEdge.Between(middleNode, leafNode));
            }

            leafIndex += middleLeafCount;
        }

        return new GraphCluster(nodes, edges);
    }

    private static AlertGraphNode CreateNode(
        string id,
        string label,
        AlertGraphLayer layer,
        int count,
        int historyCount,
        double centerX,
        double centerY,
        double radius,
        double angle) => new(
            id,
            label,
            layer,
            count,
            historyCount,
            centerX + radius * Math.Cos(angle),
            centerY + radius * Math.Sin(angle));

    private static double RadiusForNodeCount(int count) =>
        Math.Max(0, count) * NodeArcSpacing / (2 * Math.PI);

    private static double AngleForSlot(double slot, int slotCount) =>
        -Math.PI / 2 + 2 * Math.PI * slot / Math.Max(1, slotCount);

    private static int GetColumnCount(int clusterCount, double cellWidth, double cellHeight) =>
        Enumerable.Range(1, clusterCount)
            .MinBy(columns => Math.Max(
                columns * cellWidth / PreferredViewportWidth,
                Math.Ceiling((double)clusterCount / columns) * cellHeight / PreferredViewportHeight));

    private sealed record GraphCluster(
        IReadOnlyList<AlertGraphNode> Nodes,
        IReadOnlyList<AlertGraphEdge> Edges)
    {
        public double MinimumX => Nodes.Min(node => node.X) - NodeWidth / 2;
        public double MaximumX => Nodes.Max(node => node.X) + NodeWidth / 2;
        public double MinimumY => Nodes.Min(node => node.Y) - NodeHeight / 2;
        public double MaximumY => Nodes.Max(node => node.Y) + NodeHeight / 2;
        public double Width => MaximumX - MinimumX;
        public double Height => MaximumY - MinimumY;
    }
}

public sealed record AlertGraphLayoutResult(
    double Width,
    double Height,
    IReadOnlyList<AlertGraphNode> Nodes,
    IReadOnlyList<AlertGraphEdge> Edges);

public sealed record AlertGraphNode(
    string Id,
    string Label,
    AlertGraphLayer Layer,
    int Count,
    int HistoryCount,
    double X,
    double Y);

public sealed record AlertGraphEdge(double X1, double Y1, double X2, double Y2)
{
    public static AlertGraphEdge Between(AlertGraphNode start, AlertGraphNode end) =>
        new(start.X, start.Y, end.X, end.Y);
}

