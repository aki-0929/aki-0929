using System.Collections.Frozen;

namespace Kaling.IO;

/// <summary>
/// Immutable map between command types and the type ids written on the wire. Both sides of a
/// connection must use the same mapping. One registry can be shared by any number of connections.
/// </summary>
public sealed class CommandRegistry
{
    private readonly FrozenDictionary<int, Func<CommandChannel.Command>> _factories;
    private readonly FrozenDictionary<Type, int> _typeIds;

    /// <summary>Creates a registry from the commands registered in <paramref name="configure"/>.</summary>
    /// <example>
    /// <code>
    /// var registry = new CommandRegistry(b => b
    ///     .Register&lt;LoginCommand&gt;(1)
    ///     .Register&lt;ChatCommand&gt;(2));
    /// </code>
    /// </example>
    public CommandRegistry(Action<Builder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new Builder();
        configure(builder);
        builder.Seal();
        _factories = builder.Factories.ToFrozenDictionary();
        _typeIds = builder.TypeIds.ToFrozenDictionary();
    }

    internal bool TryGetTypeId(Type commandType, out int typeId)
    {
        return _typeIds.TryGetValue(commandType, out typeId);
    }

    internal bool Contains(int typeId)
    {
        return _factories.ContainsKey(typeId);
    }

    internal CommandChannel.Command Create(int typeId)
    {
        return _factories[typeId]();
    }

    /// <summary>Collects registrations for a <see cref="CommandRegistry"/>; usable only inside its constructor.</summary>
    public sealed class Builder
    {
        private bool _sealed;

        internal Builder()
        {
        }

        internal Dictionary<int, Func<CommandChannel.Command>> Factories { get; } = [];

        internal Dictionary<Type, int> TypeIds { get; } = [];

        /// <summary>
        /// Registers <typeparamref name="T"/> under <paramref name="typeId"/>. Each type and each id may be
        /// registered once.
        /// </summary>
        /// <returns>This builder, for chaining.</returns>
        /// <exception cref="ArgumentException">The type or the id is already registered.</exception>
        /// <exception cref="InvalidOperationException">The registry has already been created.</exception>
        public Builder Register<T>(int typeId) where T : CommandChannel.Command, new()
        {
            if (_sealed)
                throw new InvalidOperationException(
                    "The registry has already been created and can no longer be changed.");

            if (Factories.ContainsKey(typeId))
                throw new ArgumentException($"Type id {typeId} is already registered.", nameof(typeId));

            if (!TypeIds.TryAdd(typeof(T), typeId))
                throw new ArgumentException($"Command type '{typeof(T)}' is already registered.");

            Factories.Add(typeId, static () => new T());
            return this;
        }

        internal void Seal()
        {
            _sealed = true;
        }
    }
}