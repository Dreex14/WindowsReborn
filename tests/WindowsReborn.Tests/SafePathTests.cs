using WindowsReborn.Core.Paths;

namespace WindowsReborn.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    [InlineData("folder/nul.txt")]
    [InlineData("folder/name. ")]
    [InlineData("")]
    public void Rejects_unsafe_paths(string relative)
    {
        using var t = new TempDir();
        Assert.Throws<UnsafePathException>(() => SafePath.Combine(t.Root, relative));
    }

    [Theory]
    [InlineData("a/b/c.txt")]
    [InlineData("Documentos/Informe final (v2).docx")]
    [InlineData("日本語/ファイル.txt")]
    [InlineData("emoji 😀/archivo ñ.txt")]
    [InlineData("..hidden/file..txt")]
    public void Accepts_normal_paths_and_keeps_them_inside_root(string relative)
    {
        using var t = new TempDir();
        var full = SafePath.Combine(t.Root, relative);
        Assert.StartsWith(t.Root + Path.DirectorySeparatorChar, full);
    }

    [Fact]
    public void Prefix_sibling_is_not_inside()
    {
        Assert.False(SafePath.IsSameOrInside(@"C:\Data2\x", @"C:\Data"));
        Assert.True(SafePath.IsSameOrInside(@"C:\Data\x", @"C:\Data"));
        Assert.True(SafePath.IsSameOrInside(@"C:\Data", @"C:\Data\"));
    }
}
