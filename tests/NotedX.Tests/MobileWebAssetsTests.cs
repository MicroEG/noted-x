using Microsoft.Extensions.FileProviders;
using NotedX.MobileWeb;

namespace NotedX.Tests;

public sealed class MobileWebAssetsTests
{
    [Fact]
    public void Embedded_manifest_exposes_the_mobile_entry_point()
    {
        var provider = new ManifestEmbeddedFileProvider(MobileWebAssets.Assembly);

        var index = provider.GetFileInfo("wwwroot/index.html");

        Assert.True(index.Exists, "wwwroot/index.html should be embedded in NotedX.MobileWeb.");
        Assert.True(index.Length > 0);
    }
}
