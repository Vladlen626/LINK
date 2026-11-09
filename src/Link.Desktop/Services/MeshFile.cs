using System.Globalization;
using System.Windows.Media.Media3D;

namespace Link.Desktop.Services;

/// <summary>A model's triangles: its points, and three indices into them for each triangle.</summary>
public sealed record MeshData(IReadOnlyList<Point3D> Positions, IReadOnlyList<int> Triangles);

/// <summary>
/// Reads the 3D files agents and artists hand around most, OBJ and STL (text or binary), into triangles for the viewer.
/// Only the shape: materials and textures are left out.
/// </summary>
public static class MeshFile
{
    public static readonly string[] Extensions = [".obj", ".stl"];

    // A model larger than this would take the window's memory for a look: the viewer says so instead.
    private const long MaxBytes = 200L * 1024 * 1024;

    public static bool CanRead(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The model in the file; null when it has no triangles, is too big or is not a model at all.</summary>
    public static MeshData? Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxBytes) return null;
        var mesh = string.Equals(file.Extension, ".obj", StringComparison.OrdinalIgnoreCase)
            ? ReadObj(File.ReadLines(path))
            : ReadStl(File.ReadAllBytes(path));
        return mesh.Triangles.Count > 0 ? mesh : null;
    }

    /// <summary>OBJ's "v x y z" points and "f a b c …" faces, a polygon cut into a fan; negative indices count from the end.</summary>
    public static MeshData ReadObj(IEnumerable<string> lines)
    {
        var positions = new List<Point3D>();
        var triangles = new List<int>();
        foreach (var raw in lines)
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (parts[0] == "v" && Number(parts[1]) is { } x && Number(parts[2]) is { } y && Number(parts[3]) is { } z)
            {
                positions.Add(new Point3D(x, y, z));
            }
            else if (parts[0] == "f")
            {
                var corners = parts.Skip(1).Select(corner => Corner(corner, positions.Count)).ToArray();
                if (corners.Any(corner => corner is null)) continue;
                for (var index = 2; index < corners.Length; index++) triangles.AddRange([corners[0]!.Value, corners[index - 1]!.Value, corners[index]!.Value]);
            }
        }

        return new MeshData(positions, triangles);
    }

    /// <summary>STL as its two forms: "solid … facet … vertex x y z" text, or an 80-byte header, a count and 50 bytes a triangle.</summary>
    public static MeshData ReadStl(byte[] bytes)
    {
        var count = bytes.Length >= 84 ? BitConverter.ToUInt32(bytes, 80) : 0;
        // A text STL starts with "solid" too, so the binary form is known by its exact length.
        if (count > 0 && 84 + count * 50L == bytes.Length) return ReadBinaryStl(bytes, (int)count);

        var positions = new List<Point3D>();
        foreach (var raw in System.Text.Encoding.ASCII.GetString(bytes).Split('\n'))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["vertex", var x, var y, var z, ..] && Number(x) is { } px && Number(y) is { } py && Number(z) is { } pz)
            {
                positions.Add(new Point3D(px, py, pz));
            }
        }

        positions.RemoveRange(positions.Count - positions.Count % 3, positions.Count % 3);
        return new MeshData(positions, Enumerable.Range(0, positions.Count).ToArray());
    }

    private static MeshData ReadBinaryStl(byte[] bytes, int count)
    {
        var positions = new List<Point3D>(count * 3);
        for (var triangle = 0; triangle < count; triangle++)
        {
            // Each triangle: its normal (12 bytes), three corners of three floats, two bytes of attributes.
            var start = 84 + triangle * 50 + 12;
            for (var corner = 0; corner < 3; corner++)
            {
                var at = start + corner * 12;
                positions.Add(new Point3D(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8)));
            }
        }

        return new MeshData(positions, Enumerable.Range(0, positions.Count).ToArray());
    }

    // "7", "7/2" or "7/2/5": the point's index is the first number, from 1.
    private static int? Corner(string corner, int count)
    {
        if (!int.TryParse(corner.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0) return null;
        var zeroBased = index > 0 ? index - 1 : count + index;
        return zeroBased >= 0 && zeroBased < count ? zeroBased : null;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}
