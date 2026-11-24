using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ArchLens.Model;

namespace ArchLens.Extraction.Metadata;

internal sealed record IlMethodAnalysisResult(
    int CyclomaticComplexity,
    List<(EntityHandle Token, DependencyKind Kind)> References,
    HashSet<string> AccessedLocalFields,
    HashSet<string> CalledLocalMethods);

/// <summary>
/// Decodes ECMA-335 IL method bodies using System.Reflection.Metadata without loading assemblies into the CLR.
/// Computes IL-based cyclomatic complexity and extracts call, object creation, field, catch, and type token references.
/// Supports generic-type MemberReference/MethodSpecification resolution and auto-property state mapping for LCOM4.
/// </summary>
internal static class IlBodyAnalyzer
{
    public static IlMethodAnalysisResult Analyze(
        MethodBodyBlock body,
        MetadataReader reader,
        TypeDefinitionHandle declaringTypeHandle,
        string? declaringTypeStructuralKey = null)
    {
        var il = body.GetILReader();
        var branches = 0;
        var references = new List<(EntityHandle, DependencyKind)>();
        var localFields = new HashSet<string>(StringComparer.Ordinal);
        var localMethods = new HashSet<string>(StringComparer.Ordinal);

        // Exception catch/filter handlers add branch paths AND catch-type references!
        foreach (var region in body.ExceptionRegions)
        {
            if (region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter)
            {
                branches++;
            }

            if (region.Kind == ExceptionRegionKind.Catch && !region.CatchType.IsNil)
            {
                references.Add((region.CatchType, DependencyKind.TypeReference));
            }
        }

        while (il.RemainingBytes > 0)
        {
            var b = il.ReadByte();
            ILOpCode opcode;
            if (b == 0xFE)
            {
                if (il.RemainingBytes <= 0)
                {
                    break;
                }
                opcode = (ILOpCode)(0xFE00 | il.ReadByte());
            }
            else
            {
                opcode = (ILOpCode)b;
            }

            if (IsShortConditionalBranch(opcode))
            {
                branches++;
                SkipBytes(ref il, 1);
            }
            else if (IsLongConditionalBranch(opcode))
            {
                branches++;
                SkipBytes(ref il, 4);
            }
            else if (opcode == ILOpCode.Switch)
            {
                if (il.RemainingBytes < 4) break;
                var count = il.ReadUInt32();
                branches += (int)Math.Min(count, 256);
                var maxCount = (uint)(il.RemainingBytes / 4);
                var safeCount = Math.Min(count, maxCount);
                SkipBytes(ref il, (int)safeCount * 4);
            }
            else if (TryGetTokenDependencyKind(opcode, out var depKind))
            {
                if (il.RemainingBytes < 4) break;
                var token = MetadataTokens.EntityHandle(il.ReadInt32());
                references.Add((token, depKind));

                if (depKind == DependencyKind.MethodCall)
                {
                    RecordIfLocalMethodOrProperty(token, reader, declaringTypeHandle, declaringTypeStructuralKey, localFields, localMethods);
                }
                else if (opcode is ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld)
                {
                    RecordIfLocalField(token, reader, declaringTypeHandle, declaringTypeStructuralKey, localFields);
                }
            }
            else
            {
                var operandSize = GetOperandSize(opcode);
                if (operandSize > 0)
                {
                    SkipBytes(ref il, operandSize);
                }
            }
        }

        return new IlMethodAnalysisResult(1 + branches, references, localFields, localMethods);
    }

    private static bool IsShortConditionalBranch(ILOpCode opcode) => opcode is
        ILOpCode.Brfalse_s or ILOpCode.Brtrue_s or
        ILOpCode.Beq_s or ILOpCode.Bge_s or ILOpCode.Bgt_s or ILOpCode.Ble_s or ILOpCode.Blt_s or
        ILOpCode.Bne_un_s or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s or ILOpCode.Ble_un_s or ILOpCode.Blt_un_s;

    private static bool IsLongConditionalBranch(ILOpCode opcode) => opcode is
        ILOpCode.Brfalse or ILOpCode.Brtrue or
        ILOpCode.Beq or ILOpCode.Bge or ILOpCode.Bgt or ILOpCode.Ble or ILOpCode.Blt or
        ILOpCode.Bne_un or ILOpCode.Bge_un or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un;

    private static bool TryGetTokenDependencyKind(ILOpCode opcode, out DependencyKind kind)
    {
        switch (opcode)
        {
            case ILOpCode.Call:
            case ILOpCode.Callvirt:
            case ILOpCode.Jmp:
            case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
                kind = DependencyKind.MethodCall;
                return true;

            case ILOpCode.Newobj:
                kind = DependencyKind.ObjectCreation;
                return true;

            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
            case ILOpCode.Stfld:
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
            case ILOpCode.Stsfld:
                kind = DependencyKind.FieldOrProperty;
                return true;

            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Box:
            case ILOpCode.Unbox:
            case ILOpCode.Unbox_any:
            case ILOpCode.Newarr:
            case ILOpCode.Ldtoken:
            case ILOpCode.Initobj:
            case ILOpCode.Constrained:
            case ILOpCode.Sizeof:
            case ILOpCode.Ldobj:
            case ILOpCode.Stobj:
            case ILOpCode.Cpobj:
            case ILOpCode.Ldelema:
            case ILOpCode.Ldelem:
            case ILOpCode.Stelem:
            case ILOpCode.Mkrefany:
            case ILOpCode.Refanyval:
                kind = DependencyKind.TypeReference;
                return true;

            default:
                kind = default;
                return false;
        }
    }

    private static void RecordIfLocalField(
        EntityHandle token,
        MetadataReader reader,
        TypeDefinitionHandle declaringType,
        string? declaringStructuralKey,
        HashSet<string> localFields)
    {
        if (token.Kind == HandleKind.FieldDefinition)
        {
            var field = reader.GetFieldDefinition((FieldDefinitionHandle)token);
            if (field.GetDeclaringType() == declaringType)
            {
                localFields.Add(NormalizeBackingFieldName(reader.GetString(field.Name)));
            }
        }
        else if (token.Kind == HandleKind.MemberReference && declaringStructuralKey is not null)
        {
            var mr = reader.GetMemberReference((MemberReferenceHandle)token);
            if (IsParentMatchingType(reader, mr.Parent, declaringType, declaringStructuralKey))
            {
                localFields.Add(NormalizeBackingFieldName(reader.GetString(mr.Name)));
            }
        }
    }

    private static void RecordIfLocalMethodOrProperty(
        EntityHandle token,
        MetadataReader reader,
        TypeDefinitionHandle declaringType,
        string? declaringStructuralKey,
        HashSet<string> localFields,
        HashSet<string> localMethods)
    {
        if (token.Kind == HandleKind.MethodSpecification)
        {
            var ms = reader.GetMethodSpecification((MethodSpecificationHandle)token);
            RecordIfLocalMethodOrProperty(ms.Method, reader, declaringType, declaringStructuralKey, localFields, localMethods);
            return;
        }

        string? memberName = null;
        if (token.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)token);
            if (method.GetDeclaringType() == declaringType)
            {
                memberName = reader.GetString(method.Name);
            }
        }
        else if (token.Kind == HandleKind.MemberReference && declaringStructuralKey is not null)
        {
            var mr = reader.GetMemberReference((MemberReferenceHandle)token);
            if (IsParentMatchingType(reader, mr.Parent, declaringType, declaringStructuralKey))
            {
                memberName = reader.GetString(mr.Name);
            }
        }

        if (string.IsNullOrEmpty(memberName))
        {
            return;
        }

        // Map auto-property getter/setter calls (get_Prop / set_Prop) to shared property state for LCOM4
        if (memberName.StartsWith("get_", StringComparison.Ordinal) || memberName.StartsWith("set_", StringComparison.Ordinal))
        {
            localFields.Add($"prop:{memberName[4..]}");
        }
        else
        {
            localMethods.Add(memberName);
        }
    }

    private static bool IsParentMatchingType(
        MetadataReader reader,
        EntityHandle parent,
        TypeDefinitionHandle declaringType,
        string declaringStructuralKey)
    {
        if (parent.Kind == HandleKind.TypeDefinition)
        {
            return (TypeDefinitionHandle)parent == declaringType;
        }

        if (parent.Kind is HandleKind.TypeSpecification or HandleKind.TypeReference)
        {
            var formatted = MetadataNameFormatter.FormatEntityHandleType(reader, parent);
            if (!string.IsNullOrEmpty(formatted))
            {
                return string.Equals(
                    MetadataNameFormatter.ComputeGenericStructuralKey(formatted),
                    declaringStructuralKey,
                    StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string NormalizeBackingFieldName(string fieldName)
    {
        // Map C# auto-property backing field "<PropName>k__BackingField" to "prop:PropName" so
        // direct backing-field accesses and get_Prop/set_Prop accesses share the same LCOM4 state key!
        if (fieldName.StartsWith('<') && fieldName.EndsWith(">k__BackingField", StringComparison.Ordinal))
        {
            return $"prop:{fieldName[1..^">k__BackingField".Length]}";
        }
        return fieldName;
    }

    private static void SkipBytes(ref BlobReader reader, int count)
    {
        var safe = Math.Min(count, reader.RemainingBytes);
        if (safe > 0)
        {
            reader.Offset += safe;
        }
    }

    private static int GetOperandSize(ILOpCode opcode) => opcode switch
    {
        ILOpCode.Br_s or ILOpCode.Leave_s or
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or
        ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or
        ILOpCode.Ldc_i4_s or ILOpCode.Unaligned or (ILOpCode)0xFE19 => 1,

        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or
        ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,

        ILOpCode.Br or ILOpCode.Leave or ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 or
        ILOpCode.Ldstr or ILOpCode.Calli => 4,

        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,

        _ => 0
    };
}
