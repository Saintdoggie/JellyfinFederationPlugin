using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Federation.Configuration
{
    internal static class ConfigurationUpdate
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        internal static PluginConfiguration Merge(JsonObject submitted, PluginConfiguration? existing)
        {
            var merged = existing == null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(existing)!.AsObject();
            foreach (var property in submitted)
            {
                if (property.Value == null
                    && (property.Key.Equals("RemoteServers", System.StringComparison.OrdinalIgnoreCase)
                        || property.Key.Equals("LibraryMappings", System.StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                merged[property.Key] = property.Value?.DeepClone();
            }

            return merged.Deserialize<PluginConfiguration>(SerializerOptions) ?? new PluginConfiguration();
        }
    }
}
