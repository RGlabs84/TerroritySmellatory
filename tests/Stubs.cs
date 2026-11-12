using System;
using System.Globalization;
using System.IO;

// Minimal stand-ins for the Unity and Valheim types the tested files touch. Where the arithmetic matters -
// Quaternion.Euler and Quaternion * Vector3 - it is Unity's own formula, not an approximation, because
// "rotates the same way the game does" is exactly what the shape tests are checking.

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", x, y, z);
    }

    public struct Quaternion
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        public static Quaternion operator *(Quaternion l, Quaternion r) => new Quaternion(
            l.w * r.x + l.x * r.w + l.y * r.z - l.z * r.y,
            l.w * r.y + l.y * r.w + l.z * r.x - l.x * r.z,
            l.w * r.z + l.z * r.w + l.x * r.y - l.y * r.x,
            l.w * r.w - l.x * r.x - l.y * r.y - l.z * r.z);

        /// <summary>Unity's convention: degrees, applied Z then X then Y (q = qY * qX * qZ).</summary>
        public static Quaternion Euler(float ex, float ey, float ez)
        {
            double hx = ex * Math.PI / 360.0, hy = ey * Math.PI / 360.0, hz = ez * Math.PI / 360.0;
            var qx = new Quaternion((float)Math.Sin(hx), 0f, 0f, (float)Math.Cos(hx));
            var qy = new Quaternion(0f, (float)Math.Sin(hy), 0f, (float)Math.Cos(hy));
            var qz = new Quaternion(0f, 0f, (float)Math.Sin(hz), (float)Math.Cos(hz));
            return qy * qx * qz;
        }

        /// <summary>UnityEngine.Quaternion.operator*(Quaternion, Vector3), verbatim.</summary>
        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float num = rotation.x * 2f, num2 = rotation.y * 2f, num3 = rotation.z * 2f;
            float num4 = rotation.x * num, num5 = rotation.y * num2, num6 = rotation.z * num3;
            float num7 = rotation.x * num2, num8 = rotation.x * num3, num9 = rotation.y * num3;
            float num10 = rotation.w * num, num11 = rotation.w * num2, num12 = rotation.w * num3;
            Vector3 result;
            result.x = (1f - (num5 + num6)) * point.x + (num7 - num12) * point.y + (num8 + num11) * point.z;
            result.y = (num7 + num12) * point.x + (1f - (num4 + num6)) * point.y + (num9 - num10) * point.z;
            result.z = (num8 - num11) * point.x + (num9 + num10) * point.y + (1f - (num4 + num5)) * point.z;
            return result;
        }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Epsilon = 1.401298E-45f;

        public static float Max(float a, float b) => a > b ? a : b;
        public static float Max(float a, float b, float c) => Max(Max(a, b), c);
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Abs(float v) => Math.Abs(v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public static float Floor(float v) => (float)Math.Floor(v);
        public static float Round(float v) => (float)Math.Round(v, MidpointRounding.ToEven);
        public static int RoundToInt(float v) => (int)Math.Round(v, MidpointRounding.ToEven);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);

        public static bool Approximately(float a, float b) =>
            Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
    }

    public struct Color
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static implicit operator Color32(Color c) => new Color32(
            (byte)Math.Round(Mathf.Clamp01(c.r) * 255f), (byte)Math.Round(Mathf.Clamp01(c.g) * 255f),
            (byte)Math.Round(Mathf.Clamp01(c.b) * 255f), (byte)Math.Round(Mathf.Clamp01(c.a) * 255f));
    }

    public struct Color32
    {
        public byte r, g, b, a;

        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static implicit operator Color(Color32 c) => new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    }

    public static class ColorUtility
    {
        public static bool TryParseHtmlString(string s, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(s) || s[0] != '#' || (s.Length != 7 && s.Length != 9)) return false;
            if (!uint.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            if (s.Length == 7) v = (v << 8) | 0xFF;
            color = new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
            return true;
        }
    }
}

/// <summary>Valheim's ZPackage, reduced to the members the tested files use - same BinaryWriter/Reader layout.</summary>
public class ZPackage
{
    private readonly MemoryStream m_stream = new MemoryStream();
    private readonly BinaryWriter m_writer;
    private readonly BinaryReader m_reader;

    public ZPackage()
    {
        m_writer = new BinaryWriter(m_stream);
        m_reader = new BinaryReader(m_stream);
    }

    public ZPackage(byte[] data) : this()
    {
        m_stream.Write(data, 0, data.Length);
        m_stream.Position = 0L;
    }

    public void Write(ZPackage pkg)
    {
        byte[] array = pkg.GetArray();
        m_writer.Write(array.Length);
        m_writer.Write(array);
    }

    /// <summary>As the game's: the length, then the bytes.</summary>
    public void Write(byte[] array)
    {
        m_writer.Write(array.Length);
        m_writer.Write(array);
    }

    public void Write(bool v) => m_writer.Write(v);
    public void Write(int v) => m_writer.Write(v);
    public void Write(long v) => m_writer.Write(v);
    public void Write(float v) => m_writer.Write(v);
    public void Write(string v) => m_writer.Write(v);

    public bool ReadBool() => m_reader.ReadBoolean();
    public int ReadInt() => m_reader.ReadInt32();
    public long ReadLong() => m_reader.ReadInt64();
    public float ReadSingle() => m_reader.ReadSingle();
    public string ReadString() => m_reader.ReadString();

    public byte[] ReadByteArray()
    {
        int count = m_reader.ReadInt32();
        return m_reader.ReadBytes(count);
    }

    public int Size()
    {
        m_writer.Flush();
        m_stream.Flush();
        return (int)m_stream.Length;
    }

    public ZPackage ReadPackage()
    {
        int count = m_reader.ReadInt32();
        return new ZPackage(m_reader.ReadBytes(count));
    }

    public byte[] GetArray()
    {
        m_writer.Flush();
        m_stream.Flush();
        return m_stream.ToArray();
    }

    /// <summary>What crossing the wire does to a package: the receiver builds it from bytes, positioned at 0.</summary>
    public ZPackage Transmit() => new ZPackage(GetArray());
}

namespace TerroritySmellatory
{
    internal static class Plugin
    {
        internal static readonly StubLog Log = new StubLog();
    }

    internal sealed class StubLog
    {
        public void LogError(object o) => Console.Error.WriteLine("[error] " + o);
        public void LogWarning(object o) => Console.Error.WriteLine("[warn] " + o);
        public void LogInfo(object o) => Console.WriteLine("[info] " + o);
    }
}
