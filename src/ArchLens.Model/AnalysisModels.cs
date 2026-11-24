namespace ArchLens.Model;

public sealed record NodeMetrics(
    string NodeId,
    string Name,
    NodeKind Kind,
    int AfferentCoupling,
    int EfferentCoupling,
    double Instability,
    double? Abstractness,
    double? DistanceFromMainSequence,
    int CyclomaticComplexity,
    int MaxMethodComplexity,
    int LineCount,
    int ConstructorParameterCount,
    SourceLocation? Location = null);

public sealed record RuleViolation(
    string RuleId,
    string RuleName,
    RuleSeverity Severity,
    string Message,
    string? NodeId = null,
    string? TargetNodeId = null,
    SourceLocation? Location = null,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record CycleInfo(
    NodeKind Scope,
    IReadOnlyList<string> Nodes,
    int InternalEdgeCount);

public sealed record ImpactNode(
    string NodeId,
    string FullName,
    string ProjectName,
    int Distance,
    SourceLocation? Location);

public sealed record ImpactAnalysisResult(
    string TargetNodeId,
    string TargetFullName,
    int DirectDependentsCount,
    int TotalTransitiveDependentsCount,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<ImpactNode> ImpactedNodes);

public sealed record SnapshotDiffResult(
    string BaselineSnapshotId,
    string CurrentSnapshotId,
    int TypeCountDelta,
    int EdgeCountDelta,
    int CycleCountDelta,
    int ViolationCountDelta,
    IReadOnlyList<CycleInfo> AddedCycles,
    IReadOnlyList<CycleInfo> ResolvedCycles,
    IReadOnlyList<RuleViolation> NewViolations,
    IReadOnlyList<RuleViolation> ResolvedViolations);
