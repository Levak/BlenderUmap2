﻿using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace BlenderUmap {
    public class MyFileProvider : DefaultFileProvider {
        public static readonly DirectoryInfo JSONS_FOLDER = new("jsons");
        private readonly Cache _cache;
        private readonly bool _bDumpAssets;

        public MyFileProvider(string folder, VersionContainer version, List<EncryptionKey> encryptionKeys, bool bDumpAssets, int cacheSize) : base(folder, SearchOption.AllDirectories, version, StringComparer.InvariantCultureIgnoreCase) {
            _cache = new Cache(cacheSize);
            _bDumpAssets = bDumpAssets;

            Initialize();
            var keysToSubmit = new Dictionary<FGuid, FAesKey>();
            foreach (var entry in encryptionKeys) {
                if (!string.IsNullOrEmpty(entry.FileName)) {
                    var foundGuid = UnloadedVfs.FirstOrDefault(it => it.Name == entry.FileName);

                    if (foundGuid != null) {
                        keysToSubmit[foundGuid.EncryptionKeyGuid] = new FAesKey(entry.Key);
                    } else {
                        Log.Warning("PAK file not found: {0}", entry.FileName);
                    }
                } else {
                    keysToSubmit[entry.Guid] = new FAesKey(entry.Key);
                }
            }

            var mounted = SubmitKeys(keysToSubmit);
            Log.Information("Successfully mounted {0} containers", mounted);
        }

        //public override bool TryLoadPackage(string path, [MaybeNullWhen(false)] out IPackage package)
        //{
        //    if (_cache.TryGet(path, out package))
        //        return true;
        //    else {
        //        if (base.TryLoadPackage(path, out package)) {
        //            if (_cache.Size != 0)
        //                _cache.Add(path, package);
        //            if (_bDumpAssets)
        //                DumpJson(package);
        //            return true;
        //        }
        //    }
        //    return false;
        //}
        
        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //public override bool TryLoadObject(string? objectPath, out UObject export)
        //{
        //    export = TryLoadObjectAsync(objectPath).Result;
        //    return export != null;
        //}
        //
        //public override async Task<IPackage?> TryLoadPackageAsync(string path)
        //{
        //    if (!TryFindGameFile(path, out var file))
        //    {
        //        return null;
        //    }
        //
        //    if (TryLoadPackage(path, out var package)) {
        //        return package;
        //    }
        //
        //    return await TryLoadPackageAsync(file).ConfigureAwait(false);
        //}

        public void DumpJson(IPackage package) {
            var output = new FileInfo(Path.Combine(Program.GetExportDir(package).ToString(), package.Name.SubstringAfterLast("/") + ".json"));
            // if (output.Exists && output.Length > 0)
            //     return;
            using var writer = new StreamWriter(output.FullName);
            writer.Write(JsonConvert.SerializeObject(package.GetExports(), Formatting.Indented));
        }

        // WARNING: This does convert FortniteGame/Plugins/GameFeatures/GameFeatureName/Content/Package into /GameFeatureName/Package
        public string CompactFilePath(string path) {
            if (path[0] == '/') {
                return path;
            }

            if (path.StartsWith("Engine/Content")) { // -> /Engine
                return "/Engine" + path["Engine/Content".Length..];
            }

            if (path.StartsWith("Engine/Plugins")) { // -> /Plugins
                return path["Engine".Length..];
            }

            var delim = path.IndexOf("/Content/", StringComparison.Ordinal);
            if (delim == -1) {
                return path;
            }

            // GameName/Content -> /Game
            return "/Game" + path[(delim + "/Content".Length)..];
        }
    }

    public class Cache {
        public readonly int Size = 100;
        private readonly ConcurrentDictionary<string, IPackage> _cache;

        public Cache(int size) {
            Size = size;
            _cache = new ConcurrentDictionary<string, IPackage>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGet(string path, out IPackage package) {
            return _cache.TryGetValue(path, out package);
        }

        public void Add(string path, IPackage package) {
            if (_cache.ContainsKey(path))
                return;
            if (_cache.Count == Size) {
                _cache.Remove(_cache.Keys.First(), out var _);
            }
            _cache.TryAdd(path, package);
        }
    }

    public class EncryptionKey {
        public FGuid Guid;
        public string FileName;
        public string Key;

        public EncryptionKey() {
            Guid = new();
            Key = String.Empty;
        }

        public EncryptionKey(FGuid guid, string key) {
            Guid = guid;
            Key = key;
        }
    }
}