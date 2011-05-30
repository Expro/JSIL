﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class SimplifyOperators : JSAstVisitor {
        public readonly TypeSystem TypeSystem;
        public readonly JSILIdentifier JSIL;
        public readonly JSSpecialIdentifiers JS;

        public readonly Dictionary<JSBinaryOperator, JSBinaryOperator> InvertedOperators = new Dictionary<JSBinaryOperator, JSBinaryOperator> {
            { JSOperator.LessThan, JSOperator.GreaterThanOrEqual },
            { JSOperator.LessThanOrEqual, JSOperator.GreaterThan },
            { JSOperator.GreaterThan, JSOperator.LessThanOrEqual },
            { JSOperator.GreaterThanOrEqual, JSOperator.LessThan },
            { JSOperator.Equal, JSOperator.NotEqual },
            { JSOperator.NotEqual, JSOperator.Equal }
        };
        public readonly Dictionary<JSBinaryOperator, JSAssignmentOperator> CompoundAssignments = new Dictionary<JSBinaryOperator, JSAssignmentOperator> {
            { JSOperator.Add, JSOperator.AddAssignment },
            { JSOperator.Subtract, JSOperator.SubtractAssignment },
            { JSOperator.Multiply, JSOperator.MultiplyAssignment }
        };
        public readonly Dictionary<JSBinaryOperator, JSUnaryOperator> PrefixOperators = new Dictionary<JSBinaryOperator, JSUnaryOperator> {
            { JSOperator.Add, JSOperator.PreIncrement },
            { JSOperator.Subtract, JSOperator.PreDecrement }
        };

        public SimplifyOperators (JSILIdentifier jsil, JSSpecialIdentifiers js, TypeSystem typeSystem) {
            JSIL = jsil;
            JS = js;
            TypeSystem = typeSystem;
        }

        public void VisitNode (JSInvocationExpression ie) {
            var target = ie.Target as JSDotExpression;
            JSType type = null;
            JSMethod method = null;
            if (target != null) {
                type = target.Target as JSType;
                method = target.Member as JSMethod;
            }

            if (method != null) {
                if (
                    (type != null) &&
                    ILBlockTranslator.TypesAreEqual(TypeSystem.String, type.Type) &&
                    (method.Method.Name == "Concat")
                ) {
                    if (
                        (ie.Arguments.Count > 2) &&
                        (ie.Arguments.All(
                            (arg) => ILBlockTranslator.TypesAreEqual(
                                TypeSystem.String, arg.GetExpectedType(TypeSystem)
                            )
                        ))
                    ) {
                        var boe = JSBinaryOperatorExpression.New(
                            JSOperator.Add,
                            ie.Arguments,
                            TypeSystem.String
                        );

                        ParentNode.ReplaceChild(
                            ie,
                            boe
                        );

                        Visit(boe);
                    } else if (
                        ie.Arguments.Count == 2
                    ) {
                        var lhs = ie.Arguments[0];
                        if (!ILBlockTranslator.TypesAreEqual(TypeSystem.String, lhs.GetExpectedType(TypeSystem)))
                            lhs = new JSInvocationExpression(JSDotExpression.New(lhs, JS.toString()));

                        var rhs = ie.Arguments[1];
                        if (!ILBlockTranslator.TypesAreEqual(TypeSystem.String, rhs.GetExpectedType(TypeSystem)))
                            rhs = new JSInvocationExpression(JSDotExpression.New(rhs, JS.toString()));

                        var boe = new JSBinaryOperatorExpression(
                            JSOperator.Add, lhs, rhs, TypeSystem.String
                        );

                        ParentNode.ReplaceChild(
                            ie, boe
                        );

                        Visit(boe);
                    } else {
                        var firstArg = ie.Arguments.FirstOrDefault();

                        ParentNode.ReplaceChild(
                            ie, firstArg
                        );

                        if (firstArg != null)
                            Visit(firstArg);
                    }
                    return;
                } else if (
                    ILBlockTranslator.IsDelegateType(method.Reference.DeclaringType) &&
                    (method.Method.Name == "Invoke")
                ) {
                    var newIe = new JSDelegateInvocationExpression(target.Target, method, ie.Arguments.ToArray());
                    ParentNode.ReplaceChild(ie, newIe);

                    Visit(newIe);
                    return;
                } else if (
                    (method.Reference.DeclaringType.FullName == "System.Type") &&
                    (method.Method.Name == "GetTypeFromHandle")
                ) {
                    var typeObj = ie.Arguments.FirstOrDefault();
                    ParentNode.ReplaceChild(ie, typeObj);

                    Visit(typeObj);
                    return;
                } else if (
                    method.Method.DeclaringType.Definition.FullName == "System.Array" &&
                    (ie.Arguments.Count == 1)
                ) {
                    switch (method.Method.Name) {
                        case "GetLength":
                        case "GetUpperBound": {
                            var index = ie.Arguments[0] as JSLiteral;
                            if (index != null) {
                                var newDot = JSDotExpression.New(target.Target, new JSStringIdentifier(
                                    String.Format("length{0}", Convert.ToInt32(index.Literal)), 
                                    TypeSystem.Int32
                                ));

                                if (method.Method.Name == "GetUpperBound") {
                                    var newExpr = new JSBinaryOperatorExpression(
                                        JSOperator.Subtract, newDot, JSLiteral.New(1), TypeSystem.Int32
                                    );
                                    ParentNode.ReplaceChild(ie, newExpr);
                                } else {
                                    ParentNode.ReplaceChild(ie, newDot);
                                }
                            }
                            break;
                        }
                        case "GetLowerBound":
                            ParentNode.ReplaceChild(ie, JSLiteral.New(0));
                            break;
                    }
                }
            }

            VisitChildren(ie);
        }

        public void VisitNode (JSUnaryOperatorExpression uoe) {
            var isBoolean = ILBlockTranslator.IsBoolean(uoe.GetExpectedType(TypeSystem));

            if (isBoolean) {
                if (uoe.Operator == JSOperator.IsTrue) {
                    ParentNode.ReplaceChild(
                        uoe, uoe.Expression
                    );

                    Visit(uoe.Expression);
                    return;
                } else if (uoe.Operator == JSOperator.LogicalNot) {
                    var nestedUoe = uoe.Expression as JSUnaryOperatorExpression;
                    var boe = uoe.Expression as JSBinaryOperatorExpression;

                    JSBinaryOperator newOperator;
                    if ((boe != null) && 
                        InvertedOperators.TryGetValue(boe.Operator, out newOperator)
                    ) {
                        var newBoe = new JSBinaryOperatorExpression(
                            newOperator, boe.Left, boe.Right, boe.ExpectedType
                        );

                        ParentNode.ReplaceChild(uoe, newBoe);
                        Visit(newBoe);

                        return;
                    } else if (
                        (nestedUoe != null) && 
                        (nestedUoe.Operator == JSOperator.LogicalNot)
                    ) {
                        var nestedExpression = nestedUoe.Expression;

                        ParentNode.ReplaceChild(uoe, nestedExpression);
                        Visit(nestedExpression);

                        return;
                    }
                }
            }

            VisitChildren(uoe);
        }

        public void VisitNode (JSBinaryOperatorExpression boe) {
            JSExpression left, right, nestedLeft;

            if (!JSReferenceExpression.TryDereference(JSIL, boe.Left, out left))
                left = boe.Left;
            if (!JSReferenceExpression.TryDereference(JSIL, boe.Right, out right))
                right = boe.Right;

            var nestedBoe = right as JSBinaryOperatorExpression;
            var isAssignment = (boe.Operator == JSOperator.Assignment);
            var leftNew = left as JSNewExpression;
            var rightNew = right as JSNewExpression;
            var leftVar = left as JSVariable;

            if (
                isAssignment && (nestedBoe != null) && 
                (left.IsConstant || (leftVar != null) || left is JSDotExpression) &&
                !(ParentNode is JSVariableDeclarationStatement)
            ) {
                JSUnaryOperator prefixOperator;
                JSAssignmentOperator compoundOperator;

                if (!JSReferenceExpression.TryDereference(JSIL, nestedBoe.Left, out nestedLeft))
                    nestedLeft = nestedBoe.Left;

                var rightLiteral = nestedBoe.Right as JSIntegerLiteral;
                var areEqual = left.Equals(nestedLeft);

                if (
                    areEqual &&
                    PrefixOperators.TryGetValue(nestedBoe.Operator, out prefixOperator) &&
                    (rightLiteral != null) && (rightLiteral.Value == 1)
                ) {
                    var newUoe = new JSUnaryOperatorExpression(
                        prefixOperator, boe.Left,
                        boe.GetExpectedType(TypeSystem)
                    );

                    ParentNode.ReplaceChild(boe, newUoe);
                    Visit(newUoe);

                    return;
                } else if (
                    areEqual && 
                    CompoundAssignments.TryGetValue(nestedBoe.Operator, out compoundOperator)
                ) {
                    var newBoe = new JSBinaryOperatorExpression(
                        compoundOperator, boe.Left, nestedBoe.Right,
                        boe.GetExpectedType(TypeSystem)
                    );

                    ParentNode.ReplaceChild(boe, newBoe);
                    Visit(newBoe);

                    return;
                }
            } else if (
                isAssignment && (leftNew != null) &&
                (rightNew != null)
            ) {
                var rightType = rightNew.Type as JSDotExpression;
                if (
                    (rightType != null) &&
                    (rightType.Member.Identifier == "CollectionInitializer")
                ) {
                    var newInvocation = new JSInvocationExpression(
                        new JSDotExpression(
                            boe.Left,
                            new JSStringIdentifier("__Initialize__", boe.Left.GetExpectedType(TypeSystem))
                        ),
                        new JSArrayExpression(
                            TypeSystem.Object,
                            rightNew.Arguments.ToArray()
                        )
                    );

                    ParentNode.ReplaceChild(boe, newInvocation);
                    Visit(newInvocation);

                    return;
                }
            } else if (
                isAssignment && (leftVar != null) &&
                leftVar.IsThis
            ) {
                var leftType = leftVar.GetExpectedType(TypeSystem);
                if (!EmulateStructAssignment.IsStruct(leftType)) {
                    ParentNode.ReplaceChild(boe, new JSUntranslatableExpression(boe));

                    return;
                } else {
                    var newInvocation = new JSInvocationExpression(
                        JSIL.CopyMembers,
                        boe.Right, leftVar
                    );

                    ParentNode.ReplaceChild(boe, newInvocation);
                    Visit(newInvocation);

                    return;
                }
            }

            VisitChildren(boe);
        }
    }
}
