using System;
using dnlib.DotNet;
using dnSpy.MCP.Tools;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Unit tests for the pure helpers in <see cref="IlPatchTools"/>. These functions do not touch
/// <c>DnSpyContext</c> or dnSpy services, so they can be tested without mocking.
/// See memory: dnspy-mcp-test-strategy — these are the testable pure functions.
/// </summary>
public class IlPatchToolsTests {
    /// <summary>
    /// A throwaway module provides <see cref="CorLibTypes"/> (Int32, String, etc.) and is the
    /// canonical dnlib way to build <see cref="TypeSig"/> instances for testing.
    /// </summary>
    static readonly ModuleDefUser Module = new("TestModule");

    static TypeSig Int32 => Module.CorLibTypes.Int32;
    static TypeSig StringSig => Module.CorLibTypes.String;
    static TypeSig ObjectSig => Module.CorLibTypes.Object;

    // -------------------------------------------------------------------------
    // NormalizeMetadataTypeName
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Foo`1", "Foo")]
    [InlineData("Dictionary`2", "Dictionary")]
    [InlineData("NoGenerics", "NoGenerics")]
    [InlineData("Nested/Inner", "Nested.Inner")]
    [InlineData("A/B/C", "A.B.C")]
    public void NormalizeMetadataTypeName_strips_generic_arity_and_converts_slashes(string input, string expected) {
        IlPatchTools.NormalizeMetadataTypeName(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeMetadataTypeName_truncates_at_first_backtick_even_with_nested_slash() {
        // Behavior contract: IndexOf('`') truncates the WHOLE string at the first backtick,
        // so "Foo`2/Bar`1" becomes "Foo" — the "/Bar`1" tail is dropped, not slash-converted.
        // This is a known characteristic: nested generic types should be resolved at the
        // TypeSig level (via IsGenericInstanceType), not by string normalization.
        IlPatchTools.NormalizeMetadataTypeName("Foo`2/Bar`1").Should().Be("Foo");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Plain")]
    public void NormalizeMetadataTypeName_returns_simple_names_unchanged(string input) {
        IlPatchTools.NormalizeMetadataTypeName(input).Should().Be(input);
    }

    // -------------------------------------------------------------------------
    // ToCSharpTypeName — primitives
    // -------------------------------------------------------------------------

    [Fact]
    public void ToCSharpTypeName_returns_int_for_int32_sig() {
        IlPatchTools.ToCSharpTypeName(Int32).Should().Be("int");
    }

    [Fact]
    public void ToCSharpTypeName_returns_string_for_string_sig() {
        IlPatchTools.ToCSharpTypeName(StringSig).Should().Be("string");
    }

    [Fact]
    public void ToCSharpTypeName_returns_object_for_object_sig() {
        IlPatchTools.ToCSharpTypeName(ObjectSig).Should().Be("object");
    }

    // -------------------------------------------------------------------------
    // ToCSharpTypeName — null and edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void ToCSharpTypeName_returns_object_for_null() {
        IlPatchTools.ToCSharpTypeName(null).Should().Be("object");
    }

    [Fact]
    public void ToCSharpTypeName_returns_object_for_generic_parameter() {
        // A generic parameter (e.g. T) cannot be expressed as a concrete C# type.
        var gp = new GenericVar(0);
        IlPatchTools.ToCSharpTypeName(gp).Should().Be("object");
    }

    // -------------------------------------------------------------------------
    // ToCSharpTypeName — compound types (arrays, pointers, byref, generics)
    // -------------------------------------------------------------------------

    [Fact]
    public void ToCSharpTypeName_returns_brackets_for_szarray() {
        IlPatchTools.ToCSharpTypeName(new SZArraySig(Int32)).Should().Be("int[]");
    }

    [Fact]
    public void ToCSharpTypeName_returns_ref_for_byref() {
        IlPatchTools.ToCSharpTypeName(new ByRefSig(Int32)).Should().Be("ref int");
    }

    [Fact]
    public void ToCSharpTypeName_returns_star_for_pointer() {
        IlPatchTools.ToCSharpTypeName(new PtrSig(Int32)).Should().Be("int*");
    }

    [Fact]
    public void ToCSharpTypeName_returns_void_star_for_pointer_to_void() {
        // Pointer whose element resolves to null/void should not dereference null.
        IlPatchTools.ToCSharpTypeName(new PtrSig(null!)).Should().Be("void*");
    }

    [Fact]
    public void ToCSharpTypeName_returns_ranked_array_for_multidimensional() {
        IlPatchTools.ToCSharpTypeName(new ArraySig(Int32, 2)).Should().Be("int[,]");
        IlPatchTools.ToCSharpTypeName(new ArraySig(Int32, 3)).Should().Be("int[,,]");
    }

    [Fact]
    public void ToCSharpTypeName_returns_generic_instance() {
        // List<int> — build a TypeDef "System.Collections.Generic.List`1" and a generic instance sig.
        var listType = new TypeDefUser("System.Collections.Generic", "List`1");
        var genericInst = new GenericInstSig(new ClassSig(listType), Int32);
        var result = IlPatchTools.ToCSharpTypeName(genericInst);

        // The FullName is kept (namespace preserved for compilation); only the generic arity
        // backtick is stripped by NormalizeMetadataTypeName: "System.Collections.Generic.List`1"
        // -> "System.Collections.Generic.List".
        result.Should().Be("System.Collections.Generic.List<int>");
    }

    // -------------------------------------------------------------------------
    // MakeSafeIdentifier
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("_bar", "_bar")]
    [InlineData("AlreadyValid123", "AlreadyValid123")]
    public void MakeSafeIdentifier_leaves_valid_identifiers_unchanged(string input, string expected) {
        IlPatchTools.MakeSafeIdentifier(input).Should().Be(expected);
    }

    [Fact]
    public void MakeSafeIdentifier_prepends_underscore_when_starting_with_digit() {
        IlPatchTools.MakeSafeIdentifier("1foo").Should().Be("_1foo");
    }

    [Fact]
    public void MakeSafeIdentifier_replaces_non_identifier_chars_with_underscore() {
        IlPatchTools.MakeSafeIdentifier("a-b.c").Should().Be("a_b_c");
    }

    [Fact]
    public void MakeSafeIdentifier_returns_arg_for_blank_input() {
        IlPatchTools.MakeSafeIdentifier("").Should().Be("arg");
        IlPatchTools.MakeSafeIdentifier("   ").Should().Be("arg");
    }

    [Theory]
    [InlineData("class", "@class")]
    [InlineData("int", "@int")]
    [InlineData("return", "@return")]
    public void MakeSafeIdentifier_prefixes_csharp_keywords_with_at(string keyword, string expected) {
        // Roslyn rejects bare keywords as identifiers; the function must escape them.
        IlPatchTools.MakeSafeIdentifier(keyword).Should().Be(expected);
    }

    [Fact]
    public void MakeSafeIdentifier_leaves_contextual_keywords_unescaped_when_not_keyword() {
        // "value" is a contextual keyword, not reserved — it should NOT be prefixed.
        IlPatchTools.MakeSafeIdentifier("value").Should().Be("value");
    }
}
