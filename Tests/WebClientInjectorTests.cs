using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class WebClientInjectorTests
{
    [Fact]
    public async Task AtomicWrite_ReplacesWholeFileWithoutTruncatingTheOriginal()
    {
        var directory = Directory.CreateTempSubdirectory("federation-injection-");
        try
        {
            var path = Path.Combine(directory.FullName, "index.html");
            await File.WriteAllTextAsync(path, "<html>original</html>");
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                WebClientInjector.WriteAllTextAtomic(path, "<html>complete</html>"))));
            Assert.Equal("<html>complete</html>", await File.ReadAllTextAsync(path));
            Assert.Equal("index.html", Assert.Single(Directory.GetFiles(directory.FullName)).Split(Path.DirectorySeparatorChar)[^1]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
