using DotSight.Services;
using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

public class SymbolFormatterTests
{
    private readonly Compilation _compilation = CompilationFactory.CreateSampleCompilation();

    private INamedTypeSymbol GetType(string fullyQualifiedName) =>
        _compilation.GetTypeByMetadataName(fullyQualifiedName)
        ?? throw new InvalidOperationException($"Type '{fullyQualifiedName}' not found");

    // --- GetKind ---

    [Theory]
    [InlineData("SampleApp.Models.IAnimal", "interface")]
    [InlineData("SampleApp.Models.Dog", "class")]
    [InlineData("SampleApp.Models.Color", "enum")]
    [InlineData("SampleApp.Models.Vehicle", "class")]
    public void GetKind_ReturnsCorrectKindForTypes(string typeName, string expectedKind)
    {
        var symbol = GetType(typeName);
        Assert.Equal(expectedKind, SymbolFormatter.GetKind(symbol));
    }

    [Fact]
    public void GetKind_ReturnsMethod_ForRegularMethod()
    {
        var type = GetType("SampleApp.Models.Dog");
        var method = type.GetMembers("Fetch").First();
        Assert.Equal("method", SymbolFormatter.GetKind(method));
    }

    [Fact]
    public void GetKind_ReturnsProperty_ForProperty()
    {
        var type = GetType("SampleApp.Models.Dog");
        var prop = type.GetMembers("Name").First();
        Assert.Equal("property", SymbolFormatter.GetKind(prop));
    }

    [Fact]
    public void GetKind_ReturnsConstant_ForConst()
    {
        var type = GetType("SampleApp.Models.Constants");
        var field = type.GetMembers("AppName").First();
        Assert.Equal("constant", SymbolFormatter.GetKind(field));
    }

    [Fact]
    public void GetKind_ReturnsField_ForReadonly()
    {
        var type = GetType("SampleApp.Models.Constants");
        var field = type.GetMembers("Version").First();
        Assert.Equal("field", SymbolFormatter.GetKind(field));
    }

    [Fact]
    public void GetKind_ReturnsConstructor_ForCtor()
    {
        var type = GetType("SampleApp.Models.Dog");
        var ctor = type.GetMembers(".ctor").FirstOrDefault();
        if (ctor is not null)
            Assert.Equal("constructor", SymbolFormatter.GetKind(ctor));
    }

    // --- GetFullyQualifiedName ---

    [Theory]
    [InlineData("SampleApp.Models.IAnimal", "SampleApp.Models.IAnimal")]
    [InlineData("SampleApp.Models.Dog", "SampleApp.Models.Dog")]
    [InlineData("SampleApp.Services.AnimalService", "SampleApp.Services.AnimalService")]
    public void GetFullyQualifiedName_ReturnsExpected(string metadataName, string expected)
    {
        var symbol = GetType(metadataName);
        Assert.Equal(expected, SymbolFormatter.GetFullyQualifiedName(symbol));
    }

    [Fact]
    public void GetFullyQualifiedName_GenericType_IncludesTypeParameter()
    {
        var symbol = GetType("SampleApp.Models.Repository`1");
        var fqn = SymbolFormatter.GetFullyQualifiedName(symbol);
        Assert.Contains("Repository", fqn);
        Assert.Contains("T", fqn);
    }

    [Fact]
    public void GetFullyQualifiedName_MemberIncludesContainingTypeAndParameters()
    {
        var method = GetType("SampleApp.Models.Dog").GetMembers("Fetch").Single();

        var fqn = SymbolFormatter.GetFullyQualifiedName(method);

        Assert.Equal("SampleApp.Models.Dog.Fetch(string)", fqn);
    }

    // --- GetSignature ---

    [Fact]
    public void GetSignature_Method_IncludesParameterTypes()
    {
        var type = GetType("SampleApp.Models.Dog");
        var method = type.GetMembers("Fetch").First();
        var sig = SymbolFormatter.GetSignature(method);
        Assert.Contains("Fetch", sig);
        Assert.Contains("string", sig);
    }

    [Fact]
    public void GetSignature_Type_IncludesAccessibility()
    {
        var type = GetType("SampleApp.Models.Dog");
        var sig = SymbolFormatter.GetSignature(type);
        Assert.Contains("Dog", sig);
    }

    // --- GetXmlDocSummary ---

    [Fact]
    public void GetXmlDocSummary_ReturnsDocumentation()
    {
        var type = GetType("SampleApp.Models.IAnimal");
        var summary = SymbolFormatter.GetXmlDocSummary(type);
        Assert.Contains("animal", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetXmlDocSummary_ReturnsEmpty_WhenNoDoc()
    {
        var type = GetType("SampleApp.Services.AnimalService");
        var method = type.GetMembers("Register").First();
        var summary = SymbolFormatter.GetXmlDocSummary(method);
        Assert.Equal("", summary);
    }

    // --- MatchesKindFilter ---

    [Fact]
    public void MatchesKindFilter_NullFilter_ReturnsTrue()
    {
        var symbol = GetType("SampleApp.Models.Dog");
        Assert.True(SymbolFormatter.MatchesKindFilter(symbol, null));
    }

    [Fact]
    public void MatchesKindFilter_EmptyFilter_ReturnsTrue()
    {
        var symbol = GetType("SampleApp.Models.Dog");
        Assert.True(SymbolFormatter.MatchesKindFilter(symbol, ""));
    }

    [Fact]
    public void MatchesKindFilter_MatchingFilter_ReturnsTrue()
    {
        var symbol = GetType("SampleApp.Models.Dog");
        Assert.True(SymbolFormatter.MatchesKindFilter(symbol, "class"));
    }

    [Fact]
    public void MatchesKindFilter_NonMatchingFilter_ReturnsFalse()
    {
        var symbol = GetType("SampleApp.Models.Dog");
        Assert.False(SymbolFormatter.MatchesKindFilter(symbol, "interface"));
    }

    [Fact]
    public void MatchesKindFilter_CaseInsensitive()
    {
        var symbol = GetType("SampleApp.Models.IAnimal");
        Assert.True(SymbolFormatter.MatchesKindFilter(symbol, "INTERFACE"));
    }

    // --- FormatLocation ---

    [Fact]
    public void FormatLocation_MetadataLocation_ReturnsMetadataMarker()
    {
        // object is from metadata
        var objectType = _compilation.GetSpecialType(SpecialType.System_Object);
        var location = objectType.Locations.First();
        var result = SymbolFormatter.FormatLocation(location, "C:\\fake");
        Assert.Contains("[metadata]", result);
    }
}
