using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using ArchLens.Model;
using ModelProjectInfo = ArchLens.Model.ProjectInfo;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Extraction.Metadata;

/// <summary>
/// High-speed IL + Portable PDB architecture extractor powered by System.Reflection.Metadata.
/// Reads compiled .dll/.exe assemblies directly from disk without MSBuild or CLR assembly loading,
/// folding C# compiler-generated async/iterator state machines and lambda closures back into their owning types/methods.
/// </summary>
public sealed class MetadataCodeGraphExtractor : ICodeGraphExtractor
{
    private static readonly HashSet<string> RecordBoilerplateMethods = new(StringComparer.Ordinal)
    {
        "<Clone>$", "PrintMembers", "Equals", "GetHashCode", "ToString", "Deconstruct"
    };

    public string Name => "IL";

    public bool CanHandle(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        if (File.Exists(targetPath))
        {
            var ext = Path.GetExtension(targetPath);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        }

        if (Directory.Exists(targetPath))
        {
            var hasSourceEntry =
                Directory.GetFiles(targetPath, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0 ||
                Directory.GetFiles(targetPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0 ||
                Directory.GetFiles(targetPath, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0;
            return !hasSourceEntry && Directory.GetFiles(targetPath, "*.dll", SearchOption.TopDirectoryOnly).Length > 0;
        }

        return false;
    }

    public Task<ArchitectureSnapshot> ExtractAsync(
        string targetPath,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        var fullTarget = Path.GetFullPath(targetPath);
        var diagnostics = new List<ExtractionDiagnostic>();
        var discovery = DiscoverAssemblies(fullTarget, options, diagnostics);
        var rootDir = discovery.RootDir;
        var assemblyPaths = discovery.Assemblies;

        if (assemblyPaths.Count == 0)
        {
            throw new FileNotFoundException($"No compiled .NET assemblies (.dll) found for IL extraction at '{fullTarget}'. Build the solution first or pass a .dll / bin directory.", fullTarget);
        }

        var loaded = new List<LoadedAssembly>();
        try
        {
            foreach (var dllPath in assemblyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asm = TryOpenAssembly(dllPath, diagnostics);
                if (asm is not null)
                {
                    loaded.Add(asm);
                }
            }

            // Pass 1: Collect known assemblies, type IDs, O(1) open-generic index, and method IDs
            var knownAssemblies = loaded.Select(a => a.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var knownTypeIds = new HashSet<string>(StringComparer.Ordinal);
            var knownMethodIds = new HashSet<string>(StringComparer.Ordinal);
            var methodIdsBySimpleKey = new Dictionary<(string OwnerId, string MethodName, int ParamCount), string>();

            var excludedSyntheticCount = 0;

            foreach (var asm in loaded)
            {
                foreach (var th in asm.Reader.TypeDefinitions)
                {
                    var td = asm.Reader.GetTypeDefinition(th);
                    if (ShouldSkipType(asm.Reader, td, options.IncludeGeneratedCode))
                    {
                        var rawName = asm.Reader.GetString(td.Name);
                        if (rawName != "<Module>") excludedSyntheticCount++;
                        continue;
                    }

                    var typeId = GetCanonicalTypeId(asm.Reader, th, td, asm.AssemblyName);
                    knownTypeIds.Add(typeId);

                    if (options.IncludeMethods)
                    {
                        foreach (var mh in td.GetMethods())
                        {
                            var md = asm.Reader.GetMethodDefinition(mh);
                            if (IsTrackableMethod(asm.Reader, md))
                            {
                                var mId = FormatMethodDefinitionId(asm.Reader, typeId, md, out var mName, out var pCount);
                                knownMethodIds.Add(mId);
                                methodIdsBySimpleKey.TryAdd((typeId, mName, pCount), mId);
                            }
                        }
                    }
                }
            }

            var openGenericIndex = MetadataNameFormatter.BuildOpenGenericIndex(knownTypeIds);
            var context = new ExtractionContext(
                rootDir, options, knownAssemblies, knownTypeIds, openGenericIndex, knownMethodIds, methodIdsBySimpleKey);

            // Pass 2: Extract assemblies, types, folded state machines/closures, methods, and edges
            foreach (var asm in loaded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExtractAssembly(asm, context);
            }

            var edges = context.EdgeAggregator
                .Select(kvp => new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight))
                .ToList();

            var degradedProjects = discovery.MissingProjects.Count > 0
                ? discovery.MissingProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : null;
            var expectedCount = Math.Max(context.ProjectInfos.Count + discovery.MissingProjects.Count, discovery.ExpectedProjects);
            var isDegraded = discovery.MissingProjects.Count > 0 || context.ProjectInfos.Count < expectedCount;
            var note = isDegraded
                ? $"Compiled assemblies only; {discovery.MissingProjects.Count} project(s) had no built .dll ({string.Join(", ", discovery.MissingProjects)}). Cognitive complexity is unavailable and cyclomatic complexity is IL-approximate; use --backend roslyn for CI gates."
                : "Compiled assemblies only; cognitive complexity is unavailable and cyclomatic complexity is IL-approximate (ideal for fast overview/graph; use --backend roslyn for CI quality gates).";
            var loadStatus = new GraphLoadStatus(
                Backend: "il",
                ProjectsExpected: expectedCount,
                ProjectsLoaded: context.ProjectInfos.Count,
                ProjectsDegraded: discovery.MissingProjects.Count,
                Degraded: isDegraded,
                Approximate: true,
                Note: note,
                DegradedProjects: degradedProjects,
                ExcludedSyntheticTypeCount: excludedSyntheticCount > 0 ? excludedSyntheticCount : null);

            var graph = new CodeGraph(
                rootDir, context.Nodes, edges, context.ProjectInfos, diagnostics,
                loadStatus: loadStatus,
                excludedTestProjectCount: discovery.ExcludedTestProjects,
                methodsIncluded: options.IncludeMethods);
            var snapshot = new ArchitectureSnapshot(
                SnapshotId: $"il-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                TargetPath: fullTarget,
                CapturedAt: DateTimeOffset.UtcNow,
                Graph: graph);

            return Task.FromResult(snapshot);
        }
        finally
        {
            foreach (var asm in loaded)
            {
                asm.Dispose();
            }
        }
    }

    private static void ExtractAssembly(LoadedAssembly asm, ExtractionContext ctx)
    {
        var reader = asm.Reader;
        var projectName = asm.AssemblyName;

        var internalRefs = new List<string>();
        var externalRefs = new List<string>();
        foreach (var refHandle in reader.AssemblyReferences)
        {
            var refName = reader.GetString(reader.GetAssemblyReference(refHandle).Name);
            if (ctx.KnownAssemblies.Contains(refName) && !string.Equals(refName, projectName, StringComparison.OrdinalIgnoreCase))
            {
                internalRefs.Add(refName);
            }
            else if (!refName.StartsWith("System.", StringComparison.Ordinal) &&
                     !refName.Equals("System", StringComparison.Ordinal) &&
                     !refName.Equals("mscorlib", StringComparison.Ordinal) &&
                     !refName.Equals("netstandard", StringComparison.Ordinal))
            {
                externalRefs.Add(refName);
            }
        }

        internalRefs = internalRefs.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        externalRefs = externalRefs.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var relDllPath = MakeRelativePath(ctx.RootDir, asm.FilePath);
        // Modern .NET executables are .dlls with a non-zero CorHeader entry point token!
        var hasEntryPoint = asm.PeReader.PEHeaders.IsExe ||
                            (asm.PeReader.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0) != 0;
        var outputKind = hasEntryPoint ? "ConsoleApplication" : "DynamicallyLinkedLibrary";
        var tfm = NormalizeTargetFramework(ExtractTargetFrameworkAttribute(reader)) ?? asm.InferredTfm;

        var emittedTypeCount = 0;
        var sourceDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var th in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(th);
            if (ShouldSkipType(reader, td, ctx.Options.IncludeGeneratedCode))
            {
                continue;
            }

            emittedTypeCount++;
            ExtractType(asm, th, td, projectName, outputKind, ctx, sourceDocuments);
        }

        ctx.ProjectInfos.Add(new ModelProjectInfo(
            Name: projectName,
            FilePath: relDllPath,
            TargetFramework: tfm,
            ProjectReferences: internalRefs,
            PackageReferences: externalRefs,
            TypeCount: emittedTypeCount,
            DocumentCount: sourceDocuments.Count));

        ctx.Nodes.Add(new CodeNode(
            Id: $"project:{projectName}",
            Name: projectName,
            FullName: projectName,
            Kind: NodeKind.Project,
            ProjectName: projectName,
            Location: new SourceLocation(relDllPath, 1, 1),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["outputKind"] = outputKind,
                ["extractor"] = "IL"
            }));

        foreach (var refName in internalRefs)
        {
            AddEdge(ctx.EdgeAggregator, $"project:{projectName}", $"project:{refName}", DependencyKind.ProjectReference, null);
        }
    }

    private static void ExtractType(
        LoadedAssembly asm,
        TypeDefinitionHandle th,
        TypeDefinition td,
        string projectName,
        string outputKind,
        ExtractionContext ctx,
        HashSet<string> sourceDocuments)
    {
        var reader = asm.Reader;
        var isSyntheticTopLevel = IsSynthesizedTopLevelProgram(reader, td);
        var typeId = GetCanonicalTypeId(reader, th, td, projectName);
        var simpleName = MetadataNameFormatter.GetSimpleTypeName(reader.GetString(td.Name));
        var ns = isSyntheticTopLevel ? projectName : ResolveNamespace(reader, td);

        if (ctx.NamespacesSeen.Add(ns))
        {
            ctx.Nodes.Add(new CodeNode(
                Id: $"ns:{ns}",
                Name: ns.Contains('.') ? ns[(ns.LastIndexOf('.') + 1)..] : ns,
                FullName: ns,
                Kind: NodeKind.Namespace,
                ProjectName: projectName,
                Namespace: ns));
        }

        var typeKind = DetermineTypeKind(reader, td);
        var isAbstract = td.Attributes.HasFlag(TypeAttributes.Abstract) || typeKind == ModelTypeKind.Interface;
        var isStatic = td.Attributes.HasFlag(TypeAttributes.Abstract) && td.Attributes.HasFlag(TypeAttributes.Sealed);
        var accessibility = MapTypeAccessibility(td.Attributes);
        var structuralKey = MetadataNameFormatter.ComputeGenericStructuralKey(typeId);

        var stats = new TypeStatsAccumulator(td.GetFields().Count + td.GetProperties().Count + td.GetEvents().Count);
        var baseTypesList = ExtractTypeHierarchyAndAttributes(reader, td, typeId, ctx, out var typeAttrNames);
        ExtractFieldPropertyEventSignatures(reader, td, typeId, ctx);

        // Pre-fold compiler-generated nested async/iterator state machines (<Method>d__N) & lambda closures (<>c*)
        var foldedHelpers = FoldCompilerGeneratedNestedTypes(asm, th, td, typeId, structuralKey, ctx, stats, sourceDocuments);

        // Process declared methods of this type
        var instanceMethodRefs = new List<(string Name, HashSet<string> Fields, HashSet<string> Calls)>();
        var methodAttrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mh in td.GetMethods())
        {
            var md = reader.GetMethodDefinition(mh);
            ProcessMethod(
                asm, th, md, mh, typeId, simpleName, projectName, ns, outputKind, typeKind,
                isStatic, structuralKey, ctx, stats, sourceDocuments, foldedHelpers,
                instanceMethodRefs, methodAttrNames);
        }

        var lcom4 = typeKind is ModelTypeKind.Interface or ModelTypeKind.Enum or ModelTypeKind.Delegate || isStatic
            ? 0
            : ComputeIlLcom4(instanceMethodRefs);

        var lineCount = stats.ComputeLineCount();
        var typeMetadata = BuildTypeMetadata(
            outputKind, typeKind, isAbstract, isStatic, stats,
            typeAttrNames, baseTypesList, methodAttrNames, isSyntheticTopLevel);

        var containingTypeId = td.IsNested
            ? GetCanonicalTypeId(reader, td.GetDeclaringType(), reader.GetTypeDefinition(td.GetDeclaringType()), projectName)
            : null;

        ctx.Nodes.Add(new CodeNode(
            Id: typeId,
            Name: simpleName,
            FullName: typeId,
            Kind: NodeKind.Type,
            ProjectName: projectName,
            Namespace: ns,
            TypeKind: typeKind,
            Accessibility: accessibility,
            IsAbstract: isAbstract,
            IsStatic: isStatic,
            Location: stats.PrimaryLocation,
            LineCount: lineCount,
            CyclomaticComplexity: stats.TotalCc,
            ConstructorParameterCount: stats.MaxCtorParams,
            MemberCount: stats.MemberCount,
            Metadata: typeMetadata,
            Lcom4: lcom4,
            ContainingTypeId: containingTypeId));
    }

    /// <summary>
    /// Walks compiler-generated nested state machines (&lt;Method&gt;d__N) and lambda/closure classes (&lt;&gt;c*),
    /// folding their IL dependencies, cyclomatic complexity, field/method accesses, and PDB locations
    /// back into the declaring type and originating methods.
    /// </summary>
    private static Dictionary<string, FoldedMethodContribution> FoldCompilerGeneratedNestedTypes(
        LoadedAssembly asm,
        TypeDefinitionHandle ownerHandle,
        TypeDefinition ownerTd,
        string ownerTypeId,
        string ownerStructuralKey,
        ExtractionContext ctx,
        TypeStatsAccumulator stats,
        HashSet<string> sourceDocuments)
    {
        var byOriginMethod = new Dictionary<string, FoldedMethodContribution>(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinitionHandle>();

        foreach (var nestedHandle in ownerTd.GetNestedTypes())
        {
            var nestedTd = asm.Reader.GetTypeDefinition(nestedHandle);
            if (IsCompilerGeneratedHelperType(asm.Reader, nestedTd))
            {
                queue.Enqueue(nestedHandle);
            }
        }

        while (queue.Count > 0)
        {
            var helperHandle = queue.Dequeue();
            var helperTd = asm.Reader.GetTypeDefinition(helperHandle);
            var helperName = asm.Reader.GetString(helperTd.Name);
            var originMethodName = TryExtractOriginMethodName(helperName);

            foreach (var deeper in helperTd.GetNestedTypes())
            {
                var deeperTd = asm.Reader.GetTypeDefinition(deeper);
                if (IsCompilerGeneratedHelperType(asm.Reader, deeperTd))
                {
                    queue.Enqueue(deeper);
                }
            }

            foreach (var mh in helperTd.GetMethods())
            {
                var md = asm.Reader.GetMethodDefinition(mh);
                var mName = asm.Reader.GetString(md.Name);
                if (mName is ".ctor" or ".cctor" or "SetStateMachine")
                {
                    continue;
                }

                var effectiveOrigin = originMethodName ?? TryExtractOriginMethodName(mName);
                var (loc, lines) = TryGetMethodSourceLocation(asm.PdbReader, mh, ctx.RootDir);
                if (loc is not null)
                {
                    sourceDocuments.Add(loc.FilePath);
                    stats.RecordLocation(loc, lines);
                }

                if (md.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                try
                {
                    var body = asm.PeReader.GetMethodBody(md.RelativeVirtualAddress);
                    var isStateMachine = mName == "MoveNext" && helperName.Contains(">d__", StringComparison.Ordinal);
                    var ilResult = IlBodyAnalyzer.Analyze(body, asm.Reader, ownerHandle, ownerStructuralKey, isStateMachine);

                    foreach (var (token, depKind) in ilResult.References)
                    {
                        ResolveAndRecordIlToken(asm.Reader, ownerTypeId, token, depKind, loc ?? stats.PrimaryLocation, ctx);
                    }

                    if (!string.IsNullOrEmpty(effectiveOrigin))
                    {
                        if (!byOriginMethod.TryGetValue(effectiveOrigin, out var contrib))
                        {
                            contrib = new FoldedMethodContribution();
                            byOriginMethod[effectiveOrigin] = contrib;
                        }
                        contrib.ExtraComplexity += Math.Max(0, ilResult.CyclomaticComplexity - 1);
                        contrib.Locations.Add((loc, lines));
                        contrib.IlReferences.AddRange(ilResult.References);
                        contrib.LocalFields.UnionWith(ilResult.AccessedLocalFields);
                        contrib.LocalMethods.UnionWith(ilResult.CalledLocalMethods);
                    }
                    else
                    {
                        // Unattributed lambda (e.g. static <>c): fold extra CC into type total
                        var extra = Math.Max(0, ilResult.CyclomaticComplexity - 1);
                        stats.TotalCc += extra;
                        if (ilResult.CyclomaticComplexity > stats.MaxMethodCc)
                        {
                            stats.MaxMethodCc = ilResult.CyclomaticComplexity;
                        }
                    }
                }
                catch
                {
                    // Skip unreadable IL body
                }
            }
        }

        return byOriginMethod;
    }

    private static void ProcessMethod(
        LoadedAssembly asm,
        TypeDefinitionHandle th,
        MethodDefinition md,
        MethodDefinitionHandle mh,
        string typeId,
        string simpleName,
        string projectName,
        string ns,
        string outputKind,
        ModelTypeKind typeKind,
        bool isStaticType,
        string structuralKey,
        ExtractionContext ctx,
        TypeStatsAccumulator stats,
        HashSet<string> sourceDocuments,
        Dictionary<string, FoldedMethodContribution> foldedHelpers,
        List<(string Name, HashSet<string> Fields, HashSet<string> Calls)> instanceMethodRefs,
        HashSet<string> methodAttrNames)
    {
        var reader = asm.Reader;
        var mName = reader.GetString(md.Name);
        var isCtor = mName == ".ctor";
        var isCctor = mName == ".cctor";
        if (!mName.StartsWith("get_", StringComparison.Ordinal) && !mName.StartsWith("set_", StringComparison.Ordinal))
        {
            stats.MemberCount++;
        }

        if (mName is "Main" or "<Main>$" && md.Attributes.HasFlag(MethodAttributes.Static))
        {
            stats.HasEntryPoint = true;
        }

        // Resolve PDB source location BEFORE recording signature/IL edges so locations are accurate!
        var (methodLoc, methodLines) = TryGetMethodSourceLocation(asm.PdbReader, mh, ctx.RootDir);
        foldedHelpers.TryGetValue(mName, out var folded);
        if (methodLoc is null && folded is not null)
        {
            var firstFolded = folded.Locations.FirstOrDefault(x => x.Loc is not null);
            methodLoc = firstFolded.Loc;
            methodLines = firstFolded.Lines;
        }

        if (methodLoc is not null)
        {
            sourceDocuments.Add(methodLoc.FilePath);
            stats.RecordLocation(methodLoc, methodLines);
        }

        var edgeLoc = methodLoc ?? stats.PrimaryLocation;
        var mAttrs = CollectCustomAttributes(reader, md.GetCustomAttributes(), typeId, ctx);
        foreach (var a in mAttrs) methodAttrNames.Add(a);

        // Decode method signature WITHOUT emitting bogus Parameter edges for the return type!
        MethodSignature<string>? decodedSig = null;
        try
        {
            decodedSig = md.DecodeSignature(new SignatureTypeProvider(), null);
            if (!string.IsNullOrEmpty(decodedSig.Value.ReturnType) && decodedSig.Value.ReturnType != "void")
            {
                RecordFormattedTypeWithGenericArgs(typeId, decodedSig.Value.ReturnType, DependencyKind.ReturnType, edgeLoc, ctx);
            }

            foreach (var pType in decodedSig.Value.ParameterTypes)
            {
                RecordFormattedTypeWithGenericArgs(typeId, pType, DependencyKind.Parameter, edgeLoc, ctx);
            }

            if (isCtor)
            {
                var pCount = decodedSig.Value.ParameterTypes.Length;
                if (pCount > stats.MaxCtorParams) stats.MaxCtorParams = pCount;
                var isPub = (md.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                if (isPub && pCount > stats.BestCtorParamCount)
                {
                    stats.BestCtorParamCount = pCount;
                    stats.BestCtorParams = string.Join(";", decodedSig.Value.ParameterTypes);
                }
            }
        }
        catch
        {
            // Ignore malformed generic signature edge cases
        }

        // IL body decoding + folded state machine / closure contribution
        var methodCc = 1;
        IlMethodAnalysisResult? ilResult = null;
        if (md.RelativeVirtualAddress != 0)
        {
            try
            {
                var body = asm.PeReader.GetMethodBody(md.RelativeVirtualAddress);
                ilResult = IlBodyAnalyzer.Analyze(body, reader, th, structuralKey);
                methodCc = ilResult.CyclomaticComplexity;

                foreach (var (token, depKind) in ilResult.References)
                {
                    ResolveAndRecordIlToken(reader, typeId, token, depKind, edgeLoc, ctx);
                }
            }
            catch
            {
                // Skip unreadable IL body
            }
        }

        var localFields = new HashSet<string>(ilResult?.AccessedLocalFields ?? [], StringComparer.Ordinal);
        var localCalls = new HashSet<string>(ilResult?.CalledLocalMethods ?? [], StringComparer.Ordinal);
        if (folded is not null)
        {
            methodCc += folded.ExtraComplexity;
            localFields.UnionWith(folded.LocalFields);
            localCalls.UnionWith(folded.LocalMethods);
        }

        stats.TotalCc += methodCc;
        if (methodCc > stats.MaxMethodCc) stats.MaxMethodCc = methodCc;

        var isRecordBoilerplate = typeKind == ModelTypeKind.Record && RecordBoilerplateMethods.Contains(mName);
        if (!isStaticType && !isCtor && !isCctor && !isRecordBoilerplate &&
            typeKind is not (ModelTypeKind.Interface or ModelTypeKind.Enum or ModelTypeKind.Delegate) &&
            !md.Attributes.HasFlag(MethodAttributes.Static) &&
            !md.Attributes.HasFlag(MethodAttributes.Abstract) &&
            !md.Attributes.HasFlag(MethodAttributes.SpecialName))
        {
            instanceMethodRefs.Add((mName, localFields, localCalls));
        }

        // Emit Method node & method-level call graph edges when IncludeMethods is enabled
        if (ctx.Options.IncludeMethods && IsTrackableMethod(reader, md))
        {
            var methodId = FormatMethodDefinitionId(reader, typeId, md, out _, out _);
            var mMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containingType"] = typeId,
                ["methodKind"] = isCctor ? "StaticConstructor" : isCtor ? "Constructor" : "Ordinary",
                ["outputKind"] = outputKind
            };
            if (mAttrs.Count > 0) mMeta["attributes"] = string.Join(",", mAttrs);
            if (mName is "Main" or "<Main>$" && md.Attributes.HasFlag(MethodAttributes.Static)) mMeta["isEntryPointMain"] = "true";

            ctx.Nodes.Add(new CodeNode(
                Id: methodId,
                Name: isCctor ? ".cctor" : isCtor ? simpleName : mName,
                FullName: methodId,
                Kind: NodeKind.Method,
                ProjectName: projectName,
                Namespace: ns,
                Accessibility: MapMethodAccessibility(md.Attributes),
                IsAbstract: md.Attributes.HasFlag(MethodAttributes.Abstract),
                IsStatic: md.Attributes.HasFlag(MethodAttributes.Static),
                Location: edgeLoc,
                LineCount: Math.Max(1, methodLines),
                CyclomaticComplexity: methodCc,
                ConstructorParameterCount: isCtor ? (decodedSig?.ParameterTypes.Length ?? 0) : 0,
                Metadata: mMeta,
                ContainingTypeId: typeId));

            var allMethodRefs = new List<(EntityHandle Token, DependencyKind Kind)>();
            if (ilResult is not null) allMethodRefs.AddRange(ilResult.References);
            if (folded is not null) allMethodRefs.AddRange(folded.IlReferences);

            foreach (var (token, depKind) in allMethodRefs)
            {
                if (depKind is DependencyKind.MethodCall or DependencyKind.ObjectCreation)
                {
                    var targetMethodId = TryResolveTokenToKnownMethodId(reader, token, ctx);
                    if (targetMethodId is not null && !string.Equals(methodId, targetMethodId, StringComparison.Ordinal))
                    {
                        AddEdge(ctx.EdgeAggregator, methodId, targetMethodId, depKind, edgeLoc);
                    }
                }
            }
        }
    }

    private static List<string> ExtractTypeHierarchyAndAttributes(
        MetadataReader reader,
        TypeDefinition td,
        string typeId,
        ExtractionContext ctx,
        out List<string> typeAttrNames)
    {
        var baseTypesList = new List<string>();
        if (!td.BaseType.IsNil)
        {
            var baseId = MetadataNameFormatter.FormatEntityHandleType(reader, td.BaseType);
            if (!string.IsNullOrEmpty(baseId) && baseId is not ("System.Object" or "System.ValueType" or "System.Enum"))
            {
                baseTypesList.Add(baseId);
                RecordFormattedTypeWithGenericArgs(typeId, baseId, DependencyKind.Inherits, null, ctx);
            }
        }

        foreach (var ih in td.GetInterfaceImplementations())
        {
            var ifaceHandle = reader.GetInterfaceImplementation(ih).Interface;
            var ifaceId = MetadataNameFormatter.FormatEntityHandleType(reader, ifaceHandle);
            if (!string.IsNullOrEmpty(ifaceId))
            {
                baseTypesList.Add(ifaceId);
                RecordFormattedTypeWithGenericArgs(typeId, ifaceId, DependencyKind.Implements, null, ctx);
            }
        }

        typeAttrNames = CollectCustomAttributes(reader, td.GetCustomAttributes(), typeId, ctx);
        return baseTypesList;
    }

    private static void ExtractFieldPropertyEventSignatures(
        MetadataReader reader,
        TypeDefinition td,
        string typeId,
        ExtractionContext ctx)
    {
        foreach (var fh in td.GetFields())
        {
            var field = reader.GetFieldDefinition(fh);
            if (reader.GetString(field.Name).StartsWith('<')) continue;
            try
            {
                var fieldType = field.DecodeSignature(new SignatureTypeProvider(), null);
                RecordFormattedTypeWithGenericArgs(typeId, fieldType, DependencyKind.FieldOrProperty, null, ctx);
            }
            catch { }
        }

        foreach (var ph in td.GetProperties())
        {
            var prop = reader.GetPropertyDefinition(ph);
            try
            {
                var propType = prop.DecodeSignature(new SignatureTypeProvider(), null).ReturnType;
                RecordFormattedTypeWithGenericArgs(typeId, propType, DependencyKind.FieldOrProperty, null, ctx);
            }
            catch { }
        }

        foreach (var eh in td.GetEvents())
        {
            var ev = reader.GetEventDefinition(eh);
            if (!ev.Type.IsNil)
            {
                var evType = MetadataNameFormatter.FormatEntityHandleType(reader, ev.Type);
                RecordFormattedTypeWithGenericArgs(typeId, evType, DependencyKind.FieldOrProperty, null, ctx);
            }
        }
    }

    private static Dictionary<string, string> BuildTypeMetadata(
        string outputKind,
        ModelTypeKind typeKind,
        bool isAbstract,
        bool isStatic,
        TypeStatsAccumulator stats,
        List<string> typeAttrNames,
        List<string> baseTypesList,
        HashSet<string> methodAttrNames,
        bool isSyntheticTopLevel = false)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maxMethodComplexity"] = stats.MaxMethodCc.ToString(),
            ["outputKind"] = outputKind,
            ["extractor"] = "IL"
        };
        if (typeAttrNames.Count > 0) meta["attributes"] = string.Join(",", typeAttrNames);
        if (baseTypesList.Count > 0) meta["baseTypes"] = string.Join(";", baseTypesList.Distinct(StringComparer.Ordinal));
        if (!isAbstract && !isStatic && typeKind is ModelTypeKind.Class or ModelTypeKind.Struct or ModelTypeKind.Record)
        {
            meta["ctorParamTypes"] = stats.BestCtorParams ?? string.Empty;
        }
        if (stats.HasEntryPoint) meta["isEntryPointMain"] = "true";
        if (methodAttrNames.Any(IsTestAttributeName)) meta["hasTestMethods"] = "true";
        if (methodAttrNames.Any(IsEndpointAttributeName)) meta["hasEndpointMethods"] = "true";
        if (isSyntheticTopLevel)
        {
            meta["isSynthetic"] = "true";
            meta["syntheticKind"] = "compilationUnit";
        }
        return meta;
    }

    private static int ComputeIlLcom4(List<(string Name, HashSet<string> Fields, HashSet<string> Calls)> methods)
    {
        if (methods.Count <= 1) return methods.Count;

        var parent = Enumerable.Range(0, methods.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var byField = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < methods.Count; i++)
        {
            AddIndex(byName, methods[i].Name, i);
            foreach (var f in methods[i].Fields) AddIndex(byField, f, i);
        }

        foreach (var group in byField.Values)
        {
            for (var i = 1; i < group.Count; i++) Union(group[0], group[i]);
        }

        for (var i = 0; i < methods.Count; i++)
        {
            foreach (var called in methods[i].Calls)
            {
                if (byName.TryGetValue(called, out var targets))
                {
                    foreach (var t in targets) Union(i, t);
                }
            }
        }

        var roots = new HashSet<int>();
        for (var i = 0; i < methods.Count; i++) roots.Add(Find(i));
        return roots.Count;
    }

    private static void AddIndex(Dictionary<string, List<int>> map, string key, int idx)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(idx);
    }

    private static void ResolveAndRecordIlToken(
        MetadataReader reader,
        string sourceTypeId,
        EntityHandle token,
        DependencyKind kind,
        SourceLocation? location,
        ExtractionContext ctx)
    {
        try
        {
            switch (token.Kind)
            {
                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    var tId = MetadataNameFormatter.FormatEntityHandleType(reader, token);
                    RecordFormattedTypeWithGenericArgs(sourceTypeId, tId, kind, location, ctx);
                    break;

                case HandleKind.MethodDefinition:
                {
                    var md = reader.GetMethodDefinition((MethodDefinitionHandle)token);
                    var declId = MetadataNameFormatter.FormatTypeDefinition(reader, md.GetDeclaringType());
                    RecordTypeEdge(sourceTypeId, declId, kind, location, ctx);
                    break;
                }

                case HandleKind.FieldDefinition:
                {
                    var fd = reader.GetFieldDefinition((FieldDefinitionHandle)token);
                    var declId = MetadataNameFormatter.FormatTypeDefinition(reader, fd.GetDeclaringType());
                    RecordTypeEdge(sourceTypeId, declId, kind, location, ctx);
                    break;
                }

                case HandleKind.MemberReference:
                {
                    var mr = reader.GetMemberReference((MemberReferenceHandle)token);
                    var parentId = MetadataNameFormatter.FormatEntityHandleType(reader, mr.Parent);
                    RecordFormattedTypeWithGenericArgs(sourceTypeId, parentId, kind, location, ctx);
                    break;
                }

                case HandleKind.MethodSpecification:
                {
                    var ms = reader.GetMethodSpecification((MethodSpecificationHandle)token);
                    ResolveAndRecordIlToken(reader, sourceTypeId, ms.Method, kind, location, ctx);
                    var genArgs = ms.DecodeSignature(new SignatureTypeProvider(), null);
                    foreach (var arg in genArgs)
                    {
                        RecordFormattedTypeWithGenericArgs(sourceTypeId, arg, DependencyKind.GenericArgument, location, ctx);
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore unresolvable token edge cases
        }
    }

    private static string? TryResolveTokenToKnownMethodId(MetadataReader reader, EntityHandle token, ExtractionContext ctx)
    {
        try
        {
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var md = reader.GetMethodDefinition((MethodDefinitionHandle)token);
                    var ownerId = MetadataNameFormatter.FormatTypeDefinition(reader, md.GetDeclaringType());
                    var exact = FormatMethodDefinitionId(reader, ownerId, md, out var mName, out var pCount);
                    if (ctx.KnownMethodIds.Contains(exact)) return exact;
                    return ctx.MethodIdsBySimpleKey.GetValueOrDefault((ownerId, mName, pCount));
                }

                case HandleKind.MemberReference:
                {
                    var mr = reader.GetMemberReference((MemberReferenceHandle)token);
                    var rawOwnerId = MetadataNameFormatter.FormatEntityHandleType(reader, mr.Parent);
                    var ownerId = MetadataNameFormatter.ResolveToKnownTypeId(rawOwnerId, ctx.KnownTypeIds, ctx.OpenGenericIndex);
                    var rawName = reader.GetString(mr.Name);
                    var sig = mr.DecodeMethodSignature(new SignatureTypeProvider(), null);
                    var displayName = rawName == ".cctor" ? ".cctor" : rawName == ".ctor" ? ExtractSimpleNameFromTypeId(ownerId) : rawName;
                    var genSuffix = sig.GenericParameterCount > 0
                        ? $"<{string.Join(", ", Enumerable.Range(0, sig.GenericParameterCount).Select(i => $"M{i}"))}>"
                        : string.Empty;
                    var exact = $"{ownerId}.{displayName}{genSuffix}({string.Join(", ", sig.ParameterTypes)})";
                    if (ctx.KnownMethodIds.Contains(exact)) return exact;
                    return ctx.MethodIdsBySimpleKey.GetValueOrDefault((ownerId, displayName, sig.ParameterTypes.Length));
                }

                case HandleKind.MethodSpecification:
                {
                    var ms = reader.GetMethodSpecification((MethodSpecificationHandle)token);
                    return TryResolveTokenToKnownMethodId(reader, ms.Method, ctx);
                }

                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string FormatMethodDefinitionId(
        MetadataReader reader,
        string ownerTypeId,
        MethodDefinition md,
        out string displayName,
        out int paramCount)
    {
        var rawName = reader.GetString(md.Name);
        if (rawName == ".cctor")
        {
            displayName = ".cctor";
            paramCount = 0;
            return $"{ownerTypeId}.cctor()";
        }

        displayName = rawName == ".ctor" ? ExtractSimpleNameFromTypeId(ownerTypeId) : rawName;
        var genParams = md.GetGenericParameters();
        var genSuffix = genParams.Count > 0
            ? $"<{string.Join(", ", genParams.Select(h => reader.GetString(reader.GetGenericParameter(h).Name)))}>"
            : string.Empty;

        try
        {
            var sig = md.DecodeSignature(new SignatureTypeProvider(), null);
            paramCount = sig.ParameterTypes.Length;
            return $"{ownerTypeId}.{displayName}{genSuffix}({string.Join(", ", sig.ParameterTypes)})";
        }
        catch
        {
            paramCount = 0;
            return $"{ownerTypeId}.{displayName}{genSuffix}()";
        }
    }

    private static void RecordFormattedTypeWithGenericArgs(
        string sourceTypeId,
        string formattedType,
        DependencyKind primaryKind,
        SourceLocation? location,
        ExtractionContext ctx)
    {
        MetadataNameFormatter.CollectReferencedTypes(formattedType, (refType, isGenArg) =>
        {
            RecordTypeEdge(sourceTypeId, refType, isGenArg ? DependencyKind.GenericArgument : primaryKind, location, ctx);
        });
    }

    private static void RecordTypeEdge(
        string sourceTypeId,
        string rawTargetId,
        DependencyKind kind,
        SourceLocation? location,
        ExtractionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(rawTargetId)) return;

        var normalized = MetadataNameFormatter.ResolveToKnownTypeId(rawTargetId, ctx.KnownTypeIds, ctx.OpenGenericIndex);
        if (!string.Equals(sourceTypeId, normalized, StringComparison.Ordinal) && ctx.KnownTypeIds.Contains(normalized))
        {
            AddEdge(ctx.EdgeAggregator, sourceTypeId, normalized, kind, location);
        }
    }

    private static List<string> CollectCustomAttributes(
        MetadataReader reader,
        CustomAttributeHandleCollection attrs,
        string sourceTypeId,
        ExtractionContext ctx)
    {
        var names = new List<string>();
        foreach (var ah in attrs)
        {
            var attr = reader.GetCustomAttribute(ah);
            string? attrTypeId = null;
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                var mr = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                attrTypeId = MetadataNameFormatter.FormatEntityHandleType(reader, mr.Parent);
            }
            else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                attrTypeId = MetadataNameFormatter.FormatTypeDefinition(reader, md.GetDeclaringType());
            }

            if (!string.IsNullOrEmpty(attrTypeId))
            {
                RecordTypeEdge(sourceTypeId, attrTypeId, DependencyKind.Attribute, null, ctx);
                var simple = ExtractSimpleNameFromTypeId(attrTypeId);
                if (!names.Contains(simple, StringComparer.Ordinal))
                {
                    names.Add(simple);
                }
            }
        }
        return names;
    }

    private static void AddEdge(
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        string fromId,
        string toId,
        DependencyKind kind,
        SourceLocation? location)
    {
        var key = (fromId, toId, kind);
        if (aggregator.TryGetValue(key, out var existing))
        {
            aggregator[key] = (existing.Weight + 1, existing.Location ?? location);
        }
        else
        {
            aggregator[key] = (1, location);
        }
    }

    private static (SourceLocation? Location, int LineSpan) TryGetMethodSourceLocation(
        MetadataReader? pdbReader,
        MethodDefinitionHandle mh,
        string rootDir)
    {
        if (pdbReader is null) return (null, 0);

        try
        {
            var debugInfo = pdbReader.GetMethodDebugInformation(mh);
            string? docPath = null;
            var minLine = int.MaxValue;
            var maxLine = 0;
            var firstCol = 1;

            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;

                if (docPath is null && !sp.Document.IsNil)
                {
                    docPath = pdbReader.GetString(pdbReader.GetDocument(sp.Document).Name);
                }

                if (sp.StartLine < minLine)
                {
                    minLine = sp.StartLine;
                    firstCol = Math.Max(1, sp.StartColumn);
                }

                if (sp.EndLine > maxLine) maxLine = sp.EndLine;
            }

            if (docPath is not null && minLine < int.MaxValue)
            {
                var rel = MakeRelativePath(rootDir, docPath);
                return (new SourceLocation(rel, minLine, firstCol), Math.Max(1, maxLine - minLine + 1));
            }
        }
        catch { }

        return (null, 0);
    }

    private static bool IsSynthesizedTopLevelProgram(MetadataReader reader, TypeDefinition td) =>
        !td.IsNested &&
        string.IsNullOrEmpty(reader.GetString(td.Namespace)) &&
        reader.GetString(td.Name) == "Program" &&
        HasTopLevelMain(reader, td);

    private static string GetCanonicalTypeId(MetadataReader reader, TypeDefinitionHandle th, TypeDefinition td, string projectName)
    {
        if (IsSynthesizedTopLevelProgram(reader, td))
        {
            return $"{projectName}.Program";
        }
        return MetadataNameFormatter.FormatTypeDefinition(reader, th);
    }

    private static bool HasTopLevelMain(MetadataReader reader, TypeDefinition td) =>
        td.GetMethods().Any(mh => reader.GetString(reader.GetMethodDefinition(mh).Name) == "<Main>$");

    private static bool IsCompilerGeneratedHelperType(MetadataReader reader, TypeDefinition td)
    {
        var name = reader.GetString(td.Name);
        return name.StartsWith('<') || HasCompilerGeneratedAttribute(reader, td);
    }

    private static string? TryExtractOriginMethodName(string compilerGeneratedName)
    {
        // Matches "<MethodName>d__5", "<MethodName>b__0", "<MethodName>g__Local|1_0"
        if (compilerGeneratedName.Length > 2 && compilerGeneratedName[0] == '<')
        {
            var close = compilerGeneratedName.IndexOf('>', 1);
            if (close > 1)
            {
                var extracted = compilerGeneratedName[1..close];
                if (!string.IsNullOrEmpty(extracted) && !extracted.Contains('<'))
                {
                    return extracted;
                }
            }
        }
        return null;
    }

    private static bool ShouldSkipType(MetadataReader reader, TypeDefinition td, bool includeGenerated)
    {
        var name = reader.GetString(td.Name);
        if (name == "<Module>" || name.StartsWith('<'))
        {
            return true;
        }

        // Preserve synthesized top-level statements Program class (which has [CompilerGenerated] + <Main>$)
        if (name == "Program" && HasTopLevelMain(reader, td))
        {
            return false;
        }

        // Walk declaring type chain: nested types inside <PrivateImplementationDetails>, [CompilerGenerated], or [GeneratedCode]
        // (such as __StaticArrayInitTypeSize=N) must also be skipped!
        if (td.IsNested)
        {
            var declHandle = td.GetDeclaringType();
            if (!declHandle.IsNil && ShouldSkipType(reader, reader.GetTypeDefinition(declHandle), includeGenerated))
            {
                return true;
            }
        }

        if (!includeGenerated)
        {
            if (HasCompilerOrGeneratedAttribute(reader, td))
            {
                return true;
            }

            var ns = reader.GetString(td.Namespace);
            if (ns.StartsWith("AspNetCoreGeneratedDocument", StringComparison.Ordinal) ||
                ns == "Microsoft.CodeAnalysis" && name == "EmbeddedAttribute" ||
                ns == "System.Runtime.CompilerServices" && name is "RefSafetyRulesAttribute" or "IsExternalInit" or "RequiredMemberAttribute" or "CompilerFeatureRequiredAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCompilerOrGeneratedAttribute(MetadataReader reader, TypeDefinition td)
    {
        foreach (var ah in td.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(ah);
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                var parentName = MetadataNameFormatter.FormatEntityHandleType(reader, reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent);
                if (parentName.EndsWith("CompilerGeneratedAttribute", StringComparison.Ordinal) ||
                    parentName.EndsWith("GeneratedCodeAttribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasCompilerGeneratedAttribute(MetadataReader reader, TypeDefinition td) =>
        HasCompilerOrGeneratedAttribute(reader, td);

    private static bool IsTrackableMethod(MetadataReader reader, MethodDefinition md)
    {
        var name = reader.GetString(md.Name);
        if (name.StartsWith('<') && name != "<Main>$")
        {
            return false;
        }

        if (md.Attributes.HasFlag(MethodAttributes.SpecialName) && name is not (".ctor" or ".cctor"))
        {
            return false;
        }

        return true;
    }

    private static ModelTypeKind DetermineTypeKind(MetadataReader reader, TypeDefinition td)
    {
        if (td.Attributes.HasFlag(TypeAttributes.Interface)) return ModelTypeKind.Interface;

        if (!td.BaseType.IsNil)
        {
            var baseName = MetadataNameFormatter.FormatEntityHandleType(reader, td.BaseType);
            if (baseName == "System.Enum") return ModelTypeKind.Enum;
            if (baseName == "System.ValueType") return ModelTypeKind.Struct;
            if (baseName is "System.MulticastDelegate" or "System.Delegate") return ModelTypeKind.Delegate;
        }

        // Detect C# record types via compiler-synthesized <Clone>$ or EqualityContract
        if (td.GetMethods().Any(mh => reader.GetString(reader.GetMethodDefinition(mh).Name) is "<Clone>$" or "get_EqualityContract"))
        {
            return ModelTypeKind.Record;
        }

        return ModelTypeKind.Class;
    }

    private static string ResolveNamespace(MetadataReader reader, TypeDefinition td)
    {
        while (td.IsNested) td = reader.GetTypeDefinition(td.GetDeclaringType());
        var ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? "(global)" : ns;
    }

    private static AccessibilityKind MapTypeAccessibility(TypeAttributes attrs) => (attrs & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => AccessibilityKind.Public,
        TypeAttributes.NestedFamily => AccessibilityKind.Protected,
        TypeAttributes.NestedFamORAssem => AccessibilityKind.ProtectedInternal,
        TypeAttributes.NestedFamANDAssem => AccessibilityKind.PrivateProtected,
        TypeAttributes.NestedPrivate => AccessibilityKind.Private,
        _ => AccessibilityKind.Internal
    };

    private static AccessibilityKind MapMethodAccessibility(MethodAttributes attrs) => (attrs & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => AccessibilityKind.Public,
        MethodAttributes.Family => AccessibilityKind.Protected,
        MethodAttributes.FamORAssem => AccessibilityKind.ProtectedInternal,
        MethodAttributes.FamANDAssem => AccessibilityKind.PrivateProtected,
        MethodAttributes.Assembly => AccessibilityKind.Internal,
        _ => AccessibilityKind.Private
    };

    private static string ExtractSimpleNameFromTypeId(string typeId)
    {
        var lt = typeId.IndexOf('<');
        var withoutGeneric = lt > 0 ? typeId[..lt] : typeId;
        var dot = withoutGeneric.LastIndexOf('.');
        return dot >= 0 ? withoutGeneric[(dot + 1)..] : withoutGeneric;
    }

    private sealed record AssemblyDiscoveryResult(
        string RootDir,
        List<string> Assemblies,
        int ExpectedProjects,
        int ExcludedTestProjects,
        List<string> MissingProjects);

    private static AssemblyDiscoveryResult DiscoverAssemblies(
        string fullTarget,
        ExtractionOptions options,
        List<ExtractionDiagnostic> diagnostics)
    {
        if (File.Exists(fullTarget))
        {
            var ext = Path.GetExtension(fullTarget);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new AssemblyDiscoveryResult(Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory(), [fullTarget], 1, 0, []);
            }

            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
                var dll = FindBuiltAssemblyForCsproj(fullTarget, options.TargetFramework);
                var missing = dll is null ? new List<string> { Path.GetFileNameWithoutExtension(fullTarget) } : [];
                return new AssemblyDiscoveryResult(dir, dll is not null ? [dll] : [], 1, 0, missing);
            }

            if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var rootDir = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
                var csprojs = DiscoverSolutionProjects(fullTarget);
                var dlls = new List<string>();
                var missing = new List<string>();
                var excludedTests = 0;
                var expected = 0;
                foreach (var csproj in csprojs)
                {
                    var name = Path.GetFileNameWithoutExtension(csproj);
                    if (!options.IncludeTestProjects && IsTestProject(name, csproj))
                    {
                        excludedTests++;
                        continue;
                    }

                    expected++;
                    var dll = FindBuiltAssemblyForCsproj(csproj, options.TargetFramework);
                    if (dll is not null) dlls.Add(dll);
                    else
                    {
                        missing.Add(name);
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Warning,
                            $"No built assembly found under bin/ for '{name}'. Run 'dotnet build' before using IL backend.",
                            name));
                    }
                }
                return new AssemblyDiscoveryResult(rootDir, dlls, expected, excludedTests, missing);
            }
        }

        if (Directory.Exists(fullTarget))
        {
            var slnx = Directory.GetFiles(fullTarget, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (slnx is not null) return DiscoverAssemblies(slnx, options, diagnostics);

            var sln = Directory.GetFiles(fullTarget, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln is not null) return DiscoverAssemblies(sln, options, diagnostics);

            var csproj = Directory.GetFiles(fullTarget, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj is not null) return DiscoverAssemblies(csproj, options, diagnostics);

            var allDlls = Directory.GetFiles(fullTarget, "*.dll", SearchOption.TopDirectoryOnly);
            var excludedTests = 0;
            var dlls = new List<string>();
            foreach (var p in allDlls.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!options.IncludeTestProjects && IsTestProjectName(Path.GetFileNameWithoutExtension(p)))
                {
                    excludedTests++;
                    continue;
                }
                dlls.Add(p);
            }
            return new AssemblyDiscoveryResult(fullTarget, dlls, dlls.Count, excludedTests, []);
        }

        throw new FileNotFoundException($"Target path not found: '{fullTarget}'.", fullTarget);
    }

    private static List<string> DiscoverSolutionProjects(string solutionPath)
    {
        var baseDir = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = XDocument.Load(solutionPath);
            return doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("Path")?.Value)
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFullPath(Path.Combine(baseDir, p!.Replace('\\', Path.DirectorySeparatorChar))))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var results = new List<string>();
        foreach (var line in File.ReadLines(solutionPath))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
            var parts = line.Split(',');
            if (parts.Length >= 2)
            {
                var rel = parts[1].Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar);
                if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, rel));
                    if (File.Exists(full)) results.Add(full);
                }
            }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindBuiltAssemblyForCsproj(string csprojPath, string? requestedTfm)
    {
        var projDir = Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrWhiteSpace(projDir)) return null;

        var binDir = Path.Combine(projDir, "bin");
        if (!Directory.Exists(binDir)) return null;

        var assemblyName = ReadAssemblyName(csprojPath) ?? Path.GetFileNameWithoutExtension(csprojPath);
        var candidates = Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(requestedTfm))
        {
            var tfmMatch = candidates
                .Where(p => p.Split(['/', '\\']).Any(s => s.Equals(requestedTfm, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (tfmMatch is not null) return tfmMatch;
        }

        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static string? ReadAssemblyName(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var val = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value?.Trim();
            return string.IsNullOrWhiteSpace(val) || val.Contains("$(", StringComparison.Ordinal) ? null : val;
        }
        catch { return null; }
    }

    private static LoadedAssembly? TryOpenAssembly(string dllPath, List<ExtractionDiagnostic> diagnostics)
    {
        FileStream? peStream = null;
        PEReader? peReader = null;
        MetadataReaderProvider? pdbProvider = null;
        try
        {
            peStream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            peReader = new PEReader(peStream);
            if (!peReader.HasMetadata)
            {
                peReader.Dispose();
                peStream.Dispose();
                return null;
            }

            var reader = peReader.GetMetadataReader();
            var asmName = reader.GetString(reader.GetAssemblyDefinition().Name);

            // Try adjacent Portable PDB first; if missing or non-portable, fall back to embedded Portable PDB
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                try
                {
                    var pdbBytes = File.ReadAllBytes(pdbPath);
                    pdbProvider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdbBytes));
                }
                catch { }
            }

            if (pdbProvider is null)
            {
                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        try
                        {
                            pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                            break;
                        }
                        catch { }
                    }
                }
            }

            var inferredTfm = Path.GetFileName(Path.GetDirectoryName(dllPath));
            return new LoadedAssembly(dllPath, asmName, inferredTfm, peStream, peReader, reader, pdbProvider, pdbProvider?.GetMetadataReader());
        }
        catch (Exception ex)
        {
            pdbProvider?.Dispose();
            peReader?.Dispose();
            peStream?.Dispose();
            diagnostics.Add(new ExtractionDiagnostic(
                ExtractionDiagnosticSeverity.Warning,
                $"Skipped non-managed or unreadable assembly '{dllPath}': {ex.Message}",
                "MetadataCodeGraphExtractor"));
            return null;
        }
    }

    private static string? ExtractTargetFrameworkAttribute(MetadataReader reader)
    {
        try
        {
            foreach (var ah in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(ah);
                if (attr.Constructor.Kind == HandleKind.MemberReference)
                {
                    var parent = MetadataNameFormatter.FormatEntityHandleType(reader, reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent);
                    if (parent.EndsWith("TargetFrameworkAttribute", StringComparison.Ordinal))
                    {
                        var blob = reader.GetBlobReader(attr.Value);
                        if (blob.ReadUInt16() == 1)
                        {
                            var raw = blob.ReadSerializedString();
                            if (!string.IsNullOrWhiteSpace(raw)) return raw;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? NormalizeTargetFramework(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith(".NETCoreApp,Version=v", StringComparison.OrdinalIgnoreCase))
        {
            return "net" + raw[".NETCoreApp,Version=v".Length..];
        }
        if (raw.StartsWith(".NETStandard,Version=v", StringComparison.OrdinalIgnoreCase))
        {
            return "netstandard" + raw[".NETStandard,Version=v".Length..];
        }
        if (raw.StartsWith(".NETFramework,Version=v", StringComparison.OrdinalIgnoreCase))
        {
            return "net" + raw[".NETFramework,Version=v".Length..].Replace(".", string.Empty);
        }
        return raw;
    }

    private static bool IsTestProject(string name, string csprojPath)
    {
        if (IsTestProjectName(name)) return true;
        try
        {
            var doc = XDocument.Load(csprojPath);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Any(pkg => !string.IsNullOrWhiteSpace(pkg) &&
                            (pkg.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                             pkg.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
                             pkg.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) ||
                             pkg.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    private static bool IsTestProjectName(string name) =>
        name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".Test.", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".Testing", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestAttributeName(string attrName) =>
        attrName is "FactAttribute" or "TheoryAttribute" or "TestAttribute" or "TestCaseAttribute" or "TestMethodAttribute" or "DataTestMethodAttribute";

    private static bool IsEndpointAttributeName(string attrName) =>
        attrName is "HttpGetAttribute" or "HttpPostAttribute" or "HttpPutAttribute" or "HttpDeleteAttribute" or "HttpPatchAttribute" or "RouteAttribute" or "McpServerToolAttribute";

    private static string MakeRelativePath(string rootDir, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.GetRelativePath(rootDir, path).Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    private sealed class ExtractionContext(
        string rootDir,
        ExtractionOptions options,
        HashSet<string> knownAssemblies,
        HashSet<string> knownTypeIds,
        Dictionary<string, string> openGenericIndex,
        HashSet<string> knownMethodIds,
        Dictionary<(string OwnerId, string MethodName, int ParamCount), string> methodIdsBySimpleKey)
    {
        public string RootDir { get; } = rootDir;
        public ExtractionOptions Options { get; } = options;
        public HashSet<string> KnownAssemblies { get; } = knownAssemblies;
        public HashSet<string> KnownTypeIds { get; } = knownTypeIds;
        public Dictionary<string, string> OpenGenericIndex { get; } = openGenericIndex;
        public HashSet<string> KnownMethodIds { get; } = knownMethodIds;
        public Dictionary<(string OwnerId, string MethodName, int ParamCount), string> MethodIdsBySimpleKey { get; } = methodIdsBySimpleKey;
        public List<CodeNode> Nodes { get; } = [];
        public Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> EdgeAggregator { get; } = [];
        public List<ModelProjectInfo> ProjectInfos { get; } = [];
        public HashSet<string> NamespacesSeen { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FoldedMethodContribution
    {
        public int ExtraComplexity { get; set; }
        public List<(SourceLocation? Loc, int Lines)> Locations { get; } = [];
        public List<(EntityHandle Token, DependencyKind Kind)> IlReferences { get; } = [];
        public HashSet<string> LocalFields { get; } = new(StringComparer.Ordinal);
        public HashSet<string> LocalMethods { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TypeStatsAccumulator(int initialMemberCount)
    {
        private readonly Dictionary<string, (int MinLine, int MaxLine)> _lineSpansByFile = new(StringComparer.OrdinalIgnoreCase);

        public SourceLocation? PrimaryLocation { get; private set; }
        public int TotalCc { get; set; }
        public int MaxMethodCc { get; set; }
        public int MaxCtorParams { get; set; }
        public int BestCtorParamCount { get; set; } = -1;
        public string? BestCtorParams { get; set; }
        public int MemberCount { get; set; } = initialMemberCount;
        public bool HasEntryPoint { get; set; }

        public void RecordLocation(SourceLocation loc, int lineSpan)
        {
            if (PrimaryLocation is null || (string.Equals(loc.FilePath, PrimaryLocation.FilePath, StringComparison.OrdinalIgnoreCase) && loc.Line < PrimaryLocation.Line))
            {
                PrimaryLocation = loc;
            }

            var endLine = loc.Line + Math.Max(0, lineSpan - 1);
            if (_lineSpansByFile.TryGetValue(loc.FilePath, out var existing))
            {
                _lineSpansByFile[loc.FilePath] = (Math.Min(existing.MinLine, loc.Line), Math.Max(existing.MaxLine, endLine));
            }
            else
            {
                _lineSpansByFile[loc.FilePath] = (loc.Line, endLine);
            }
        }

        public int ComputeLineCount()
        {
            if (_lineSpansByFile.Count == 0)
            {
                return Math.Max(1, MemberCount * 3);
            }

            var sum = 0;
            foreach (var (min, max) in _lineSpansByFile.Values)
            {
                sum += Math.Max(1, max - min + 1);
            }
            return sum;
        }
    }

    private sealed class LoadedAssembly(
        string filePath,
        string assemblyName,
        string? inferredTfm,
        FileStream peStream,
        PEReader peReader,
        MetadataReader reader,
        MetadataReaderProvider? pdbProvider,
        MetadataReader? pdbReader) : IDisposable
    {
        public string FilePath { get; } = filePath;
        public string AssemblyName { get; } = assemblyName;
        public string? InferredTfm { get; } = inferredTfm;
        public PEReader PeReader { get; } = peReader;
        public MetadataReader Reader { get; } = reader;
        public MetadataReader? PdbReader { get; } = pdbReader;

        public void Dispose()
        {
            pdbProvider?.Dispose();
            PeReader.Dispose();
            peStream.Dispose();
        }
    }
}
