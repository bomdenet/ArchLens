namespace ArchLens.Model;

public sealed record CodeNode(
    string Id,
    string Name,
    string FullName,
    NodeKind Kind,
    string ProjectName,
    string? Namespace = null,
    TypeKind? TypeKind = null,
    AccessibilityKind Accessibility = AccessibilityKind.Internal,
    bool IsAbstract = false,
    bool IsStatic = false,
    SourceLocation? Location = null,
    int LineCount = 0,
    int CyclomaticComplexity = 0,
    int ConstructorParameterCount = 0,
    int MemberCount = 0,
    IReadOnlyDictionary<string, string>? Metadata = null,
    int CognitiveComplexity = 0,
    int Lcom4 = 0,
    string? ContainingTypeId = null);

public sealed record DependencyEdge(
    string FromId,
    string ToId,
    DependencyKind Kind,
    SourceLocation? Location = null,
    int Weight = 1);

public sealed record ProjectInfo(
    string Name,
    string FilePath,
    string? TargetFramework,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    int TypeCount,
    int DocumentCount);

public sealed record ExtractionDiagnostic(
    ExtractionDiagnosticSeverity Severity,
    string Message,
    string? Source = null);

public sealed record GraphLoadStatus(
    string Backend,
    int ProjectsExpected,
    int ProjectsLoaded,
    int ProjectsDegraded,
    bool Degraded,
    bool Approximate = false,
    string? Note = null,
    IReadOnlyList<string>? DegradedProjects = null);
