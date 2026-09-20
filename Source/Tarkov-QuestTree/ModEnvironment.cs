using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace QuestTree
{
    /// <summary>Same detection QuickSell uses: a Fika headless client has no player and no UI, so
    /// there is no trader dialog to add a tab to and nothing for this mod to do there.</summary>
    internal static class ModEnvironment
    {
        private static bool? _isHeadless;

        private static bool? _isFika;

        /// <summary>Fika's "is this raid a solo one" property, looked up once. Null with
        /// <see cref="_soloLooked"/> set means Fika is loaded and the property could not be found at
        /// all, which is the case a caller must treat as "unknown" rather than as "solo".</summary>
        private static PropertyInfo _soloProperty;

        private static bool _soloLooked;

        public static bool IsHeadlessClient
        {
            get
            {
                if (_isHeadless.HasValue) return _isHeadless.Value;

                // Fika's own plugin family, not any GUID with the word in it: another mod
                // mentioning "headless" in its id used to switch this UI off on a normal client.
                _isHeadless = Chainloader.PluginInfos.Keys.Any(guid =>
                    guid.StartsWith("com.fika.", StringComparison.OrdinalIgnoreCase) &&
                    guid.IndexOf("headless", StringComparison.OrdinalIgnoreCase) >= 0);

                return _isHeadless.Value;
            }
        }

        /// <summary>Whether Fika - the co-op mod - is loaded at all, headless or not. A plain SPT
        /// install answers false, and everything that would disturb another player is then free to
        /// run.</summary>
        public static bool IsFikaLoaded
        {
            get
            {
                if (_isFika.HasValue) return _isFika.Value;

                // The same family test IsHeadlessClient uses, without the headless part: the whole of
                // Fika ships under com.fika.*, and a mod that merely mentions Fika in its id is not
                // Fika.
                _isFika = Chainloader.PluginInfos.Keys.Any(guid =>
                    guid.StartsWith("com.fika.", StringComparison.OrdinalIgnoreCase));

                return _isFika.Value;
            }
        }

        /// <summary>Whether the raid in progress is one nobody else is in - the question anything that
        /// moves the player without telling the network has to ask.
        ///
        ///   - TRUE on a plain SPT install, where every raid is solo, and on a Fika client that says
        ///     this raid is a single-player one;
        ///   - FALSE when Fika says there are other players in it;
        ///   - NULL when Fika is loaded but its own answer could not be read (a Fika version that
        ///     renamed the property, a plugin that failed to instantiate). A caller must treat null as
        ///     "not solo", because the cost of being wrong is somebody else's raid.
        ///
        /// Read through reflection rather than a reference to Fika: this mod must build and run
        /// without it. Not cached - it is a property of the RAID, and the same client plays a solo one
        /// and a co-op one in the same session.</summary>
        public static bool? RaidIsSolo
        {
            get
            {
                try
                {
                    if (!IsFikaLoaded) return true;

                    var property = SoloProperty();
                    if (property == null) return null;

                    return property.GetValue(null, null) as bool?;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Fika's static "this raid is a single-player one" property, or null when it cannot be
        /// found. Looked up once, including the failure: a miss means every later read is a miss too,
        /// and reflecting over an assembly's types every frame is not free.</summary>
        private static PropertyInfo SoloProperty()
        {
            if (_soloLooked) return _soloProperty;
            _soloLooked = true;

            try
            {
                var assembly = FikaAssembly();
                if (assembly == null)
                {
                    Miss("its own assembly could not be reached through the loaded plugin");
                    return null;
                }

                // The type's namespace moved between Fika versions (Coop.Utils, then Main.Utils), so
                // it is found by NAME; the property is the one Fika's own code tests before it treats a
                // raid as networked.
                var type = assembly.GetType("Fika.Core.Main.Utils.FikaBackendUtils", throwOnError: false) ??
                           assembly.GetType("Fika.Core.Coop.Utils.FikaBackendUtils", throwOnError: false) ??
                           FikaBackendUtilsType(assembly);

                if (type == null)
                {
                    Miss("it has no FikaBackendUtils type");
                    return null;
                }

                var solo = type.GetProperty(
                    "IsSinglePlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (solo == null || solo.PropertyType != typeof(bool))
                {
                    Miss($"{type.FullName} has no static bool IsSinglePlayer");
                    return null;
                }

                var getter = solo.GetGetMethod(nonPublic: true);
                if (getter == null || !getter.IsStatic)
                {
                    Miss($"{type.FullName}.IsSinglePlayer cannot be read without an instance");
                    return null;
                }

                _soloProperty = solo;
                return _soloProperty;
            }
            catch (Exception ex)
            {
                Miss($"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Says, once, why Fika's solo-raid flag could not be read. At Debug, because the
        /// feature that cares says so at Info when it refuses, and because a plain SPT install never
        /// reaches here at all.</summary>
        /// <param name="why">What was missing, as it goes in the line.</param>
        private static void Miss(string why) =>
            Plugin.LogSource?.LogDebug(
                $"QuestTree: Fika is loaded but its solo-raid flag could not be read ({why}) - anything that " +
                "would disturb another player refuses to run.");

        /// <summary>Fika's own assembly, through the loaded plugin rather than a file path.</summary>
        private static Assembly FikaAssembly()
        {
            foreach (var plugin in Chainloader.PluginInfos)
            {
                if (plugin.Key == null ||
                    !plugin.Key.StartsWith("com.fika.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instance = plugin.Value?.Instance;
                if (instance != null) return instance.GetType().Assembly;
            }

            return null;
        }

        /// <summary>The last resort when the two known namespaces both miss: the one type in Fika's
        /// assembly called FikaBackendUtils. A type load failure here is a miss, not a throw.</summary>
        private static Type FikaBackendUtilsType(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().FirstOrDefault(t =>
                    string.Equals(t.Name, "FikaBackendUtils", StringComparison.Ordinal));
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types?.FirstOrDefault(t =>
                    t != null && string.Equals(t.Name, "FikaBackendUtils", StringComparison.Ordinal));
            }
        }
    }
}
