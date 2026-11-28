using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    /// <summary>
    /// The nearest overridden method that has a <c>Spec.Requires</c> or <c>Spec.Ensures</c>, if any. A virtual call is
    /// proved against the base method's contracts, so an override must not silently differ from them.
    /// </summary>
    internal static IMethodSymbol? FindContractedOverriddenMethod(Compilation compilation, IMethodSymbol method)
    {
        for (var baseMethod = method.OverriddenMethod; baseMethod is not null; baseMethod = baseMethod.OverriddenMethod)
        {
            var specs = ExtractMethodSpecs(compilation, baseMethod, default);
            if (specs.Requires.Count > 0 || specs.Ensures.Count > 0)
            {
                return baseMethod;
            }
        }

        return null;
    }
}
