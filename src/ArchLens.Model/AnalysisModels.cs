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
    SourceLocation? Location = null,
    int CognitiveComplexity = 0,
    int MaxMethodCognitiveComplexity = 0,
    int Lcom4 = 0,
    IReadOnlyDictionary<string, double>? CustomMetrics = null);

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

public sealed record DiRegistration(
    string ServiceTypeId,
    string ImplementationTypeId,
    ServiceLifetimeKind Lifetime,
    string? RegisteredByTypeId = null,
    SourceLocation? Location = null,
    string? ProjectName = null);

public sealed record CaptiveDependencyIssue(
    string ConsumerTypeId,
    ServiceLifetimeKind ConsumerLifetime,
    string DependencyTypeId,
    ServiceLifetimeKind DependencyLifetime,
    string ResolvedImplementationTypeId,
    string DependencyPath,
    SourceLocation? Location = null);

public sealed record SingleImplementationInterfaceInfo(
    string InterfaceTypeId,
    string ImplementationTypeId,
    bool IsDiRegistered,
    int ConsumerCount,
    SourceLocation? Location = null);

public sealed record DiAnalysisReport(
    int RegistrationCount,
    int CaptiveDependencyCount,
    int SingleImplementationCount,
    IReadOnlyList<DiRegistration> Registrations,
    IReadOnlyList<CaptiveDependencyIssue> CaptiveDependencies,
    IReadOnlyList<SingleImplementationInterfaceInfo> SingleImplementationInterfaces);

public sealed record DeadCodeCandidate(
    string NodeId,
    string FullName,
    NodeKind Kind,
    string ProjectName,
    AccessibilityKind Accessibility,
    string Reason,
    SourceLocation? Location = null);

public sealed record DeadCodeReport(
    NodeKind Scope,
    int TotalEvaluated,
    int EntryPointCount,
    int UnreferencedCount,
    bool Truncated,
    IReadOnlyList<string> ActiveDetectors,
    IReadOnlyList<DeadCodeCandidate> Candidates);

public sealed record AnalyzerDiagnostic(
    string Id,
    string Title,
    string Category,
    RuleSeverity Severity,
    string Message,
    SourceLocation? Location = null,
    string? ProjectName = null);

public sealed record AnalyzerDiagnosticsReport(
    int TotalDiagnostics,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    bool Truncated,
    IReadOnlyList<string> Categories,
    IReadOnlyList<AnalyzerDiagnostic> Diagnostics);
