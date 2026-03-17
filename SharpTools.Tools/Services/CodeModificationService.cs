using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace SharpTools.Tools.Services;

public class CodeModificationService : ICodeModificationService {
    private readonly ISolutionManager _solutionManager;
    private readonly IGitService _gitService;
    private readonly ILogger<CodeModificationService> _logger;

    public CodeModificationService(
        ISolutionManager solutionManager,
        IGitService gitService,
        ILogger<CodeModificationService> logger) {
        _solutionManager = solutionManager ?? throw new ArgumentNullException(nameof(solutionManager));
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Solution GetCurrentSolutionOrThrow() {
        if (!_solutionManager.IsSolutionLoaded) {
            throw new InvalidOperationException("No solution is currently loaded.");
        }
        return _solutionManager.CurrentSolution;
    }

    public async Task<Solution> AddMemberAsync(DocumentId documentId, INamedTypeSymbol targetTypeSymbol, MemberDeclarationSyntax newMember, int lineNumberHint = -1, CancellationToken cancellationToken = default) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        var typeDeclarationNode = targetTypeSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as TypeDeclarationSyntax;
        if (typeDeclarationNode == null) {
            throw new InvalidOperationException($"Could not find syntax node for type '{targetTypeSymbol.Name}'.");
        }

        _logger.LogInformation("Adding member to type {TypeName} in document {DocumentPath}", targetTypeSymbol.Name, document.FilePath);

        NormalizeMemberDeclarationTrivia(newMember);

        if (lineNumberHint > 0) {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root != null) {
                var sourceText = await document.GetTextAsync(cancellationToken);

                var members = typeDeclarationNode.Members
                    .Select(member => new {
                        Member = member,
                        LineSpan = member.GetLocation().GetLineSpan()
                    })
                    .OrderBy(m => m.LineSpan.StartLinePosition.Line)
                    .ToList();

                int insertIndex = 0;
                for (int i = 0; i < members.Count; i++) {
                    if (members[i].LineSpan.StartLinePosition.Line >= lineNumberHint) {
                        insertIndex = i;
                        break;
                    }
                    insertIndex = i + 1;
                }

                var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
                var membersList = typeDeclarationNode.Members.ToList();
                membersList.Insert(insertIndex, newMember);
                var newTypeDeclaration = typeDeclarationNode.WithMembers(SyntaxFactory.List(membersList));

                editor.ReplaceNode(typeDeclarationNode, newTypeDeclaration);

                var newDocument = editor.GetChangedDocument();
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                return formattedDocument.Project.Solution;
            }
        }

        var defaultEditor = await DocumentEditor.CreateAsync(document, cancellationToken);
        defaultEditor.AddMember(typeDeclarationNode, newMember);

        var changedDocument = defaultEditor.GetChangedDocument();
        var finalDocument = await FormatDocumentAsync(changedDocument, cancellationToken);
        return finalDocument.Project.Solution;
    }

    public async Task<Solution> AddStatementAsync(DocumentId documentId, MethodDeclarationSyntax targetMethodNode, StatementSyntax newStatement, CancellationToken cancellationToken, bool addToBeginning = false) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        _logger.LogInformation("Adding statement to method {MethodName} in document {DocumentPath}", targetMethodNode.Identifier.Text, document.FilePath);
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken);

        if (targetMethodNode.Body != null) {
            var currentBody = targetMethodNode.Body;
            BlockSyntax newBody;
            if (addToBeginning) {
                var newStatements = currentBody.Statements.Insert(0, newStatement);
                newBody = currentBody.WithStatements(newStatements);
            } else {
                newBody = currentBody.AddStatements(newStatement);
            }
            editor.ReplaceNode(currentBody, newBody);
        } else if (targetMethodNode.ExpressionBody != null) {
            // Converting expression body to block body
            var returnStatement = SyntaxFactory.ReturnStatement(targetMethodNode.ExpressionBody.Expression);
            BlockSyntax bodyBlock;
            if (addToBeginning) {
                bodyBlock = SyntaxFactory.Block(newStatement, returnStatement);
            } else {
                bodyBlock = SyntaxFactory.Block(returnStatement, newStatement);
            }
            // Create a new method node with the block body
            var newMethod = targetMethodNode.WithBody(bodyBlock)
                .WithExpressionBody(null) // Remove expression body
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None)); // Remove semicolon if any
            editor.ReplaceNode(targetMethodNode, newMethod);
        } else {
            // Method has no body (e.g. abstract, partial, extern). Create one.
            var bodyBlock = SyntaxFactory.Block(newStatement);
            var newMethodWithBody = targetMethodNode.WithBody(bodyBlock)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
            editor.ReplaceNode(targetMethodNode, newMethodWithBody);
        }

        var newDocument = editor.GetChangedDocument();
        var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
        return formattedDocument.Project.Solution;
    }

    private static SyntaxTrivia newline = SyntaxFactory.EndOfLine("\n");
    private SyntaxTriviaList NormalizeLeadingTrivia(SyntaxTriviaList trivia) {
        // Remove all newlines
        var filtered = trivia.Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia));

        // Create a new list with a newline at the beginning followed by the filtered trivia
        return SyntaxFactory.TriviaList(newline, newline).AddRange(filtered);
    }

    private SyntaxTriviaList NormalizeTrailingTrivia(SyntaxTriviaList trivia) {
        // Remove all newlines
        var filtered = trivia.Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia));

        // Create a new SyntaxTriviaList from the filtered items
        var result = SyntaxFactory.TriviaList(filtered);

        // Add the end-of-line trivia and return
        return result.Add(newline).Add(newline);
    }

    private SyntaxNode NormalizeMemberDeclarationTrivia(SyntaxNode member) {
        if (member is MemberDeclarationSyntax memberDeclaration) {
            var leadingTrivia = memberDeclaration.GetLeadingTrivia();
            var trailingTrivia = memberDeclaration.GetTrailingTrivia();

            // Normalize trivia
            var normalizedLeading = NormalizeLeadingTrivia(leadingTrivia);
            var normalizedTrailing = NormalizeTrailingTrivia(trailingTrivia);

            // Apply the normalized trivia
            return memberDeclaration.WithLeadingTrivia(normalizedLeading)
                                    .WithTrailingTrivia(normalizedTrailing);
        }
        return member;
    }
    public async Task<Solution> ReplaceNodeAsync(DocumentId documentId, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        _logger.LogInformation("Replacing node in document {DocumentPath}", document.FilePath);

        // Check if this is a deletion operation (newNode is an EmptyStatement with a delete comment)
        bool isDeleteOperation = newNode is Microsoft.CodeAnalysis.CSharp.Syntax.EmptyStatementSyntax emptyStmt &&
                                 emptyStmt.HasLeadingTrivia &&
                                 emptyStmt.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                                                                     t.ToString().StartsWith("// Delete", StringComparison.OrdinalIgnoreCase));

        if (isDeleteOperation) {
            _logger.LogInformation("Detected deletion operation for node {NodeKind}", oldNode.Kind());

            // For deletion, we need to remove the node from its parent
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) {
                throw new InvalidOperationException("Could not get syntax root for document.");
            }

            // Different approach based on the node's parent context
            SyntaxNode newRoot;

            if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax compilationUnit) {
                // Handle top-level members in the compilation unit
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = compilationUnit.Members.Remove(memberToRemove);
                    newRoot = compilationUnit.WithMembers(newMembers);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} directly from compilation unit.");
                }
            } else if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax namespaceDecl) {
                // Handle members in a namespace
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = namespaceDecl.Members.Remove(memberToRemove);
                    var newNamespace = namespaceDecl.WithMembers(newMembers);
                    newRoot = root.ReplaceNode(namespaceDecl, newNamespace);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from namespace declaration.");
                }
            } else if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax typeDecl) {
                // Handle members in a type declaration (class, struct, interface, etc.)
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = typeDecl.Members.Remove(memberToRemove);
                    var newType = typeDecl.WithMembers(newMembers);
                    newRoot = root.ReplaceNode(typeDecl, newType);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from type declaration.");
                }
            } else {
                throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from parent of type {oldNode.Parent?.GetType().Name ?? "null"}.");
            }

            var newDocument = document.WithSyntaxRoot(newRoot);
            var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
            return formattedDocument.Project.Solution;
        } else {
            // Standard node replacement
            NormalizeMemberDeclarationTrivia(newNode);

            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            editor.ReplaceNode(oldNode, newNode);

            var newDocument = editor.GetChangedDocument();
            var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
            return formattedDocument.Project.Solution;
        }
    }
    public async Task<Solution> RenameSymbolAsync(ISymbol symbol, string newName, CancellationToken cancellationToken) {
        var solution = GetCurrentSolutionOrThrow();
        return await RenameSymbolAsync(solution, symbol, newName, cancellationToken);
    }

    private async Task<Solution> RenameSymbolAsync(Solution solution, ISymbol symbol, string newName, CancellationToken cancellationToken) {
        _logger.LogInformation("Renaming symbol {SymbolName} to {NewName}", symbol.ToDisplayString(), newName);

        var options = solution.Workspace.Options;

        // Using the older API for now
        // Temporarily disable obsolete warning
#pragma warning disable CS0618
        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, newName, options, cancellationToken);
#pragma warning restore CS0618

        return newSolution;
    }
    public async Task<Solution> ReplaceAllReferencesAsync(ISymbol symbol, string replacementText, CancellationToken cancellationToken, Func<SyntaxNode, bool>? predicateFilter = null) {
        var solution = GetCurrentSolutionOrThrow();
        _logger.LogInformation("Replacing all references to symbol {SymbolName} with text '{ReplacementText}'",
            symbol.ToDisplayString(), replacementText);

        // Find all references to the symbol
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var changedSolution = solution;

        foreach (var referencedSymbol in referencedSymbols) {
            foreach (var location in referencedSymbol.Locations) {
                cancellationToken.ThrowIfCancellationRequested();

                var document = changedSolution.GetDocument(location.Document.Id);
                if (document == null) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                var node = root.FindNode(location.Location.SourceSpan);

                // Apply filter if provided
                if (predicateFilter != null && !predicateFilter(node)) {
                    _logger.LogDebug("Skipping replacement for node at {Location} due to filter predicate",
                        location.Location.GetLineSpan());
                    continue;
                }

                // Create a new syntax node with the replacement text
                var replacementNode = SyntaxFactory.ParseExpression(replacementText)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());

                // Replace the node in the document
                var newRoot = root.ReplaceNode(node, replacementNode);
                var newDocument = document.WithSyntaxRoot(newRoot);

                // Format the document and update the solution
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                changedSolution = formattedDocument.Project.Solution;
            }
        }

        return changedSolution;
    }
    public async Task<Solution> FindAndReplaceAsync(string targetString, string regexPattern, string replacementText, CancellationToken cancellationToken, RegexOptions options = RegexOptions.Multiline) {
        var solution = GetCurrentSolutionOrThrow();
        _logger.LogInformation("Performing find and replace with regex '{RegexPattern}' on target '{TargetString}'",
            regexPattern, targetString);

        // Create the regex with multiline option
        var regex = new Regex(regexPattern, options);
        Solution resultSolution = solution;

        // Check if the target is a fully qualified name (no wildcards)
        if (!targetString.Contains("*") && !targetString.Contains("?")) {
            try {
                // Try to resolve as a symbol
                var symbol = await _solutionManager.FindRoslynSymbolAsync(targetString, cancellationToken);
                if (symbol != null) {
                    _logger.LogInformation("Target is a valid symbol: {SymbolName}", symbol.ToDisplayString());

                    // For a symbol, we'll get its defining document and limit replacements to the symbol's span
                    var syntaxReferences = symbol.DeclaringSyntaxReferences;
                    if (syntaxReferences.Any()) {
                        foreach (var syntaxRef in syntaxReferences) {
                            cancellationToken.ThrowIfCancellationRequested();

                            var node = await syntaxRef.GetSyntaxAsync(cancellationToken);
                            var document = solution.GetDocument(node.SyntaxTree);

                            if (document == null) continue;

                            // Get the source text and limit replacement to the symbol's node span
                            var sourceText = await document.GetTextAsync(cancellationToken);
                            var originalText = sourceText.ToString();

                            // Extract only the text within the symbol's node span
                            var nodeSpan = node.Span;
                            var symbolText = originalText.Substring(nodeSpan.Start, nodeSpan.Length).NormalizeEndOfLines();

                            // Apply regex replacement only to the symbol's text
                            var newSymbolText = regex.Replace(symbolText, replacementText);

                            // Only update if changes were made to the symbol text
                            if (newSymbolText != symbolText) {
                                // Create new text by replacing the symbol's span with the modified text
                                var newFullText = originalText.Substring(0, nodeSpan.Start) +
                                    newSymbolText +
                                    originalText.Substring(nodeSpan.Start + nodeSpan.Length);

                                var newDocument = document.WithText(SourceText.From(newFullText, sourceText.Encoding));
                                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                                resultSolution = formattedDocument.Project.Solution;
                            }
                        }

                        return resultSolution;
                    }
                }
            } catch (Exception ex) {
                _logger.LogInformation("Target string is not a valid symbol: {Error}", ex.Message);
                // Fall through to file-based search
            }
        }

        // Handle as file path with potential wildcards
        var documentIds = new List<DocumentId>();

        // Log the pattern we're using
        _logger.LogInformation("Treating '{Target}' as a file path pattern", targetString);

        // Normalize path separators in the target pattern to use forward slashes consistently
        string normalizedTarget = targetString.Replace('\\', '/');

        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(normalizedTarget);
        string root = Path.GetPathRoot(solution.FilePath) ?? Path.GetPathRoot(Environment.CurrentDirectory)!;

        // Process all projects and documents
        foreach (var project in solution.Projects) {
            foreach (var document in project.Documents) {
                if (string.IsNullOrWhiteSpace(document.FilePath)) continue;
                // Use wildcard matching
                if (matcher.Match(root, document.FilePath).HasMatches) {
                    _logger.LogInformation("Document matched pattern: {DocumentPath}", document.FilePath);
                    documentIds.Add(document.Id);
                }
            }
        }

        _logger.LogInformation("Found {Count} documents matching pattern '{Pattern}'",
            documentIds.Count, targetString);

        resultSolution = solution;
        // Process all matching documents
        foreach (var documentId in documentIds) {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply regex replacement
            var document = resultSolution.GetDocument(documentId);
            if (document == null) continue;
            var sourceText = await document.GetTextAsync(cancellationToken);
            var originalText = sourceText.ToString().NormalizeEndOfLines();

            var newText = regex.Replace(originalText, replacementText);

            // Only update if changes were made
            if (newText != originalText) {
                var newDocument = document.WithText(SourceText.From(newText, sourceText.Encoding));
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                resultSolution = formattedDocument.Project.Solution;
            }
        }

        return resultSolution;
    }
    public async Task<Document> FormatDocumentAsync(Document document, CancellationToken cancellationToken) {
        _logger.LogDebug("Formatting document: {DocumentPath}", document.FilePath);
        var formattingOptions = await document.GetOptionsAsync(cancellationToken);
        var formattedDocument = await Formatter.FormatAsync(document, formattingOptions, cancellationToken);
        _logger.LogDebug("Document formatted: {DocumentPath}", document.FilePath);
        return formattedDocument;
    }
    public async Task ApplyChangesAsync(Solution newSolution, CancellationToken cancellationToken, string commitMessage, IEnumerable<string>? additionalFilePaths = null) {
        if (_solutionManager.CurrentWorkspace is not MSBuildWorkspace workspace) {
            _logger.LogError("Cannot apply changes: Workspace is not an MSBuildWorkspace or is null.");
            throw new InvalidOperationException("Workspace is not suitable for applying changes.");
        }

        var originalSolution = _solutionManager.CurrentSolution ?? throw new InvalidOperationException("Original solution is null before applying changes.");
        var solutionPath = originalSolution.FilePath ?? "";
        var finalSolutionToApply = newSolution;

        var solutionChanges = finalSolutionToApply.GetChanges(originalSolution);

        // Collect changed file paths for git operations - include both changed and new documents
        var changedFilePaths = new List<string>();

        foreach (var projectChange in solutionChanges.GetProjectChanges()) {
            // Handle changed documents
            foreach (var changedDocumentId in projectChange.GetChangedDocuments()) {
                var documentToFormat = finalSolutionToApply.GetDocument(changedDocumentId);
                if (documentToFormat != null) {
                    _logger.LogDebug("Pre-apply formatting for changed document: {DocumentPath}", documentToFormat.FilePath);
                    var formattedDocument = await FormatDocumentAsync(documentToFormat, cancellationToken);
                    finalSolutionToApply = formattedDocument.Project.Solution;

                    if (!string.IsNullOrEmpty(documentToFormat.FilePath)) {
                        changedFilePaths.Add(documentToFormat.FilePath);
                    }
                }
            }

            // Handle added documents (new files)
            foreach (var addedDocumentId in projectChange.GetAddedDocuments()) {
                var addedDocument = finalSolutionToApply.GetDocument(addedDocumentId);
                if (addedDocument != null) {
                    _logger.LogDebug("Pre-apply formatting for added document: {DocumentPath}", addedDocument.FilePath);
                    var formattedDocument = await FormatDocumentAsync(addedDocument, cancellationToken);
                    finalSolutionToApply = formattedDocument.Project.Solution;

                    if (!string.IsNullOrEmpty(addedDocument.FilePath)) {
                        changedFilePaths.Add(addedDocument.FilePath);
                        _logger.LogInformation("Added new document for git tracking: {DocumentPath}", addedDocument.FilePath);
                    }
                }
            }

            // Handle removed documents
            foreach (var removedDocumentId in projectChange.GetRemovedDocuments()) {
                var removedDocument = originalSolution.GetDocument(removedDocumentId);
                if (removedDocument != null && !string.IsNullOrEmpty(removedDocument.FilePath)) {
                    changedFilePaths.Add(removedDocument.FilePath);
                    _logger.LogInformation("Marked removed document for git tracking: {DocumentPath}", removedDocument.FilePath);
                }
            }
        }

        _logger.LogInformation("Applying changes to workspace for {DocumentCount} changed documents across {ProjectCount} projects.",
            solutionChanges.GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()).Concat(pc.GetRemovedDocuments())).Count(),
            solutionChanges.GetProjectChanges().Count());

        if (workspace.TryApplyChanges(finalSolutionToApply)) {
            _logger.LogInformation("Changes applied successfully to the workspace.");

            // If additional file paths are provided, add them to the changed file paths
            if (additionalFilePaths != null) {
                changedFilePaths.AddRange(additionalFilePaths.Where(fp => !string.IsNullOrEmpty(fp) && File.Exists(fp)));
            }
            // Git operations after successful changes
            await ProcessGitOperationsAsync(solutionPath, changedFilePaths, commitMessage, cancellationToken);

            _solutionManager.RefreshCurrentSolution();
        } else {
            _logger.LogError("Failed to apply changes to the workspace.");
            throw new InvalidOperationException("Failed to apply changes to the workspace. Files might have been modified externally.");
        }
    }
    private async Task ProcessGitOperationsAsync(string solutionPath, List<string> changedFilePaths, string commitMessage, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(solutionPath) || changedFilePaths.Count == 0) {
            return;
        }

        try {
            // Check if solution is in a git repo
            if (!await _gitService.IsRepositoryAsync(solutionPath, cancellationToken)) {
                _logger.LogDebug("Solution is not in a Git repository, skipping Git operations");
                return;
            }

            _logger.LogDebug("Solution is in a Git repository, processing Git operations");

            // Check if already on sharptools branch
            if (!await _gitService.IsOnSharpToolsBranchAsync(solutionPath, cancellationToken)) {
                _logger.LogInformation("Not on a SharpTools branch, creating one");
                await _gitService.EnsureSharpToolsBranchAsync(solutionPath, cancellationToken);
            }

            // Commit changes with the provided commit message
            await _gitService.CommitChangesAsync(solutionPath, changedFilePaths, commitMessage, cancellationToken);
            _logger.LogInformation("Git operations completed successfully with commit message: {CommitMessage}", commitMessage);
        } catch (Exception ex) {
            // Log but don't fail the operation if Git operations fail
            _logger.LogWarning(ex, "Git operations failed but code changes were still applied");
        }
    }
    public async Task<(bool success, string message)> UndoLastChangeAsync(CancellationToken cancellationToken) {
        if (_solutionManager.CurrentWorkspace is not MSBuildWorkspace workspace) {
            _logger.LogError("Cannot undo changes: Workspace is not an MSBuildWorkspace or is null.");
            var message = "Error: Workspace is not an MSBuildWorkspace or is null. Cannot undo.";
            return (false, message);
        }

        var currentSolution = _solutionManager.CurrentSolution;
        if (currentSolution?.FilePath == null) {
            _logger.LogError("Cannot undo changes: Current solution or its file path is null.");
            var message = "Error: No solution loaded or solution file path is null. Cannot undo.";
            return (false, message);
        }

        var solutionPath = currentSolution.FilePath;

        // Check if solution is in a git repository
        if (!await _gitService.IsRepositoryAsync(solutionPath, cancellationToken)) {
            _logger.LogError("Cannot undo changes: Solution is not in a Git repository.");
            throw new McpException("Error: Solution is not in a Git repository. Undo functionality requires Git version control.");
        }

        // Check if we're on a sharptools branch
        if (!await _gitService.IsOnSharpToolsBranchAsync(solutionPath, cancellationToken)) {
            _logger.LogError("Cannot undo changes: Not on a SharpTools branch.");
            var message = "Error: Not on a SharpTools branch. Undo is only available on SharpTools branches.";
            return (false, message);
        }

        _logger.LogInformation("Attempting to undo last change by reverting last Git commit.");

        // Perform git revert with diff
        var (revertSuccess, diff) = await _gitService.RevertLastCommitAsync(solutionPath, cancellationToken);
        if (!revertSuccess) {
            _logger.LogError("Git revert operation failed.");
            var message = "Error: Failed to revert the last Git commit. There may be no commits to revert or the operation failed.";
            return (false, message);
        }

        // Reload the solution from disk to reflect the reverted changes
        await _solutionManager.ReloadSolutionFromDiskAsync(cancellationToken);

        _logger.LogInformation("Successfully reverted the last change using Git.");
        var successMessage = "Successfully reverted the last change by reverting the last Git commit. Solution reloaded from disk.";

        // Add the diff to the success message if available
        if (!string.IsNullOrEmpty(diff)) {
            successMessage += "\n\nChanges undone:\n" + diff;
        }

        return (true, successMessage);
    }

    public async Task<Solution> ExtractMethodAsync(DocumentId documentId, Microsoft.CodeAnalysis.Text.TextSpan textSpan, string methodName, string visibility, CancellationToken cancellationToken) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        _logger.LogInformation("Extracting method {MethodName} with visibility {Visibility} in document {DocumentPath}", methodName, visibility, document.FilePath);

        var requestedAccessibility = ParseAccessibility(visibility);
        var originalRoot = await document.GetSyntaxRootAsync(cancellationToken) ?? throw new InvalidOperationException("Could not get syntax root for the original document.");

        // The service lives in Microsoft.CodeAnalysis.Features, not in the CSharp.Workspaces assembly.
        var featuresAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Microsoft.CodeAnalysis.Features", StringComparison.Ordinal))
            ?? System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.Features");
        var extractMethodServiceType = featuresAssembly.GetType("Microsoft.CodeAnalysis.ExtractMethod.IExtractMethodService");

        if (extractMethodServiceType == null) {
            throw new InvalidOperationException("IExtractMethodService type not found in Microsoft.CodeAnalysis.Features assembly.");
        }

        // Use reflection to call the generic GetService<T> method
        var languageServices = document.Project.Services;
        var getServiceMethod = languageServices.GetType()
            .GetMethods()
            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0);
        
        if (getServiceMethod == null) {
            throw new InvalidOperationException("GetService<T> method not found on HostLanguageServices.");
        }

        var genericGetService = getServiceMethod.MakeGenericMethod(extractMethodServiceType);
        var extractMethodService = genericGetService.Invoke(languageServices, null);
        
        if (extractMethodService == null) {
            throw new InvalidOperationException("IExtractMethodService not found for this language.");
        }

        // Call ExtractMethodAsync(Document, TextSpan, bool, ExtractMethodGenerationOptions, CancellationToken)
        // Note: The bool parameter might be useMethodBody or similar, usually true.
        // ExtractMethodGenerationOptions can be null for defaults.
        var methodInfo = extractMethodServiceType.GetMethod("ExtractMethodAsync");
        if (methodInfo == null) {
            throw new InvalidOperationException("ExtractMethodAsync method not found on IExtractMethodService.");
        }

        try {
            var generationOptionsType = methodInfo.GetParameters()[3].ParameterType;
            var generationOptions = CreateExtractMethodGenerationOptions(generationOptionsType, document.Project, methodName);
            var task = (Task)methodInfo.Invoke(extractMethodService, new object[] { document, textSpan, false, generationOptions, cancellationToken })!;
            await task.ConfigureAwait(false);

            // Get result via reflection since result type is also likely internal
            var resultProperty = task.GetType().GetProperty("Result");
            var result = resultProperty?.GetValue(task);
            
            if (result == null) {
                throw new McpException("ExtractMethodAsync returned null result.");
            }

            var succeededProperty = result.GetType().GetProperty("Succeeded");
            bool succeeded = (bool)(succeededProperty?.GetValue(result) ?? false);

            if (!succeeded) {
                var reasonsProperty = result.GetType().GetProperty("Reasons");
                var reasons = reasonsProperty?.GetValue(result) as System.Collections.IEnumerable;
                var reasonText = reasons == null
                    ? "unknown reason"
                    : string.Join("; ", reasons.Cast<object>());
                throw new McpException($"Failed to extract method: {reasonText}");
            }

            var newDocument = await GetExtractedDocumentAsync(result, cancellationToken);

            var updatedSolution = await RenameAndAdjustExtractedMethodAsync(
                originalRoot,
                newDocument,
                textSpan,
                methodName,
                requestedAccessibility,
                cancellationToken);

            var updatedDocument = updatedSolution.GetDocument(documentId) ?? throw new InvalidOperationException("Updated document not found after method extraction.");
            var formattedDocument = await FormatDocumentAsync(updatedDocument, cancellationToken);
            return formattedDocument.Project.Solution;
        } catch (System.Reflection.TargetInvocationException ex) {
            throw new McpException($"Error during method extraction: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private async Task<Solution> RenameAndAdjustExtractedMethodAsync(
        SyntaxNode originalRoot,
        Document extractedDocument,
        TextSpan textSpan,
        string requestedMethodName,
        Accessibility requestedAccessibility,
        CancellationToken cancellationToken) {
        var updatedSolution = extractedDocument.Project.Solution;
        var updatedDocument = extractedDocument;
        var updatedRoot = await updatedDocument.GetSyntaxRootAsync(cancellationToken) ?? throw new InvalidOperationException("Could not get syntax root after method extraction.");

        var extractedMethod = FindExtractedMethod(originalRoot, updatedRoot, textSpan, preferredMethodName: null);
        if (extractedMethod == null) {
            throw new InvalidOperationException("Could not identify the extracted method in the updated document.");
        }

        var semanticModel = await updatedDocument.GetSemanticModelAsync(cancellationToken) ?? throw new InvalidOperationException("Could not get semantic model for extracted method.");
        var extractedSymbol = semanticModel.GetDeclaredSymbol(extractedMethod, cancellationToken) ?? throw new InvalidOperationException("Could not resolve the extracted method symbol.");

        if (!string.Equals(extractedSymbol.Name, requestedMethodName, StringComparison.Ordinal)) {
            updatedSolution = await RenameSymbolAsync(updatedSolution, extractedSymbol, requestedMethodName, cancellationToken);
            updatedDocument = updatedSolution.GetDocument(extractedDocument.Id) ?? throw new InvalidOperationException("Updated document not found after renaming extracted method.");
            updatedRoot = await updatedDocument.GetSyntaxRootAsync(cancellationToken) ?? throw new InvalidOperationException("Could not get syntax root after renaming extracted method.");
        }

        extractedMethod = FindExtractedMethod(originalRoot, updatedRoot, textSpan, requestedMethodName);
        if (extractedMethod == null) {
            throw new InvalidOperationException($"Could not find extracted method '{requestedMethodName}' after renaming.");
        }

        var adjustedMethod = ApplyAccessibility(extractedMethod, requestedAccessibility);
        if (adjustedMethod != extractedMethod) {
            updatedRoot = updatedRoot.ReplaceNode(extractedMethod, adjustedMethod);
            updatedDocument = updatedDocument.WithSyntaxRoot(updatedRoot);
            updatedSolution = updatedDocument.Project.Solution;
        }

        return updatedSolution;
    }

    private static async Task<Document> GetExtractedDocumentAsync(object extractMethodResult, CancellationToken cancellationToken) {
        var getDocumentMethod = extractMethodResult.GetType().GetMethod("GetDocumentAsync")
            ?? throw new InvalidOperationException($"GetDocumentAsync method not found on extract result type '{extractMethodResult.GetType().FullName}'.");

        var task = (Task)getDocumentMethod.Invoke(extractMethodResult, new object[] { cancellationToken })!;
        await task.ConfigureAwait(false);

        var taskResult = task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("GetDocumentAsync returned a null result.");

        var tupleType = taskResult.GetType();
        var documentValue = tupleType.GetField("document")?.GetValue(taskResult)
            ?? tupleType.GetField("Item1")?.GetValue(taskResult)
            ?? tupleType.GetProperty("document")?.GetValue(taskResult)
            ?? tupleType.GetProperty("Item1")?.GetValue(taskResult);

        return documentValue as Document
            ?? throw new InvalidOperationException($"GetDocumentAsync did not return a Document. Actual value type: '{documentValue?.GetType().FullName ?? "null"}'.");
    }

    private static object CreateExtractMethodGenerationOptions(Type optionsType, Project project, string methodName) {
        var defaultFactory = optionsType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "GetDefault", StringComparison.Ordinal) &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType.IsInstanceOfType(project.Services));

        if (defaultFactory != null) {
            var defaultOptions = defaultFactory.Invoke(null, new object[] { project.Services });
            if (defaultOptions != null) {
                return defaultOptions;
            }
        }

        object options = CreateOptionsInstance(optionsType, project, methodName)
            ?? throw new InvalidOperationException("Could not construct ExtractMethodGenerationOptions.");

        InitializeOptionMembers(options, project, methodName);
        return options;
    }

    private static object? CreateOptionsInstance(Type optionsType, Project project, string methodName) {
        var constructors = optionsType
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .OrderBy(ctor => ctor.GetParameters().Count(parameter => !parameter.IsOptional));

        foreach (var constructor in constructors) {
            var parameters = constructor.GetParameters();
            var args = new object?[parameters.Length];
            bool canInvoke = true;

            for (int index = 0; index < parameters.Length; index++) {
                if (!TryCreateConstructorArgument(parameters[index], project, methodName, out var argumentValue)) {
                    canInvoke = false;
                    break;
                }

                args[index] = argumentValue;
            }

            if (!canInvoke) {
                continue;
            }

            try {
                return constructor.Invoke(args);
            } catch {
                // Try the next constructor shape.
            }
        }

        try {
            return Activator.CreateInstance(optionsType, nonPublic: true);
        } catch {
            return null;
        }
    }

    private static bool TryCreateConstructorArgument(System.Reflection.ParameterInfo parameter, Project project, string methodName, out object? argumentValue) {
        if (parameter.ParameterType == typeof(string) && ParameterLooksLikeMethodName(parameter.Name)) {
            argumentValue = methodName;
            return true;
        }

        if (parameter.IsOptional) {
            argumentValue = parameter.DefaultValue;
            return true;
        }

        if (TryCreateMemberValue(parameter.ParameterType, parameter.Name, project, methodName, out argumentValue)) {
            return true;
        }

        argumentValue = null;
        return false;
    }

    private static void InitializeOptionMembers(object options, Project project, string methodName) {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        foreach (var property in options.GetType().GetProperties(flags).Where(property => property.CanWrite)) {
            object? currentValue;
            try {
                currentValue = property.GetValue(options);
            } catch {
                continue;
            }

            if (property.PropertyType == typeof(string) && ParameterLooksLikeMethodName(property.Name)) {
                property.SetValue(options, methodName);
                continue;
            }

            if (currentValue != null) {
                continue;
            }

            if (TryCreateMemberValue(property.PropertyType, property.Name, project, methodName, out var propertyValue)) {
                property.SetValue(options, propertyValue);
            }
        }

        foreach (var field in options.GetType().GetFields(flags).Where(field => !field.IsInitOnly)) {
            object? currentValue;
            try {
                currentValue = field.GetValue(options);
            } catch {
                continue;
            }

            if (field.FieldType == typeof(string) && ParameterLooksLikeMethodName(field.Name)) {
                field.SetValue(options, methodName);
                continue;
            }

            if (currentValue != null) {
                continue;
            }

            if (TryCreateMemberValue(field.FieldType, field.Name, project, methodName, out var fieldValue)) {
                field.SetValue(options, fieldValue);
            }
        }
    }

    private static bool TryCreateMemberValue(Type memberType, string? memberName, Project project, string methodName, out object? value) {
        if (memberType == typeof(string) && ParameterLooksLikeMethodName(memberName)) {
            value = methodName;
            return true;
        }

        if (memberType == typeof(bool)) {
            value = false;
            return true;
        }

        if (memberType.IsEnum) {
            value = Activator.CreateInstance(memberType);
            return true;
        }

        if (memberType.IsValueType) {
            value = Activator.CreateInstance(memberType);
            return true;
        }

        if (memberType.FullName?.Contains("CodeGenerationOptions", StringComparison.Ordinal) == true &&
            TryCreateCodeGenerationOptions(memberType, project, out value)) {
            if (value != null) {
                InitializeOptionMembers(value, project, methodName);
            }
            return true;
        }

        if (HasAccessibleParameterlessConstructor(memberType)) {
            value = Activator.CreateInstance(memberType, nonPublic: true);
            if (value != null) {
                InitializeOptionMembers(value, project, methodName);
            }
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryCreateCodeGenerationOptions(Type codeGenerationOptionsType, Project project, out object? value) {
        var candidateMethods = codeGenerationOptionsType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(method => string.Equals(method.Name, "GetDefault", StringComparison.Ordinal) ||
                             string.Equals(method.Name, "CreateDefault", StringComparison.Ordinal));

        foreach (var method in candidateMethods.OrderBy(method => method.GetParameters().Length)) {
            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            bool canInvoke = true;

            for (int index = 0; index < parameters.Length; index++) {
                var parameterType = parameters[index].ParameterType;

                if (parameterType.IsInstanceOfType(project.Services)) {
                    args[index] = project.Services;
                } else if (parameterType == typeof(string)) {
                    args[index] = project.Language;
                } else if (parameters[index].IsOptional) {
                    args[index] = parameters[index].DefaultValue;
                } else {
                    canInvoke = false;
                    break;
                }
            }

            if (!canInvoke) {
                continue;
            }

            try {
                value = method.Invoke(null, args);
                if (value != null && codeGenerationOptionsType.IsInstanceOfType(value)) {
                    return true;
                }

                if (value != null && TryAdaptCodeGenerationOptions(codeGenerationOptionsType, value, out var adaptedValue)) {
                    value = adaptedValue;
                    return true;
                }
            } catch {
                // Try the next factory shape.
            }
        }

        try {
            value = Activator.CreateInstance(codeGenerationOptionsType, nonPublic: true);
            return value != null;
        } catch {
            value = null;
            return false;
        }
    }

    private static bool TryAdaptCodeGenerationOptions(Type targetType, object sourceValue, out object? adaptedValue) {
        var compatibleConstructor = targetType
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(constructor => new {
                Constructor = constructor,
                Parameters = constructor.GetParameters()
            })
            .FirstOrDefault(candidate =>
                candidate.Parameters.Length == 1 &&
                candidate.Parameters[0].ParameterType.IsInstanceOfType(sourceValue));

        if (compatibleConstructor == null) {
            adaptedValue = null;
            return false;
        }

        try {
            adaptedValue = compatibleConstructor.Constructor.Invoke(new[] { sourceValue });
            return adaptedValue != null;
        } catch {
            adaptedValue = null;
            return false;
        }
    }

    private static bool HasAccessibleParameterlessConstructor(Type type) =>
        type.GetConstructor(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null) != null;

    private static bool ParameterLooksLikeMethodName(string? memberName) =>
        !string.IsNullOrWhiteSpace(memberName) &&
        (memberName.Contains("methodname", StringComparison.OrdinalIgnoreCase) ||
         memberName.Contains("preferredname", StringComparison.OrdinalIgnoreCase) ||
         memberName.Contains("nameofextract", StringComparison.OrdinalIgnoreCase));

    private static MethodDeclarationSyntax? FindExtractedMethod(
        SyntaxNode originalRoot,
        SyntaxNode updatedRoot,
        TextSpan textSpan,
        string? preferredMethodName) {
        var originalContainingType = originalRoot.FindNode(textSpan).AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var updatedContainingType = FindMatchingTypeDeclaration(updatedRoot, originalContainingType);

        if (originalContainingType == null || updatedContainingType == null) {
            return null;
        }

        var originalContainingMethod = originalRoot.FindNode(textSpan).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var originalMethods = originalContainingType.Members.OfType<MethodDeclarationSyntax>().ToList();

        var candidates = updatedContainingType.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => !MatchesMethodSignature(method, originalContainingMethod))
            .Where(method => !originalMethods.Any(existingMethod => MatchesMethodSignature(method, existingMethod)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredMethodName)) {
            var preferredCandidate = candidates.FirstOrDefault(method => string.Equals(method.Identifier.ValueText, preferredMethodName, StringComparison.Ordinal));
            if (preferredCandidate != null) {
                return preferredCandidate;
            }
        }

        if (candidates.Count == 1) {
            return candidates[0];
        }

        return candidates
            .OrderByDescending(method => method.Identifier.ValueText.StartsWith("NewMethod", StringComparison.Ordinal))
            .ThenByDescending(method => method.SpanStart)
            .FirstOrDefault();
    }

    private static TypeDeclarationSyntax? FindMatchingTypeDeclaration(SyntaxNode updatedRoot, TypeDeclarationSyntax? originalContainingType) {
        if (originalContainingType == null) {
            return null;
        }

        var typePath = originalContainingType.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText)
            .Reverse()
            .ToArray();

        return updatedRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(type =>
                type.AncestorsAndSelf()
                    .OfType<TypeDeclarationSyntax>()
                    .Select(candidate => candidate.Identifier.ValueText)
                    .Reverse()
                    .SequenceEqual(typePath));
    }

    private static bool MatchesMethodSignature(MethodDeclarationSyntax? left, MethodDeclarationSyntax? right) {
        if (left == null || right == null) {
            return false;
        }

        if (!string.Equals(left.Identifier.ValueText, right.Identifier.ValueText, StringComparison.Ordinal)) {
            return false;
        }

        if (!string.Equals(left.ReturnType.ToString(), right.ReturnType.ToString(), StringComparison.Ordinal)) {
            return false;
        }

        if (left.ParameterList.Parameters.Count != right.ParameterList.Parameters.Count) {
            return false;
        }

        for (int i = 0; i < left.ParameterList.Parameters.Count; i++) {
            var leftParameter = left.ParameterList.Parameters[i];
            var rightParameter = right.ParameterList.Parameters[i];

            if (!string.Equals(leftParameter.Type?.ToString(), rightParameter.Type?.ToString(), StringComparison.Ordinal)) {
                return false;
            }

            if (!string.Equals(leftParameter.Modifiers.ToString(), rightParameter.Modifiers.ToString(), StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    private static Accessibility ParseAccessibility(string visibility) {
        var normalizedVisibility = Regex.Replace(visibility.Trim(), "\\s+", " ").ToLowerInvariant();

        return normalizedVisibility switch {
            "private" => Accessibility.Private,
            "public" => Accessibility.Public,
            "internal" => Accessibility.Internal,
            "protected" => Accessibility.Protected,
            "protected internal" => Accessibility.ProtectedOrInternal,
            "private protected" => Accessibility.ProtectedAndInternal,
            _ => throw new ArgumentException($"Unsupported method visibility '{visibility}'.", nameof(visibility))
        };
    }

    private static MethodDeclarationSyntax ApplyAccessibility(MethodDeclarationSyntax methodDeclaration, Accessibility accessibility) {
        var nonAccessibilityModifiers = methodDeclaration.Modifiers
            .Where(modifier =>
                !modifier.IsKind(SyntaxKind.PublicKeyword) &&
                !modifier.IsKind(SyntaxKind.PrivateKeyword) &&
                !modifier.IsKind(SyntaxKind.ProtectedKeyword) &&
                !modifier.IsKind(SyntaxKind.InternalKeyword))
            .ToArray();

        var accessibilityModifiers = accessibility switch {
            Accessibility.Public => new[] { SyntaxFactory.Token(SyntaxKind.PublicKeyword) },
            Accessibility.Private => new[] { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) },
            Accessibility.Internal => new[] { SyntaxFactory.Token(SyntaxKind.InternalKeyword) },
            Accessibility.Protected => new[] { SyntaxFactory.Token(SyntaxKind.ProtectedKeyword) },
            Accessibility.ProtectedOrInternal => new[] {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
                SyntaxFactory.Token(SyntaxKind.InternalKeyword)
            },
            Accessibility.ProtectedAndInternal => new[] {
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(accessibility), accessibility, "Unsupported accessibility for extracted method.")
        };

        return methodDeclaration.WithModifiers(SyntaxFactory.TokenList(accessibilityModifiers.Concat(nonAccessibilityModifiers)));
    }
}

