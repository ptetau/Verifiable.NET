using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalaxyCheck;
using GalaxyCheck.Stateful;

namespace Verifiable.Testing;

/// <summary>
/// Model-versus-database tests for any domain (requirement R11). Register the domain's commands — how to
/// generate their arguments from the current state, the pure <c>[Verifiable]</c> transition, and how the
/// repository commits them — and <see cref="Property"/> generates GalaxyCheck command sequences with
/// crashes and duplicate deliveries injected, comparing the model with what the database holds after
/// every step. The repository code is not proved; this is what earns it trust.
/// </summary>
/// <typeparam name="TDb">The system under test: a repository over a <see cref="FaultInjectedSqliteHarness"/>.</typeparam>
/// <typeparam name="TState">The domain state the pure model works on.</typeparam>
public sealed class DomainModel<TDb, TState>
    where TDb : IDisposable
    where TState : notnull
{
    /// <summary>The model state, boxed for GalaxyCheck.</summary>
    public sealed class Model
    {
        internal Model(TState state) => State = state;

        /// <summary>Gets the state the pure model holds.</summary>
        public TState State { get; }

        /// <inheritdoc />
        public override string ToString() => State.ToString() ?? string.Empty;
    }

    private sealed record Registered(string Name, int Weight, Func<TState, IGen<Command<TDb, Model>>?> Generate);

    private readonly List<Registered> _commands = new();

    private static readonly IGen<PersistenceFaultKind> DefaultFaults = Gen.Choose(
        (Gen.Constant(PersistenceFaultKind.None), 5),
        (Gen.Constant(PersistenceFaultKind.DuplicateDelivery), 2),
        (Gen.Constant(PersistenceFaultKind.PreCommitCrash), 1),
        (Gen.Constant(PersistenceFaultKind.PostCommitCrashBeforeAck), 1));

    private IGen<PersistenceFaultKind> _faults = DefaultFaults;

    /// <summary>The names of the registered commands, in registration order.</summary>
    public IReadOnlyList<string> CommandNames => _commands.Select(c => c.Name).ToList();

    /// <summary>
    /// Registers a domain command.
    /// </summary>
    /// <param name="name">A name for counterexamples, e.g. <c>"Add"</c>.</param>
    /// <param name="arguments">The arguments to try in a state, or null when the command does not apply there.</param>
    /// <param name="model">The pure transition: what the state becomes.</param>
    /// <param name="commit">
    /// Prepares the command against the database and returns the commit to run. The commit is what crashes
    /// and duplicate deliveries are injected into, so it must be idempotent under its own command ID.
    /// </param>
    /// <param name="weight">How often to choose this command relative to the others.</param>
    public DomainModel<TDb, TState> Command<TArgs>(
        string name,
        Func<TState, IGen<TArgs>?> arguments,
        Func<TState, TArgs, TState> model,
        Func<TDb, TArgs, Func<Task>> commit,
        int weight = 1)
    {
        if (weight < 1) throw new ArgumentOutOfRangeException(nameof(weight), "Weights are at least 1.");
        _commands.Add(new Registered(name, weight, state =>
        {
            var args = arguments(state);
            if (args is null) return null;
            return
                from a in args
                from fault in _faults
                select (Command<TDb, Model>)new Step<TArgs>(name, a, fault, model, commit, this);
        }));
        return this;
    }

    /// <summary>Replaces the default mix of faults (mostly none, some duplicates, some crashes).</summary>
    public DomainModel<TDb, TState> WithFaults(IGen<PersistenceFaultKind> faults)
    {
        _faults = faults;
        return this;
    }

    private Func<TDb, FaultInjectedSqliteHarness>? _harness;
    private Func<TDb, TState>? _load;
    private Func<TState, TState, bool> _equal = (a, b) => EqualityComparer<TState>.Default.Equals(a, b);

    /// <summary>
    /// The stateful property: from <paramref name="initial"/>, any sequence of the registered commands,
    /// under any injected fault, leaves the database (read back with <paramref name="load"/>) equal to the
    /// model. A crash before commit must change nothing; a crash after commit, or a duplicate delivery,
    /// must leave exactly one effect.
    /// </summary>
    public Property Property(
        Func<TDb> createDatabase,
        TState initial,
        Func<TDb, TState> load,
        Func<TDb, FaultInjectedSqliteHarness> harness,
        Func<TState, TState, bool>? equal = null)
    {
        if (_commands.Count == 0) throw new InvalidOperationException("Register at least one command.");
        _harness = harness;
        _load = load;
        if (equal is not null) _equal = equal;

        return StatefulProperty.Create(createDatabase, () => new Model(initial), NextCommand);
    }

    private IGen<Command<TDb, Model>> NextCommand(Model model)
    {
        var choices = _commands
            .Select(c => (Gen: c.Generate(model.State), c.Weight))
            .Where(c => c.Gen is not null)
            .Select(c => (c.Gen!, c.Weight))
            .ToArray();
        if (choices.Length == 0) throw new InvalidOperationException("No registered command applies to the initial state.");
        return Gen.Choose(choices);
    }

    private sealed class Step<TArgs>(
        string name,
        TArgs args,
        PersistenceFaultKind fault,
        Func<TState, TArgs, TState> model,
        Func<TDb, TArgs, Func<Task>> commit,
        DomainModel<TDb, TState> owner) : Command<TDb, Model>
    {
        public override void Run(TDb system)
        {
            var operation = commit(system, args);
            owner._harness!(system).ExecuteWithFaultAsync(fault, operation).GetAwaiter().GetResult();
        }

        public override Model NextModel(Model before) =>
            fault == PersistenceFaultKind.PreCommitCrash ? before : new Model(model(before.State, args));

        public override bool Expects(Exception exception) =>
            fault is PersistenceFaultKind.PreCommitCrash or PersistenceFaultKind.PostCommitCrashBeforeAck &&
            exception is SimulatedPersistenceCrashException;

        public override bool Postcondition(TDb system, Model before, Model after) =>
            owner._equal(owner._load!(system), after.State);

        public override string ToString() => $"{name}({args}{(fault == PersistenceFaultKind.None ? string.Empty : $", fault={fault}")})";
    }
}
