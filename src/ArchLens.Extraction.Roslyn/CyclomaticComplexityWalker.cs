using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Computes McCabe cyclomatic complexity for a C# syntax node (method, property accessor, or type).
/// </summary>
internal sealed class CyclomaticComplexityWalker : CSharpSyntaxWalker
{
    public int Complexity { get; private set; } = 1;

    public static int Calculate(SyntaxNode node)
    {
        var walker = new CyclomaticComplexityWalker();
        walker.Visit(node);
        return walker.Complexity;
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        Complexity++;
        base.VisitIfStatement(node);
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        Complexity++;
        base.VisitWhileStatement(node);
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        Complexity++;
        base.VisitForStatement(node);
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        Complexity++;
        base.VisitForEachStatement(node);
    }

    public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        Complexity++;
        base.VisitForEachVariableStatement(node);
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        Complexity++;
        base.VisitDoStatement(node);
    }

    public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node)
    {
        Complexity++;
        base.VisitCaseSwitchLabel(node);
    }

    public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node)
    {
        Complexity++;
        base.VisitCasePatternSwitchLabel(node);
    }

    public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node)
    {
        if (node.Pattern is not DiscardPatternSyntax)
        {
            Complexity++;
        }
        base.VisitSwitchExpressionArm(node);
    }

    public override void VisitCatchClause(CatchClauseSyntax node)
    {
        Complexity++;
        base.VisitCatchClause(node);
    }

    public override void VisitCatchFilterClause(CatchFilterClauseSyntax node)
    {
        Complexity++;
        base.VisitCatchFilterClause(node);
    }

    public override void VisitWhenClause(WhenClauseSyntax node)
    {
        Complexity++;
        base.VisitWhenClause(node);
    }

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        Complexity++;
        base.VisitConditionalExpression(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            Complexity++;
        }
        base.VisitAssignmentExpression(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression) ||
            node.IsKind(SyntaxKind.LogicalOrExpression) ||
            node.IsKind(SyntaxKind.CoalesceExpression))
        {
            Complexity++;
        }
        base.VisitBinaryExpression(node);
    }

    public override void VisitBinaryPattern(BinaryPatternSyntax node)
    {
        if (node.IsKind(SyntaxKind.AndPattern) || node.IsKind(SyntaxKind.OrPattern))
        {
            Complexity++;
        }
        base.VisitBinaryPattern(node);
    }

    public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        Complexity++;
        base.VisitConditionalAccessExpression(node);
    }
}
