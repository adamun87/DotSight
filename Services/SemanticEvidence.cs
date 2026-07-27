using Microsoft.CodeAnalysis;

namespace DotSight.Services;

internal sealed record SourceEvidence(
    string File,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Project,
    string? EnclosingSymbol,
    string Snippet,
    bool IsTestProject,
    string? TestProjectEvidence);

internal static class SemanticEvidence
{
    public static async Task<SourceEvidence?> CreateAsync(
        Solution solution,
        Location location,
        Project? preferredProject,
        CancellationToken ct)
    {
        if (!location.IsInSource)
            return null;

        var span = location.GetLineSpan();
        var document = FindDocument(solution, span.Path, preferredProject);
        if (document is null)
            return null;

        var text = await document.GetTextAsync(ct);
        var lineIndex = text.Lines.GetLineFromPosition(
            Math.Min(location.SourceSpan.Start, Math.Max(0, text.Length - 1))).LineNumber;
        var snippet = text.Lines[lineIndex].ToString().Trim();
        if (snippet.Length > 240)
            snippet = snippet[..240] + "...";

        var semanticModel = await document.GetSemanticModelAsync(ct);
        var enclosingSymbol = semanticModel?.GetEnclosingSymbol(location.SourceSpan.Start, ct);
        var testEvidence = GetTestProjectEvidence(document.Project);
        var solutionDirectory = GetSolutionDirectory(solution);

        return new SourceEvidence(
            MakeRelativePath(solutionDirectory, span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            document.Project.Name,
            enclosingSymbol is null ? null : SymbolFormatter.GetFullyQualifiedName(enclosingSymbol),
            snippet,
            testEvidence is not null,
            testEvidence);
    }

    public static Document? FindDocument(
        Solution solution,
        string filePath,
        Project? preferredProject = null)
    {
        var documentIds = solution.GetDocumentIdsWithFilePath(filePath);
        if (preferredProject is not null)
        {
            var preferredId = documentIds.FirstOrDefault(id => id.ProjectId == preferredProject.Id);
            if (preferredId is not null)
                return solution.GetDocument(preferredId);
        }

        var documentId = documentIds.FirstOrDefault();
        return documentId is null ? null : solution.GetDocument(documentId);
    }

    public static string GetSolutionDirectory(Solution solution)
    {
        var path = solution.FilePath
            ?? solution.Projects.Select(project => project.FilePath).FirstOrDefault(path => path is not null);
        return path is null
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
    }

    public static string MakeRelativePath(string solutionDirectory, string path) =>
        Path.IsPathRooted(path) ? Path.GetRelativePath(solutionDirectory, path) : path;

    public static string? GetTestProjectEvidence(Project project)
    {
        if (project.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || project.Name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
            || project.Name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase)
            || project.Name.Contains(".Test.", StringComparison.OrdinalIgnoreCase))
        {
            return "project name";
        }

        if (project.FilePath is null)
            return null;

        var segments = Path.GetDirectoryName(project.FilePath)?
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        return segments?.Any(segment =>
            string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase)) == true
            ? "project path"
            : null;
    }
}
