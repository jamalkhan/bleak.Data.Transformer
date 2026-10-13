using bleak.Data.Transformer;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class PathUtilTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [TestMethod]
    public void ExpandHome_BareTilde_ReturnsHome()
        => Assert.AreEqual(Home, PathUtil.ExpandHome("~"));

    [TestMethod]
    public void ExpandHome_TildeSlash_JoinsHome()
        => Assert.AreEqual(Path.Combine(Home, "Desktop"), PathUtil.ExpandHome("~/Desktop"));

    [TestMethod]
    public void ExpandHome_AbsoluteAndRelative_Unchanged()
    {
        Assert.AreEqual("/tmp/out", PathUtil.ExpandHome("/tmp/out"));
        Assert.AreEqual("out/sub", PathUtil.ExpandHome("out/sub"));
        Assert.AreEqual("", PathUtil.ExpandHome(""));
    }

    [TestMethod]
    public void Resolve_RelativeIsAnchoredToBaseDirectory()
    {
        var resolved = PathUtil.Resolve("out", "/base/dir");
        Assert.AreEqual(Path.GetFullPath(Path.Combine("/base/dir", "out")), resolved);
    }
}
