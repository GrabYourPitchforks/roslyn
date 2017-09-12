﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.ConvertAutoPropertyToFullProperty;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.ConvertAutoPropertyToFullProperty
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(CSharpConvertAutoPropertyToFullPropertyCodeRefactoringProvider)), Shared]
    internal class CSharpConvertAutoPropertyToFullPropertyCodeRefactoringProvider : AbstractConvertAutoPropertyToFullPropertyCodeRefactoringProvider
    {
        internal override SyntaxNode GetProperty(SyntaxToken token)
        {
            var containingProperty = token.Parent.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
            if (containingProperty == null)
            {
                return null;
            }

            var start = containingProperty.AttributeLists.Count > 0
                ? containingProperty.AttributeLists.Last().GetLastToken().GetNextToken().SpanStart
                : containingProperty.SpanStart;

            // Offer this refactoring anywhere in the signature of the property
            var position = token.SpanStart;
            if (position < start || position > containingProperty.Identifier.Span.End)
            {
                return null;
            }

            return containingProperty;
        }

        internal override (SyntaxNode newGetAccessor, SyntaxNode newSetAccessor) GetNewAccessors(
            DocumentOptionSet options, SyntaxNode property, 
            string fieldName, SyntaxGenerator generator)
        {
            // C# might have trivia with the accessors that needs to be preserved.  
            // so we will update the existing accessors instead of creating new ones
            var accessorListSyntax = ((PropertyDeclarationSyntax)property).AccessorList;
            var existingAccessors = GetExistingAccessors(accessorListSyntax);

            var getAccessorStatement = generator.ReturnStatement(generator.IdentifierName(fieldName));
            var newGetter = GetUpdatedAccessor(
                options, existingAccessors.getAccessor,
                getAccessorStatement, generator);

            SyntaxNode newSetter = null;
            if (existingAccessors.setAccessor != null)
            {
                var setAccessorStatement = generator.ExpressionStatement(generator.AssignmentStatement(
                    generator.IdentifierName(fieldName),
                    generator.IdentifierName("value")));
                newSetter = GetUpdatedAccessor(
                    options, existingAccessors.setAccessor,
                    setAccessorStatement, generator);
            }

            return (newGetAccessor: newGetter, newSetAccessor: newSetter);
        }

        private (AccessorDeclarationSyntax getAccessor, AccessorDeclarationSyntax setAccessor) 
            GetExistingAccessors(AccessorListSyntax accessorListSyntax)
            => (accessorListSyntax.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)),
                accessorListSyntax.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)));

        private SyntaxNode GetUpdatedAccessor(DocumentOptionSet options, 
            SyntaxNode accessor, SyntaxNode statement, SyntaxGenerator generator)
        {
            var newAccessor = AddStatement(accessor, statement);
            var accessorDeclarationSyntax = (AccessorDeclarationSyntax)newAccessor;

            var preference = GetAccessorExpressionBodyPreference(options);
            if (preference == ExpressionBodyPreference.Never)
            {
                return accessorDeclarationSyntax.WithSemicolonToken(default);
            }

            if (!accessorDeclarationSyntax.Body.TryConvertToExpressionBody(
                accessorDeclarationSyntax.Kind(), accessor.SyntaxTree.Options, preference,
                out var arrowExpression, out var semicolonToken))
            {
                return accessorDeclarationSyntax.WithSemicolonToken(default);
            };

            return accessorDeclarationSyntax
                .WithExpressionBody(arrowExpression)
                .WithBody(null)
                .WithSemicolonToken(accessorDeclarationSyntax.SemicolonToken)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        internal SyntaxNode AddStatement(SyntaxNode accessor, SyntaxNode statement)
        {
            var blockSyntax = SyntaxFactory.Block(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed),
                new SyntaxList<StatementSyntax>((StatementSyntax)statement),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                    .WithTrailingTrivia(((AccessorDeclarationSyntax)accessor).SemicolonToken.TrailingTrivia));

            return ((AccessorDeclarationSyntax)accessor).WithBody(blockSyntax);
        }

        internal override SyntaxNode ConvertPropertyToExpressionBodyIfDesired(
            DocumentOptionSet options, SyntaxNode property)
        {
            var propertyDeclaration = (PropertyDeclarationSyntax)property;

            var preference = GetPropertyExpressionBodyPreference(options);
            if (preference == ExpressionBodyPreference.Never)
            {
                return propertyDeclaration.WithSemicolonToken(default);
            }

            // if there is a get accessor only, we can move the expression body to the property
            if (propertyDeclaration.AccessorList?.Accessors.Count == 1 &&
                propertyDeclaration.AccessorList.Accessors[0].Kind() == SyntaxKind.GetAccessorDeclaration)
            {
                var getAccessor = propertyDeclaration.AccessorList.Accessors[0];
                if (getAccessor.ExpressionBody != null)
                {
                    return propertyDeclaration.WithExpressionBody(getAccessor.ExpressionBody)
                        .WithSemicolonToken(getAccessor.SemicolonToken)
                        .WithAccessorList(null);
                }
            }

            return propertyDeclaration.WithSemicolonToken(default);
        }

        internal ExpressionBodyPreference GetAccessorExpressionBodyPreference(DocumentOptionSet options)
            => options.GetOption(CSharpCodeStyleOptions.PreferExpressionBodiedAccessors).Value;

        internal ExpressionBodyPreference GetPropertyExpressionBodyPreference(DocumentOptionSet options)
            => options.GetOption(CSharpCodeStyleOptions.PreferExpressionBodiedProperties).Value;

        internal override string GetUniqueName(string fieldName, IPropertySymbol property)
            => NameGenerator.GenerateUniqueName(fieldName, n => !property.ContainingType.GetMembers(n).Any());

        internal override SyntaxNode GetTypeBlock(SyntaxNode syntaxNode) 
            => syntaxNode;

        internal override SyntaxNode GetInitializerValue(SyntaxNode property)
            => ((PropertyDeclarationSyntax)property).Initializer?.Value;

        internal override SyntaxNode GetPropertyWithoutInitializer(SyntaxNode property) 
            => ((PropertyDeclarationSyntax)property).WithInitializer(null);
    }
}
