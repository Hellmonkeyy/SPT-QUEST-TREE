using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace QuestTree.UI
{
    /// <summary>
    /// The 3D map's floor, remembered per map across game sessions (slice slider S4, the owner's decision): the floor a map
    /// last showed in 3D, in a small JSON file beside the plugin (viewstate.json), with a signature of the map's floors so a
    /// recapture that changes them falls back to the default. NOT a ConfigEntry: every config change bumps
    /// ModSettings.Generation, which is in the viewport key and would rebuild the viewport.
    ///
    /// Main thread for every call; the file is written on a worker from a snapshot taken here (temp file, then moved over
    /// the old one), so a commit never blocks a frame on the disk. Read once, lazily. A missing, empty or corrupt file is "no
    /// memory" (a corrupt one is renamed aside as .bad-(time), never deleted); a file of a newer format is left untouched and not
    /// written over. Nothing here throws into the UI: failures are logged once, at Warning.
    /// </summary>
    internal static class SliceMemory
    {
        // BEGIN TESTABLE SliceMemory - tools/tests/unit/run_unit.py compiles this region on its own: no Unity type.
        /// <summary>One map's remembered 3D floor.</summary>
        internal sealed class Remembered
        {
            /// <summary>The floor (layer level) the map showed.</summary>
            public int Level;

            /// <summary>The slice height on that floor, metres; NaN for its home (phase 1a: always the home).</summary>
            public float Y = float.NaN;

            /// <summary><see cref="Signature"/> of the map's floors when it was remembered.</summary>
            public string Signature = "";
        }

        /// <summary>The file format written; a file with a higher one is a newer mod's, read as no memory and never
        /// written over.</summary>
        internal const int FormatVersion = 1;

        /// <summary>How far apart two floor heights may be and still sign the same, metres: they are rounded to it.</summary>
        internal const float SignatureMetres = 0.1f;

        /// <summary>
        /// The map's floors as one string: per layer level, lowest first (the first layer of a level, as LayerOf finds it),
        /// its height band rounded to <see cref="SignatureMetres"/> - "?" for a band that is no real height (NaN, or outside
        /// +-1000 m: the catalog's placeholder). A recapture that adds, drops or moves a floor signs differently.
        /// </summary>
        internal static string Signature(IReadOnlyList<(int Level, float MinY, float MaxY)> layers)
        {
            var seen = new List<int>();
            var floors = new List<(int Level, float MinY, float MaxY)>();

            for (var i = 0; i < layers.Count; i++)
            {
                if (seen.Contains(layers[i].Level)) continue;
                seen.Add(layers[i].Level);
                floors.Add(layers[i]);
            }

            floors.Sort((a, b) => a.Level.CompareTo(b.Level));

            var text = new StringBuilder();
            text.Append("n=").Append(floors.Count.ToString(CultureInfo.InvariantCulture));

            foreach (var floor in floors)
            {
                text.Append(';').Append(floor.Level.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(Height(floor.MinY))
                    .Append(':').Append(Height(floor.MaxY));
            }

            return text.ToString();
        }

        private static string Height(float metres)
        {
            if (float.IsNaN(metres) || float.IsInfinity(metres) || metres <= -1000f || metres >= 1000f) return "?";

            var steps = Math.Round(metres / SignatureMetres, MidpointRounding.AwayFromZero);
            return (steps * SignatureMetres).ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The floor a map's 3D view opens on when no floor was chosen for it this session: the remembered one when its
        /// signature is the map's current one and the floor still exists and can be drawn; else the TOP floor that can be
        /// drawn (the "no cut" stop - the owner's default). Null for a map with no drawable floor (the caller leaves the
        /// floor as it is). <paramref name="drawable"/>: the floor's picture can be drawn (artwork, not failed).
        /// </summary>
        internal static int? OpeningLevel(IReadOnlyList<(int Level, float MinY, float MaxY)> layers, Remembered remembered,
            Func<int, bool> drawable)
        {
            if (layers == null || layers.Count == 0) return null;

            var top = int.MinValue;
            var any = false;
            var has = false;

            for (var i = 0; i < layers.Count; i++)
            {
                if (!drawable(layers[i].Level)) continue;

                if (!any || layers[i].Level > top) top = layers[i].Level;
                any = true;

                if (remembered != null && layers[i].Level == remembered.Level) has = true;
            }

            if (remembered != null && has && remembered.Signature == Signature(layers)) return remembered.Level;

            return any ? top : (int?)null;
        }

        /// <summary>The file's text: {"version":1,"maps":{"key":{"level":1,"y":null,"signature":"..."}}}. A home (NaN)
        /// height is null - JSON has no NaN.</summary>
        internal static string Encode(IDictionary<string, Remembered> maps)
        {
            var text = new StringBuilder();
            text.Append("{\n  \"version\": ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n  \"maps\": {");

            var first = true;
            var keys = new List<string>(maps.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var entry = maps[key];
                if (entry == null) continue;

                text.Append(first ? "\n    " : ",\n    ");
                first = false;

                Quote(text, key);
                text.Append(": { \"level\": ").Append(entry.Level.ToString(CultureInfo.InvariantCulture)).Append(", \"y\": ");
                text.Append(float.IsNaN(entry.Y) || float.IsInfinity(entry.Y)
                    ? "null"
                    : entry.Y.ToString("R", CultureInfo.InvariantCulture));
                text.Append(", \"signature\": ");
                Quote(text, entry.Signature ?? "");
                text.Append(" }");
            }

            text.Append(first ? "}\n}\n" : "\n  }\n}\n");
            return text.ToString();
        }

        private static void Quote(StringBuilder text, string value)
        {
            text.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }

            text.Append('"');
        }

        /// <summary>
        /// Reads the file's text. False when it is not this format at all (not JSON, not an object, no numeric version):
        /// the caller sets the file aside. <paramref name="newer"/> when its version is above <see cref="FormatVersion"/>:
        /// no memory, and the file must not be written over. Unknown fields are ignored; an entry whose fields are missing
        /// or of the wrong type is skipped, the rest kept.
        /// </summary>
        internal static bool TryDecode(string text, out Dictionary<string, Remembered> maps, out bool newer)
        {
            maps = new Dictionary<string, Remembered>(StringComparer.Ordinal);
            newer = false;

            object root;
            try
            {
                var at = 0;
                root = ParseValue(text ?? "", ref at, 0);
                SkipSpace(text ?? "", ref at);
                if (at != (text ?? "").Length) return false;
            }
            catch (FormatException)
            {
                return false;
            }

            if (!(root is Dictionary<string, object> top)) return false;
            if (!top.TryGetValue("version", out var version) || !(version is double number)) return false;

            if (number > FormatVersion)
            {
                newer = true;
                return true;
            }

            if (!top.TryGetValue("maps", out var all) || !(all is Dictionary<string, object> entries)) return true;

            foreach (var pair in entries)
            {
                if (!(pair.Value is Dictionary<string, object> fields)) continue;
                if (!fields.TryGetValue("level", out var level) || !(level is double levelNumber)) continue;
                if (levelNumber != Math.Floor(levelNumber) || Math.Abs(levelNumber) > 1000d) continue;
                if (!fields.TryGetValue("signature", out var signature) || !(signature is string signed)) continue;

                var y = float.NaN;
                if (fields.TryGetValue("y", out var height) && height is double metres) y = (float)metres;

                maps[pair.Key] = new Remembered { Level = (int)levelNumber, Y = y, Signature = signed };
            }

            return true;
        }

        // A minimal JSON reader for the file above: objects, arrays, strings, numbers, true, false, null.

        private static object ParseValue(string text, ref int at, int depth)
        {
            if (depth > 32) throw new FormatException("nested too deep");

            SkipSpace(text, ref at);
            if (at >= text.Length) throw new FormatException("ends early");

            var c = text[at];

            if (c == '{')
            {
                at++;
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipSpace(text, ref at);

                if (at < text.Length && text[at] == '}')
                {
                    at++;
                    return result;
                }

                while (true)
                {
                    SkipSpace(text, ref at);
                    var key = ParseString(text, ref at);
                    SkipSpace(text, ref at);
                    Expect(text, ref at, ':');
                    result[key] = ParseValue(text, ref at, depth + 1);
                    SkipSpace(text, ref at);

                    if (at < text.Length && text[at] == ',')
                    {
                        at++;
                        continue;
                    }

                    Expect(text, ref at, '}');
                    return result;
                }
            }

            if (c == '[')
            {
                at++;
                var result = new List<object>();
                SkipSpace(text, ref at);

                if (at < text.Length && text[at] == ']')
                {
                    at++;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue(text, ref at, depth + 1));
                    SkipSpace(text, ref at);

                    if (at < text.Length && text[at] == ',')
                    {
                        at++;
                        continue;
                    }

                    Expect(text, ref at, ']');
                    return result;
                }
            }

            if (c == '"') return ParseString(text, ref at);
            if (Word(text, ref at, "true")) return true;
            if (Word(text, ref at, "false")) return false;
            if (Word(text, ref at, "null")) return null;

            var start = at;
            while (at < text.Length && "+-0123456789.eE".IndexOf(text[at]) >= 0) at++;
            if (at == start) throw new FormatException("unexpected character");

            if (!double.TryParse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException("bad number");

            return number;
        }

        private static string ParseString(string text, ref int at)
        {
            Expect(text, ref at, '"');
            var result = new StringBuilder();

            while (true)
            {
                if (at >= text.Length) throw new FormatException("unterminated string");

                var c = text[at++];
                if (c == '"') return result.ToString();

                if (c != '\\')
                {
                    result.Append(c);
                    continue;
                }

                if (at >= text.Length) throw new FormatException("unterminated escape");

                var e = text[at++];
                switch (e)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (at + 4 > text.Length ||
                            !int.TryParse(text.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw new FormatException("bad unicode escape");
                        result.Append((char)code);
                        at += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }
        }

        private static bool Word(string text, ref int at, string word)
        {
            if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0) return false;

            at += word.Length;
            return true;
        }

        private static void Expect(string text, ref int at, char c)
        {
            if (at >= text.Length || text[at] != c) throw new FormatException("expected '" + c + "'");
            at++;
        }

        private static void SkipSpace(string text, ref int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }
        // END TESTABLE SliceMemory

        // --- the file -----------------------------------------------------------------------------------------------

        private static readonly object FileGate = new object();

        private static Dictionary<string, Remembered> _maps;

        /// <summary>The file is a newer format's: read as no memory and never written over.</summary>
        private static bool _readOnly;

        private static bool _warned;
        private static int _sequence;
        private static int _written;

        /// <summary>BepInEx/plugins/QuestTree/viewstate.json - beside the plugin, as the captures and selftest folders are.</summary>
        internal static string FilePath
        {
            get
            {
                var dir = Path.GetDirectoryName(typeof(SliceMemory).Assembly.Location);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "viewstate.json");
            }
        }

        /// <summary>The signature of an entry's floors (<see cref="Signature"/>), from its layers' height bands.</summary>
        internal static string SignatureOf(DynamicMapsLibrary.MapEntry entry) => Signature(FloorsOf(entry));

        /// <summary>An entry's layers as (level, minY, maxY), NaN where a layer has no height band.</summary>
        internal static List<(int Level, float MinY, float MaxY)> FloorsOf(DynamicMapsLibrary.MapEntry entry)
        {
            var floors = new List<(int Level, float MinY, float MaxY)>();
            if (entry == null) return floors;

            foreach (var layer in entry.Layers)
            {
                if (layer == null) continue;

                var has = layer.GameBounds.Count > 0;
                floors.Add((layer.Level, has ? layer.GameBounds[0].Min.z : float.NaN, has ? layer.GameBounds[0].Max.z : float.NaN));
            }

            return floors;
        }

        /// <summary>The map's remembered floor, or null. Main thread.</summary>
        internal static Remembered Recall(string mapKey)
        {
            if (string.IsNullOrEmpty(mapKey)) return null;

            EnsureLoaded();
            return _maps.TryGetValue(mapKey, out var remembered) ? remembered : null;
        }

        /// <summary>Remembers the map's floor (a committed slice) and writes the file on a worker. Main thread.</summary>
        internal static void Remember(string mapKey, int level, float y, string signature)
        {
            if (string.IsNullOrEmpty(mapKey)) return;

            EnsureLoaded();

            if (_maps.TryGetValue(mapKey, out var had) && had.Level == level && had.Signature == signature &&
                (had.Y.Equals(y) || (float.IsNaN(had.Y) && float.IsNaN(y))))
                return;

            _maps[mapKey] = new Remembered { Level = level, Y = y, Signature = signature ?? "" };
            Save();
        }

        /// <summary>Forgets the map's floor (F: back to the default view). Main thread.</summary>
        internal static void Forget(string mapKey)
        {
            if (string.IsNullOrEmpty(mapKey)) return;

            EnsureLoaded();
            if (_maps.Remove(mapKey)) Save();
        }

        private static void EnsureLoaded()
        {
            if (_maps != null) return;

            _maps = new Dictionary<string, Remembered>(StringComparer.Ordinal);

            var path = FilePath;

            try
            {
                if (path == null || !File.Exists(path)) return;

                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return;

                if (!TryDecode(text, out var maps, out var newer))
                {
                    Warn($"the 3D floor memory '{path}' could not be read - it is set aside as .bad-<time> and the maps open on " +
                         "their default floor");
                    SetAside(path);
                    return;
                }

                if (newer)
                {
                    _readOnly = true;
                    Warn($"the 3D floor memory '{path}' is from a newer version of the mod - it is left as it is, and " +
                         "floors are not remembered this session");
                    return;
                }

                _maps = maps;
            }
            catch (Exception ex)
            {
                // Read-only for the session: the file is there but could not be read (locked, say), and writing the one
                // entry this session makes over it would lose every other map's.
                _readOnly = true;
                Warn($"the 3D floor memory could not be read ({ex.GetType().Name}: {ex.Message}) - floors are neither " +
                     "remembered from it nor written to it this session");
            }
        }

        private static void SetAside(string path)
        {
            try
            {
                // never deleted: an earlier one keeps its own name
                var bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (!File.Exists(bad)) File.Move(path, bad);
            }
            catch (Exception ex)
            {
                // left where it is; the next write replaces it with a good file
                Plugin.LogSource?.LogDebug($"QuestTree: could not set the 3D floor memory aside ({ex.Message}).");
            }
        }

        private static void Save()
        {
            if (_readOnly) return;

            var path = FilePath;
            if (path == null) return;

            var text = Encode(_maps);
            var sequence = ++_sequence;

            Task.Run(() => Write(path, text, sequence));
        }

        /// <summary>On a worker: the newest snapshot only, to a temp file and then over the old one.</summary>
        private static void Write(string path, string text, int sequence)
        {
            try
            {
                lock (FileGate)
                {
                    if (sequence <= _written) return;

                    var temp = path + ".tmp";
                    File.WriteAllText(temp, text, new UTF8Encoding(false));

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);

                    _written = sequence;
                }
            }
            catch (Exception ex)
            {
                Warn($"the 3D floor memory could not be written ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static void Warn(string message)
        {
            if (_warned) return;
            _warned = true;

            Plugin.LogSource?.LogWarning("QuestTree: " + message + ".");
        }
    }
}
