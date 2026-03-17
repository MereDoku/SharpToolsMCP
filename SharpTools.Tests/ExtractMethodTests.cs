using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SharpTools.Tests;

public class ExtractMethodTests {
    [Fact]
    public async Task ExtractMethodAsync_ExtractsMethod_RenamesIt_AndAppliesVisibility() {
        const string source = """
            using System;

            internal sealed class DeviceManagerHost
            {
                private void Configure()
                {
                    /*start*/
                    int x = 1;
                    int y = 2;
                    Console.WriteLine(x + y);
                    /*end*/
                }
            }
            """;

        var (document, textSpan, solutionManager) = CreateDocument(source);
        var service = CreateService(solutionManager);

        var updatedSolution = await service.ExtractMethodAsync(document.Id, textSpan, "SetDeviceManager", "private", CancellationToken.None);
        var updatedDocument = updatedSolution.GetDocument(document.Id)!;
        var updatedRoot = await updatedDocument.GetSyntaxRootAsync();
        Assert.NotNull(updatedRoot);

        var extractedMethod = updatedRoot!.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.ValueText == "SetDeviceManager");

        Assert.NotNull(extractedMethod);
        Assert.Contains(extractedMethod!.Modifiers, modifier => modifier.IsKind(SyntaxKind.PrivateKeyword));

        var invocationNames = updatedRoot.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression.ToString())
            .ToList();

        Assert.Contains("SetDeviceManager", invocationNames);
        Assert.DoesNotContain(updatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "NewMethod");
    }

    [Fact]
    public async Task GetExtractedDocumentAsync_ReadsDocumentFromGetDocumentAsyncResult() {
        var (document, _, _) = CreateDocument("""
            internal sealed class C
            {
                private void M()
                {
                    /*start*/
                    int value = 1;
                    /*end*/
                }
            }
            """);

        var helper = typeof(CodeModificationService).GetMethod("GetExtractedDocumentAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helper);

        var task = (Task<Document>)helper!.Invoke(null, new object[] { new FakeExtractMethodResult(document), CancellationToken.None })!;
        var extractedDocument = await task;

        Assert.Same(document, extractedDocument);
    }

    private static CodeModificationService CreateService(TestSolutionManager solutionManager) =>
        new(solutionManager, new NoOpGitService(), NullLogger<CodeModificationService>.Instance);

    private static (Document document, TextSpan textSpan, TestSolutionManager solutionManager) CreateDocument(string sourceWithMarkers) {
        const string startMarker = "/*start*/";
        const string endMarker = "/*end*/";

        int startIndex = sourceWithMarkers.IndexOf(startMarker, StringComparison.Ordinal);
        int endIndex = sourceWithMarkers.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, "Source must contain /*start*/ and /*end*/ markers.");

        string source = sourceWithMarkers.Replace(startMarker, "", StringComparison.Ordinal)
            .Replace(endMarker, "", StringComparison.Ordinal);
        int spanStart = startIndex;
        int spanEnd = endIndex - startMarker.Length;

        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        var workspace = new AdhocWorkspace(host);
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(Console).Assembly.Location))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location))
            .AddDocument(documentId, "DeviceManagerHost.cs", SourceText.From(source), filePath: Path.Combine(Path.GetTempPath(), "DeviceManagerHost.cs"));

        Assert.True(workspace.TryApplyChanges(solution), "Failed to apply AdhocWorkspace changes.");

        var document = workspace.CurrentSolution.GetDocument(documentId)!;
        var textSpan = TextSpan.FromBounds(spanStart, spanEnd);
        var solutionManager = new TestSolutionManager(workspace.CurrentSolution);
        return (document, textSpan, solutionManager);
    }

    private sealed class FakeExtractMethodResult(Document document) {
        public Task<(Document document, SyntaxToken? invocationNameToken)> GetDocumentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(Document document, SyntaxToken? invocationNameToken)>((document, null));
    }

    private sealed class TestSolutionManager(Solution currentSolution) : ISolutionManager {
        public bool IsSolutionLoaded => CurrentSolution != null;
        public MSBuildWorkspace? CurrentWorkspace => null;
        public Solution? CurrentSolution { get; private set; } = currentSolution;

        public void Dispose() { }
        public Task LoadSolutionAsync(string solutionPath, string? targetFramework, string? runtimeIdentifier, CancellationToken cancellationToken) => Task.CompletedTask;
        public void UnloadSolution() => CurrentSolution = null;
        public Task<ISymbol?> FindRoslynSymbolAsync(string fullyQualifiedName, CancellationToken cancellationToken) => Task.FromResult<ISymbol?>(null);
        public Task<INamedTypeSymbol?> FindRoslynNamedTypeSymbolAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) => Task.FromResult<INamedTypeSymbol?>(null);
        public Task<Type?> FindReflectionTypeAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) => Task.FromResult<Type?>(null);
        public Task<IEnumerable<Type>> SearchReflectionTypesAsync(string regexPattern, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Type>>(Array.Empty<Type>());
        public IEnumerable<Project> GetProjects() => CurrentSolution?.Projects ?? Array.Empty<Project>();
        public Project? GetProjectByName(string projectName) => CurrentSolution?.Projects.FirstOrDefault(project => project.Name == projectName);
        public Task<SemanticModel?> GetSemanticModelAsync(DocumentId documentId, CancellationToken cancellationToken) =>
            CurrentSolution?.GetDocument(documentId)?.GetSemanticModelAsync(cancellationToken) ?? Task.FromResult<SemanticModel?>(null);
        public Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken) =>
            CurrentSolution?.GetProject(projectId)?.GetCompilationAsync(cancellationToken) ?? Task.FromResult<Compilation?>(null);
        public Task ReloadSolutionFromDiskAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void RefreshCurrentSolution() { }
    }

    private sealed class NoOpGitService : IGitService {
        public Task<bool> IsRepositoryAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsOnSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task EnsureSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitChangesAsync(string solutionPath, IEnumerable<string> changedFilePaths, string commitMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<(bool success, string diff)> RevertLastCommitAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.FromResult((false, string.Empty));
        public Task<string> GetBranchOriginCommitAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> CreateUndoBranchAsync(string solutionPath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> GetDiffAsync(string solutionPath, string oldCommitSha, string newCommitSha, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
