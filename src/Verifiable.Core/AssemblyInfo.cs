// Every type and method in the kernel is verified. There is no [Unverifiable] escape hatch here:
// anything that cannot be proved belongs in the utility layer (Verifiable.Analyzers / Verifiable.Query).
[assembly: Verifiable]
