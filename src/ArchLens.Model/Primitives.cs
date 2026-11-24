namespace ArchLens.Model;

public sealed record SourceLocation(string FilePath, int Line, int Column = 1)
{
    public string ToShortString() => $"{FilePath}:{Line}";
}

public enum NodeKind
{
    Project,
    Namespace,
    Type,
    Method
}

public enum TypeKind
{
    Unknown,
    Class,
    Interface,
    Struct,
    Record,
    Enum,
    Delegate
}

public enum AccessibilityKind
{
    Private,
    PrivateProtected,
    Protected,
    Internal,
    ProtectedInternal,
    Public
}

public enum DependencyKind
{
    ProjectReference,
    Inherits,
    Implements,
    FieldOrProperty,
    Parameter,
    ReturnType,
    MethodCall,
    ObjectCreation,
    GenericArgument,
    Attribute,
    TypeReference
}

public enum RuleSeverity
{
    Info,
    Warning,
    Error
}

public enum ExtractionDiagnosticSeverity
{
    Info,
    Warning,
    Error
}
