using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Visitors;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// Expression propagation and Phi node elimination pass
    /// </summary>
    /// <remarks>
    /// This pass handles:
    /// 1. Phi node simplification (when all branches have the same value)
    /// 2. Phi node elimination (replace with actual expressions)
    /// 3. Expression propagation and optimization
    /// </remarks>
    class ExpressionPropagationPass : IPass
    {
        private Dictionary<int, Expression> _phiReplacements = new Dictionary<int, Expression>();

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _phiReplacements.Clear();
            
            // First pass: analyze and simplify Phi nodes
            foreach (var block in context.Blocks)
            {
                AnalyzePhiNodes(block);
            }

            // Second pass: propagate expressions and eliminate Phi nodes
            foreach (var block in context.Blocks)
            {
                ExpressionPropagation(block);
            }

            // Third pass: replace Phi nodes in all expressions recursively
            var replacer = new PhiReplacer(_phiReplacements);
            replacer.Visit(statement);
            
            // Fourth pass: clean up any remaining Phi expression statements
            CleanupPhiStatements(statement);

            return statement;
        }

        /// <summary>
        /// Analyze and simplify Phi nodes
        /// </summary>
        private void AnalyzePhiNodes(Block block)
        {
            if (block.Statements == null) return;

            for (int i = 0; i < block.Statements.Count; i++)
            {
                var stmt = block.Statements[i];
                
                if (stmt is ExpressionStatement expStmt && expStmt.Expression is PhiExpression phi)
                {
                    // Try to simplify the Phi node
                    var simplified = phi.Simplify();
                    if (simplified != phi)
                    {
                        // Replace with simplified expression
                        expStmt.Expression = simplified;
                        // Record replacement for later use
                        _phiReplacements[phi.Slot] = simplified;
                    }
                    else if (phi.PossibleExpressions.Count > 0)
                    {
                        // Even if not simplified, record the phi for potential resolution
                        _phiReplacements[phi.Slot] = phi;
                    }
                }
            }
        }

        /// <summary>
        /// Propagate expressions and eliminate unnecessary Phi nodes
        /// </summary>
        private void ExpressionPropagation(Block block)
        {
            if (block.Statements == null) return;

            for (int i = block.Statements.Count - 1; i >= 0; i--)
            {
                var statement = block.Statements[i];

                if (statement is ExpressionStatement exp && exp.Expression is PhiExpression phi)
                {
                    // Strategy 1: All possible expressions are the same
                    if (phi.CanSimplify)
                    {
                        exp.Expression = phi.Simplify();
                        continue;
                    }

                    // Strategy 2: Phi with empty possible expressions - remove it
                    if (phi.PossibleExpressions.Count == 0)
                    {
                        block.Statements.RemoveAt(i);
                        continue;
                    }

                    // Strategy 3: Try to resolve Phi from control flow
                    if (block.From.Count == 2 && phi.PossibleExpressions.Count == 2)
                    {
                        // This might be an if-else merge point
                        TryResolveConditionalPhi(block, phi);
                    }

                    // Strategy 4: Single possible expression
                    if (phi.PossibleExpressions.Count == 1)
                    {
                        exp.Expression = phi.PossibleExpressions[0];
                    }
                }
            }
        }

        /// <summary>
        /// Try to resolve Phi node from conditional branches (if-else)
        /// </summary>
        private void TryResolveConditionalPhi(Block block, PhiExpression phi)
        {
            if (phi.PossibleExpressions.Count != 2)
            {
                return;
            }

            // Get the two predecessor blocks
            var pred1 = block.From[0];
            var pred2 = block.From[1];

            // Try to find condition from a common predecessor
            Block conditionBlock = null;
            ConditionExpression condition = null;

            // Check if both predecessors have a common predecessor with a condition
            var commonPreds = pred1.From.Intersect(pred2.From).ToList();
            foreach (var commonPred in commonPreds)
            {
                if (commonPred.Statements != null && commonPred.Statements.Count > 0)
                {
                    var lastStmt = commonPred.Statements.LastOrDefault();
                    if (lastStmt is ConditionExpression cond)
                    {
                        condition = cond;
                        conditionBlock = commonPred;
                        break;
                    }
                }
            }

            // If we found a condition, try to determine which branch is which
            if (condition != null)
            {
                // Determine which predecessor is the 'then' branch and which is 'else'
                // by checking if the condition jumps to pred1 or pred2
                bool pred1IsThen = conditionBlock.To.IndexOf(pred1) == 0;
                
                phi.Condition = condition;
                phi.ThenBranch = pred1IsThen ? phi.PossibleExpressions[0] : phi.PossibleExpressions[1];
                phi.ElseBranch = pred1IsThen ? phi.PossibleExpressions[1] : phi.PossibleExpressions[0];
            }
        }

        /// <summary>
        /// Clean up any remaining Phi expression statements
        /// </summary>
        private void CleanupPhiStatements(BlockStatement statement)
        {
            var cleaner = new PhiStatementCleaner();
            cleaner.Visit(statement);
        }

        /// <summary>
        /// Visitor to replace Phi expressions in the AST
        /// </summary>
        private class PhiReplacer : BaseVisitor
        {
            private Dictionary<int, Expression> _replacements;

            public PhiReplacer(Dictionary<int, Expression> replacements)
            {
                _replacements = replacements;
            }

            internal override void VisitBinaryExpr(BinaryExpression binary)
            {
                // Replace Phi in left operand
                binary.Left = ReplacePhi(binary.Left);
                // Replace Phi in right operand  
                binary.Right = ReplacePhi(binary.Right);
                
                // After replacement, check if the binary itself contains any nested Phi
                // This handles cases like: (phi_value + 1) where phi should be resolved first
                if (binary.Left is PhiExpression phiLeft)
                {
                    if (phiLeft.CanSimplify)
                    {
                        binary.Left = phiLeft.Simplify();
                    }
                }
                
                if (binary.Right is PhiExpression phiRight)
                {
                    if (phiRight.CanSimplify)
                    {
                        binary.Right = phiRight.Simplify();
                    }
                }

                base.VisitBinaryExpr(binary);
            }

            internal override void VisitUnaryExpr(UnaryExpression unary)
            {
                // Replace Phi in target
                unary.Target = ReplacePhi(unary.Target);
                
                // After replacement, simplify if target is still a Phi
                if (unary.Target is PhiExpression phi && phi.CanSimplify)
                {
                    unary.Target = phi.Simplify();
                }

                base.VisitUnaryExpr(unary);
            }

            internal override void VisitReturnExpr(ReturnExpression ret)
            {
                if (ret.Return != null)
                {
                    ret.Return = ReplacePhi(ret.Return);
                }

                base.VisitReturnExpr(ret);
            }

            internal override void VisitInvokeExpr(InvokeExpression invoke)
            {
                if (invoke.Instance != null)
                {
                    invoke.Instance = ReplacePhi(invoke.Instance);
                }

                if (invoke.MethodExpression != null)
                {
                    invoke.MethodExpression = ReplacePhi(invoke.MethodExpression);
                }

                for (int i = 0; i < invoke.Parameters.Count; i++)
                {
                    invoke.Parameters[i] = ReplacePhi(invoke.Parameters[i]);
                }

                base.VisitInvokeExpr(invoke);
            }

            internal override void VisitPropertyAccessExpr(PropertyAccessExpression prop)
            {
                if (prop.Instance != null)
                {
                    prop.Instance = ReplacePhi(prop.Instance);
                }

                if (prop.Property != null)
                {
                    prop.Property = ReplacePhi(prop.Property);
                }

                base.VisitPropertyAccessExpr(prop);
            }

            internal override void VisitConditionExpr(ConditionExpression condition)
            {
                if (condition.Condition != null)
                {
                    condition.Condition = ReplacePhi(condition.Condition);
                }

                base.VisitConditionExpr(condition);
            }

            private Expression ReplacePhi(Expression expr)
            {
                if (expr is PhiExpression phi)
                {
                    // If Phi can be simplified, return the simplified version
                    if (phi.CanSimplify)
                    {
                        return phi.Simplify();
                    }
                    
                    // If Phi is conditional, keep it but ensure children are replaced
                    if (phi.IsConditional)
                    {
                        phi.ThenBranch = ReplacePhi(phi.ThenBranch);
                        phi.ElseBranch = ReplacePhi(phi.ElseBranch);
                        return phi;
                    }

                    // Try to find a replacement from our map
                    if (_replacements.TryGetValue(phi.Slot, out var replacement))
                    {
                        if (replacement != phi && !(replacement is PhiExpression))
                        {
                            return replacement;
                        }
                    }
                }
                else if (expr is LocalExpression local)
                {
                    // Check if this local has a Phi replacement
                    if (_replacements.TryGetValue(local.Slot, out var replacement))
                    {
                        if (replacement is PhiExpression phi2)
                        {
                            if (phi2.CanSimplify)
                            {
                                return phi2.Simplify();
                            }
                        }
                        else
                        {
                            return replacement;
                        }
                    }
                }

                return expr;
            }
        }

        /// <summary>
        /// Visitor to clean up Phi expression statements
        /// </summary>
        private class PhiStatementCleaner : BaseVisitor
        {
            internal override void VisitBlockStmt(BlockStatement block)
            {
                if (block.Statements != null)
                {
                    // Remove standalone Phi expression statements
                    block.Statements.RemoveAll(stmt =>
                    {
                        if (stmt is ExpressionStatement expStmt && expStmt.Expression is PhiExpression phi)
                        {
                            // Keep only if it's a conditional phi (ternary) or can't be simplified
                            return !phi.IsConditional && phi.CanSimplify;
                        }
                        return false;
                    });
                }

                base.VisitBlockStmt(block);
            }
        }
    }
}