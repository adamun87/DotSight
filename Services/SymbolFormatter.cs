using Microsoft.CodeAnalysis;

namespace DotSight.Services;

/// <summary>
/// Helper methods for formatting Roslyn symbols into MCP-friendly text.
/// </summary>
public static class SymbolFormatter
{
    private static readonly SymbolDisplayFormat FullyQualifiedSymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut |
                          SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string GetKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Module => "module",
            _ => "type"
        },
        IMethodSymbol m => m.MethodKind switch
        {
            MethodKind.Constructor => "constructor",
            MethodKind.PropertyGet or MethodKind.PropertySet => "property-accessor",
            MethodKind.EventAdd or MethodKind.EventRemove => "event-accessor",
            MethodKind.Conversion or MethodKind.UserDefinedOperator => "operator",
            _ => "method"
        },
        IPropertySymbol => "property",
        IFieldSymbol f => f.IsConst ? "constant" : "field",
        IEventSymbol => "event",
        IParameterSymbol => "parameter",
        ITypeParameterSymbol => "type-parameter",
        ILocalSymbol => "local",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    public static string GetSignature(ISymbol symbol)
    {
        return symbol.ToDisplayString(new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters |
                             SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeType |
                           SymbolDisplayMemberOptions.IncludeParameters |
                           SymbolDisplayMemberOptions.IncludeAccessibility |
                           SymbolDisplayMemberOptions.IncludeModifiers,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                              SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeDefaultValue,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                                  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
    }

    public static string GetFullyQualifiedName(ISymbol symbol)
    {
        return symbol.ToDisplayString(FullyQualifiedSymbolFormat)
            .Replace("global::", "");
    }

    public static string FormatLocation(Location location, string solutionDir)
    {
        if (location.IsInSource)
        {
            var span = location.GetLineSpan();
            var filePath = Path.GetRelativePath(solutionDir, span.Path);
            return $"{filePath}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
        }

        if (location.IsInMetadata)
        {
            var metadataModule = location.MetadataModule;
            return metadataModule is not null
                ? $"[metadata] {metadataModule.ContainingAssembly.Name}"
                : "[metadata]";
        }

        return "[unknown]";
    }

    public static string GetXmlDocSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return "";

        // Extract <summary> content simply
        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return "";

        var content = xml[(start + 9)..end].Trim();
        // Strip XML tags and normalize whitespace
        content = System.Text.RegularExpressions.Regex.Replace(content, @"<[^>]+>", "");
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ").Trim();
        return content;
    }

    public static bool MatchesKindFilter(ISymbol symbol, string? kindFilter)
    {
        if (string.IsNullOrEmpty(kindFilter)) return true;
        var kind = GetKind(symbol);
        return string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase);
    }
}
