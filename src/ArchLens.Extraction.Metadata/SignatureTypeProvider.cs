using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ArchLens.Extraction.Metadata;

/// <summary>
/// Decodes .NET metadata type signatures into canonical ArchLens type IDs without emitting side-effect duplicates.
/// Supports extracting top-level type vs generic type arguments separately for accurate edge classification.
/// </summary>
internal sealed class SignatureTypeProvider(Action<string, bool>? onTypeRef = null)
    : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var id = MetadataNameFormatter.FormatTypeDefinition(reader, handle);
        onTypeRef?.Invoke(id, false);
        return id;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var id = MetadataNameFormatter.FormatTypeReference(reader, handle);
        onTypeRef?.Invoke(id, false);
        return id;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        var spec = reader.GetTypeSpecification(handle);
        return spec.DecodeSignature(this, genericContext);
    }

    public string GetSZArrayType(string elementType) => $"{elementType}[]";

    public string GetArrayType(string elementType, ArrayShape shape) =>
        shape.Rank <= 1 ? $"{elementType}[]" : $"{elementType}[{new string(',', shape.Rank - 1)}]";

    public string GetByReferenceType(string elementType) => $"ref {elementType}";

    public string GetPointerType(string elementType) => $"{elementType}*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        // Emit generic argument refs if not already emitted with generic-arg flag
        foreach (var arg in typeArguments)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                onTypeRef?.Invoke(arg, true);
            }
        }

        // Handle nested types inside generic outer types (e.g. Outer<T>.Inner<U> -> Outer<int>.Inner<string>)
        return ReplaceGenericPlaceholders(genericType, typeArguments);
    }

    public string GetGenericTypeParameter(object? genericContext, int index) => $"T{index}";

    public string GetGenericMethodParameter(object? genericContext, int index) => $"M{index}";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

    private static string ReplaceGenericPlaceholders(string genericType, ImmutableArray<string> typeArguments)
    {
        var argIndex = 0;
        var result = new System.Text.StringBuilder(genericType.Length + 16);
        var i = 0;

        while (i < genericType.Length)
        {
            if (genericType[i] == '<')
            {
                var close = genericType.IndexOf('>', i + 1);
                if (close > i)
                {
                    var slotCount = MetadataNameFormatter.CountTopLevelArgs(genericType[(i + 1)..close]);
                    result.Append('<');
                    for (var s = 0; s < slotCount; s++)
                    {
                        if (s > 0) result.Append(", ");
                        result.Append(argIndex < typeArguments.Length ? typeArguments[argIndex++] : $"T{s}");
                    }
                    result.Append('>');
                    i = close + 1;
                    continue;
                }
            }

            result.Append(genericType[i]);
            i++;
        }

        // If genericType had no '<' slots at all, append <args>
        if (argIndex == 0 && typeArguments.Length > 0)
        {
            result.Append('<').Append(string.Join(", ", typeArguments)).Append('>');
        }

        return result.ToString();
    }
}

internal static class MetadataNameFormatter
{
    public static string FormatTypeDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var typeDef = reader.GetTypeDefinition(handle);
        var rawName = reader.GetString(typeDef.Name);
        var ns = reader.GetString(typeDef.Namespace);
        var formattedName = FormatGenericName(rawName, typeDef.GetGenericParameters(), reader);

        if (typeDef.IsNested)
        {
            var declaring = FormatTypeDefinition(reader, typeDef.GetDeclaringType());
            return $"{declaring}.{formattedName}";
        }

        return string.IsNullOrEmpty(ns) ? formattedName : $"{ns}.{formattedName}";
    }

    public static string FormatTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var typeRef = reader.GetTypeReference(handle);
        var rawName = reader.GetString(typeRef.Name);
        var ns = reader.GetString(typeRef.Namespace);
        var formattedName = FormatBacktickName(rawName);

        if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var declaring = FormatTypeReference(reader, (TypeReferenceHandle)typeRef.ResolutionScope);
            return $"{declaring}.{formattedName}";
        }

        return string.IsNullOrEmpty(ns) ? formattedName : $"{ns}.{formattedName}";
    }

    public static string FormatEntityHandleType(
        MetadataReader reader,
        EntityHandle handle,
        Action<string, bool>? onTypeRef = null) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FormatTypeDefinition(reader, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => FormatTypeReference(reader, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
            .DecodeSignature(new SignatureTypeProvider(onTypeRef), null),
        _ => string.Empty
    };

    /// <summary>
    /// Builds an O(1) lookup index mapping normalized generic structural keys (e.g. "MyApp.Repo`1" or "MyApp.Outer`1.Inner`1")
    /// to their canonical TypeDefinition ID in knownTypeIds.
    /// </summary>
    public static Dictionary<string, string> BuildOpenGenericIndex(IEnumerable<string> knownTypeIds)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in knownTypeIds)
        {
            if (id.Contains('<', StringComparison.Ordinal))
            {
                var key = ComputeGenericStructuralKey(id);
                index.TryAdd(key, id);
            }
        }
        return index;
    }

    /// <summary>
    /// Resolves a formatted type string (possibly with arrays, ref/out, pointers, or closed generic args)
    /// to its canonical known type definition ID in O(1) time.
    /// </summary>
    public static string ResolveToKnownTypeId(
        string formattedType,
        HashSet<string> knownTypeIds,
        Dictionary<string, string> openGenericIndex)
    {
        var cleaned = StripTypeDecorators(formattedType);
        if (knownTypeIds.Contains(cleaned))
        {
            return cleaned;
        }

        if (cleaned.Contains('<', StringComparison.Ordinal))
        {
            var key = ComputeGenericStructuralKey(cleaned);
            if (openGenericIndex.TryGetValue(key, out var canonical))
            {
                return canonical;
            }
        }

        return cleaned;
    }

    /// <summary>
    /// Walks a formatted type string (e.g. "System.Collections.Generic.List&lt;MyApp.Order&gt;[]")
    /// and invokes callback(typeName, isGenericArgument) for the outer type and every nested generic argument.
    /// </summary>
    public static void CollectReferencedTypes(string formattedType, Action<string, bool> onType, bool isGenericArg = false)
    {
        var cleaned = StripTypeDecorators(formattedType);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        onType(cleaned, isGenericArg);

        // Extract every top-level <...> generic argument list (supports Outer<A>.Inner<B>)
        var i = 0;
        while (i < cleaned.Length)
        {
            if (cleaned[i] == '<')
            {
                var depth = 0;
                var start = i + 1;
                var end = -1;
                for (var j = i; j < cleaned.Length; j++)
                {
                    if (cleaned[j] == '<') depth++;
                    else if (cleaned[j] == '>')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            end = j;
                            break;
                        }
                    }
                }

                if (end > start)
                {
                    foreach (var arg in SplitTopLevelArgs(cleaned[start..end]))
                    {
                        CollectReferencedTypes(arg, onType, isGenericArg: true);
                    }
                    i = end + 1;
                    continue;
                }
            }
            i++;
        }
    }

    public static string StripTypeDecorators(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("ref ", StringComparison.Ordinal)) s = s[4..].TrimStart();
        if (s.StartsWith("out ", StringComparison.Ordinal)) s = s[4..].TrimStart();
        if (s.StartsWith("in ", StringComparison.Ordinal)) s = s[3..].TrimStart();

        while (s.EndsWith("[]", StringComparison.Ordinal) || s.EndsWith('*') || s.EndsWith(']'))
        {
            if (s.EndsWith("[]", StringComparison.Ordinal))
            {
                s = s[..^2];
            }
            else if (s.EndsWith('*'))
            {
                s = s[..^1];
            }
            else
            {
                var openBracket = s.LastIndexOf('[');
                if (openBracket > 0) s = s[..openBracket];
                else break;
            }
        }

        return s.Trim();
    }

    public static string GetSimpleTypeName(string rawMetadataName)
    {
        var tick = rawMetadataName.IndexOf('`');
        return tick > 0 ? rawMetadataName[..tick] : rawMetadataName;
    }

    /// <summary>
    /// Converts "Ns.Outer&lt;int&gt;.Inner&lt;string, bool&gt;" into structural key "Ns.Outer`1.Inner`2".
    /// </summary>
    internal static string ComputeGenericStructuralKey(string formattedType)
    {
        var sb = new System.Text.StringBuilder(formattedType.Length);
        var i = 0;
        while (i < formattedType.Length)
        {
            if (formattedType[i] == '<')
            {
                var depth = 0;
                var start = i + 1;
                var end = -1;
                for (var j = i; j < formattedType.Length; j++)
                {
                    if (formattedType[j] == '<') depth++;
                    else if (formattedType[j] == '>')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            end = j;
                            break;
                        }
                    }
                }

                if (end > start)
                {
                    var arity = CountTopLevelArgs(formattedType[start..end]);
                    sb.Append('`').Append(arity);
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(formattedType[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string FormatGenericName(string rawName, GenericParameterHandleCollection genericParams, MetadataReader reader)
    {
        var tick = rawName.IndexOf('`');
        if (tick < 0)
        {
            return rawName;
        }

        var baseName = rawName[..tick];
        if (!int.TryParse(rawName[(tick + 1)..], out var arity) || arity <= 0)
        {
            return baseName;
        }

        var allParams = genericParams.Select(h => reader.GetString(reader.GetGenericParameter(h).Name)).ToList();
        var ownParams = allParams.Count >= arity
            ? allParams.Skip(allParams.Count - arity).ToList()
            : Enumerable.Range(0, arity).Select(i => arity == 1 ? "T" : $"T{i + 1}").ToList();

        return $"{baseName}<{string.Join(", ", ownParams)}>";
    }

    private static string FormatBacktickName(string rawName)
    {
        var tick = rawName.IndexOf('`');
        if (tick < 0)
        {
            return rawName;
        }

        var baseName = rawName[..tick];
        if (!int.TryParse(rawName[(tick + 1)..], out var arity) || arity <= 0)
        {
            return baseName;
        }

        var args = Enumerable.Range(0, arity).Select(i => arity == 1 ? "T" : $"T{i + 1}");
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    internal static int CountTopLevelArgs(string inner) =>
        string.IsNullOrWhiteSpace(inner) ? 0 : SplitTopLevelArgs(inner).Count;

    internal static List<string> SplitTopLevelArgs(string inner)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '<') depth++;
            else if (inner[i] == '>') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                result.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(inner[start..].Trim());
        return result;
    }
}
