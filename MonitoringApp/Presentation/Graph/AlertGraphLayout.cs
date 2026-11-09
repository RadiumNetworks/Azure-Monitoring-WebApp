namespace MonitoringApp;

/// <summary>
/// Calculates node and edge coordinates for the alert hierarchy graph. Root groups are arranged as radial clusters within a responsive grid.
/// </summary>
public static class AlertGraphLayout
{
    public const double NodeWidth = 190;
    public const double NodeHeight = 50;

    private const double MinimumInnerRadius = 220;
    private const double RingSpacing = 220;
    private const double NodeArcSpacing = 230;
    private const double ClusterPadding = 70;
    private const double SegmentGap = 36;
    private const double SegmentPadding = 42;
    private const double SegmentHeaderHeight = 50;
    private const double PreferredViewportWidth = 1200;
    private const double PreferredViewportHeight = 650;

    /// <summary>
    /// Builds a complete graph layout from hierarchy roots, including viewport size, positioned nodes, and edges. An empty hierarchy returns a default-sized empty layout.
    /// </summary>
    public static AlertGraphLayoutResult Build(
        IReadOnlyList<AlertGraphHierarchyNode> roots,
        IReadOnlyDictionary<string, string>? rootNamespaces = null)
    {
        if (roots.Count == 0)
        {
            return new AlertGraphLayoutResult(960, 560, [], [], []);
        }

        if (rootNamespaces is not null && rootNamespaces.Count > 0)
        {
            return BuildSegmented(roots, rootNamespaces);
        }

        var clusters = roots
            .Select((root, index) => BuildCluster(root, index, string.Empty))
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
            edges,
            []);
    }

    private static AlertGraphLayoutResult BuildSegmented(
        IReadOnlyList<AlertGraphHierarchyNode> roots,
        IReadOnlyDictionary<string, string> rootNamespaces)
    {
        var groupedClusters = roots
            .Select((root, index) => new
            {
                Namespace = rootNamespaces.GetValueOrDefault(root.Name) ?? root.Name,
                Cluster = BuildCluster(root, index, rootNamespaces.GetValueOrDefault(root.Name) ?? root.Name)
            })
            .GroupBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allClusters = groupedClusters.SelectMany(group => group.Select(item => item.Cluster)).ToArray();
        var cellWidth = allClusters.Max(cluster => cluster.Width) + 2 * ClusterPadding;
        var cellHeight = allClusters.Max(cluster => cluster.Height) + 2 * ClusterPadding;
        var plans = groupedClusters.Select(group =>
        {
            var clusters = group.Select(item => item.Cluster).ToArray();
            var columns = GetColumnCount(clusters.Length, cellWidth, cellHeight);
            var rows = (int)Math.Ceiling((double)clusters.Length / columns);
            return new SegmentPlan(
                group.Key,
                clusters,
                columns,
                2 * SegmentPadding + columns * cellWidth,
                SegmentHeaderHeight + SegmentPadding + rows * cellHeight);
        }).ToArray();
        var segmentCellWidth = plans.Max(plan => plan.Width) + SegmentGap;
        var segmentCellHeight = plans.Max(plan => plan.Height) + SegmentGap;
        var segmentColumns = GetColumnCount(plans.Length, segmentCellWidth, segmentCellHeight);
        var segmentRows = (int)Math.Ceiling((double)plans.Length / segmentColumns);
        var nodes = new List<AlertGraphNode>();
        var edges = new List<AlertGraphEdge>();
        var segments = new List<AlertGraphSegment>();

        for (var segmentIndex = 0; segmentIndex < plans.Length; segmentIndex++)
        {
            var plan = plans[segmentIndex];
            var segmentX = SegmentGap + segmentCellWidth * (segmentIndex % segmentColumns);
            var segmentY = SegmentGap + segmentCellHeight * (segmentIndex / segmentColumns);
            segments.Add(new AlertGraphSegment(
                plan.Label,
                segmentX,
                segmentY,
                plan.Width,
                plan.Height));

            for (var clusterIndex = 0; clusterIndex < plan.Clusters.Count; clusterIndex++)
            {
                var cluster = plan.Clusters[clusterIndex];
                var centerX = segmentX + SegmentPadding + cellWidth * (clusterIndex % plan.Columns + 0.5);
                var centerY = segmentY + SegmentHeaderHeight + cellHeight * (clusterIndex / plan.Columns + 0.5);
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
        }

        MergeSharedTargets(nodes, edges);
        return new AlertGraphLayoutResult(
            Math.Max(960, SegmentGap + segmentColumns * segmentCellWidth),
            Math.Max(560, SegmentGap + segmentRows * segmentCellHeight),
            nodes,
            edges,
            segments);
    }

    /// <summary>
    /// Combines references to the same domain-scoped target across root clusters and redirects incoming edges.
    /// </summary>
    private static void MergeSharedTargets(List<AlertGraphNode> nodes, List<AlertGraphEdge> edges)
    {
        var targetGroups = nodes
            .Where(node => node.Layer == AlertGraphLayer.Target)
            .GroupBy(node => $"{node.Domain}\u001f{node.Label}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var positions = new Dictionary<(double X, double Y), (double X, double Y)>();
        var mergedTargets = new List<AlertGraphNode>(targetGroups.Length);

        foreach (var group in targetGroups)
        {
            var targetNodes = group.ToArray();
            var canonical = targetNodes[0] with
            {
                Count = targetNodes.Sum(node => node.Count),
                HistoryCount = targetNodes.Sum(node => node.HistoryCount),
                LastHeartbeatAt = targetNodes
                    .Where(node => node.LastHeartbeatAt.HasValue)
                    .Select(node => node.LastHeartbeatAt)
                    .Max(),
                Maintenance = targetNodes.All(node => node.Maintenance),
                HasServiceHealthAlert = targetNodes.Any(node => node.HasServiceHealthAlert)
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

    /// <summary>
    /// Creates one radial cluster with a root at the center, middle nodes on the inner ring, and targets on the outer ring. Duplicate leaf names within the cluster share a node.
    /// </summary>
    private static GraphCluster BuildCluster(
        AlertGraphHierarchyNode root,
        int rootIndex,
        string dnsNamespace)
    {
        var nodes = new List<AlertGraphNode>();
        var edges = new List<AlertGraphEdge>();
        var rootId = $"root-{rootIndex}";
        var rootNode = new AlertGraphNode(
            rootId,
            root.Name,
            root.Layer,
            root.Level,
            root.Count,
            root.HistoryCount,
            0,
            0,
            root.LastHeartbeatAt,
            dnsNamespace,
            root.Maintenance,
            root.SubscriptionId,
            root.Domain,
            root.HasServiceHealthAlert);
        nodes.Add(rootNode);

        string SubscriptionFor(AlertGraphHierarchyNode leaf) =>
            !string.IsNullOrWhiteSpace(leaf.SubscriptionId) ? leaf.SubscriptionId :
                root.Layer == AlertGraphLayer.Subscription ? root.Name : string.Empty;
        string LeafKey(AlertGraphHierarchyNode leaf) =>
            leaf.Layer == AlertGraphLayer.Target
                ? $"{leaf.Domain}\u001f{leaf.Name}"
                : leaf.Name;

        var leaves = root.Children
            .SelectMany((middle, middleIndex) =>
                middle.Children.Select(leaf => new { Leaf = leaf, MiddleIndex = middleIndex }))
            .GroupBy(item => LeafKey(item.Leaf), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Average(item => item.MiddleIndex))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new
            {
                Key = group.Key,
                Name = group.First().Leaf.Name,
                SubscriptionId = group.First().Leaf.Layer == AlertGraphLayer.Target
                    ? SubscriptionFor(group.First().Leaf)
                    : string.Empty,
                Domain = group.First().Leaf.Layer == AlertGraphLayer.Target
                    ? group.First().Leaf.Domain
                    : string.Empty,
                Layer = group.First().Leaf.Layer,
                Level = group.First().Leaf.Level,
                Count = group.Sum(item => item.Leaf.Count),
                HistoryCount = group.Sum(item => item.Leaf.HistoryCount),
                LastHeartbeatAt = group
                    .Where(item => item.Leaf.LastHeartbeatAt.HasValue)
                    .Select(item => item.Leaf.LastHeartbeatAt)
                    .Max(),
                Maintenance = group.All(item => item.Leaf.Maintenance),
                HasServiceHealthAlert = group.Any(item => item.Leaf.HasServiceHealthAlert),
                Index = index
            })
            .ToArray();
        var leavesByKey = leaves.ToDictionary(leaf => leaf.Key, StringComparer.OrdinalIgnoreCase);
        var leafCount = leaves.Length;
        var middleCount = root.Children.Count;
        var innerRadius = Math.Max(MinimumInnerRadius, RadiusForNodeCount(middleCount));
        var outerRadius = Math.Max(innerRadius + RingSpacing, RadiusForNodeCount(leafCount));
        var leafNodes = leaves.ToDictionary(
            leaf => leaf.Key,
            leaf => CreateNode(
                $"{rootId}-leaf-{leaf.Index}",
                leaf.Name,
                leaf.Layer,
                leaf.Level,
                leaf.Count,
                leaf.HistoryCount,
                0,
                0,
                outerRadius,
                AngleForSlot(leaf.Index + 0.5, leafCount),
                leaf.LastHeartbeatAt,
                dnsNamespace,
                leaf.Maintenance,
                leaf.SubscriptionId,
                leaf.Domain,
                leaf.HasServiceHealthAlert),
            StringComparer.OrdinalIgnoreCase);
        var connectedLeafIndexesByMiddle = root.Children
            .Select(middle => middle.Children
                .DistinctBy(LeafKey, StringComparer.OrdinalIgnoreCase)
                .Select(leaf => leavesByKey[LeafKey(leaf)].Index)
                .Order()
                .ToArray())
            .ToArray();
        var sharedTargetSets = connectedLeafIndexesByMiddle
            .Where(indexes => indexes.Length > 0)
            .GroupBy(indexes => string.Join('\u001f', indexes))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var preferredMiddleAngles = connectedLeafIndexesByMiddle
            .Select((connectedLeafIndexes, middleIndex) =>
            {
                var targetSetKey = string.Join('\u001f', connectedLeafIndexes);
                return connectedLeafIndexes.Length == 0 || sharedTargetSets.Contains(targetSetKey)
                    ? AngleForSlot(middleIndex + 0.5, middleCount)
                    : AngleForSlot(connectedLeafIndexes.Average() + 0.5, leafCount);
            })
            .ToArray();
        var middleAngles = SpreadAngles(preferredMiddleAngles);

        for (var middleIndex = 0; middleIndex < middleCount; middleIndex++)
        {
            var middle = root.Children[middleIndex];
            var middleNode = CreateNode(
                $"{rootId}-middle-{middleIndex}",
                middle.Name,
                middle.Layer,
                middle.Level,
                middle.Count,
                middle.HistoryCount,
                0,
                0,
                innerRadius,
                middleAngles[middleIndex],
                middle.LastHeartbeatAt,
                dnsNamespace,
                middle.Maintenance,
                domain: middle.Domain,
                hasServiceHealthAlert: middle.HasServiceHealthAlert);
            nodes.Add(middleNode);
            edges.Add(AlertGraphEdge.Between(rootNode, middleNode));

            foreach (var leaf in middle.Children
                .DistinctBy(LeafKey, StringComparer.OrdinalIgnoreCase))
            {
                edges.Add(AlertGraphEdge.Between(middleNode, leafNodes[LeafKey(leaf)]));
            }
        }

        nodes.AddRange(leaves.Select(leaf => leafNodes[leaf.Key]));

        return new GraphCluster(nodes, edges);
    }

    /// <summary>
    /// Creates a positioned graph node from a center point, radius, and angle. Cartesian coordinates are calculated for the final node record.
    /// </summary>
    private static AlertGraphNode CreateNode(
        string id,
        string label,
        AlertGraphLayer layer,
        int level,
        int count,
        int historyCount,
        double centerX,
        double centerY,
        double radius,
        double angle,
        DateTimeOffset? lastHeartbeatAt,
        string dnsNamespace,
        bool maintenance,
        string subscriptionId = "",
        string domain = "",
        bool hasServiceHealthAlert = false) => new(
            id,
            label,
            layer,
            level,
            count,
            historyCount,
            centerX + radius * Math.Cos(angle),
            centerY + radius * Math.Sin(angle),
            lastHeartbeatAt,
            dnsNamespace,
            maintenance,
            subscriptionId,
            domain,
            hasServiceHealthAlert);

    /// <summary>
    /// Calculates the ring radius needed to maintain the preferred arc spacing for a node count. Negative counts are treated as zero.
    /// </summary>
    private static double RadiusForNodeCount(int count) =>
        Math.Max(0, count) * NodeArcSpacing / (2 * Math.PI);

    /// <summary>
    /// Converts a circular slot into an angle, starting at the top of the ring. A zero slot count is treated as one to avoid division by zero.
    /// </summary>
    private static double AngleForSlot(double slot, int slotCount) =>
        -Math.PI / 2 + 2 * Math.PI * slot / Math.Max(1, slotCount);

    private static double[] SpreadAngles(IReadOnlyList<double> preferredAngles)
    {
        if (preferredAngles.Count <= 1)
        {
            return preferredAngles.ToArray();
        }

        var ordered = preferredAngles
            .Select((angle, index) => new { Angle = NormalizeAngle(angle), Index = index })
            .OrderBy(item => item.Angle)
            .ToArray();
        var spacing = 2 * Math.PI / ordered.Length;
        var sine = 0d;
        var cosine = 0d;
        for (var rank = 0; rank < ordered.Length; rank++)
        {
            var offset = ordered[rank].Angle - rank * spacing;
            sine += Math.Sin(offset);
            cosine += Math.Cos(offset);
        }

        var rotation = Math.Atan2(sine, cosine);
        var result = new double[ordered.Length];
        for (var rank = 0; rank < ordered.Length; rank++)
        {
            result[ordered[rank].Index] = rotation + rank * spacing;
        }
        return result;
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % (2 * Math.PI);
        return normalized < 0 ? normalized + 2 * Math.PI : normalized;
    }

    /// <summary>
    /// Chooses the cluster-grid column count that best fits the preferred viewport proportions. It balances horizontal and vertical scaling.
    /// </summary>
    private static int GetColumnCount(int clusterCount, double cellWidth, double cellHeight) =>
        Enumerable.Range(1, clusterCount)
            .MinBy(columns => Math.Max(
                columns * cellWidth / PreferredViewportWidth,
                Math.Ceiling((double)clusterCount / columns) * cellHeight / PreferredViewportHeight));

    /// <summary>
    /// Holds the local nodes, edges, and calculated bounds for one root cluster. The bounds are used to position clusters without overlap.
    /// </summary>
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

    private sealed record SegmentPlan(
        string Label,
        IReadOnlyList<GraphCluster> Clusters,
        int Columns,
        double Width,
        double Height);
}

/// <summary>
/// Contains the final graph viewport dimensions and positioned graph elements. The UI uses this result directly to render the SVG graph.
/// </summary>
public sealed record AlertGraphLayoutResult(
    double Width,
    double Height,
    IReadOnlyList<AlertGraphNode> Nodes,
    IReadOnlyList<AlertGraphEdge> Edges,
    IReadOnlyList<AlertGraphSegment> Segments);

public sealed record AlertGraphSegment(string Label, double X, double Y, double Width, double Height);

/// <summary>
/// Represents one positioned graph node with its display label, hierarchy layer, and alert counts. Coordinates identify the center of the node.
/// </summary>
public sealed record AlertGraphNode(
    string Id,
    string Label,
    AlertGraphLayer Layer,
    int Level,
    int Count,
    int HistoryCount,
    double X,
    double Y,
    DateTimeOffset? LastHeartbeatAt = null,
    string Namespace = "",
    bool Maintenance = false,
    string SubscriptionId = "",
    string Domain = "",
    bool HasServiceHealthAlert = false);

/// <summary>
/// Represents a straight graph edge between two coordinate pairs. Edges connect related hierarchy nodes in the SVG view.
/// </summary>
public sealed record AlertGraphEdge(double X1, double Y1, double X2, double Y2)
{
    /// <summary>
    /// Creates an edge between the center coordinates of two graph nodes. The start and end nodes themselves are not retained.
    /// </summary>
    public static AlertGraphEdge Between(AlertGraphNode start, AlertGraphNode end) =>
        new(start.X, start.Y, end.X, end.Y);
}
