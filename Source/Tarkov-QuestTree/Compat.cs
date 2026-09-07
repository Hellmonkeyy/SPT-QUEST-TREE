using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuestTree
{
    /// <summary>
    /// Same pattern Quick Sell uses: resolves a private game field by its TYPE rather than its
    /// name, since names drift between game builds but "the one field of this type" tends not to.
    /// Confirmed necessary here - TarkovApplication's MainMenuShowOperation field is
    /// private and decompiles today as "_menuOperation", not any name worth hardcoding.
    /// </summary>
    internal static class Compat
    {
        private static readonly Dictionary<(Type Owner, Type Field), FieldInfo> Cache = new();

        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static T Get<T>(object instance) where T : class
        {
            if (instance == null) return null;

            var owner = instance.GetType();
            var key = (owner, typeof(T));

            if (!Cache.TryGetValue(key, out var field))
            {
                // Base classes too: GetFields with NonPublic returns a class's OWN privates only,
                // and the field wanted may be declared on the class the game type derives from.
                for (var type = owner; type != null && field == null; type = type.BaseType)
                {
                    field = type.GetFields(Flags | BindingFlags.DeclaredOnly)
                        .FirstOrDefault(f => typeof(T).IsAssignableFrom(f.FieldType));
                }

                Cache[key] = field;

                if (field == null)
                {
                    Plugin.LogSource?.LogError(
                        $"QuestTree: no field of type {typeof(T).Name} found on {owner.Name}. " +
                        "The game build changed; this feature will not work.");
                }
            }

            return field?.GetValue(instance) as T;
        }
    }
}
