using System;
using System.IO;
using System.Linq;
using System.Text;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

internal static class ViewerTests
{
    public static void Run()
    {
        var quad = MeshFile.ReadObj(["# a square", "v 0 0 0", "v 1 0 0", "v 1 1 0", "v 0 1 0", "f 1/1/1 2/2/2 3/3/3 4/4/4"]);
        Program.Equal("obj quad points", quad.Positions.Count, 4);
        Program.Equal("obj quad is two triangles", string.Join(",", quad.Triangles), "0,1,2,0,2,3");
        var relative = MeshFile.ReadObj(["v 0 0 0", "v 1 0 0", "v 0 1 0", "f -3 -2 -1"]);
        Program.Equal("obj negative indices count back", string.Join(",", relative.Triangles), "0,1,2");

        var ascii = Encoding.ASCII.GetBytes(
            "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n");
        var fromAscii = MeshFile.ReadStl(ascii);
        Program.Equal("ascii stl triangle", (fromAscii.Positions.Count, fromAscii.Triangles.Count), (3, 3));
        Program.Equal("ascii stl second point", fromAscii.Positions[1].X, 1.0);

        var fromBinary = MeshFile.ReadStl(BinaryStl(2));
        Program.Equal("binary stl triangles", fromBinary.Triangles.Count / 3, 2);
        Program.Equal("binary stl last point", fromBinary.Positions[^1].Y, 1.0);

        Program.Equal("kind md", FileViewer.KindOf("C:/a/README.md"), ViewKind.Markdown);
        Program.Equal("kind picture ignores case", FileViewer.KindOf("C:/a/shot.PNG"), ViewKind.Picture);
        Program.Equal("kind model", FileViewer.KindOf("C:/a/part.stl"), ViewKind.Model);
        Program.Equal("kind code is text", FileViewer.KindOf("C:/a/Program.cs"), ViewKind.Text);

        const string source = "# Plan\n\nThe **build** must pass.\nThen ship.\n";
        Program.Equal("picked words find their source line", FileComments.LineOf(source, "build must"), (int?)null);
        Program.Equal("picked plain words give the line", FileComments.Picked(source, "Then ship"), "line 4 · “Then ship”");
        Program.Equal("a known line wins", FileComments.Picked(null, "  a\nb ", 7), "line 7 · “a b”");
        Program.Equal("a repeated phrase gives no line", FileComments.LineOf("ab ab", "ab"), (int?)null);
        Program.Equal("a picture point in pixels", FileComments.Point(0.5, 0.25, 1920, 1080), "at 960, 270 px of 1920 × 1080");
        var message = FileComments.Message([
            new FileComment("C:/p/README.md", "line 4 · “Then ship”", "Say how"),
            new FileComment("C:/p/shot.png", string.Empty, "  "),
            new FileComment("C:/p/shot.png", "at 1, 2 px of 3 × 4", "Too dark\nhere")]);
        Program.Equal("comments become the task's words", message,
            "My comments on files, from Link's viewer. Address each one, then say what you changed:\n\n- C:/p/README.md, line 4 · “Then ship”\n  Say how\n\n- C:/p/shot.png, at 1, 2 px of 3 × 4\n  Too dark\n  here");

        var workspace = Path.Combine(Path.GetTempPath(), "link-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            Program.Equal("no memory without a map or notes", ProjectMemory.Files(workspace).Count, 0);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    // A binary STL: 80-byte header, the count, then 50 bytes a triangle (normal, three points, attribute).
    private static byte[] BinaryStl(int count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[80]);
        writer.Write((uint)count);
        foreach (var _ in Enumerable.Range(0, count))
        {
            foreach (var value in new float[] { 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
            writer.Write((ushort)0);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
