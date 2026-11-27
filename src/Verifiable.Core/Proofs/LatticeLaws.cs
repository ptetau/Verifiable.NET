namespace Verifiable.Core.Proofs;

/// <summary>
/// Lemmas about <see cref="ObligationLattice.JoinCore"/>. Each method's postcondition says
/// "this law always holds"; Z3 checks it for every combination of states when the project builds.
/// </summary>
/// <remarks>
/// These lemmas are only compiled into <c>Verifiable.Core</c>; they are proofs, not shipped code.
/// Together they show the join is a semilattice, which is what makes the VER007 worklist analysis
/// order-independent and guarantees it terminates (the lattice has height 2).
/// </remarks>
internal static class LatticeLaws
{
    internal static bool JoinIsCommutative(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Merging path A into path B gives the same state as merging B into A.");
        return ObligationLattice.JoinCore(a, b) == ObligationLattice.JoinCore(b, a);
    }

    internal static bool JoinIsIdempotent(ObligationState a)
    {
        Spec.Ensures<bool>(r => r, "Merging a path with itself changes nothing.");
        return ObligationLattice.JoinCore(a, a) == a;
    }

    internal static bool JoinIsAssociative(ObligationState a, ObligationState b, ObligationState c)
    {
        Spec.Ensures<bool>(r => r, "The order in which three paths are merged does not matter.");
        return ObligationLattice.JoinCore(ObligationLattice.JoinCore(a, b), c) ==
               ObligationLattice.JoinCore(a, ObligationLattice.JoinCore(b, c));
    }

    internal static bool JoinIsAnUpperBound(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Merging again with an input that is already included changes nothing, so the result is above both inputs.");
        var j = ObligationLattice.JoinCore(a, b);
        return ObligationLattice.JoinCore(a, j) == j && ObligationLattice.JoinCore(b, j) == j;
    }
}
