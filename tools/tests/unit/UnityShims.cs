// The few UnityEngine members the TESTABLE regions use, re-declared with Unity's own semantics so the regions compile
// outside the game. Nothing else: a region that needs more than this is not pure enough to be marked.
using System;

namespace UnityEngine
{
    internal struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }
    }

    internal struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
    }

    internal struct Bounds
    {
        public Vector3 center;
        public Vector3 size;

        public Bounds(Vector3 center, Vector3 size)
        {
            this.center = center;
            this.size = size;
        }

        public Vector3 min => new Vector3(center.x - size.x * 0.5f, center.y - size.y * 0.5f, center.z - size.z * 0.5f);
        public Vector3 max => new Vector3(center.x + size.x * 0.5f, center.y + size.y * 0.5f, center.z + size.z * 0.5f);
    }

    internal static class Mathf
    {
        // Unity: Epsilon is the smallest denormal unless flush-to-zero is on.
        public static readonly float Epsilon = float.Epsilon;

        // Unity's own bodies (UnityEngine.Mathf, 2022.3).
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Abs(float f) => Math.Abs(f);

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static bool Approximately(float a, float b) =>
            Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
    }
}
