using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotSight.Services;

internal sealed record RenamePreviewOptions(
    bool RenameOverloads,
    bool RenameInStrings,
    bool RenameInComments,
    bool RenameFile);

internal sealed record RenameTextEdit(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string OldText,
    string NewText);

internal sealed record RenameFilePreview(
    string ChangeKind,
    string? File,
    string? NewFile,
    string? BaseSha256,
    int TotalEdits,
    IReadOnlyList<RenameTextEdit> Edits);

internal sealed record RenameDiagnostic(
    string Id,
    string Message,
    string? File,
    int? Line,
    int? Column);

internal sealed record RenameValidation(
    bool Complete,
    int PreExistingCompilerErrors,
    int NewCompilerErrors,
    bool DiagnosticsTruncated,
    IReadOnlyList<RenameDiagnostic> Diagnostics);

internal sealed record RenamePreviewResult(
    SymbolCandidate Symbol,
    string NewName,
    RenamePreviewOptions Options,
    int ChangedFiles,
    int TotalEdits,
    int ReturnedEdits,
    bool Truncated,
    IReadOnlyList<RenameFilePreview> Files,
    RenameValidation Validation,
    IReadOnlyList<string> Warnings);

internal sealed class PendingRenameFile
{
    public required string ChangeKind { get; init; }
    public required string? File { get; init; }
    public required string? NewFile { get; init; }
    public required string? BaseSha256 { get; init; }
    public required SourceText OldText { get; init; }
    public List<TextChange> Changes { get; } = [];
}

internal static class RenamePreviewService
{
    private const int MaxDiagnostics = 50;
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static async Task<RenamePreviewResult> PreviewAsync(
        Solution solution,
        ResolvedSymbol target,
        string newName,
        RenamePreviewOptions options,
        int maxEdits,
        bool includeDiagnostics,
        CancellationToken ct)
    {
        maxEdits = Math.Clamp(maxEdits, 1, 5000);
        var renameOptions = new SymbolRenameOptions(
            options.RenameOverloads,
            options.RenameInStrings,
            options.RenameInComments,
            options.RenameFile);
        var renamedSolution = await Renamer.RenameSymbolAsync(
            solution,
            target.Symbol,
            renameOptions,
            newName,
            ct);

        var solutionDirectory = SemanticEvidence.GetSolutionDirectory(solution);
        var pendingFiles = new Dictionary<string, PendingRenameFile>(FilePathComparer);

        foreach (var projectChanges in renamedSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var oldDocument = solution.GetDocument(documentId);
                var newDocument = renamedSolution.GetDocument(documentId);
                if (oldDocument is null || newDocument is null)
                    continue;

                var oldText = await oldDocument.GetTextAsync(ct);
                var changes = (await newDocument.GetTextChangesAsync(oldDocument, ct)).ToList();
                AddOrMergeFile(
                    pendingFiles,
                    GetFileIdentity(oldDocument),
                    new PendingRenameFile
                    {
                        ChangeKind = "changed",
                        File = MakeRelative(solutionDirectory, oldDocument.FilePath),
                        NewFile = MakeRelative(solutionDirectory, newDocument.FilePath),
                        BaseSha256 = await ComputeBaseSha256Async(oldDocument, oldText, ct),
                        OldText = oldText,
                    },
                    changes);
            }

            foreach (var documentId in projectChanges.GetAddedDocuments())
            {
                var document = renamedSolution.GetDocument(documentId);
                if (document is null)
                    continue;

                var text = await document.GetTextAsync(ct);
                AddOrMergeFile(
                    pendingFiles,
                    GetFileIdentity(document),
                    new PendingRenameFile
                    {
                        ChangeKind = "added",
                        File = null,
                        NewFile = MakeRelative(solutionDirectory, document.FilePath),
                        BaseSha256 = null,
                        OldText = SourceText.From("", text.Encoding),
                    },
                    [new TextChange(new TextSpan(0, 0), text.ToString())]);
            }

            foreach (var documentId in projectChanges.GetRemovedDocuments())
            {
                var document = solution.GetDocument(documentId);
                if (document is null)
                    continue;

                var text = await document.GetTextAsync(ct);
                AddOrMergeFile(
                    pendingFiles,
                    GetFileIdentity(document),
                    new PendingRenameFile
                    {
                        ChangeKind = "removed",
                        File = MakeRelative(solutionDirectory, document.FilePath),
                        NewFile = null,
                        BaseSha256 = await ComputeBaseSha256Async(document, text, ct),
                        OldText = text,
                    },
                    [new TextChange(new TextSpan(0, text.Length), "")]);
            }
        }

        var files = new List<RenameFilePreview>();
        var totalEdits = pendingFiles.Values.Sum(file => file.Changes.Count);
        var returnedEdits = 0;
        foreach (var pending in pendingFiles.Values)
        {
            var edits = pending.Changes
                .OrderBy(change => change.Span.Start)
                .ThenBy(change => change.Span.Length)
                .Take(Math.Max(0, maxEdits - returnedEdits))
                .Select(change => FormatEdit(pending.OldText, change))
                .ToList();
            returnedEdits += edits.Count;
            files.Add(new RenameFilePreview(
                pending.ChangeKind,
                pending.File,
                pending.NewFile,
                pending.BaseSha256,
                pending.Changes.Count,
                edits));
        }

        var validation = includeDiagnostics
            ? await ValidateCompilerErrorsAsync(
                solution,
                renamedSolution,
                target.Symbol.Name,
                newName,
                ct)
            : new RenameValidation(false, 0, 0, false, []);

        var warnings = new List<string>
        {
            "Preview only: DotSight did not modify source files.",
            "Edits are based on saved files in the reported workspace snapshot. Verify each baseSha256 before applying them.",
        };
        if (returnedEdits < totalEdits)
            warnings.Add("The edit list is truncated. Rerun with a larger maxEdits before applying the rename.");
        if (!includeDiagnostics)
            warnings.Add("Post-rename compiler diagnostics were not requested.");
        if (validation.NewCompilerErrors > 0)
            warnings.Add("The renamed solution introduced compiler errors; inspect validation.diagnostics before applying edits.");

        return new RenamePreviewResult(
            SymbolResolver.Describe(solution, target.Symbol, target.Project),
            newName,
            options,
            files.Count,
            totalEdits,
            returnedEdits,
            returnedEdits < totalEdits,
            files,
            validation,
            warnings);
    }

    private static void AddOrMergeFile(
        Dictionary<string, PendingRenameFile> files,
        string identity,
        PendingRenameFile candidate,
        IEnumerable<TextChange> changes)
    {
        if (!files.TryGetValue(identity, out var existing))
        {
            existing = candidate;
            files.Add(identity, existing);
        }
        else
        {
            if (!string.Equals(existing.ChangeKind, candidate.ChangeKind, StringComparison.Ordinal)
                || !FilePathComparer.Equals(existing.File, candidate.File)
                || !FilePathComparer.Equals(existing.NewFile, candidate.NewFile)
                || !string.Equals(
                    existing.BaseSha256,
                    candidate.BaseSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !existing.OldText.ContentEquals(candidate.OldText))
            {
                throw CreateLinkedDocumentConflict(candidate);
            }
        }

        foreach (var change in changes)
        {
            var sameSpanIndex = existing.Changes.FindIndex(
                current => current.Span == change.Span);
            if (sameSpanIndex >= 0)
            {
                var sameSpan = existing.Changes[sameSpanIndex];
                if (string.Equals(sameSpan.NewText, change.NewText, StringComparison.Ordinal))
                    continue;
                throw CreateLinkedDocumentConflict(candidate);
            }

            if (existing.Changes.Any(current => current.Span.OverlapsWith(change.Span)))
                throw CreateLinkedDocumentConflict(candidate);

            existing.Changes.Add(change);
        }
    }

    private static InvalidOperationException CreateLinkedDocumentConflict(
        PendingRenameFile file) =>
        new(
            $"Linked documents for '{file.File ?? file.NewFile}' produced conflicting rename edits. "
            + "DotSight cannot produce a safely applicable preview for this symbol.");

    private static string GetFileIdentity(Document document) =>
        document.FilePath is null
            ? $"document:{document.Id.Id:N}"
            : $"file:{Path.GetFullPath(document.FilePath)}";

    private static RenameTextEdit FormatEdit(SourceText oldText, TextChange change)
    {
        var start = oldText.Lines.GetLinePosition(change.Span.Start);
        var end = oldText.Lines.GetLinePosition(change.Span.End);
        return new RenameTextEdit(
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            oldText.ToString(change.Span),
            change.NewText ?? "");
    }

    private static async Task<RenameValidation> ValidateCompilerErrorsAsync(
        Solution original,
        Solution renamed,
        string oldName,
        string newName,
        CancellationToken ct)
    {
        var changedProjectIds = renamed.GetChanges(original)
            .GetProjectChanges()
            .Select(change => change.ProjectId)
            .Distinct()
            .ToList();
        var preExistingErrors = 0;
        var newErrors = new List<RenameDiagnostic>();
        var validationComplete = true;

        foreach (var projectId in changedProjectIds)
        {
            var originalCompilation = await original.GetProject(projectId)!.GetCompilationAsync(ct);
            var renamedCompilation = await renamed.GetProject(projectId)!.GetCompilationAsync(ct);
            if (originalCompilation is null || renamedCompilation is null)
            {
                validationComplete = false;
                continue;
            }

            var originalErrors = originalCompilation.GetDiagnostics(ct)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToList();
            var unmatchedOriginalErrors = originalErrors
                .GroupBy(
                    diagnostic => GetDiagnosticKey(diagnostic, oldName, newName),
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);
            preExistingErrors += originalErrors.Count;

            foreach (var diagnostic in renamedCompilation.GetDiagnostics(ct)
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                var key = GetDiagnosticKey(diagnostic, oldName, newName);
                if (unmatchedOriginalErrors.TryGetValue(key, out var count) && count > 0)
                {
                    unmatchedOriginalErrors[key] = count - 1;
                    continue;
                }

                newErrors.Add(FormatDiagnostic(diagnostic, renamed));
            }
        }

        return new RenameValidation(
            validationComplete,
            preExistingErrors,
            newErrors.Count,
            newErrors.Count > MaxDiagnostics,
            newErrors.Take(MaxDiagnostics).ToList());
    }

    private static string GetDiagnosticKey(
        Diagnostic diagnostic,
        string oldName,
        string newName)
    {
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        foreach (var name in new[] { oldName, newName }
                     .Where(name => !string.IsNullOrEmpty(name))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(name => name.Length))
        {
            message = message.Replace(name, "{renamed-symbol}", StringComparison.Ordinal);
        }

        return $"{diagnostic.Id}:{message}";
    }

    private static RenameDiagnostic FormatDiagnostic(Diagnostic diagnostic, Solution solution)
    {
        if (!diagnostic.Location.IsInSource)
            return new RenameDiagnostic(diagnostic.Id, diagnostic.GetMessage(), null, null, null);

        var span = diagnostic.Location.GetLineSpan();
        return new RenameDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            MakeRelative(SemanticEvidence.GetSolutionDirectory(solution), span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }

    private static async Task<string> ComputeBaseSha256Async(
        Document document,
        SourceText text,
        CancellationToken ct)
    {
        if (document.FilePath is not null)
        {
            if (!File.Exists(document.FilePath))
            {
                throw new InvalidOperationException(
                    $"Saved file '{document.FilePath}' was deleted after the workspace snapshot "
                    + "was loaded. Rerun preview_rename to refresh the snapshot.");
            }

            var bytes = await File.ReadAllBytesAsync(document.FilePath, ct);
            using var stream = new MemoryStream(bytes, writable: false);
            var diskText = SourceText.From(
                stream,
                text.Encoding,
                SourceHashAlgorithm.Sha256,
                throwIfBinaryDetected: false,
                canBeEmbedded: false);
            if (!diskText.ContentEquals(text))
            {
                throw new InvalidOperationException(
                    $"Saved file '{document.FilePath}' changed after the workspace snapshot was loaded. "
                    + "Rerun preview_rename to refresh the snapshot.");
            }

            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        var encoding = text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var preamble = encoding.GetPreamble();
        var content = encoding.GetBytes(text.ToString());
        if (preamble.Length == 0)
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var encoded = new byte[preamble.Length + content.Length];
        preamble.CopyTo(encoded, 0);
        content.CopyTo(encoded, preamble.Length);
        return Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
    }

    private static string? MakeRelative(string solutionDirectory, string? path) =>
        path is null ? null : SemanticEvidence.MakeRelativePath(solutionDirectory, path);
}
