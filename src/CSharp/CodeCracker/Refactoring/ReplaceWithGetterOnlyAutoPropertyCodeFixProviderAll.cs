﻿using CodeCracker.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCracker.CSharp.Refactoring
{
    /// <summary>
    /// This interface must be implemented by the associated CodeFixProvider. The CodeFixProvider must operate on a single document and
    /// should only change the document. This limits the possible operations of the CodeFixProvider to change only document internals without
    /// effecting other parts of the solution.
    /// </summary>
    public interface IFixDocumentInternalsOnly
    {
        Task<Document> FixDocumentAsync(SyntaxNode nodeWithDiagnostic, Document document, CancellationToken cancellationToken);
    }

    public sealed class DocumentCodeFixProviderAll : FixAllProvider
    {
        private const string SyntaxAnnotationKey = "DocumentCodeFixProviderAllSyntaxAnnotation";

        public DocumentCodeFixProviderAll(string codeFixTitle)
        {
            CodeFixTitle = codeFixTitle;
        }

        private string CodeFixTitle { get; }

        public override Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            switch (fixAllContext.Scope)
            {
                case FixAllScope.Document:
                    return Task.FromResult(CodeAction.Create(CodeFixTitle,
                        ct => GetFixedDocumentsAsync(fixAllContext, Enumerable.Repeat(fixAllContext.Document, 1))));
                case FixAllScope.Project:
                    return Task.FromResult(CodeAction.Create(CodeFixTitle,
                        ct => GetFixedDocumentsAsync(fixAllContext, fixAllContext.Project.Documents)));
                case FixAllScope.Solution:
                    return Task.FromResult(CodeAction.Create(CodeFixTitle,
                        ct => GetFixedDocumentsAsync(fixAllContext, fixAllContext.Solution.Projects.SelectMany(p => p.Documents))));
            }
            return null;
        }

        private async static Task<Solution> GetFixedDocumentsAsync(FixAllContext fixAllContext, IEnumerable<Document> documents)
        {
            var solution = fixAllContext.Solution;
            var newDocuments = documents.ToDictionary(d => d.Id, d => GetFixedDocumentAsync(fixAllContext, d));
            await Task.WhenAll(newDocuments.Values).ConfigureAwait(false);
            var changedDocuments = from kvp in newDocuments
                                   where kvp.Value.Result != null
                                   select new { DocumentId = kvp.Key, Document = kvp.Value.Result };
            foreach (var newDocument in changedDocuments)
                solution = solution.WithDocumentSyntaxRoot(newDocument.DocumentId, await newDocument.Document.GetSyntaxRootAsync().ConfigureAwait(false));
            return solution;
        }

        private async static Task<Document> GetFixedDocumentAsync(FixAllContext fixAllContext, Document document)
        {
            var codeFixer = fixAllContext.CodeFixProvider as IFixDocumentInternalsOnly;
            if (codeFixer == null) throw new ArgumentException("This CodeFixAllProvider requires that your CodeFixProvider implements the IFixDocumentInternalsOnly.");
            var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
            if (diagnostics.Length == 0) return null;
            var root = await document.GetSyntaxRootAsync(fixAllContext.CancellationToken).ConfigureAwait(false);
            var nodes = diagnostics.Select(d => root.FindNode(d.Location.SourceSpan)).Where(n => !n.IsMissing);
            var annotations = new List<SyntaxAnnotation>();
            var newRoot = root.ReplaceNodes(nodes, (original, rewritten) =>
                {
                    var annotation = new SyntaxAnnotation(SyntaxAnnotationKey);
                    annotations.Add(annotation);
                    var newNode = original.WithAdditionalAnnotations(annotation);
                    return newNode;
                });
            var newDocument = document.WithSyntaxRoot(newRoot);
            newDocument = await FixCodeForAnnotatedNodes(newDocument, codeFixer, annotations, fixAllContext.CancellationToken).ConfigureAwait(false);
            newDocument = await RemoveAnnontationsAsync(newDocument, annotations).ConfigureAwait(false);
            return newDocument;
        }

        private static async Task<Document> FixCodeForAnnotatedNodes(Document document, IFixDocumentInternalsOnly codeFixer, IEnumerable<SyntaxAnnotation> annotations, CancellationToken cancellationToken)
        {
            var newRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            foreach (var annotation in annotations)
            {
                var annotatedNodes = newRoot.GetAnnotatedNodes(annotation);
                var node = annotatedNodes.FirstOrDefault();
                if (node == null) continue;
                document = await codeFixer.FixDocumentAsync(node, document, cancellationToken).ConfigureAwait(false);
                newRoot = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            }
            return document;
        }

        private static async Task<Document> RemoveAnnontationsAsync(Document document, IEnumerable<SyntaxAnnotation> annotations)
        {
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var nodes = annotations.SelectMany(annotation => root.GetAnnotatedNodes(annotation));
            root = root.ReplaceNodes(nodes, (original, rewritten) => original.WithoutAnnotations(annotations));
            var newDocument = document.WithSyntaxRoot(root);
            return newDocument;
        }
    }
}