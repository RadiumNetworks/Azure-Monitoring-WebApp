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

        MergeSharedTargets(nodes, edges);

        return new AlertGraphLayoutResult(
            Math.Max(960, columnCount * cellWidth),
            Math.Max(560, rowCount * cellHeight),
            nodes,
            edges);
    }

    private static void MergeSharedTargets(List<AlertGraphNode> nodes, List<AlertGraphEdge> edges)
    {
        var targetGroups = nodes
            .Where(node => node.Layer == AlertGraphLayer.Target)
            .GroupBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var positions = new Dictionary<(double X, double Y), (double X, double Y)>();
        var mergedTargets = new List<AlertGraphNode>(targetGroups.Length);

        foreach (var group in targetGroups)
        {
            var targetNodes = group.ToArray();
            var canonical = targetNodes[0] with
            {
                Count = targetNodes.Sum(node => node.Count),
                HistoryCount = targetNodes.Sum(node => node.HistoryCount)
            };
            mergedTargets.Add(canonical);

            foreach (var target in targetNodes)
            {
                positions[(target.X, target.Y)] = (canonical.X, canonical.Y);
            }
        }

        nodes.RemoveAll(node => node.Layer == AlertGraphLayer.Target);
        nodes.AddRange(mergedTargets);

        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            var edge = edges[edgeIndex];
            if (positions.TryGetValue((edge.X2, edge.Y2), out var targetPosition))
            {
                edges[edgeIndex] = edge with { X2 = targetPosition.X, Y2 = targetPosition.Y };
            }
        }
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

        var leaves = root.Children
            .SelectMany(middle => middle.Children)
            .GroupBy(leaf => leaf.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new
            {
                Name = group.Key,
                Layer = group.First().Layer,
                Count = group.Sum(leaf => leaf.Count),
                HistoryCount = group.Sum(leaf => leaf.HistoryCount),
                Index = index
            })
            .ToArray();
        var leafCount = leaves.Length;
        var middleCount = root.Children.Count;
        var innerRadius = Math.Max(MinimumInnerRadius, RadiusForNodeCount(middleCount));
        var outerRadius = Math.Max(innerRadius + RingSpacing, RadiusForNodeCount(leafCount));
        var leafNodes = leaves.ToDictionary(
            leaf => leaf.Name,
            leaf => CreateNode(
                $"{rootId}-leaf-{leaf.Index}",
                leaf.Name,
                leaf.Layer,
                leaf.Count,
                leaf.HistoryCount,
                0,
                0,
                outerRadius,
                AngleForSlot(leaf.Index + 0.5, leafCount)),
            StringComparer.OrdinalIgnoreCase);

        for (var middleIndex = 0; middleIndex < middleCount; middleIndex++)
        {
            var middle = root.Children[middleIndex];
            var middleAngle = AngleForSlot(middleIndex + 0.5, middleCount);
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

            foreach (var leaf in middle.Children
                .DistinctBy(leaf => leaf.Name, StringComparer.OrdinalIgnoreCase))
            {
                edges.Add(AlertGraphEdge.Between(middleNode, leafNodes[leaf.Name]));
            }
        }

        nodes.AddRange(leaves.Select(leaf => leafNodes[leaf.Name]));

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

