﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.CodeFixes.DeclareAsNullable
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.DeclareAsNullable), Shared]
    internal class CSharpDeclareAsNullableCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        // warning CS8625: Cannot convert null literal to non-nullable reference or unconstrained type parameter.
        // warning CS8600: Converting null literal or possible null value to non-nullable type.
        public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS8625", "CS8600");

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            var declarationTypeToFix = TryGetDeclarationTypeToFix(node);
            if (declarationTypeToFix == null)
            {
                return;
            }

            context.RegisterCodeFix(new MyCodeAction(
                c => FixAsync(context.Document, diagnostic, c)),
                context.Diagnostics);
        }

        protected override Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var root = editor.OriginalRoot;

            // a method can have multiple `return null;` statements, but we should only fix its return type once
            var alreadyHandled = PooledHashSet<TypeSyntax>.GetInstance();

            foreach (var diagnostic in diagnostics)
            {
                var node = diagnostic.Location.FindNode(getInnermostNodeForTie: true, cancellationToken);
                MakeDeclarationNullable(document, editor, node, alreadyHandled);
            }

            alreadyHandled.Free();
            return Task.CompletedTask;
        }

        private static void MakeDeclarationNullable(Document document, SyntaxEditor editor, SyntaxNode node, HashSet<TypeSyntax> alreadyHandled)
        {
            var declarationTypeToFix = TryGetDeclarationTypeToFix(node);
            if (declarationTypeToFix != null && alreadyHandled.Add(declarationTypeToFix))
            {
                var fixedDeclaration = SyntaxFactory.NullableType(declarationTypeToFix.WithoutTrivia()).WithTriviaFrom(declarationTypeToFix);
                editor.ReplaceNode(declarationTypeToFix, fixedDeclaration);
            }
        }

        private static TypeSyntax TryGetDeclarationTypeToFix(SyntaxNode node)
        {
            if (!node.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return null;
            }

            if (node.IsParentKind(SyntaxKind.ReturnStatement))
            {
                var containingMember = node.GetAncestors().FirstOrDefault(a => a.IsKind(
                    SyntaxKind.MethodDeclaration, SyntaxKind.PropertyDeclaration, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.SimpleLambdaExpression,
                    SyntaxKind.LocalFunctionStatement, SyntaxKind.AnonymousMethodExpression, SyntaxKind.ConstructorDeclaration, SyntaxKind.DestructorDeclaration,
                    SyntaxKind.OperatorDeclaration, SyntaxKind.IndexerDeclaration, SyntaxKind.EventDeclaration));

                if (containingMember == null)
                {
                    return null;
                }

                switch (containingMember)
                {
                    case MethodDeclarationSyntax method:
                        // string M() { return null; }
                        return method.ReturnType;

                    case PropertyDeclarationSyntax property:
                        // string x { get { return null; } }
                        return property.Type;

                    default:
                        return null;
                }
            }

            // string x = null;
            if (node.Parent?.Parent?.IsParentKind(SyntaxKind.VariableDeclaration) == true)
            {
                var variableDeclaration = (VariableDeclarationSyntax)node.Parent.Parent.Parent;
                if (variableDeclaration.Variables.Count != 1)
                {
                    // string x = null, y = null;
                    return null;
                }

                return variableDeclaration.Type;
            }

            // string x { get; set; } = null;
            if (node.Parent.IsParentKind(SyntaxKind.PropertyDeclaration) == true)
            {
                var propertyDeclaration = (PropertyDeclarationSyntax)node.Parent.Parent;
                return propertyDeclaration.Type;
            }

            // void M(string x = null) { }
            if (node.Parent.IsParentKind(SyntaxKind.Parameter) == true)
            {
                var parameter = (ParameterSyntax)node.Parent.Parent;
                return parameter.Type;
            }

            // static string M() => null;
            if (node.IsParentKind(SyntaxKind.ArrowExpressionClause) && node.Parent.IsParentKind(SyntaxKind.MethodDeclaration))
            {
                var arrowMethod = (MethodDeclarationSyntax)node.Parent.Parent;
                return arrowMethod.ReturnType;
            }

            return null;
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument) :
                base(CSharpFeaturesResources.Declare_as_nullable,
                     createChangedDocument,
                     CSharpFeaturesResources.Declare_as_nullable)
            {
            }
        }
    }
}
