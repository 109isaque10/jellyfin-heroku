using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.Movies;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), _namingOptions);
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            Mock.Of<IDirectoryService>())
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }
}
