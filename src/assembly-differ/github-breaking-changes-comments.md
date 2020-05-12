```diff
Scanned: 📑 1 projects
- ⚠️ 0 breaking changes detected in 📑0 projects ⚠️
```

## 📑 assembly-differ.dll

```diff

assembly-differ, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
assembly-differ.dll
- 🔴  JustAssembly.Core, Version=0.0.7290.33974, Culture=neutral, PublicKeyToken=null
+ 🌟  Differ.AllComparisonResults
+ 🌟  Differ.AssemblyComparison
- 🔴  Differ.AssemblyDiffPair
+ 🌟  Differ.Exporters.GitHubActionCommentExporter
+ 🌟  Differ.Exporters.IAllComparisonResultsExporter
+ 🌟  Differ.Exporters.IAssemblyComparisonExporter
+ 🌟  Differ.SuggestedVersionChange
```
```diff
Differ.Exporters.IExporter
- 🔴  Export(AssemblyDiffPair, String)
```
```diff
Differ.Providers.NuGet.NuGet
+ 🔷 GetPackageDependencies(PackageIdentity, NuGetFramework, SourceCacheContext, ILogger, IEnumerable<SourceRepository>, ISet<SourcePackageDependencyInfo>)
+ 🔷 Member is more visible.
```
```diff
Differ.Exporters.AsciiDocExporter
+ 🌟  Export(AssemblyComparison, String)
- 🔴  Export(AssemblyDiffPair, String)
```
```diff
Differ.Exporters.MarkdownExporter
+ 🌟  Export(AssemblyComparison, String)
- 🔴  Export(AssemblyDiffPair, String)
```
```diff
Differ.Exporters.XmlExporter
+ 🌟  Export(AssemblyComparison, String)
- 🔴  Export(AssemblyDiffPair, String)
```
