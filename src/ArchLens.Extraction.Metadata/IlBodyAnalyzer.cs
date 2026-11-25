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
        string? declaringTypeStructuralKey = null,
        bool isStateMachine = false)
    {
        var il = body.GetILReader();
        var branches = 0;
        var references = new List<(EntityHandle, DependencyKind)>();
        var localFields = new HashSet<string>(StringComparer.Ordinal);
        var localMethods = new HashSet<string>(StringComparer.Ordinal);

        // Identify Finally/Fault handler ranges (compiler-emitted `using`/`foreach` disposal null-checks)
        var finallyRanges = new List<(int Start, int End)>();
        var catchRegions = new List<ExceptionRegion>();
        foreach (var region in body.ExceptionRegions)
        {
            if (region.Kind is ExceptionRegionKind.Finally or ExceptionRegionKind.Fault)
            {
                finallyRanges.Add((region.HandlerOffset, region.HandlerOffset + region.HandlerLength));
            }
            else if (region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter)
            {
                catchRegions.Add(region);
                if (region.Kind == ExceptionRegionKind.Catch && !region.CatchType.IsNil)
                {
                    references.Add((region.CatchType, DependencyKind.TypeReference));
                }
            }
        }

        // In async MoveNext() state machines, Roslyn wraps the entire body in one synthetic catch (Exception)
        // to forward exceptions to <>t__builder.SetException(e). Exclude that single outermost catch.
        var skippedSyntheticCatch = false;
        var maxTryLength = catchRegions.Count > 0 ? catchRegions.Max(r => r.TryLength) : 0;
        foreach (var region in catchRegions)
        {
            if (isStateMachine && !skippedSyntheticCatch &&
                region.Kind == ExceptionRegionKind.Catch &&
                region.TryLength == maxTryLength &&
                !region.CatchType.IsNil)
            {
                var catchTypeName = MetadataNameFormatter.FormatEntityHandleType(reader, region.CatchType);
                if (catchTypeName == "System.Exception")
                {
                    skippedSyntheticCatch = true;
                    continue;
                }
            }

            branches++;
        }

        var hasComputeStringHash = false;
        var lastCalledMethod = string.Empty;
        var lastOpcodeWasHashConst = false;
        var skippedStateMachineDispatch = false;

        while (il.RemainingBytes > 0)
        {
            var offset = il.Offset;
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

            var inFinally = IsOffsetInRanges(offset, finallyRanges);

            if (IsShortConditionalBranch(opcode))
            {
                if (!inFinally && ShouldCountConditionalBranch(opcode, isStateMachine, hasComputeStringHash, lastCalledMethod, lastOpcodeWasHashConst))
                {
                    branches++;
                }
                lastCalledMethod = string.Empty;
                lastOpcodeWasHashConst = false;
                SkipBytes(ref il, 1);
            }
            else if (IsLongConditionalBranch(opcode))
            {
                if (!inFinally && ShouldCountConditionalBranch(opcode, isStateMachine, hasComputeStringHash, lastCalledMethod, lastOpcodeWasHashConst))
                {
                    branches++;
                }
                lastCalledMethod = string.Empty;
                lastOpcodeWasHashConst = false;
                SkipBytes(ref il, 4);
            }
            else if (opcode == ILOpCode.Switch)
            {
                if (il.RemainingBytes < 4) break;
                var count = il.ReadUInt32();
                // Skip the compiler-generated state dispatch switch in async/iterator MoveNext(),
                // and skip hash-bucket switches emitted after ComputeStringHash (each string arm's op_Equality branch is counted).
                var skipSwitch = (isStateMachine && !skippedStateMachineDispatch) || hasComputeStringHash;
                if (isStateMachine && !skippedStateMachineDispatch)
                {
                    skippedStateMachineDispatch = true;
                }
                if (!skipSwitch && !inFinally)
                {
                    branches += (int)Math.Min(count, 256);
                }
                lastCalledMethod = string.Empty;
                lastOpcodeWasHashConst = false;
                var maxCount = (uint)(il.RemainingBytes / 4);
                var safeCount = Math.Min(count, maxCount);
                SkipBytes(ref il, (int)safeCount * 4);
            }
            else if (TryGetTokenDependencyKind(opcode, out var depKind))
            {
                if (il.RemainingBytes < 4) break;
                var token = MetadataTokens.EntityHandle(il.ReadInt32());
                references.Add((token, depKind));
                lastOpcodeWasHashConst = false;

                if (depKind == DependencyKind.MethodCall)
                {
                    lastCalledMethod = TryGetMethodName(token, reader) ?? string.Empty;
                    if (lastCalledMethod.Equals("ComputeStringHash", StringComparison.Ordinal))
                    {
                        hasComputeStringHash = true;
                    }
                    RecordIfLocalMethodOrProperty(token, reader, declaringTypeHandle, declaringTypeStructuralKey, localFields, localMethods);
                }
                else
                {
                    lastCalledMethod = string.Empty;
                    if (opcode is ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld)
                    {
                        RecordIfLocalField(token, reader, declaringTypeHandle, declaringTypeStructuralKey, localFields);
                    }
                }
            }
            else if (opcode == ILOpCode.Ldc_i4)
            {
                if (il.RemainingBytes < 4) break;
                var val = il.ReadInt32();
                // Large 32-bit unsigned FNV-1a hash constants used by Roslyn's string-switch binary search tree
                lastOpcodeWasHashConst = hasComputeStringHash && Math.Abs((long)val) > 65536;
                lastCalledMethod = string.Empty;
            }
            else
            {
                // Preserve lastCalledMethod across simple stack/local store-load sequences (e.g. Debug builds: stloc; ldloc; brtrue)
                if (!IsStackNoiseOpcode(opcode))
                {
                    lastCalledMethod = string.Empty;
                    lastOpcodeWasHashConst = false;
                }
                var operandSize = GetOperandSize(opcode);
                if (operandSize > 0)
                {
                    SkipBytes(ref il, operandSize);
                }
            }
        }

        return new IlMethodAnalysisResult(1 + branches, references, localFields, localMethods);
    }

    private static bool ShouldCountConditionalBranch(
        ILOpCode opcode,
        bool isStateMachine,
        bool hasComputeStringHash,
        string lastCalledMethod,
        bool lastOpcodeWasHashConst)
    {
        // Every `await` emits `call get_IsCompleted; brtrue` — not a C# source branch
        if (isStateMachine && lastCalledMethod.Equals("get_IsCompleted", StringComparison.Ordinal))
        {
            return false;
        }

        // Compiler-emitted string-switch binary search on 32-bit FNV-1a hash constants (actual arm op_Equality is counted)
        if (hasComputeStringHash && lastOpcodeWasHashConst &&
            opcode is ILOpCode.Bgt_un or ILOpCode.Bgt_un_s or ILOpCode.Blt_un or ILOpCode.Blt_un_s or
            ILOpCode.Bge_un or ILOpCode.Bge_un_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s or
            ILOpCode.Beq or ILOpCode.Beq_s or ILOpCode.Bne_un or ILOpCode.Bne_un_s)
        {
            return false;
        }

        return true;
    }

    private static bool IsOffsetInRanges(int offset, List<(int Start, int End)> ranges)
    {
        foreach (var (start, end) in ranges)
        {
            if (offset >= start && offset < end)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStackNoiseOpcode(ILOpCode opcode) => opcode is
        ILOpCode.Nop or ILOpCode.Dup or
        ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3 or ILOpCode.Stloc_s or ILOpCode.Stloc or
        ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3 or ILOpCode.Ldloc_s or ILOpCode.Ldloc;

    private static string? TryGetMethodName(EntityHandle token, MetadataReader reader)
    {
        try
        {
            return token.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)token).Name),
                HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)token).Name),
                HandleKind.MethodSpecification => TryGetMethodName(reader.GetMethodSpecification((MethodSpecificationHandle)token).Method, reader),
                _ => null
            };
        }
        catch
        {
            return null;
        }
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
