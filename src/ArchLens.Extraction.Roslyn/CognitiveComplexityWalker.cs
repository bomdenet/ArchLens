using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Computes SonarSource-style Cognitive Complexity for a C# method, accessor, or syntax node.
/// Starts at 0, penalizes nesting depth, treats switch blocks as a single structural increment,
/// counts sequences of logical operators (&& / || and C# 9+ pattern combinators and / or),
/// and counts direct recursion.
/// </summary>
public sealed class CognitiveComplexityWalker : CSharpSyntaxWalker
{
    private readonly SyntaxNode _root;
    private readonly string? _methodName;
    private readonly int? _parameterCount;
    private readonly SemanticModel? _semanticModel;
    private readonly IMethodSymbol? _enclosingMethod;
    private int _nesting;
    private bool _countedRecursion;

    private CognitiveComplexityWalker(
        SyntaxNode root,
        string? methodName,
        int? parameterCount,
        SemanticModel? semanticModel,
        IMethodSymbol? enclosingMethod)
    {
        _root = root;
        _methodName = methodName;
        _parameterCount = parameterCount;
        _semanticModel = semanticModel;
        _enclosingMethod = enclosingMethod;
    }

    public int Complexity { get; private set; }

    public static int Calculate(
        SyntaxNode node,
        string? methodName = null,
        int? parameterCount = null,
        SemanticModel? semanticModel = null,
        IMethodSymbol? enclosingMethod = null)
    {
        var effectiveMethod = enclosingMethod;
        if (effectiveMethod is null && semanticModel is not null && node is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
        {
            effectiveMethod = semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        }

        var effectiveName = methodName ?? effectiveMethod?.Name ?? node switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            LocalFunctionStatementSyntax lf => lf.Identifier.ValueText,
            _ => null
        };

        var effectiveParamCount = parameterCount ?? effectiveMethod?.Parameters.Length ?? node switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters.Count,
            LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters.Count,
            _ => null
        };

        var walker = new CognitiveComplexityWalker(node, effectiveName, effectiveParamCount, semanticModel, effectiveMethod?.OriginalDefinition);
        walker.Visit(node);
        return walker.Complexity;
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        var isElseIf = node.Parent is ElseClauseSyntax;
        Complexity += isElseIf ? 1 : 1 + _nesting;

        Visit(node.Condition);

        _nesting++;
        Visit(node.Statement);
        _nesting--;

        if (node.Else is not null)
        {
            if (node.Else.Statement is IfStatementSyntax elseIf)
            {
                Visit(elseIf);
            }
            else
            {
                Complexity += 1;
                _nesting++;
                Visit(node.Else.Statement);
                _nesting--;
            }
        }
    }

    public override void VisitSwitchStatement(SwitchStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitSwitchStatement(node);
        _nesting--;
    }

    public override void VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitSwitchExpression(node);
        _nesting--;
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitForStatement(node);
        _nesting--;
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitForEachStatement(node);
        _nesting--;
    }

    public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitForEachVariableStatement(node);
        _nesting--;
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitWhileStatement(node);
        _nesting--;
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitDoStatement(node);
        _nesting--;
    }

    public override void VisitCatchClause(CatchClauseSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitCatchClause(node);
        _nesting--;
    }

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        Complexity += 1 + _nesting;
        _nesting++;
        base.VisitConditionalExpression(node);
        _nesting--;
    }

    public override void VisitGotoStatement(GotoStatementSyntax node)
    {
        Complexity += 1;
        base.VisitGotoStatement(node);
    }

    public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        VisitNestingOnly(node, base.VisitSimpleLambdaExpression);
    }

    public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        VisitNestingOnly(node, base.VisitParenthesizedLambdaExpression);
    }

    public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        VisitNestingOnly(node, base.VisitAnonymousMethodExpression);
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        VisitNestingOnly(node, base.VisitLocalFunctionStatement);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (IsLogicalOperator(node.Kind()))
        {
            SyntaxKind? lastOp = null;
            VisitLogicalChain(node, ref lastOp);
            return;
        }

        base.VisitBinaryExpression(node);
    }

    public override void VisitBinaryPattern(BinaryPatternSyntax node)
    {
        if (IsLogicalPattern(node.Kind()))
        {
            SyntaxKind? lastOp = null;
            VisitPatternChain(node, ref lastOp);
            return;
        }

        base.VisitBinaryPattern(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (!_countedRecursion && !string.IsNullOrEmpty(_methodName))
        {
            if (_semanticModel is not null && _enclosingMethod is not null)
            {
                var invokedSym = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
                if (invokedSym is not null)
                {
                    if (SymbolEqualityComparer.Default.Equals(invokedSym.OriginalDefinition, _enclosingMethod))
                    {
                        Complexity += 1;
                        _countedRecursion = true;
                    }
                    base.VisitInvocationExpression(node);
                    return;
                }
            }

            var invokedName = node.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax gen => gen.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: SimpleNameSyntax sn } => sn.Identifier.ValueText,
                _ => null
            };

            var arityMatches = !_parameterCount.HasValue ||
                               node.ArgumentList.Arguments.Count == _parameterCount.Value;

            if (arityMatches && string.Equals(invokedName, _methodName, StringComparison.Ordinal))
            {
                Complexity += 1;
                _countedRecursion = true;
            }
        }

        base.VisitInvocationExpression(node);
    }

    private void VisitNestingOnly<TNode>(TNode node, Action<TNode> baseVisit) where TNode : SyntaxNode
    {
        if (ReferenceEquals(node, _root))
        {
            baseVisit(node);
            return;
        }

        _nesting++;
        baseVisit(node);
        _nesting--;
    }

    private void VisitLogicalChain(ExpressionSyntax expr, ref SyntaxKind? lastOp)
    {
        while (expr is ParenthesizedExpressionSyntax paren)
        {
            expr = paren.Expression;
        }

        if (expr is BinaryExpressionSyntax bin && IsLogicalOperator(bin.Kind()))
        {
            VisitLogicalChain(bin.Left, ref lastOp);
            if (lastOp != bin.Kind())
            {
                Complexity += 1;
                lastOp = bin.Kind();
            }
            VisitLogicalChain(bin.Right, ref lastOp);
            return;
        }

        Visit(expr);
    }

    private void VisitPatternChain(PatternSyntax pattern, ref SyntaxKind? lastOp)
    {
        while (pattern is ParenthesizedPatternSyntax paren)
        {
            pattern = paren.Pattern;
        }

        if (pattern is BinaryPatternSyntax bin && IsLogicalPattern(bin.Kind()))
        {
            VisitPatternChain(bin.Left, ref lastOp);
            if (lastOp != bin.Kind())
            {
                Complexity += 1;
                lastOp = bin.Kind();
            }
            VisitPatternChain(bin.Right, ref lastOp);
            return;
        }

        Visit(pattern);
    }

    private static bool IsLogicalOperator(SyntaxKind kind) =>
        kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression;

    private static bool IsLogicalPattern(SyntaxKind kind) =>
        kind is SyntaxKind.AndPattern or SyntaxKind.OrPattern;
}
