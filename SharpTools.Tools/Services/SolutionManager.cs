using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using ModelContextProtocol;
using SharpTools.Tools.Mcp.Tools;
namespace SharpTools.Tools.Services;

public sealed class SolutionManager : ISolutionManager {
    private readonly ILogger<SolutionManager> _logger;
    private readonly IFuzzyFqnLookupService _fuzzyFqnLookupService;
    private MSBuildWorkspace? _workspace;
    private Solution? _currentSolution;
    private MetadataLoadContext? _metadataLoadContext;
    private PathAssemblyResolver? _pathAssemblyResolver;
    private HashSet<string> _assemblyPathsForReflection = new();
    private readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new();
    private readonly ConcurrentDictionary<DocumentId, SemanticModel> _semanticModelCache = new();
    private readonly ConcurrentDictionary<string, Type> _allLoadedReflectionTypesCache = new();
    [MemberNotNullWhen(true, nameof(_workspace), nameof(_currentSolution))]
    public bool IsSolutionLoaded => _workspace != null && _currentSolution != null;
    public MSBuildWorkspace? CurrentWorkspace => _workspace;
    public Solution? CurrentSolution => _currentSolution;
    private readonly string? _buildConfiguration;
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private readonly SemaphoreSlim _loadSolutionSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, MauiRefreshAttempt> _mauiRefreshAttemptCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex XamlFilePathAttributeRegex = new(@"XamlFilePath(?:Attribute)?\s*\(\s*@?""(?<path>[^""]+\.xaml)""\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XamlFilePathAssignmentRegex = new(@"XamlFilePath\s*=\s*@?""(?<path>[^""]+\.xaml)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan MauiRefreshFailureCooldown = TimeSpan.FromMinutes(10);

    private sealed record MauiGeneratedSourceCandidate(string FilePath, string Source, string? XamlFilePath);
    private sealed record MauiRefreshAttempt(DateTime AttemptUtc, bool Success, string Detail);
    private sealed record MauiGeneratedRefreshAnalysis(
        bool NeedsRefresh,
        string Reason,
        int DiscoveredCandidateCount,
        int MappedXamlCount,
        int IgnoredCandidateCount);

    public SolutionManager(ILogger<SolutionManager> logger, IFuzzyFqnLookupService fuzzyFqnLookupService, string? buildConfiguration = null) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fuzzyFqnLookupService = fuzzyFqnLookupService ?? throw new ArgumentNullException(nameof(fuzzyFqnLookupService));
        _buildConfiguration = buildConfiguration;
    }
    public async Task LoadSolutionAsync(string solutionPath, string? targetFramework, string? runtimeIdentifier, CancellationToken cancellationToken) {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string currentPhase = "wait-load-lock";
        long waitLoadLockDurationMs = 0;
        long unloadDurationMs = 0;
        long configurePropertiesDurationMs = 0;
        long restoreDurationMs = 0;
        long openSolutionDurationMs = 0;
        long injectMauiGlobalUsingsDurationMs = 0;
        long injectMauiXamlDurationMs = 0;
        long metadataInitializationDurationMs = 0;

        _logger.LogInformation(
            "LoadSolutionAsync started for {SolutionPath} (RequestedTargetFramework={TargetFramework}, RequestedRuntimeIdentifier={RuntimeIdentifier}, Configuration={Configuration})",
            solutionPath,
            string.IsNullOrWhiteSpace(targetFramework) ? "auto" : targetFramework,
            string.IsNullOrWhiteSpace(runtimeIdentifier) ? "auto" : runtimeIdentifier,
            string.IsNullOrWhiteSpace(_buildConfiguration) ? "auto" : _buildConfiguration);

        bool hasLoadLock = false;
        try {
            Stopwatch waitLoadLockStopwatch = Stopwatch.StartNew();
            await _loadSolutionSemaphore.WaitAsync(cancellationToken);
            waitLoadLockStopwatch.Stop();
            waitLoadLockDurationMs = waitLoadLockStopwatch.ElapsedMilliseconds;
            _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, waitLoadLockDurationMs);

            hasLoadLock = true;
            if (!File.Exists(solutionPath)) {
                _logger.LogError("Solution file not found: {SolutionPath}", solutionPath);
                throw new FileNotFoundException("Solution file not found.", solutionPath);
            }

            currentPhase = "unload-current-solution";
            Stopwatch unloadStopwatch = Stopwatch.StartNew();
            UnloadSolution(); // Clears previous state including _allLoadedReflectionTypesCache
            unloadStopwatch.Stop();
            unloadDurationMs = unloadStopwatch.ElapsedMilliseconds;
            _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, unloadDurationMs);

            try {
                currentPhase = "configure-msbuild-properties";
                Stopwatch configurePropertiesStopwatch = Stopwatch.StartNew();
                _logger.LogInformation("Creating MSBuildWorkspace...");
                var properties = new Dictionary<string, string> {
                    { "DesignTimeBuild", "true" }
                };

                if (!string.IsNullOrEmpty(_buildConfiguration)) {
                    properties.Add("Configuration", _buildConfiguration);
                }

                _targetFramework = NormalizeTargetFramework(targetFramework);
                if (!string.IsNullOrEmpty(_targetFramework)) {
                    properties.Add("TargetFramework", _targetFramework);
                    _logger.LogInformation("Using target framework override: {TargetFramework}", _targetFramework);
                }

                _runtimeIdentifier = NormalizeRuntimeIdentifier(runtimeIdentifier);
                if (!string.IsNullOrEmpty(_runtimeIdentifier)) {
                    properties.Add("RuntimeIdentifier", _runtimeIdentifier);
                    _logger.LogInformation("Using runtime identifier override: {RuntimeIdentifier}", _runtimeIdentifier);
                }

                configurePropertiesStopwatch.Stop();
                configurePropertiesDurationMs = configurePropertiesStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, configurePropertiesDurationMs);

                currentPhase = "restore-solution-dependencies";
                Stopwatch restoreStopwatch = Stopwatch.StartNew();
                await RestoreSolutionDependenciesAsync(solutionPath, cancellationToken);
                restoreStopwatch.Stop();
                restoreDurationMs = restoreStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, restoreDurationMs);

                currentPhase = "open-solution";
                Stopwatch openSolutionStopwatch = Stopwatch.StartNew();
                _workspace = MSBuildWorkspace.Create(properties, MefHostServices.DefaultHost);
                _workspace.WorkspaceFailed += OnWorkspaceFailed;
                _logger.LogInformation("Loading solution: {SolutionPath}", solutionPath);
                _currentSolution = await _workspace.OpenSolutionAsync(solutionPath, new ProgressReporter(_logger), cancellationToken);
                _logger.LogInformation("Solution loaded successfully with {ProjectCount} projects.", _currentSolution.Projects.Count());
                openSolutionStopwatch.Stop();
                openSolutionDurationMs = openSolutionStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, openSolutionDurationMs);

                currentPhase = "inject-maui-global-usings";
                Stopwatch injectMauiGlobalUsingsStopwatch = Stopwatch.StartNew();
                await InjectMauiGlobalUsingsAsync(_currentSolution, cancellationToken);
                injectMauiGlobalUsingsStopwatch.Stop();
                injectMauiGlobalUsingsDurationMs = injectMauiGlobalUsingsStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, injectMauiGlobalUsingsDurationMs);

                currentPhase = "inject-maui-xaml-generated-sources";
                Stopwatch injectMauiXamlStopwatch = Stopwatch.StartNew();
                if (_currentSolution != null) {
                    await InjectMauiXamlGeneratedSourcesAsync(_currentSolution, cancellationToken);
                }
                injectMauiXamlStopwatch.Stop();
                injectMauiXamlDurationMs = injectMauiXamlStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, injectMauiXamlDurationMs);

                currentPhase = "initialize-metadata-context";
                Stopwatch metadataInitializationStopwatch = Stopwatch.StartNew();
                InitializeMetadataContextAndReflectionCache(_currentSolution, cancellationToken);
                metadataInitializationStopwatch.Stop();
                metadataInitializationDurationMs = metadataInitializationStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", currentPhase, metadataInitializationDurationMs);
            } catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Failed to load solution: {SolutionPath} during phase '{PhaseName}' after {ElapsedMs} ms",
                    solutionPath,
                    currentPhase,
                    totalStopwatch.ElapsedMilliseconds);
                UnloadSolution();
                throw;
            }
        } catch (OperationCanceledException) {
            _logger.LogWarning(
                "LoadSolutionAsync cancelled during phase '{PhaseName}' after {ElapsedMs} ms",
                currentPhase,
                totalStopwatch.ElapsedMilliseconds);
            throw;
        } finally {
            if (totalStopwatch.IsRunning) {
                totalStopwatch.Stop();
            }

            _logger.LogInformation(
                "LoadSolutionAsync summary: total={TotalMs} ms; waitLock={WaitLockMs} ms; unload={UnloadMs} ms; configure={ConfigureMs} ms; restore={RestoreMs} ms; openSolution={OpenSolutionMs} ms; injectMauiGlobalUsings={InjectMauiGlobalUsingsMs} ms; injectMauiXaml={InjectMauiXamlMs} ms; metadata={MetadataMs} ms",
                totalStopwatch.ElapsedMilliseconds,
                waitLoadLockDurationMs,
                unloadDurationMs,
                configurePropertiesDurationMs,
                restoreDurationMs,
                openSolutionDurationMs,
                injectMauiGlobalUsingsDurationMs,
                injectMauiXamlDurationMs,
                metadataInitializationDurationMs);

            if (totalStopwatch.ElapsedMilliseconds > 45_000) {
                _logger.LogWarning("LoadSolutionAsync exceeded expected duration: {ElapsedMs} ms", totalStopwatch.ElapsedMilliseconds);
            }

            if (hasLoadLock) {
                _loadSolutionSemaphore.Release();
            }
        }
    }
    private void InitializeMetadataContextAndReflectionCache(Solution solution, CancellationToken cancellationToken = default) {
        Stopwatch metadataStopwatch = Stopwatch.StartNew();
        try {
            // Check cancellation at entry point
            cancellationToken.ThrowIfCancellationRequested();

            _assemblyPathsForReflection.Clear();

            // Add runtime assemblies
            string[] runtimeAssemblies = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
            foreach (var assemblyPath in runtimeAssemblies) {
                // Check cancellation periodically
                cancellationToken.ThrowIfCancellationRequested();
                if (!_assemblyPathsForReflection.Contains(assemblyPath)) {
                    _assemblyPathsForReflection.Add(assemblyPath);
                }
            }

            // Load NuGet package assemblies from global cache instead of output directories
            var nugetAssemblies = GetNuGetAssemblyPaths(solution, cancellationToken);
            foreach (var assemblyPath in nugetAssemblies) {
                cancellationToken.ThrowIfCancellationRequested();
                _assemblyPathsForReflection.Add(assemblyPath);
            }

            // Check cancellation before cleanup operations
            cancellationToken.ThrowIfCancellationRequested();

            // Remove mscorlib.dll from the list of assemblies as it is loaded by default
            _assemblyPathsForReflection.RemoveWhere(p => p.EndsWith("mscorlib.dll", StringComparison.OrdinalIgnoreCase));

            // Remove duplicate files regardless of path
            _assemblyPathsForReflection = _assemblyPathsForReflection
                .GroupBy(Path.GetFileName)
                .Select(g => g.First())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check cancellation before creating context
            cancellationToken.ThrowIfCancellationRequested();

            _pathAssemblyResolver = new PathAssemblyResolver(_assemblyPathsForReflection);
            _metadataLoadContext = new MetadataLoadContext(_pathAssemblyResolver);
            _logger.LogInformation("MetadataLoadContext initialized with {PathCount} distinct search paths.", _assemblyPathsForReflection.Count);

            // Check cancellation before populating cache
            cancellationToken.ThrowIfCancellationRequested();

            PopulateReflectionCache(_assemblyPathsForReflection, cancellationToken);
        } finally {
            metadataStopwatch.Stop();
            _logger.LogInformation("Phase '{PhaseName}' completed in {ElapsedMs} ms", nameof(InitializeMetadataContextAndReflectionCache), metadataStopwatch.ElapsedMilliseconds);
        }
    }
    private async Task InjectMauiGlobalUsingsAsync(Solution solution, CancellationToken cancellationToken) {
        string configuration = string.IsNullOrWhiteSpace(_buildConfiguration) ? "Debug" : _buildConfiguration;
        var updatedSolution = solution;
        int injectedProjects = 0;

        foreach (var project in solution.Projects) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(project.FilePath)) {
                continue;
            }

            if (!ProjectUsesMaui(project.FilePath)) {
                continue;
            }

            var fastCheck = await TryProjectHasMauiGlobalUsingsFastAsync(project, cancellationToken);
            if (fastCheck.Completed && fastCheck.HasMauiGlobalUsings) {
                _logger.LogInformation(
                    "Skipped MAUI global usings injection for project {ProjectName}: fast check found MAUI global usings in source files.",
                    project.Name);
                continue;
            }

            if (!fastCheck.Completed) {
                _logger.LogInformation(
                    "Fast MAUI global usings check was inconclusive for project {ProjectName}; falling back to compilation-based check.",
                    project.Name);
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) {
                continue;
            }

            if (CompilationHasMauiGlobalUsings(compilation)) {
                _logger.LogInformation(
                    "Skipped MAUI global usings injection for project {ProjectName}: compilation already contains MAUI global usings.",
                    project.Name);
                continue;
            }

            string projectTargetFramework = _targetFramework ?? SolutionTools.ExtractTargetFrameworkFromProjectFile(project.FilePath);
            if (string.IsNullOrWhiteSpace(projectTargetFramework)) {
                _logger.LogWarning("Could not determine target framework for project {ProjectName}; skipping MAUI global using injection.", project.Name);
                continue;
            }

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(projectDir)) {
                continue;
            }
            var objSearchPaths = GetMauiGlobalUsingsSearchPaths(projectDir, configuration, projectTargetFramework);
            string? globalUsingsPath = null;
            foreach (var objPath in objSearchPaths) {
                globalUsingsPath = FindMauiGlobalUsingsFile(objPath);
                if (!string.IsNullOrEmpty(globalUsingsPath)) {
                    break;
                }
            }
            if (string.IsNullOrEmpty(globalUsingsPath)) {
                _logger.LogWarning(
                    "MAUI global usings file not found for project {ProjectName} under expected obj paths: {ObjPaths}",
                    project.Name,
                    string.Join(", ", objSearchPaths));
                continue;
            }

            string mauiUsingsContent = await File.ReadAllTextAsync(globalUsingsPath, cancellationToken);
            var mauiGlobalUsings = ExtractMauiGlobalUsings(mauiUsingsContent);
            if (mauiGlobalUsings.Count == 0) {
                _logger.LogWarning("No MAUI global usings found in {GlobalUsingsPath} for project {ProjectName}", globalUsingsPath, project.Name);
                continue;
            }

            const string injectedDocName = "SharpTools.Maui.GlobalUsings.g.cs";
            if (project.Documents.Any(d => d.Name.Equals(injectedDocName, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            var injectedFilePath = Path.Combine(
                projectDir,
                "obj",
                configuration,
                projectTargetFramework,
                "SharpTools",
                "Generated",
                injectedDocName);
            var injectedContent = "// Generated by SharpTools to inject MAUI global usings\n" +
                string.Join("\n", mauiGlobalUsings) + "\n";
            var injectedText = SourceText.From(injectedContent, Encoding.UTF8);
            updatedSolution = updatedSolution.AddDocument(
                DocumentId.CreateNewId(project.Id),
                injectedDocName,
                injectedText,
                new[] { "SharpTools", "Generated" },
                filePath: injectedFilePath
            );

            injectedProjects++;
            _logger.LogInformation(
                "Injected {UsingCount} MAUI global usings into project {ProjectName} from {GlobalUsingsPath} (doc path: {InjectedPath})",
                mauiGlobalUsings.Count,
                project.Name,
                globalUsingsPath,
                injectedFilePath);
        }

        if (injectedProjects > 0 && _workspace != null) {
            if (_workspace.TryApplyChanges(updatedSolution)) {
                _currentSolution = _workspace.CurrentSolution;
            } else {
                _currentSolution = updatedSolution;
            }

            _compilationCache.Clear();
            _semanticModelCache.Clear();
            _logger.LogInformation("MAUI global usings injected into {ProjectCount} project(s).", injectedProjects);
        }
    }
    private async Task<(bool HasMauiGlobalUsings, bool Completed)> TryProjectHasMauiGlobalUsingsFastAsync(Project project, CancellationToken cancellationToken) {
        bool hadReadErrors = false;
        IEnumerable<Document> orderedDocuments = project.Documents
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular)
            .OrderByDescending(d => IsPotentialGlobalUsingsDocumentName(d.Name));

        foreach (Document document in orderedDocuments) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                SourceText? sourceText = await document.GetTextAsync(cancellationToken);
                if (sourceText != null && ContainsMauiGlobalUsingLine(sourceText.ToString())) {
                    return (true, true);
                }
            } catch (Exception ex) when (!(ex is OperationCanceledException)) {
                hadReadErrors = true;
                _logger.LogDebug(
                    ex,
                    "Fast MAUI global usings check failed for document {DocumentName} in project {ProjectName}",
                    document.Name,
                    project.Name);
            }
        }

        return (false, !hadReadErrors);
    }
    private static bool IsPotentialGlobalUsingsDocumentName(string? documentName) {
        if (string.IsNullOrWhiteSpace(documentName)) {
            return false;
        }

        return documentName.Contains("GlobalUsings", StringComparison.OrdinalIgnoreCase);
    }
    private static bool ContainsMauiGlobalUsingLine(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("global using ", StringComparison.Ordinal)) {
                continue;
            }

            if (trimmed.Contains("Microsoft.Maui", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
    private async Task InjectMauiXamlGeneratedSourcesAsync(Solution solution, CancellationToken cancellationToken) {
        Stopwatch injectMauiXamlStopwatch = Stopwatch.StartNew();
        string configuration = string.IsNullOrWhiteSpace(_buildConfiguration) ? "Debug" : _buildConfiguration;
        var updatedSolution = solution;
        int mauiProjectsProcessed = 0;
        int injectedProjects = 0;
        int injectedDocuments = 0;
        string? vsGeneratedDocumentsPath = GetVsGeneratedDocumentsPath();
        var vsGeneratedFiles = string.IsNullOrWhiteSpace(vsGeneratedDocumentsPath)
            ? new List<string>()
            : FindMauiXamlGeneratedFiles(vsGeneratedDocumentsPath).ToList();
        var xamlPathCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (var project in solution.Projects) {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(project.FilePath)) {
                    continue;
                }

                if (!ProjectUsesMaui(project.FilePath)) {
                    continue;
                }

                mauiProjectsProcessed++;
                Stopwatch projectInjectionStopwatch = Stopwatch.StartNew();
                int projectDiscoveredCandidates = 0;
                int projectSelectedCandidates = 0;
                int projectInjectedDocuments = 0;
                bool refreshRequested = false;
                bool refreshSkipped = false;

                try {
                    string projectTargetFramework = _targetFramework ?? SolutionTools.ExtractTargetFrameworkFromProjectFile(project.FilePath);
                    if (string.IsNullOrWhiteSpace(projectTargetFramework)) {
                        _logger.LogWarning("Could not determine target framework for project {ProjectName}; skipping MAUI XAML injection.", project.Name);
                        continue;
                    }

                    var projectDir = Path.GetDirectoryName(project.FilePath);
                    if (string.IsNullOrEmpty(projectDir)) {
                        continue;
                    }

                    var objSearchPaths = GetMauiGlobalUsingsSearchPaths(projectDir, configuration, projectTargetFramework);
                    var compilerGeneratedSearchPaths = GetMauiCompilerGeneratedSearchPaths(projectDir, configuration, projectTargetFramework);
                    string? effectiveRuntimeIdentifier = ResolveRuntimeIdentifierForTargetFramework(projectTargetFramework);
                    string refreshCacheKey = BuildMauiRefreshCacheKey(project.FilePath, projectTargetFramework, effectiveRuntimeIdentifier, configuration);

                    var discoveredCandidates = new List<MauiGeneratedSourceCandidate>();
                    CollectMauiGeneratedSourceCandidates(
                        discoveredCandidates,
                        objSearchPaths,
                        compilerGeneratedSearchPaths,
                        vsGeneratedFiles,
                        projectDir,
                        xamlPathCache,
                        cancellationToken);
                    projectDiscoveredCandidates = discoveredCandidates.Count;

                    MauiGeneratedRefreshAnalysis refreshAnalysis = AnalyzeMauiGeneratedSourcesRefresh(projectDir, discoveredCandidates, cancellationToken);
                    _logger.LogInformation(
                        "MAUI generated source mapping for project {ProjectName}: discovered={DiscoveredCount}, mapped={MappedCount}, ignored={IgnoredCount}",
                        project.Name,
                        refreshAnalysis.DiscoveredCandidateCount,
                        refreshAnalysis.MappedXamlCount,
                        refreshAnalysis.IgnoredCandidateCount);
                    if (refreshAnalysis.NeedsRefresh) {
                        refreshRequested = true;
                        _logger.LogInformation(
                            "MAUI generated sources refresh required for project {ProjectName}: {Reason}",
                            project.Name,
                            refreshAnalysis.Reason);

                        if (ShouldSkipMauiRefreshBuild(refreshCacheKey, out MauiRefreshAttempt? lastFailedAttempt, out TimeSpan retryAfter)) {
                            refreshSkipped = true;
                            _logger.LogWarning(
                                "Skipping MAUI refresh build for project {ProjectName} due to recent failed attempt. Retry in {RetryAfter}. Last failure detail: {LastFailureDetail}",
                                project.Name,
                                retryAfter,
                                lastFailedAttempt?.Detail ?? "unknown");
                        } else {
                            var (generatedOutputPath, refreshSucceeded) = await GenerateMauiXamlSourcesForProjectAsync(project.FilePath, configuration, projectTargetFramework, cancellationToken);
                            RecordMauiRefreshAttempt(refreshCacheKey, refreshSucceeded, refreshSucceeded ? "refresh build succeeded" : "refresh build failed");

                            discoveredCandidates.Clear();
                            AddMauiGeneratedSourceCandidates(
                                discoveredCandidates,
                                FindMauiXamlGeneratedFiles(generatedOutputPath),
                                "compiler-generated",
                                projectDir,
                                requireProjectMatchByXamlPath: false,
                                xamlPathCache,
                                cancellationToken);
                            CollectMauiGeneratedSourceCandidates(
                                discoveredCandidates,
                                objSearchPaths,
                                compilerGeneratedSearchPaths,
                                vsGeneratedFiles,
                                projectDir,
                                xamlPathCache,
                                cancellationToken);
                            projectDiscoveredCandidates = discoveredCandidates.Count;

                            MauiGeneratedRefreshAnalysis postRefreshAnalysis = AnalyzeMauiGeneratedSourcesRefresh(projectDir, discoveredCandidates, cancellationToken);
                            _logger.LogInformation(
                                "Post-refresh MAUI source mapping for project {ProjectName}: discovered={DiscoveredCount}, mapped={MappedCount}, ignored={IgnoredCount}",
                                project.Name,
                                postRefreshAnalysis.DiscoveredCandidateCount,
                                postRefreshAnalysis.MappedXamlCount,
                                postRefreshAnalysis.IgnoredCandidateCount);
                            if (postRefreshAnalysis.NeedsRefresh) {
                                _logger.LogWarning(
                                    "MAUI generated sources may still be stale for project {ProjectName} after refresh: {Reason}",
                                    project.Name,
                                    postRefreshAnalysis.Reason);
                            }
                        }
                    }

                    if (discoveredCandidates.Count == 0) {
                        continue;
                    }

                    var selectedCandidates = SelectPreferredMauiGeneratedCandidates(discoveredCandidates);
                    projectSelectedCandidates = selectedCandidates.Count;
                    int objCount = discoveredCandidates.Count(c => c.Source == "obj");
                    int compilerGeneratedCount = discoveredCandidates.Count(c => c.Source == "compiler-generated");
                    int vsGeneratedDocumentsCount = discoveredCandidates.Count(c => c.Source == "vs-generated-documents");
                    _logger.LogInformation(
                        "Discovered {TotalCount} MAUI generated source candidate(s) for project {ProjectName}. obj={ObjCount}, compiler-generated={CompilerGeneratedCount}, vs-generated-documents={VsGeneratedCount}",
                        discoveredCandidates.Count,
                        project.Name,
                        objCount,
                        compilerGeneratedCount,
                        vsGeneratedDocumentsCount);

                    foreach (var candidate in selectedCandidates) {
                        string generatedFile = candidate.FilePath;
                        if (project.Documents.Any(d => d.FilePath != null &&
                            string.Equals(d.FilePath, generatedFile, StringComparison.OrdinalIgnoreCase))) {
                            continue;
                        }

                        string generatedContent = await File.ReadAllTextAsync(generatedFile, cancellationToken);
                        var injectedText = SourceText.From(generatedContent, Encoding.UTF8);
                        var injectedDocName = Path.GetFileName(generatedFile);
                        updatedSolution = updatedSolution.AddDocument(
                            DocumentId.CreateNewId(project.Id),
                            injectedDocName,
                            injectedText,
                            new[] { "SharpTools", "Generated", "MauiXaml" },
                            filePath: generatedFile
                        );

                        injectedDocuments++;
                        projectInjectedDocuments++;
                    }

                    if (projectInjectedDocuments > 0) {
                        injectedProjects++;
                        _logger.LogInformation(
                            "Injected {DocumentCount} MAUI XAML generated document(s) into project {ProjectName} after selecting {SelectedCount} preferred candidate(s).",
                            projectInjectedDocuments,
                            project.Name,
                            selectedCandidates.Count);
                    }
                } finally {
                    projectInjectionStopwatch.Stop();
                    _logger.LogInformation(
                        "MAUI XAML injection phase completed for project {ProjectName} in {ElapsedMs} ms (refreshRequested={RefreshRequested}, refreshSkipped={RefreshSkipped}, discovered={DiscoveredCount}, selected={SelectedCount}, injected={InjectedCount})",
                        project.Name,
                        projectInjectionStopwatch.ElapsedMilliseconds,
                        refreshRequested,
                        refreshSkipped,
                        projectDiscoveredCandidates,
                        projectSelectedCandidates,
                        projectInjectedDocuments);
                }
            }

            if (injectedDocuments > 0) {
                _currentSolution = updatedSolution;
                _compilationCache.Clear();
                _semanticModelCache.Clear();
                _logger.LogInformation(
                    "MAUI XAML generated sources injected into {ProjectCount} project(s) in-memory only (no project file persistence).",
                    injectedProjects);
            }
        } finally {
            injectMauiXamlStopwatch.Stop();
            _logger.LogInformation(
                "Phase '{PhaseName}' completed in {ElapsedMs} ms (mauiProjectsProcessed={MauiProjectsProcessed}, injectedProjects={InjectedProjects}, injectedDocuments={InjectedDocuments})",
                nameof(InjectMauiXamlGeneratedSourcesAsync),
                injectMauiXamlStopwatch.ElapsedMilliseconds,
                mauiProjectsProcessed,
                injectedProjects,
                injectedDocuments);
        }
    }
    private async Task<(string GeneratedOutputPath, bool Success)> GenerateMauiXamlSourcesForProjectAsync(string projectFilePath, string configuration, string targetFramework, CancellationToken cancellationToken) {
        string projectDir = Path.GetDirectoryName(projectFilePath) ?? string.Empty;
        string generatedOutputPath = GetMauiCompilerGeneratedOutputPath(projectDir, configuration, targetFramework);
        try {
            Directory.CreateDirectory(generatedOutputPath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to create compiler-generated output directory at {GeneratedOutputPath}", generatedOutputPath);
            return (generatedOutputPath, false);
        }

        ProcessStartInfo startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = projectDir
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectFilePath);
        startInfo.ArgumentList.Add("-t:Compile");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-verbosity:minimal");
        startInfo.ArgumentList.Add("-p:DesignTimeBuild=true");
        startInfo.ArgumentList.Add("-p:BuildingProject=true");
        startInfo.ArgumentList.Add("-p:SkipCompilerExecution=false");
        startInfo.ArgumentList.Add("-p:EmitCompilerGeneratedFiles=true");
        startInfo.ArgumentList.Add($"-p:CompilerGeneratedFilesOutputPath={generatedOutputPath}");
        startInfo.ArgumentList.Add("-p:SkipInvalidConfigurations=true");
        startInfo.ArgumentList.Add($"-p:TargetFramework={targetFramework}");

        if (!string.IsNullOrWhiteSpace(_runtimeIdentifier)) {
            startInfo.ArgumentList.Add($"-p:RuntimeIdentifier={_runtimeIdentifier}");
        }

        if (!string.IsNullOrWhiteSpace(configuration)) {
            startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        }

        _logger.LogInformation(
            "Generating MAUI XAML sources for project {ProjectFilePath} (TargetFramework={TargetFramework}, RuntimeIdentifier={RuntimeIdentifier}, Configuration={Configuration}, Output={OutputPath})",
            projectFilePath,
            targetFramework,
            string.IsNullOrWhiteSpace(_runtimeIdentifier) ? "auto" : _runtimeIdentifier,
            configuration,
            generatedOutputPath);

        using Process process = new Process {
            StartInfo = startInfo
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) {
                outputBuilder.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) => {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) {
                errorBuilder.AppendLine(eventArgs.Data);
            }
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!process.Start()) {
            _logger.LogWarning("Failed to start dotnet msbuild for MAUI generated sources on project {ProjectFilePath}", projectFilePath);
            return (generatedOutputPath, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try {
            await process.WaitForExitAsync(cancellationToken);
        } catch (OperationCanceledException) {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            } catch {
                // Best-effort kill on cancellation.
            }
            throw;
        } finally {
            stopwatch.Stop();
        }

        if (process.ExitCode == 0) {
            _logger.LogInformation(
                "MAUI XAML generation build succeeded for {ProjectFilePath} in {ElapsedMilliseconds} ms.",
                projectFilePath,
                stopwatch.ElapsedMilliseconds);
            return (generatedOutputPath, true);
        }

        string errorOutput = errorBuilder.ToString().Trim();
        string standardOutput = outputBuilder.ToString().Trim();
        string details = string.IsNullOrWhiteSpace(errorOutput) ? standardOutput : errorOutput;
        if (string.IsNullOrWhiteSpace(details)) {
            details = "No output captured.";
        }

        _logger.LogWarning(
            "MAUI XAML generation build failed for {ProjectFilePath} with exit code {ExitCode} after {ElapsedMilliseconds} ms. Details: {Details}",
            projectFilePath,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            TrimForLog(details, 2000));

        return (generatedOutputPath, false);
    }
    private static string TrimForLog(string text, int maxLength) {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) {
            return text;
        }

        return text.Substring(0, maxLength) + "...";
    }
    private static void AddMauiGeneratedSourceCandidates(
        List<MauiGeneratedSourceCandidate> destination,
        IEnumerable<string> filePaths,
        string source,
        string projectDir,
        bool requireProjectMatchByXamlPath,
        Dictionary<string, string?> xamlPathCache,
        CancellationToken cancellationToken) {
        foreach (string filePath in filePaths) {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) {
                continue;
            }

            string? xamlFilePath = GetOrExtractXamlFilePath(filePath, projectDir, xamlPathCache);
            bool hasProjectMatch = !string.IsNullOrWhiteSpace(xamlFilePath) && IsPathUnderDirectory(xamlFilePath, projectDir);

            if (requireProjectMatchByXamlPath && !hasProjectMatch) {
                continue;
            }

            destination.Add(new MauiGeneratedSourceCandidate(filePath, source, xamlFilePath));
        }
    }
    private static void CollectMauiGeneratedSourceCandidates(
        List<MauiGeneratedSourceCandidate> destination,
        IEnumerable<string> objSearchPaths,
        IEnumerable<string> compilerGeneratedSearchPaths,
        IEnumerable<string> vsGeneratedFiles,
        string projectDir,
        Dictionary<string, string?> xamlPathCache,
        CancellationToken cancellationToken) {
        AddMauiGeneratedSourceCandidates(
            destination,
            objSearchPaths.SelectMany(FindMauiXamlGeneratedFiles),
            "obj",
            projectDir,
            requireProjectMatchByXamlPath: false,
            xamlPathCache,
            cancellationToken);
        AddMauiGeneratedSourceCandidates(
            destination,
            compilerGeneratedSearchPaths.SelectMany(FindMauiXamlGeneratedFiles),
            "compiler-generated",
            projectDir,
            requireProjectMatchByXamlPath: false,
            xamlPathCache,
            cancellationToken);
        AddMauiGeneratedSourceCandidates(
            destination,
            vsGeneratedFiles,
            "vs-generated-documents",
            projectDir,
            requireProjectMatchByXamlPath: true,
            xamlPathCache,
            cancellationToken);
    }
    private static MauiGeneratedRefreshAnalysis AnalyzeMauiGeneratedSourcesRefresh(
        string projectDir,
        IReadOnlyCollection<MauiGeneratedSourceCandidate> discoveredCandidates,
        CancellationToken cancellationToken) {
        if (discoveredCandidates.Count == 0) {
            return new MauiGeneratedRefreshAnalysis(
                NeedsRefresh: true,
                Reason: "no generated source candidates were found",
                DiscoveredCandidateCount: 0,
                MappedXamlCount: 0,
                IgnoredCandidateCount: 0);
        }

        int ignoredCandidateCount = 0;
        var candidateByXamlPath = new Dictionary<string, MauiGeneratedSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (MauiGeneratedSourceCandidate candidate in discoveredCandidates) {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate.XamlFilePath)) {
                ignoredCandidateCount++;
                continue;
            }
            if (!IsPathUnderDirectory(candidate.XamlFilePath, projectDir)) {
                ignoredCandidateCount++;
                continue;
            }
            if (!File.Exists(candidate.FilePath)) {
                ignoredCandidateCount++;
                continue;
            }
            if (!File.Exists(candidate.XamlFilePath)) {
                ignoredCandidateCount++;
                continue;
            }

            if (!candidateByXamlPath.TryGetValue(candidate.XamlFilePath, out MauiGeneratedSourceCandidate? existingCandidate)) {
                candidateByXamlPath[candidate.XamlFilePath] = candidate;
                continue;
            }

            DateTime existingWriteTime = File.GetLastWriteTimeUtc(existingCandidate.FilePath);
            DateTime currentWriteTime = File.GetLastWriteTimeUtc(candidate.FilePath);
            if (currentWriteTime > existingWriteTime) {
                candidateByXamlPath[candidate.XamlFilePath] = candidate;
            }
        }

        if (candidateByXamlPath.Count == 0) {
            return new MauiGeneratedRefreshAnalysis(
                NeedsRefresh: true,
                Reason: "no discovered generated sources could be mapped back to source-generator XAML paths",
                DiscoveredCandidateCount: discoveredCandidates.Count,
                MappedXamlCount: 0,
                IgnoredCandidateCount: ignoredCandidateCount);
        }

        foreach (var mappedEntry in candidateByXamlPath) {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsGeneratedSourceStale(mappedEntry.Key, mappedEntry.Value.FilePath)) {
                return new MauiGeneratedRefreshAnalysis(
                    NeedsRefresh: true,
                    Reason: $"generated source is stale for '{Path.GetFileName(mappedEntry.Key)}' ({mappedEntry.Key})",
                    DiscoveredCandidateCount: discoveredCandidates.Count,
                    MappedXamlCount: candidateByXamlPath.Count,
                    IgnoredCandidateCount: ignoredCandidateCount);
            }
        }

        return new MauiGeneratedRefreshAnalysis(
            NeedsRefresh: false,
            Reason: string.Empty,
            DiscoveredCandidateCount: discoveredCandidates.Count,
            MappedXamlCount: candidateByXamlPath.Count,
            IgnoredCandidateCount: ignoredCandidateCount);
    }
    private static bool IsGeneratedSourceStale(string xamlFilePath, string generatedFilePath) {
        if (!File.Exists(xamlFilePath) || !File.Exists(generatedFilePath)) {
            return true;
        }

        DateTime xamlWriteTime = File.GetLastWriteTimeUtc(xamlFilePath);
        DateTime generatedWriteTime = File.GetLastWriteTimeUtc(generatedFilePath);
        return xamlWriteTime > generatedWriteTime;
    }
    private static List<MauiGeneratedSourceCandidate> SelectPreferredMauiGeneratedCandidates(IEnumerable<MauiGeneratedSourceCandidate> candidates) {
        var bestByKey = new Dictionary<string, MauiGeneratedSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (MauiGeneratedSourceCandidate candidate in candidates) {
            string key = string.IsNullOrWhiteSpace(candidate.XamlFilePath) ? candidate.FilePath : candidate.XamlFilePath!;
            if (!bestByKey.TryGetValue(key, out MauiGeneratedSourceCandidate? currentBest)) {
                bestByKey[key] = candidate;
                continue;
            }

            int currentScore = GetMauiGeneratedCandidatePreferenceScore(currentBest);
            int newScore = GetMauiGeneratedCandidatePreferenceScore(candidate);
            if (newScore > currentScore) {
                bestByKey[key] = candidate;
            }
        }

        return bestByKey.Values
            .OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private static int GetMauiGeneratedCandidatePreferenceScore(MauiGeneratedSourceCandidate candidate) {
        int score = 0;
        if (candidate.FilePath.EndsWith(".xaml.sg.cs", StringComparison.OrdinalIgnoreCase)) {
            score += 400;
        } else if (candidate.FilePath.EndsWith(".xaml.g.cs", StringComparison.OrdinalIgnoreCase)) {
            score += 300;
        } else if (candidate.FilePath.EndsWith(".sg.cs", StringComparison.OrdinalIgnoreCase)) {
            score += 250;
        } else {
            score += 200;
        }

        if (!string.Equals(candidate.Source, "vs-generated-documents", StringComparison.OrdinalIgnoreCase)) {
            score += 25;
        }

        if (!string.IsNullOrWhiteSpace(candidate.XamlFilePath)) {
            score += 10;
        }

        return score;
    }
    private static string? GetOrExtractXamlFilePath(string generatedFilePath, string projectDir, Dictionary<string, string?> xamlPathCache) {
        if (xamlPathCache.TryGetValue(generatedFilePath, out string? cachedValue)) {
            return cachedValue;
        }

        string? extractedValue = TryExtractXamlFilePath(generatedFilePath, projectDir);
        xamlPathCache[generatedFilePath] = extractedValue;
        return extractedValue;
    }
    private static string? TryExtractXamlFilePath(string generatedFilePath, string projectDir) {
        try {
            string text = File.ReadAllText(generatedFilePath);
            Match match = XamlFilePathAttributeRegex.Match(text);
            if (!match.Success) {
                match = XamlFilePathAssignmentRegex.Match(text);
            }

            if (!match.Success) {
                return null;
            }

            string extractedPath = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(extractedPath)) {
                return null;
            }

            return NormalizeExtractedPath(extractedPath, projectDir);
        } catch {
            return null;
        }
    }
    private static string? NormalizeExtractedPath(string extractedPath, string projectDir) {
        try {
            string normalized = extractedPath.Trim();
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
            normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(normalized)) {
                normalized = Path.GetFullPath(Path.Combine(projectDir, normalized));
            } else {
                normalized = Path.GetFullPath(normalized);
            }

            return normalized;
        } catch {
            return null;
        }
    }
    private static bool IsPathUnderDirectory(string candidatePath, string directoryPath) {
        try {
            string fullCandidatePath = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDirectoryPath = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string directoryPrefix = fullDirectoryPath + Path.DirectorySeparatorChar;
            return fullCandidatePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullCandidatePath, fullDirectoryPath, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }
    private static string? GetVsGeneratedDocumentsPath() {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) {
            return null;
        }

        string vsGeneratedDocumentsPath = Path.Combine(localAppData, "Temp", "VSGeneratedDocuments");
        if (!Directory.Exists(vsGeneratedDocumentsPath)) {
            return null;
        }

        return vsGeneratedDocumentsPath;
    }
    private List<string> GetMauiCompilerGeneratedSearchPaths(string projectDir, string configuration, string targetFramework) {
        var paths = new List<string>();
        string basePath = Path.Combine(projectDir, "obj", configuration, targetFramework, "SharpTools", "CompilerGenerated");
        paths.Add(basePath);

        if (targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase)) {
            string? runtimeIdentifier = _runtimeIdentifier;
            if (string.IsNullOrWhiteSpace(runtimeIdentifier)) {
                runtimeIdentifier = GetDefaultRuntimeIdentifier();
            }

            if (!string.IsNullOrWhiteSpace(runtimeIdentifier)) {
                paths.Insert(0, Path.Combine(projectDir, "obj", configuration, targetFramework, runtimeIdentifier, "SharpTools", "CompilerGenerated"));
            }
        }

        return paths;
    }
    private string GetMauiCompilerGeneratedOutputPath(string projectDir, string configuration, string targetFramework) {
        List<string> searchPaths = GetMauiCompilerGeneratedSearchPaths(projectDir, configuration, targetFramework);
        if (searchPaths.Count == 0) {
            return Path.Combine(projectDir, "obj", configuration, targetFramework, "SharpTools", "CompilerGenerated");
        }

        return searchPaths[0];
    }
    private string? ResolveRuntimeIdentifierForTargetFramework(string targetFramework) {
        if (!string.IsNullOrWhiteSpace(_runtimeIdentifier)) {
            return _runtimeIdentifier;
        }

        if (targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase)) {
            return GetDefaultRuntimeIdentifier();
        }

        return null;
    }
    private static string BuildMauiRefreshCacheKey(string projectFilePath, string targetFramework, string? runtimeIdentifier, string configuration) {
        string normalizedProjectPath = Path.GetFullPath(projectFilePath);
        string normalizedRuntimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier) ? "auto" : runtimeIdentifier;
        string normalizedConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
        return $"{normalizedProjectPath}|{targetFramework}|{normalizedRuntimeIdentifier}|{normalizedConfiguration}";
    }
    private bool ShouldSkipMauiRefreshBuild(string cacheKey, out MauiRefreshAttempt? lastAttempt, out TimeSpan retryAfter) {
        retryAfter = TimeSpan.Zero;
        if (!_mauiRefreshAttemptCache.TryGetValue(cacheKey, out lastAttempt) || lastAttempt == null) {
            return false;
        }

        if (lastAttempt.Success) {
            return false;
        }

        TimeSpan elapsed = DateTime.UtcNow - lastAttempt.AttemptUtc;
        if (elapsed >= MauiRefreshFailureCooldown) {
            return false;
        }

        retryAfter = MauiRefreshFailureCooldown - elapsed;
        return true;
    }
    private void RecordMauiRefreshAttempt(string cacheKey, bool success, string detail) {
        _mauiRefreshAttemptCache[cacheKey] = new MauiRefreshAttempt(DateTime.UtcNow, success, detail);
    }
    private async Task RestoreSolutionDependenciesAsync(string solutionPath, CancellationToken cancellationToken) {
        Stopwatch restoreStopwatch = Stopwatch.StartNew();
        ProcessStartInfo startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(solutionPath);
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.ArgumentList.Add("--disable-parallel");
        startInfo.ArgumentList.Add("-p:DesignTimeBuild=true");
        startInfo.ArgumentList.Add("-p:SkipInvalidConfigurations=true");

        if (!string.IsNullOrWhiteSpace(_targetFramework)) {
            startInfo.ArgumentList.Add($"-p:TargetFramework={_targetFramework}");
        }

        if (!string.IsNullOrWhiteSpace(_runtimeIdentifier)) {
            startInfo.ArgumentList.Add($"-p:RuntimeIdentifier={_runtimeIdentifier}");
        }

        if (!string.IsNullOrWhiteSpace(_buildConfiguration)) {
            startInfo.ArgumentList.Add($"-p:Configuration={_buildConfiguration}");
        }

        _logger.LogInformation(
            "Running restore for solution {SolutionPath} (TargetFramework={TargetFramework}, RuntimeIdentifier={RuntimeIdentifier}, Configuration={Configuration})",
            solutionPath,
            string.IsNullOrWhiteSpace(_targetFramework) ? "auto" : _targetFramework,
            string.IsNullOrWhiteSpace(_runtimeIdentifier) ? "auto" : _runtimeIdentifier,
            string.IsNullOrWhiteSpace(_buildConfiguration) ? "auto" : _buildConfiguration);

        using Process process = new Process {
            StartInfo = startInfo
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) => {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) {
                outputBuilder.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) => {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) {
                errorBuilder.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start()) {
            throw new InvalidOperationException("Failed to start 'dotnet restore'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try {
            await process.WaitForExitAsync(cancellationToken);
        } catch (OperationCanceledException) {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            } catch {
                // Best-effort kill on cancellation.
            }

            restoreStopwatch.Stop();
            _logger.LogWarning("Restore cancelled after {ElapsedMs} ms for {SolutionPath}", restoreStopwatch.ElapsedMilliseconds, solutionPath);
            throw;
        }

        if (process.ExitCode == 0) {
            restoreStopwatch.Stop();
            _logger.LogInformation("Restore completed successfully in {ElapsedMs} ms.", restoreStopwatch.ElapsedMilliseconds);
            return;
        }

        string errorOutput = errorBuilder.ToString().Trim();
        string standardOutput = outputBuilder.ToString().Trim();
        string details = string.IsNullOrWhiteSpace(errorOutput) ? standardOutput : errorOutput;
        if (string.IsNullOrWhiteSpace(details)) {
            details = "No output captured.";
        }

        restoreStopwatch.Stop();
        _logger.LogWarning(
            "Restore failed for {SolutionPath} with exit code {ExitCode} after {ElapsedMs} ms. Details: {Details}",
            solutionPath,
            process.ExitCode,
            restoreStopwatch.ElapsedMilliseconds,
            TrimForLog(details, 2000));

        throw new InvalidOperationException($"dotnet restore failed with exit code {process.ExitCode}. {details}");
    }
    private static bool ProjectUsesMaui(string projectFilePath) {
        try {
            if (!File.Exists(projectFilePath)) {
                return false;
            }

            var xDoc = XDocument.Load(projectFilePath);
            var ns = xDoc.Root?.Name.Namespace ?? XNamespace.None;
            var useMauiValue = xDoc.Descendants(ns + "UseMaui").FirstOrDefault()?.Value;
            return string.Equals(useMauiValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }
    private static bool CompilationHasMauiGlobalUsings(Compilation compilation) {
        foreach (var syntaxTree in compilation.SyntaxTrees) {
            var root = syntaxTree.GetRoot();
            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>()) {
                var usingText = usingDirective.ToFullString().TrimStart();
                if (!usingText.StartsWith("global using ", StringComparison.Ordinal)) {
                    continue;
                }
                var usingName = usingDirective.Name?.ToString();
                if (!string.IsNullOrWhiteSpace(usingName)) {
                    var normalizedName = usingName.StartsWith("global::", StringComparison.Ordinal)
                        ? usingName.Substring("global::".Length)
                        : usingName;
                    if (normalizedName.StartsWith("Microsoft.Maui", StringComparison.Ordinal)) {
                        return true;
                    }
                }
            }
        }
        return false;
    }
    private static string? FindMauiGlobalUsingsFile(string objBaseDir) {
        if (!Directory.Exists(objBaseDir)) {
            return null;
        }

        var candidates = Directory.GetFiles(objBaseDir, "*GlobalUsings.g.cs", SearchOption.AllDirectories);
        foreach (var candidate in candidates) {
            try {
                var text = File.ReadAllText(candidate);
                if (text.Contains("Microsoft.Maui", StringComparison.Ordinal)) {
                    return candidate;
                }
            } catch {
                // ignore candidate
            }
        }

        return null;
    }
    private static IEnumerable<string> FindMauiXamlGeneratedFiles(string rootDirectory) {
        if (!Directory.Exists(rootDirectory)) {
            return Enumerable.Empty<string>();
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new[] { "*.g.cs", "*.sg.cs" };

        foreach (var pattern in patterns) {
            string[] candidates;
            try {
                candidates = Directory.GetFiles(rootDirectory, pattern, SearchOption.AllDirectories);
            } catch {
                continue;
            }

            foreach (var candidate in candidates) {
                if (candidate.EndsWith(".xaml.g.cs", StringComparison.OrdinalIgnoreCase)
                    || candidate.EndsWith(".xaml.sg.cs", StringComparison.OrdinalIgnoreCase)) {
                    matched.Add(candidate);
                    continue;
                }

                if (LooksLikeXamlGeneratedFile(candidate)) {
                    matched.Add(candidate);
                }
            }
        }

        return matched;
    }
    private static bool LooksLikeXamlGeneratedFile(string filePath) {
        try {
            var fileName = Path.GetFileName(filePath);
            if (!fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".sg.cs", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            var text = File.ReadAllText(filePath);
            return text.Contains("InitializeComponent", StringComparison.Ordinal)
                && (text.Contains("XamlFilePath", StringComparison.Ordinal) || text.Contains("XamlCompilation", StringComparison.Ordinal));
        } catch {
            return false;
        }
    }
    private List<string> GetMauiGlobalUsingsSearchPaths(string projectDir, string configuration, string targetFramework) {
        var paths = new List<string>();
        var baseObjPath = Path.Combine(projectDir, "obj", configuration, targetFramework);
        paths.Add(baseObjPath);

        if (targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase)) {
            string? runtimeIdentifier = _runtimeIdentifier;
            if (string.IsNullOrWhiteSpace(runtimeIdentifier)) {
                runtimeIdentifier = GetDefaultRuntimeIdentifier();
                if (!string.IsNullOrWhiteSpace(runtimeIdentifier)) {
                    _logger.LogInformation("Runtime identifier not provided; using default: {RuntimeIdentifier}", runtimeIdentifier);
                }
            }

            if (!string.IsNullOrWhiteSpace(runtimeIdentifier)) {
                paths.Insert(0, Path.Combine(projectDir, "obj", configuration, targetFramework, runtimeIdentifier));
            }
        }

        return paths;
    }
    private static string? GetDefaultRuntimeIdentifier() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return null;
        }

        return RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "win10-arm64",
            Architecture.X64 => "win10-x64",
            Architecture.X86 => "win10-x86",
            _ => null
        };
    }
    private static List<string> ExtractMauiGlobalUsings(string fileContent) {
        var results = new List<string>();
        foreach (var line in fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("global using ", StringComparison.Ordinal)) {
                continue;
            }
            if (!trimmed.Contains("Microsoft.Maui", StringComparison.Ordinal)) {
                continue;
            }
            results.Add(trimmed);
        }
        return results;
    }
    private void PopulateReflectionCache(IEnumerable<string> assemblyPathsToInspect, CancellationToken cancellationToken = default) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot populate reflection cache: MetadataLoadContext not initialized.");
            return;
        }
        // _allLoadedReflectionTypesCache is cleared in UnloadSolution
        _logger.LogInformation("Starting population of reflection type cache...");
        int typesCachedCount = 0;

        // Convert to list to avoid multiple enumeration and enable progress tracking
        var pathsList = assemblyPathsToInspect.ToList();
        int totalPaths = pathsList.Count;
        int processedPaths = 0;
        const int progressCheckInterval = 10; // Report progress and check cancellation every 10 assemblies

        foreach (var assemblyPath in pathsList) {
            // Check cancellation and log progress periodically
            if (++processedPaths % progressCheckInterval == 0) {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogTrace("Reflection cache population progress: {Progress}% ({Current}/{Total})",
                (int)((float)processedPaths / totalPaths * 100), processedPaths, totalPaths);
            }

            LoadTypesFromAssembly(assemblyPath, ref typesCachedCount, cancellationToken);
        }

        _logger.LogInformation("Reflection type cache population complete. Cached {Count} types from {AssemblyCount} unique assembly paths processed.", typesCachedCount, pathsList.Count);
    }
    private void LoadTypesFromAssembly(string assemblyPath, ref int typesCachedCount, CancellationToken cancellationToken = default) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null || string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
                _logger.LogTrace("Assembly path is invalid or file does not exist, skipping for reflection cache: {Path}", assemblyPath);
            }
            return;
        }
        try {
            var assembly = _metadataLoadContext.LoadFromAssemblyPath(assemblyPath);

            // For large assemblies, check cancellation periodically during type collection
            // We can't check during GetTypes() directly since it's atomic, but we can after
            cancellationToken.ThrowIfCancellationRequested();

            var types = assembly.GetTypes();
            int processedTypes = 0;
            const int typeCheckInterval = 50; // Check cancellation every 50 types

            foreach (var type in types) {
                // Check cancellation periodically when processing many types
                if (++processedTypes % typeCheckInterval == 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (type?.FullName != null && !_allLoadedReflectionTypesCache.ContainsKey(type.FullName)) {
                    _allLoadedReflectionTypesCache.TryAdd(type.FullName, type);
                    typesCachedCount++;
                }
            }
        } catch (ReflectionTypeLoadException rtlex) {
            _logger.LogWarning("Could not load all types from assembly {Path} for reflection cache. LoaderExceptions: {Count}", assemblyPath, rtlex.LoaderExceptions.Length);
            foreach (var loaderEx in rtlex.LoaderExceptions.Where(e => e != null)) {
                _logger.LogTrace("LoaderException: {Message}", loaderEx!.Message);
            }

            // For partial load errors, still process the types that did load
            int processedTypes = 0;
            const int typeCheckInterval = 20; // Check cancellation more frequently when dealing with problematic assemblies

            foreach (var type in rtlex.Types.Where(t => t != null)) {
                // Check cancellation periodically
                if (++processedTypes % typeCheckInterval == 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (type!.FullName != null && !_allLoadedReflectionTypesCache.ContainsKey(type.FullName)) {
                    _allLoadedReflectionTypesCache.TryAdd(type.FullName, type);
                    typesCachedCount++;
                }
            }
        } catch (FileNotFoundException) { // Should be rare due to File.Exists check, but MLC might have its own resolution logic
            _logger.LogTrace("Assembly file not found by MetadataLoadContext: {Path}", assemblyPath);
        } catch (BadImageFormatException) {
            _logger.LogTrace("Bad image format for assembly file: {Path}", assemblyPath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Error loading types from assembly {Path} for reflection cache.", assemblyPath);
        }
    }
    public void UnloadSolution() {
        _logger.LogInformation("Unloading current solution and workspace.");
        _compilationCache.Clear();
        _semanticModelCache.Clear();
        _allLoadedReflectionTypesCache.Clear();
        if (_workspace != null) {
            _workspace.WorkspaceFailed -= OnWorkspaceFailed;
            _workspace.CloseSolution();
            _workspace.Dispose();
            _workspace = null;
        }
        _metadataLoadContext?.Dispose();
        _metadataLoadContext = null;
        _pathAssemblyResolver = null; // PathAssemblyResolver doesn't implement IDisposable
        _assemblyPathsForReflection.Clear();
    }
    public void RefreshCurrentSolution() {
        if (_workspace == null) {
            _logger.LogWarning("Cannot refresh solution: Workspace is null.");
            return;
        }
        if (_workspace.CurrentSolution == null) {
            _logger.LogWarning("Cannot refresh solution: No solution loaded.");
            return;
        }
        _currentSolution = _workspace.CurrentSolution;
        _compilationCache.Clear();
        _semanticModelCache.Clear();
        _logger.LogDebug("Current solution state has been refreshed from workspace.");
    }
    public async Task ReloadSolutionFromDiskAsync(CancellationToken cancellationToken) {
        if (_workspace == null) {
            _logger.LogWarning("Cannot reload solution: Workspace is null.");
            return;
        }
        if (_workspace.CurrentSolution == null) {
            _logger.LogWarning("Cannot reload solution: No solution loaded.");
            return;
        }
        await LoadSolutionAsync(_workspace.CurrentSolution.FilePath!, _targetFramework, _runtimeIdentifier, cancellationToken);
        _logger.LogDebug("Current solution state has been refreshed from workspace.");
    }
    private void OnWorkspaceFailed(object? sender, WorkspaceDiagnosticEventArgs e) {
        var diagnostic = e.Diagnostic;
        var level = diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(level, "Workspace diagnostic ({Kind}): {Message}", diagnostic.Kind, diagnostic.Message);
    }
    public async Task<INamedTypeSymbol?> FindRoslynNamedTypeSymbolAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot find Roslyn symbol: No solution loaded.");
            return null;
        }
        // Check cancellation before starting lookup
        cancellationToken.ThrowIfCancellationRequested();
        // Use fuzzy FQN lookup service
        var matches = await _fuzzyFqnLookupService.FindMatchesAsync(fullyQualifiedTypeName, this, cancellationToken);
        var matchList = matches.Where(m => m.Symbol is INamedTypeSymbol).ToList();
        // Check cancellation after initial matching
        cancellationToken.ThrowIfCancellationRequested();
        if (matchList.Count == 1) {
            var match = matchList.First();
            _logger.LogDebug("Roslyn named type symbol found: {FullyQualifiedTypeName} (score: {Score}, reason: {Reason})",
            match.CanonicalFqn, match.Score, match.MatchReason);
            return (INamedTypeSymbol)match.Symbol;
        }
        if (matchList.Count > 1) {
            _logger.LogWarning("Multiple matches found for {FullyQualifiedTypeName}", fullyQualifiedTypeName);
            throw new McpException($"FQN was ambiguous, did you mean one of these?\n{string.Join("\n", matchList.Select(m => m.CanonicalFqn))}");
        }
        // Direct lookup as fallback
        foreach (var project in CurrentSolution.Projects) {
            // Check cancellation before each project
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await GetCompilationAsync(project.Id, cancellationToken);
            if (compilation == null) {
                continue;
            }
            var symbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
            if (symbol != null) {
                _logger.LogDebug("Roslyn named type symbol found via direct lookup: {FullyQualifiedTypeName} in project {ProjectName}",
                fullyQualifiedTypeName, project.Name);
                return symbol;
            }
        }
        // Check cancellation before nested type check
        cancellationToken.ThrowIfCancellationRequested();
        // Check for nested type with dot notation as last resort
        var lastDotIndex = fullyQualifiedTypeName.LastIndexOf('.');
        if (lastDotIndex > 0) {
            var parentTypeName = fullyQualifiedTypeName.Substring(0, lastDotIndex);
            var nestedTypeName = fullyQualifiedTypeName.Substring(lastDotIndex + 1);
            foreach (var project in CurrentSolution.Projects) {
                // Check cancellation before each project
                cancellationToken.ThrowIfCancellationRequested();
                var compilation = await GetCompilationAsync(project.Id, cancellationToken);
                if (compilation == null) {
                    continue;
                }
                var parentSymbol = compilation.GetTypeByMetadataName(parentTypeName);
                if (parentSymbol != null) {
                    // Check if there's a nested type with this name
                    var nestedType = parentSymbol.GetTypeMembers(nestedTypeName).FirstOrDefault();
                    if (nestedType != null) {
                        var correctName = $"{parentTypeName}+{nestedTypeName}";
                        _logger.LogWarning("Type not found: '{FullyQualifiedTypeName}'. This appears to be a nested type - use '{CorrectName}' instead (use + instead of . for nested types)",
                        fullyQualifiedTypeName, correctName);
                        throw new McpException(
                        $"Type not found: '{fullyQualifiedTypeName}'. This appears to be a nested type - use '{correctName}' instead (use + instead of . for nested types)");
                    }
                }
            }
        }
        _logger.LogDebug("Roslyn named type symbol not found: {FullyQualifiedTypeName}", fullyQualifiedTypeName);
        return null;
    }
    public async Task<ISymbol?> FindRoslynSymbolAsync(string fullyQualifiedName, CancellationToken cancellationToken) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot find Roslyn symbol: No solution loaded.");
            return null;
        }

        // Check cancellation before starting lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Use fuzzy FQN lookup service
        var matches = await _fuzzyFqnLookupService.FindMatchesAsync(fullyQualifiedName, this, cancellationToken);
        var matchList = matches.ToList();

        // Check cancellation after initial matching
        cancellationToken.ThrowIfCancellationRequested();

        if (matchList.Count == 1) {
            var match = matchList.First();
            _logger.LogDebug("Roslyn symbol found: {FullyQualifiedName} (score: {Score}, reason: {Reason})",
            match.CanonicalFqn, match.Score, match.MatchReason);
            return match.Symbol;
        }

        if (matchList.Count > 1) {
            _logger.LogWarning("Multiple matches found for {FullyQualifiedName}", fullyQualifiedName);
            throw new McpException($"FQN was ambiguous, did you mean one of these?\n{string.Join("\n", matchList.Select(m => m.CanonicalFqn))}");
        }

        // Check cancellation before fallback lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Fall back to type lookup
        var typeSymbol = await FindRoslynNamedTypeSymbolAsync(fullyQualifiedName, cancellationToken);
        if (typeSymbol != null) {
            return typeSymbol;
        }

        // Check cancellation before member lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Check for member of a type as fallback
        var lastDotIndex = fullyQualifiedName.LastIndexOf('.');
        if (lastDotIndex > 0 && lastDotIndex < fullyQualifiedName.Length - 1) {
            var typeName = fullyQualifiedName.Substring(0, lastDotIndex);
            var memberName = fullyQualifiedName.Substring(lastDotIndex + 1);

            var parentTypeSymbol = await FindRoslynNamedTypeSymbolAsync(typeName, cancellationToken);
            if (parentTypeSymbol != null) {
                // Check cancellation before final lookup step
                cancellationToken.ThrowIfCancellationRequested();

                var members = parentTypeSymbol.GetMembers(memberName);
                if (members.Any()) {
                    // TODO: Handle overloads if necessary, for now, take the first.
                    var memberSymbol = members.First();
                    _logger.LogDebug("Roslyn member symbol found: {FullyQualifiedName}", fullyQualifiedName);
                    return memberSymbol;
                }
            }
        }

        _logger.LogDebug("Roslyn symbol not found: {FullyQualifiedName}", fullyQualifiedName);
        return null;
    }
    public Task<Type?> FindReflectionTypeAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) {
        // Check cancellation at the beginning of the method
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot find reflection type: MetadataLoadContext not initialized.");
            return Task.FromResult<Type?>(null);
        }
        if (_allLoadedReflectionTypesCache.TryGetValue(fullyQualifiedTypeName, out var type)) {
            _logger.LogDebug("Reflection type found in cache: {Name}", fullyQualifiedTypeName);
            return Task.FromResult<Type?>(type);
        }
        _logger.LogDebug("Reflection type '{FullyQualifiedTypeName}' not found in cache. It might not exist in the loaded solution's dependencies or was not loadable.", fullyQualifiedTypeName);
        return Task.FromResult<Type?>(null);
    }
    public Task<IEnumerable<Type>> SearchReflectionTypesAsync(string regexPattern, CancellationToken cancellationToken) {
        // Check cancellation at the method entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot search reflection types: MetadataLoadContext not initialized.");
            return Task.FromResult(Enumerable.Empty<Type>());
        }
        if (!_allLoadedReflectionTypesCache.Any()) {
            _logger.LogInformation("Reflection type cache is empty. Search will yield no results.");
            return Task.FromResult(Enumerable.Empty<Type>());
        }

        // Check cancellation before regex compilation
        cancellationToken.ThrowIfCancellationRequested();

        var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var matchedTypes = new List<Type>();

        // Consider batching in chunks to check cancellation more frequently on large type caches
        int processedCount = 0;
        const int batchSize = 100; // Check cancellation every 100 types

        foreach (var typeEntry in _allLoadedReflectionTypesCache) { // Iterate KeyValuePair to access FQN directly
            if (++processedCount % batchSize == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Key is type.FullName which should not be null for cached types
            if (regex.IsMatch(typeEntry.Key)) { // Search FQN
                matchedTypes.Add(typeEntry.Value);
            } else if (regex.IsMatch(typeEntry.Value.Name)) { // Search simple name
                matchedTypes.Add(typeEntry.Value);
            }
        }

        // Check cancellation before returning results
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Found {Count} reflection types matching pattern '{Pattern}'.", matchedTypes.Count, regexPattern);
        return Task.FromResult<IEnumerable<Type>>(matchedTypes.Distinct());
    }
    public IEnumerable<Project> GetProjects() {
        return CurrentSolution?.Projects ?? Enumerable.Empty<Project>();
    }
    public Project? GetProjectByName(string projectName) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get project by name: No solution loaded.");
            return null;
        }

        var solution = CurrentSolution;
        if (solution == null)
            return null;

        // 1. Match sur Project.Name
        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

        // 2. Match sur nom du csproj
        if (project == null) {
            project = solution.Projects.FirstOrDefault(p =>
                p.FilePath != null &&
                string.Equals(
                    Path.GetFileNameWithoutExtension(p.FilePath),
                    projectName,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (project == null) {
            _logger.LogWarning(
                "Project not found: {ProjectName}. Available projects: {Projects}",
                projectName,
                string.Join(", ", solution.Projects.Select(p => p.Name))
            );
        }

        return project;
    }

    public async Task<SemanticModel?> GetSemanticModelAsync(DocumentId documentId, CancellationToken cancellationToken) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get semantic model: No solution loaded.");
            return null;
        }

        // Fast path: check cache first
        if (_semanticModelCache.TryGetValue(documentId, out var cachedModel)) {
            _logger.LogTrace("Returning cached semantic model for document ID: {DocumentId}", documentId);
            return cachedModel;
        }

        // Check cancellation before document lookup
        cancellationToken.ThrowIfCancellationRequested();

        var document = CurrentSolution.GetDocument(documentId);
        if (document == null) {
            _logger.LogWarning("Document not found for ID: {DocumentId}", documentId);
            return null;
        }

        _logger.LogTrace("Requesting semantic model for document: {DocumentFilePath}", document.FilePath);

        // Check cancellation before expensive GetSemanticModelAsync call
        cancellationToken.ThrowIfCancellationRequested();

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model != null) {
            _semanticModelCache.TryAdd(documentId, model);
        } else {
            _logger.LogWarning("Failed to get semantic model for document: {DocumentFilePath}", document.FilePath);
        }
        return model;
    }
    public async Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get compilation: No solution loaded.");
            return null;
        }

        // Fast path: check cache first
        if (_compilationCache.TryGetValue(projectId, out var cachedCompilation)) {
            _logger.LogTrace("Returning cached compilation for project ID: {ProjectId}", projectId);
            return cachedCompilation;
        }

        // Check cancellation before project lookup
        cancellationToken.ThrowIfCancellationRequested();

        var project = CurrentSolution.GetProject(projectId);
        if (project == null) {
            _logger.LogWarning("Project not found for ID: {ProjectId}", projectId);
            return null;
        }

        _logger.LogTrace("Requesting compilation for project: {ProjectName}", project.Name);

        // Check cancellation before expensive GetCompilationAsync call
        cancellationToken.ThrowIfCancellationRequested();

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation != null) {
            _compilationCache.TryAdd(projectId, compilation);
        } else {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
        }
        return compilation;
    }
    public void Dispose() {
        UnloadSolution();
        _loadSolutionSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
    private class ProgressReporter : IProgress<ProjectLoadProgress> {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        public ProgressReporter(Microsoft.Extensions.Logging.ILogger logger) {
            _logger = logger;
        }
        public void Report(ProjectLoadProgress loadProgress) {
            var projectDisplay = Path.GetFileName(loadProgress.FilePath);
            _logger.LogTrace("Project Load Progress: {ProjectDisplayName}, Operation: {Operation}, Time: {TimeElapsed}",
            projectDisplay, loadProgress.Operation, loadProgress.ElapsedTime);
        }
    }
    private static string? NormalizeTargetFramework(string? targetFramework) {
        if (string.IsNullOrWhiteSpace(targetFramework)) {
            return null;
        }

        var normalized = targetFramework.Trim();
        if (normalized.Contains(';')) {
            normalized = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
    private static string? NormalizeRuntimeIdentifier(string? runtimeIdentifier) {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier)) {
            return null;
        }

        return runtimeIdentifier.Trim();
    }
    private HashSet<string> GetNuGetAssemblyPaths(Solution solution, CancellationToken cancellationToken = default) {
        var nugetAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nugetCacheDir = GetNuGetGlobalPackagesFolder();

        if (string.IsNullOrEmpty(nugetCacheDir) || !Directory.Exists(nugetCacheDir)) {
            _logger.LogWarning("NuGet global packages folder not found or inaccessible: {NuGetCacheDir}", nugetCacheDir);
            return nugetAssemblyPaths;
        }

        foreach (var project in solution.Projects) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(project.FilePath)) {
                continue;
            }

            var packageReferences = LegacyNuGetPackageReader.GetAllPackageReferences(project.FilePath);
            var projectTargetFramework = _targetFramework ?? SolutionTools.ExtractTargetFrameworkFromProjectFile(project.FilePath);

            foreach (var package in packageReferences) {
                cancellationToken.ThrowIfCancellationRequested();

                var packageDir = Path.Combine(nugetCacheDir, package.PackageId.ToLowerInvariant(), package.Version);
                if (!Directory.Exists(packageDir)) {
                    _logger.LogTrace("Package directory not found: {PackageDir}", packageDir);
                    continue;
                }

                var libDir = Path.Combine(packageDir, "lib");
                if (!Directory.Exists(libDir)) {
                    _logger.LogTrace("No lib directory found for package {PackageId} {Version}", package.PackageId, package.Version);
                    continue;
                }

                // Find assemblies using the project's target framework
                var assemblyPaths = GetAssembliesForTargetFramework(libDir, package.TargetFramework ?? projectTargetFramework, package.PackageId, package.Version);
                foreach (var assemblyPath in assemblyPaths) {
                    nugetAssemblyPaths.Add(assemblyPath);
                }
            }
        }

        _logger.LogInformation("Found {AssemblyCount} NuGet assemblies from global packages cache", nugetAssemblyPaths.Count);
        return nugetAssemblyPaths;
    }
    private static string GetNuGetGlobalPackagesFolder() {
        // Check environment variable first
        var globalPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(globalPackagesPath)) {
            return globalPackagesPath;
        }

        // Default location based on OS
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nuget", "packages");
    }
    private List<string> GetAssembliesForTargetFramework(string libDir, string targetFramework, string packageId, string version) {
        var assemblies = new List<string>();

        if (!Directory.Exists(libDir)) {
            return assemblies;
        }

        // First try exact target framework match
        var exactFrameworkDir = Path.Combine(libDir, targetFramework);
        if (Directory.Exists(exactFrameworkDir)) {
            var exactAssemblies = Directory.GetFiles(exactFrameworkDir, "*.dll", SearchOption.TopDirectoryOnly);
            assemblies.AddRange(exactAssemblies);
            _logger.LogTrace("Found {AssemblyCount} assemblies in exact framework match {Framework} for {PackageId} {Version}",
                exactAssemblies.Length, targetFramework, packageId, version);
            return assemblies;
        }

        // Try compatible frameworks in order of preference
        var compatibleFrameworks = GetCompatibleFrameworks(targetFramework);

        foreach (var framework in compatibleFrameworks) {
            var frameworkDir = Path.Combine(libDir, framework);
            if (Directory.Exists(frameworkDir)) {
                var frameworkAssemblies = Directory.GetFiles(frameworkDir, "*.dll", SearchOption.TopDirectoryOnly);
                assemblies.AddRange(frameworkAssemblies);
                _logger.LogTrace("Found {AssemblyCount} assemblies in compatible framework {Framework} for {PackageId} {Version}",
                    frameworkAssemblies.Length, framework, packageId, version);
                return assemblies; // Take the first compatible framework found
            }
        }

        // Fallback: check if there are any DLLs directly in lib directory
        if (assemblies.Count == 0) {
            var libAssemblies = Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly);
            assemblies.AddRange(libAssemblies);
            if (libAssemblies.Length > 0) {
                _logger.LogTrace("Found {AssemblyCount} assemblies in lib root for {PackageId} {Version}",
                    libAssemblies.Length, packageId, version);
            }
        }

        return assemblies;
    }
    private static string[] GetCompatibleFrameworks(string targetFramework) {
        // Return frameworks in order of compatibility preference.
        var normalized = targetFramework.ToLowerInvariant();
        var baseFramework = normalized.Contains('-')
            ? normalized.Split('-')[0]
            : normalized;

        var compatible = baseFramework switch {
            "net10.0" => new[] { "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net9.0" => new[] { "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net8.0" => new[] { "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net7.0" => new[] { "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net6.0" => new[] { "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net5.0" => new[] { "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp3.1" => new[] { "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp3.0" => new[] { "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp2.1" => new[] { "netcoreapp2.1", "netstandard2.0", "netstandard1.6" },
            "netstandard2.1" => new[] { "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netstandard2.0" => new[] { "netstandard2.0", "netstandard1.6" },
            _ => new[] { "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" }
        };

        if (string.Equals(normalized, baseFramework, StringComparison.OrdinalIgnoreCase)) {
            return compatible;
        }

        var ordered = new List<string> { normalized };
        foreach (var tfm in compatible) {
            if (!ordered.Contains(tfm, StringComparer.OrdinalIgnoreCase)) {
                ordered.Add(tfm);
            }
        }

        return ordered.ToArray();
    }
}
