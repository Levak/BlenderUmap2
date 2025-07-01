﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Extensions.Logging;
using Unreal.Core.Models.Enums;

namespace BlenderUmap.Extensions;

public class NullPackage : AbstractUePackage {
    public NullPackage(string name, IFileProvider provider) : base(name, provider) {
    }

    public override FPackageFileSummary Summary => throw new NotImplementedException();

    public override FNameEntrySerialized[] NameMap => throw new NotImplementedException();

    //public override Lazy<UObject>[] ExportsLazy => throw new NotImplementedException();

    public override int GetExportIndex(string name, StringComparison comparisonType = StringComparison.Ordinal) => throw new NotImplementedException();

    public override int ImportMapLength => throw new NotImplementedException();
    public override int ExportMapLength => throw new NotImplementedException();

    public UObject GetExportOrNull(string name, StringComparison comparisonType = StringComparison.Ordinal) {
        throw new NotImplementedException();
    }

    public override ResolvedObject ResolvePackageIndex(FPackageIndex index) {
        throw new NotImplementedException();
    }
}

public class ReplayExporter
{
    public static IPackage ExportAndProduceProcessed(string obj, MyFileProvider provider) {
            var comps = new JArray();

            var rr = new ReplayReader(null, ParseMode.Full); //new Logger<ReplayExporter>(new SerilogLoggerFactory())

            try {
                var replay = rr.ReadReplay(File.OpenRead(obj));
            }
            catch (Exception e) {
                throw new ParserException("corrupted or unsupported replay file", e);
            }

            var lights = new List<LightInfo2>();

            // var channels = rr.Channels.ToList().FindAll(x => x != null).ToArray();
            var actors = rr.Builder._actor_actors.Values.ToArray();
            for (var index = 0; index < actors.Length; index++)
            {
                if (index % 100 == 0) { // every 100th actor
                    GC.Collect();
                }

                // var channel = channels[index];
                // if (channel == null) continue;
                // if (channel.Actor == null || (channel.Actor.GetObject() == null || !provider.TryLoadObject(channel.Actor.GetObject(), out var record) ))
                //     continue;
                //
                var actor = actors[index];
                UObject record = null;
                // if (provider.TryLoadPackage(actor.GetObject(), out var pkg_)) {
                //     record = pkg_.GetExportOrNull("")
                // }

                if ((actor.GetObject() == null || !provider.TryLoadPackageObject(actor.GetObject(), out record)))
                    continue;

                var ac = record;
                // UObject staticMeshComp = new UObject();
                FPackageIndex mesh = new FPackageIndex();
                if (ac is UBlueprintGeneratedClass actorBlueprint) {
                    ac = actorBlueprint.ClassDefaultObject.Load();
                }

                // mesh = ac?.GetOrDefault<FPackageIndex>("StaticMesh");
                var staticMeshComp = ac?.GetOrDefault<UObject>("StaticMeshComponent");
                mesh = (staticMeshComp as UStaticMeshComponent)?.GetStaticMesh();
                
                if (staticMeshComp != null && mesh is { IsNull: true}) {
                    foreach (var export in ac.Owner.GetExports()) {
                        if (export.ExportType == "StaticMeshComponent") {
                            staticMeshComp = export as UStaticMeshComponent;
                            mesh = (export as UStaticMeshComponent).GetStaticMesh();
                            break;
                        }
                        if (mesh == null) {
                            // look in parent struct if not found
                            var super = (record as UBlueprintGeneratedClass)?.SuperStruct.Load<UBlueprintGeneratedClass>();
                            if (super == null) continue;
                            foreach (var actorExp in super.Owner.GetExports()) {
                                if (actorExp.ExportType != "FortKillVolume_C" && (mesh = actorExp.GetOrDefault<FPackageIndex>("StaticMesh")) != null) {
                                    break;
                                }
                            }
                        }
                    }
                }

                if (mesh == null || mesh.IsNull) continue;

                Log.Information("Loading {0}: {1}/{2} {3}", actor.Level, index, actors.Length, ac);

                var comp = new JArray();
                comps.Add(comp);
                comp.Add(Guid.NewGuid().ToString().Replace("-", ""));
                comp.Add(ac.Name);

                var matsObj = new JObject(); // matpath: [4x[str]]
                var textureDataArr = new List<Dictionary<string, string>>();
                var materials = new List<Program.Mat>();
                Program.ExportMesh(mesh, materials);

                if (Program.config.bReadMaterials) {
                    var material = ac.GetOrDefault<FPackageIndex>("BaseMaterial");
                    var overrideMaterials = staticMeshComp?.GetOrDefault<List<FPackageIndex>>("OverrideMaterials");

                    var textureDatas = ac.GetProps<FPackageIndex>("TextureData"); // /Script/FortniteGame.BuildingSMActor:TextureData
                    for (var texIndex = 0; texIndex < textureDatas.Length; texIndex++) {
                        var textureDataIdx = textureDatas[texIndex];
                        var td = textureDataIdx?.Load();

                        if (td != null) {
                            var textures = new Dictionary<string, string>();
                            Program.AddToArray(textures, td.GetOrDefault<FPackageIndex>("Diffuse"), texIndex == 0 ? "Diffuse" : $"Diffuse_Texture_{texIndex+1}");
                            Program.AddToArray(textures, td.GetOrDefault<FPackageIndex>("Normal"),  texIndex == 0 ? "Normals" : $"Normals_Texture_{texIndex+1}");
                            Program.AddToArray(textures, td.GetOrDefault<FPackageIndex>("Specular"), texIndex == 0 ? "SpecularMasks" : $"SpecularMasks_{texIndex+1}");
                            textureDataArr.Add(textures);
                            var overrideMaterial = td.GetOrDefault<FPackageIndex>("OverrideMaterial");
                            if (overrideMaterial is {IsNull: false}) {
                                material = overrideMaterial;
                            }
                        } else {
                            textureDataArr.Add(new Dictionary<string, string>());
                        }
                    }

                    for (int i = 0; i < materials.Count; i++) {
                        var mat = materials[i];
                        if (material != null) {
                            var matIndex = overrideMaterials != null && i < overrideMaterials.Count && overrideMaterials[i] != null ? overrideMaterials[i] : material;
                            mat.Material = matIndex?.ResolvedObject;
                        }

                        mat.PopulateTextures();
                        mat.AddToObj(matsObj, textureDataArr);
                    }
                }

                var children = new JArray();
                comp.Add(Program.PackageIndexToDirPath(mesh));
                comp.Add(matsObj);
                comp.Add(JArray.FromObject(textureDataArr));
                comp.Add(Vector(actor.Location));
                comp.Add(Rotator(actor.Rotation));
                comp.Add(Vector(actor.Scale));
                comp.Add(children);

                int LightIndex = 0;
                if (Program.CheckIfHasLights(ac.Owner, out var lightinfo)) {
                    var infor = new LightInfo2() {
                        Props = lightinfo.ToArray()
                    };
                    // X               Y                 Z
                    // rotator.Roll2, -rotator.Pitch0, -rotator.Yaw1
                    lights.Add(infor);
                    LightIndex = lights.Count;
                }
                comp.Add(LightIndex);
            }

            string pkgName = "Replay\\" + obj.SubstringAfterLast("\\");
            var file = new FileInfo(Path.Combine(MyFileProvider.JSONS_FOLDER.ToString(), pkgName + ".processed.json"));
            file.Directory?.Create();
            Log.Information("Writing to {0}", file.FullName);

            using var writer = file.CreateText();
            new JsonSerializer().Serialize(writer, comps);

            var file2 = new FileInfo(Path.Combine(MyFileProvider.JSONS_FOLDER.ToString(), pkgName + ".lights.processed.json"));
            file2.Directory?.Create();

            using var writer2 = file2.CreateText();
#if DEBUG
            new JsonSerializer() { Formatting = Formatting.Indented }.Serialize(writer2, lights);
#else
            new JsonSerializer().Serialize(writer2, lights);
#endif

            return new NullPackage("/"+pkgName.Replace("\\", "/"), null);
    }

    public static JArray Vector(Unreal.Core.Models.FVector vector) => new() {vector.X, vector.Y, vector.Z};
    public static JArray Rotator(Unreal.Core.Models.FRotator rotator) => new() {rotator.Pitch, rotator.Yaw, rotator.Roll};
    public static JArray Quat(Unreal.Core.Models.FQuat quat) => new() {quat.X, quat.Y, quat.Z, quat.W};

    public static FVector ToFVector(Unreal.Core.Models.FVector vector) {
        return new FVector((float)vector.X, (float)vector.Y, (float)vector.Z);
    }

    public static FRotator ToRotator(Unreal.Core.Models.FRotator vector) {
        return new FRotator(vector.Pitch, vector.Yaw, vector.Roll);
    }
}