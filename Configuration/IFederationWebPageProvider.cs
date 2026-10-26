using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>Additional dashboard pages supplied by optional Federation add-ins.</summary>
    public interface IFederationWebPageProvider
    {
        IEnumerable<PluginPageInfo> GetPages();
    }

    internal static class FederationWebPageDiscovery
    {
        public static IEnumerable<IFederationWebPageProvider> CreateProviders(ILogger logger)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var type in types.Where(t => t.IsClass && !t.IsAbstract && typeof(IFederationWebPageProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    IFederationWebPageProvider? provider = null;
                    try
                    {
                        provider = Activator.CreateInstance(type) as IFederationWebPageProvider;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[Federation] Could not create optional dashboard page provider {ProviderType}", type.FullName);
                    }

                    if (provider != null)
                    {
                        yield return provider;
                    }
                }
            }
        }
    }
}
