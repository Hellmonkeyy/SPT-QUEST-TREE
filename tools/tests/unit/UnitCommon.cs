// The unit harness' runner: every type named Tests in a UnitTests.<Suite> namespace (nested or not) with a static
// Run(T) is run; each case prints one line "RESULT<TAB>suite<TAB>case<TAB>PASS|FAIL|SKIP<TAB>detail" that
// tools/tests/unit/run_unit.py reads. A case that asserts nothing FAILS, so a test cannot pass by doing nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnitTests
{
    internal sealed class T
    {
        private readonly string _suite;
        private readonly List<string> _failures = new List<string>();
        private int _asserts;

        public int Cases;

        public T(string suite) => _suite = suite;

        public void Case(string name, Action body)
        {
            Cases++;
            _failures.Clear();
            _asserts = 0;

            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
            }

            if (_failures.Count == 0 && _asserts == 0) _failures.Add("no assertion ran");

            Emit(name, _failures.Count == 0 ? "PASS" : "FAIL", _failures.Count == 0 ? $"{_asserts} check(s)" : string.Join("; ", _failures));
        }

        public void Skip(string name, string why)
        {
            Cases++;
            Emit(name, "SKIP", why);
        }

        public void True(bool condition, string what)
        {
            _asserts++;
            if (!condition) _failures.Add(what);
        }

        public void Eq<TValue>(TValue expected, TValue actual, string what)
        {
            _asserts++;
            if (!EqualityComparer<TValue>.Default.Equals(expected, actual))
                _failures.Add($"{what}: expected {Show(expected)}, got {Show(actual)}");
        }

        private static string Show(object value) =>
            value == null ? "null" : value is float f ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : value.ToString();

        public void Emit(string name, string status, string detail) =>
            Console.WriteLine("RESULT\t" + _suite + "\t" + Clean(name) + "\t" + status + "\t" + Clean(detail));

        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    internal static class Program
    {
        private static int Main()
        {
            var suites = typeof(T).Assembly.GetTypes()
                .Where(t => t.Name == "Tests" && t.Namespace != null && t.Namespace.StartsWith("UnitTests.", StringComparison.Ordinal))
                .OrderBy(t => t.Namespace, StringComparer.Ordinal);

            foreach (var type in suites)
            {
                var t = new T(type.Namespace.Substring("UnitTests.".Length));
                var run = type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(T) }, null);

                if (run == null)
                {
                    t.Emit("(suite)", "FAIL", "no static Run(T) in " + type.FullName);
                    continue;
                }

                try
                {
                    run.Invoke(null, new object[] { t });
                }
                catch (TargetInvocationException ex)
                {
                    t.Emit("(suite)", "FAIL", "Run threw " + ex.InnerException);
                }

                if (t.Cases == 0) t.Emit("(suite)", "FAIL", "no case ran");
            }

            return 0;
        }
    }
}
