using System.Diagnostics;

namespace SharpTools.Tools.Utilities;

public sealed class MsBuildSdkInfo {
    public MsBuildSdkInfo(string sdkVersion, string sdkBasePath, bool usedGlobalJson, string? globalJsonPath, string? requestedVersion) {
        SdkVersion = sdkVersion;
        SdkBasePath = sdkBasePath;
        UsedGlobalJson = usedGlobalJson;
        GlobalJsonPath = globalJsonPath;
        RequestedVersion = requestedVersion;
    }

    public string SdkVersion { get; }
    public string SdkBasePath { get; }
    public bool UsedGlobalJson { get; }
    public string? GlobalJsonPath { get; }
    public string? RequestedVersion { get; }
    public string SdkPath => Path.Combine(SdkBasePath, SdkVersion);
}

public static class MsBuildSdkResolver {
    public static MsBuildSdkInfo ResolveSdkInfo(string? solutionPath) {
        string solutionRoot = ResolveSolutionRoot(solutionPath);
        string? globalJsonPath = GetGlobalJsonPath(solutionRoot);
        string? requestedVersion = ReadGlobalJsonSdkVersion(globalJsonPath);

        List<SdkEntry> installedSdks = GetInstalledSdks();
        if (installedSdks.Count == 0) {
            throw new InvalidOperationException("No .NET SDKs were found. Install the .NET SDK to continue.");
        }

        if (!string.IsNullOrWhiteSpace(requestedVersion)) {
            SdkEntry? matchingSdk = installedSdks.FirstOrDefault(sdk => StringComparer.OrdinalIgnoreCase.Equals(sdk.Version, requestedVersion));
            if (matchingSdk != null) {
                return new MsBuildSdkInfo(matchingSdk.Version, matchingSdk.BasePath, true, globalJsonPath, requestedVersion);
            }
        }

        SdkEntry latestSdk = GetLatestSdk(installedSdks);
        return new MsBuildSdkInfo(latestSdk.Version, latestSdk.BasePath, false, globalJsonPath, requestedVersion);
    }

    private static string ResolveSolutionRoot(string? solutionPath) {
        if (!string.IsNullOrWhiteSpace(solutionPath)) {
            string? solutionDirectory = Path.GetDirectoryName(solutionPath);
            if (!string.IsNullOrWhiteSpace(solutionDirectory)) {
                return solutionDirectory;
            }
        }

        string currentDirectory = Directory.GetCurrentDirectory();
        DirectoryInfo? directoryInfo = new DirectoryInfo(currentDirectory);
        while (directoryInfo != null) {
            string solutionFilePath = Path.Combine(directoryInfo.FullName, "SharpTools.sln");
            if (File.Exists(solutionFilePath)) {
                return directoryInfo.FullName;
            }
            directoryInfo = directoryInfo.Parent;
        }

        return currentDirectory;
    }

    private static string? GetGlobalJsonPath(string solutionRoot) {
        string globalJsonPath = Path.Combine(solutionRoot, "global.json");
        if (File.Exists(globalJsonPath)) {
            return globalJsonPath;
        }

        return null;
    }

    private static string? ReadGlobalJsonSdkVersion(string? globalJsonPath) {
        if (string.IsNullOrWhiteSpace(globalJsonPath) || !File.Exists(globalJsonPath)) {
            return null;
        }

        try {
            string json = File.ReadAllText(globalJsonPath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sdk", out JsonElement sdkElement)) {
                return null;
            }
            if (!sdkElement.TryGetProperty("version", out JsonElement versionElement)) {
                return null;
            }
            if (versionElement.ValueKind != JsonValueKind.String) {
                return null;
            }

            return versionElement.GetString();
        } catch (JsonException) {
            return null;
        } catch (IOException) {
            return null;
        }
    }

    private static List<SdkEntry> GetInstalledSdks() {
        ProcessStartInfo startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            Arguments = "--list-sdks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new Process {
            StartInfo = startInfo
        };

        if (!process.Start()) {
            throw new InvalidOperationException("Failed to start dotnet to list installed SDKs.");
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"dotnet --list-sdks failed: {error}");
        }

        List<SdkEntry> entries = new List<SdkEntry>();
        using StringReader reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) {
                continue;
            }

            int bracketIndex = trimmed.IndexOf('[', StringComparison.Ordinal);
            if (bracketIndex <= 0) {
                continue;
            }

            string version = trimmed.Substring(0, bracketIndex).Trim();
            string pathPart = trimmed.Substring(bracketIndex).Trim();
            if (pathPart.StartsWith("[", StringComparison.Ordinal) && pathPart.EndsWith("]", StringComparison.Ordinal)) {
                pathPart = pathPart.Substring(1, pathPart.Length - 2);
            }

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(pathPart)) {
                continue;
            }

            entries.Add(new SdkEntry(version, pathPart));
        }

        return entries;
    }

    private static SdkEntry GetLatestSdk(List<SdkEntry> entries) {
        SdkEntry? latest = null;
        foreach (SdkEntry entry in entries) {
            if (latest == null) {
                latest = entry;
                continue;
            }

            if (CompareSdkVersions(entry.Version, latest.Version) > 0) {
                latest = entry;
            }
        }

        return latest ?? throw new InvalidOperationException("No .NET SDK entries were available.");
    }

    private static int CompareSdkVersions(string left, string right) {
        SdkVersionSortKey leftKey = SdkVersionSortKey.Parse(left);
        SdkVersionSortKey rightKey = SdkVersionSortKey.Parse(right);

        int coreCompare = leftKey.CoreVersion.CompareTo(rightKey.CoreVersion);
        if (coreCompare != 0) {
            return coreCompare;
        }

        if (leftKey.IsPrerelease != rightKey.IsPrerelease) {
            return leftKey.IsPrerelease ? -1 : 1;
        }

        return string.CompareOrdinal(leftKey.Original, rightKey.Original);
    }

    private sealed class SdkEntry {
        public SdkEntry(string version, string basePath) {
            Version = version;
            BasePath = basePath;
        }

        public string Version { get; }
        public string BasePath { get; }
    }

    private sealed class SdkVersionSortKey {
        private SdkVersionSortKey(Version coreVersion, bool isPrerelease, string original) {
            CoreVersion = coreVersion;
            IsPrerelease = isPrerelease;
            Original = original;
        }

        public Version CoreVersion { get; }
        public bool IsPrerelease { get; }
        public string Original { get; }

        public static SdkVersionSortKey Parse(string version) {
            string normalized = version;
            int dashIndex = normalized.IndexOf('-', StringComparison.Ordinal);
            int plusIndex = normalized.IndexOf('+', StringComparison.Ordinal);
            int cutIndex = -1;

            if (dashIndex >= 0 && plusIndex >= 0) {
                cutIndex = Math.Min(dashIndex, plusIndex);
            } else if (dashIndex >= 0) {
                cutIndex = dashIndex;
            } else if (plusIndex >= 0) {
                cutIndex = plusIndex;
            }

            string coreText = cutIndex >= 0 ? normalized.Substring(0, cutIndex) : normalized;
            if (!Version.TryParse(coreText, out Version? coreVersion)) {
                coreVersion = new Version(0, 0, 0, 0);
            }

            bool isPrerelease = cutIndex >= 0;
            return new SdkVersionSortKey(coreVersion, isPrerelease, version);
        }
    }
}
