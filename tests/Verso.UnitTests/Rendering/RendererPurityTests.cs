using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Verso.Rendering.Html;

namespace Verso.UnitTests.Rendering;

/// <summary>
/// The renderer must stay pure (plan §3): no file system, network, process, database, ASP.NET or Playwright use.
/// Checked by reading the compiled assembly's metadata: which assemblies and types it references.
/// </summary>
public sealed class RendererPurityTests
{
    private static readonly string[] ForbiddenTypeNamespaces = ["System.IO", "System.Net", "System.Diagnostics", "System.Reflection.Emit", "System.Runtime.InteropServices", "Microsoft"];
    private static readonly string[] AllowedAssemblyPrefixes = ["System.Runtime", "System.Collections", "System.Linq", "System.Text.Json", "System.Text.Encodings.Web", "System.Text.RegularExpressions", "System.Memory", "netstandard"];

    [Fact]
    public void Renderer_ReferencesOnlyBaseLibrary_AndNoIoTypes()
    {
        using var stream = File.OpenRead(typeof(HtmlRenderer).Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var assemblies = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        var unexpectedAssemblies = assemblies.Where(a => !AllowedAssemblyPrefixes.Any(p => a.StartsWith(p, StringComparison.Ordinal))).ToList();

        var types = md.TypeReferences.Select(h => md.GetTypeReference(h)).Select(t => $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}").ToList();
        var forbiddenTypes = types
            .Where(t => ForbiddenTypeNamespaces.Any(ns => t.StartsWith(ns + ".", StringComparison.Ordinal)))
            .Where(t => t is not "System.Diagnostics.DebuggableAttribute" and not "System.Diagnostics.DebuggerBrowsableAttribute"
                and not "System.Diagnostics.DebuggerBrowsableState" and not "System.Diagnostics.DebuggerNonUserCodeAttribute"
                and not "System.Diagnostics.DebuggerHiddenAttribute" and not "System.Diagnostics.DebuggerStepThroughAttribute"
                and not "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"
                and not "System.Diagnostics.DebuggableAttribute.DebuggingModes"
                and not "System.Runtime.InteropServices.InAttribute") // compiler-emitted marker for in/readonly parameters, not interop
            .Distinct()
            .ToList();

        Assert.True(unexpectedAssemblies.Count == 0, "Unexpected assembly references: " + string.Join(", ", unexpectedAssemblies));
        Assert.True(forbiddenTypes.Count == 0, "Forbidden type references: " + string.Join(", ", forbiddenTypes));
    }
}
