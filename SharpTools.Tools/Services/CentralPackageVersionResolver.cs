using System.Xml.Linq;

namespace SharpTools.Tools.Services {
    public static class CentralPackageVersionResolver {
        public static List<PackageReference> GetPackageReferencesWithCentralVersions(string projectPath) {
            var refs = GetBasicPackageReferencesWithoutMSBuild(projectPath);
            if (refs.Count == 0) return refs;

            // 1) Central versions (Directory.Packages.props)
            var propsPath = FindNearestDirectoryPackagesProps(projectPath);
            var centralVersions = propsPath is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : LoadCentralVersions(propsPath);

            // 2) VersionOverride in csproj (has priority over central)
            var versionOverrides = LoadVersionOverridesFromProject(projectPath);

            foreach (var pr in refs) {
                if (!string.IsNullOrWhiteSpace(pr.Version))
                    continue;

                if (versionOverrides.TryGetValue(pr.PackageId, out var ov) && !string.IsNullOrWhiteSpace(ov)) {
                    pr.Version = ov;
                    continue;
                }

                if (centralVersions.TryGetValue(pr.PackageId, out var v) && !string.IsNullOrWhiteSpace(v)) {
                    pr.Version = v;
                }
            }

            return refs;
        }

        // Find nearest Directory.Packages.props, walking up directories
        public static string? FindNearestDirectoryPackagesProps(string projectPath) {
            var dir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(dir)) return null;

            while (!string.IsNullOrEmpty(dir)) {
                var candidate = Path.Combine(dir, "Directory.Packages.props");
                if (File.Exists(candidate)) return candidate;

                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        public static Dictionary<string, string> LoadCentralVersions(string propsPath) {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try {
                var xDoc = XDocument.Load(propsPath);
                var ns = xDoc.Root?.Name.Namespace ?? XNamespace.None;

                // <PackageVersion Include="X" Version="Y" />
                // sometimes: <PackageVersion Update="X" Version="Y" />
                var packageVersions = xDoc.Descendants(ns + "PackageVersion");

                foreach (var pv in packageVersions) {
                    var id = pv.Attribute("Include")?.Value ?? pv.Attribute("Update")?.Value;
                    var version = pv.Attribute("Version")?.Value;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                        dict[id] = version;
                }
            } catch {
                // ignore and return what we have
            }

            return dict;
        }

        public static Dictionary<string, string> LoadVersionOverridesFromProject(string projectPath) {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try {
                var xDoc = XDocument.Load(projectPath);
                var ns = xDoc.Root?.Name.Namespace ?? XNamespace.None;

                // Handles:
                // <PackageReference Include="X"><VersionOverride>1.2.3</VersionOverride></PackageReference>
                // Also handles <PackageReference Include="X" Version="1.2.3" /> already covered elsewhere,
                // but doesn't hurt to capture.
                var packageRefs = xDoc.Descendants(ns + "PackageReference");

                foreach (var pr in packageRefs) {
                    var id = pr.Attribute("Include")?.Value ?? pr.Attribute("Update")?.Value;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var overrideValue = pr.Element(ns + "VersionOverride")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(overrideValue)) {
                        dict[id] = overrideValue;
                        continue;
                    }

                    var versionAttr = pr.Attribute("Version")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(versionAttr))
                        dict[id] = versionAttr;

                    var versionElem = pr.Element(ns + "Version")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(versionElem))
                        dict[id] = versionElem;
                }
            } catch {
                // ignore
            }

            return dict;
        }

        // Your existing function (kept as-is but consider adding ns handling similarly)
        public static List<PackageReference> GetBasicPackageReferencesWithoutMSBuild(string projectPath) {
            var packages = new List<PackageReference>();

            try {
                if (!File.Exists(projectPath))
                    return packages;

                var xDoc = XDocument.Load(projectPath);
                var ns = xDoc.Root?.Name.Namespace ?? XNamespace.None;

                var packageRefs = xDoc.Descendants(ns + "PackageReference");

                foreach (var packageRef in packageRefs) {
                    string? packageId = packageRef.Attribute("Include")?.Value ?? packageRef.Attribute("Update")?.Value;
                    if (string.IsNullOrWhiteSpace(packageId)) continue;

                    string? version = packageRef.Attribute("Version")?.Value;

                    if (string.IsNullOrEmpty(version))
                        version = packageRef.Element(ns + "Version")?.Value;

                    // NOTE: leave version empty here; central resolver will fill it
                    packages.Add(new PackageReference {
                        PackageId = packageId,
                        Version = version ?? "",
                        Format = PackageFormat.PackageReference
                    });
                }
            } catch {
                // ignore
            }

            return packages;
        }
    }

    public class PackageReference {
        public string PackageId { get; set; } = "";
        public string Version { get; set; } = "";
        public PackageFormat Format { get; set; }
    }

    public enum PackageFormat {
        PackageReference
    }

}
