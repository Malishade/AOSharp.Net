using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.ObjectModel;

namespace AOSharp
{
    public class Config
    {
        public const string AoSharpSdkKey = "aosharp-sdk-default";
        public const string AoSharpSdkRepoUrl = "https://github.com/Malishade/AOSharp.SDK";

        /// <summary>The SDK this loader used before; its entries are dropped on load so the current one replaces it.</summary>
        private const string ReplacedSdkRepoUrl = "https://github.com/aosharp/AOSharp.SDK";
        public const string DefaultLoadoutId = "00000000-0000-0000-0000-000000000001";
        public const string DefaultLoadoutName = "Default";

        public ObservableDictionary<string, PluginModel> Plugins { get; set; }

        public ObservableCollection<Profile> Profiles { get; set; }

        public ObservableCollection<Loadout> Loadouts { get; set; }

        public bool AutoInject { get; set; }

        protected string _path;

        public static Config Load(string path)
        {
            Config config;

            if (File.Exists(path))
            {
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path));
            }
            else
            {
                config = new Config()
                {
                    Plugins = new ObservableDictionary<string, PluginModel>(),
                    Profiles = new ObservableCollection<Profile>(),
                    Loadouts = new ObservableCollection<Loadout>(),
                    AutoInject = false
                };
            }

            if (config.Plugins == null)
                config.Plugins = new ObservableDictionary<string, PluginModel>();
            if (config.Profiles == null)
                config.Profiles = new ObservableCollection<Profile>();

            config._path = path;
            config.EnsureDefaults();
            config.EnsureLoadouts();

            return config;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// Ensures built-in defaults (AOSharp.SDK) are present and marked correctly.
        /// Works whether the SDK exists as a single stub or has been expanded to per-project entries.
        /// </summary>
        public void EnsureDefaultsPublic() => EnsureDefaults();

        public Loadout GetDefaultLoadout() =>
            Loadouts.First(l => l.Id == DefaultLoadoutId);

        private void EnsureLoadouts()
        {
            if (Loadouts == null)
                Loadouts = new ObservableCollection<Loadout>();

            if (!Loadouts.Any(l => l.Id == DefaultLoadoutId))
            {
                Loadouts.Insert(0, new Loadout
                {
                    Id = DefaultLoadoutId,
                    Name = DefaultLoadoutName,
                    PluginKeys = new List<string>()
                });
            }

            foreach (var profile in Profiles ?? Enumerable.Empty<Profile>())
            {
                if (string.IsNullOrEmpty(profile.LoadoutId))
                    profile.LoadoutId = DefaultLoadoutId;
            }
        }

        private void EnsureDefaults()
        {
            DropReplacedSdk();

            var sdkEntries = Plugins
                .Where(kvp => kvp.Value.RepoUrl == AoSharpSdkRepoUrl)
                .ToList();

            if (sdkEntries.Any())
            {
                foreach (var kvp in sdkEntries)
                    kvp.Value.IsDefault = true;
            }
            else
            {
                Plugins.Add(AoSharpSdkKey, new PluginModel
                {
                    Name = "AOSharp.SDK",
                    PluginType = PluginType.Repo,
                    RepoUrl = AoSharpSdkRepoUrl,
                    IsLibrary = true,
                    Section = Models.PluginSections.Library,
                    AutoUpdate = true,
                    IsDefault = true
                });
            }
        }

        private void DropReplacedSdk()
        {
            var keys = Plugins
                .Where(kvp => string.Equals(kvp.Value.RepoUrl, ReplacedSdkRepoUrl, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keys)
            {
                Plugins.Remove(key);
                foreach (var loadout in Loadouts ?? Enumerable.Empty<Loadout>())
                    loadout.PluginKeys?.Remove(key);
            }
        }

        /// <summary>
        /// Returns (packageId, dllPath) for every compiled <see cref="PluginModel.IsLibrary"/> plugin in the loader config.
        /// RepoCompiler merges these into reference injection so AOSharp SDK and other libraries always resolve from local builds (same idea as NuGet package substitution).
        /// </summary>
        public IEnumerable<(string packageId, string dllPath)> GetCompiledLibraryPaths()
        {
            foreach (var kvp in Plugins)
            {
                var plugin = kvp.Value;
                if (plugin.IsLibrary && plugin.IsCompiled)
                {
                    yield return (System.IO.Path.GetFileNameWithoutExtension(plugin.Path), plugin.Path);
                }
            }
        }
    }
}
