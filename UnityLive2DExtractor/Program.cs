using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using AssetStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityLive2DExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 1)
                return;
            if (!Directory.Exists(args[0]))
                return;
            Console.WriteLine($"Loading...");
            var assetsManager = new AssetsManager();
            assetsManager.LoadFolder(args[0]);
            if (assetsManager.assetsFileList.Count == 0)
                return;
            var containers = new Dictionary<AssetStudio.Object, string>();
            var cubismMocs = new List<MonoBehaviour>();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    switch (asset)
                    {
                        case MonoBehaviour m_MonoBehaviour:
                            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                            {
                                if (m_Script.m_ClassName == "CubismMoc")
                                {
                                    cubismMocs.Add(m_MonoBehaviour);
                                }
                            }
                            break;
                        case AssetBundle m_AssetBundle:
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (int k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = m_AssetBundle.m_PreloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;
                    }
                }
            }
            var basePathList = new List<string>();
            foreach (var cubismMoc in cubismMocs)
            {
                var container = containers[cubismMoc];
                var basePath = container.Substring(0, container.LastIndexOf("/"));
                basePathList.Add(basePath);
            }
            var lookup = containers.ToLookup(x => basePathList.Find(b => x.Value.Contains(b)), x => x.Key);
            var baseDestPath = Path.Combine(Path.GetDirectoryName(args[0]), "Live2DOutput");
            foreach (var assets in lookup)
            {
                if (assets.Key == null)
                    continue;
                var name = assets.Key.Substring(assets.Key.LastIndexOf("/") + 1);
                Console.WriteLine($"Extract {name}");

                var destPath = Path.Combine(baseDestPath, name) + Path.DirectorySeparatorChar;
                var destTexturePath = Path.Combine(destPath, "textures") + Path.DirectorySeparatorChar;
                var destAnimationPath = Path.Combine(destPath, "motions") + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(destPath);
                Directory.CreateDirectory(destTexturePath);
                Directory.CreateDirectory(destAnimationPath);

                //MonoBehaviour
                var monoBehaviours = assets.OfType<MonoBehaviour>().ToArray();
                //physics
                var physics = monoBehaviours.FirstOrDefault(x =>
                {
                    if (x.m_Script.TryGet(out var m_Script))
                    {
                        return m_Script.m_ClassName == "CubismPhysicsController";
                    }
                    return false;
                });
                if (physics != null)
                {
                    File.WriteAllText($"{destPath}{name}.physics3.json", ParsePhysics(physics));
                }
                //moc
                var moc = monoBehaviours.First(x =>
                {
                    if (x.m_Script.TryGet(out var m_Script))
                    {
                        return m_Script.m_ClassName == "CubismMoc";
                    }
                    return false;
                });
                File.WriteAllBytes($"{destPath}{name}.moc3", ParseMoc(moc));
                //texture
                var textures = new SortedSet<string>();
                foreach (var texture2D in assets.OfType<Texture2D>())
                {
                    using (var bitmap = new Texture2DConverter(texture2D).ConvertToBitmap(true))
                    {
                        textures.Add($"textures/{texture2D.m_Name}.png");
                        bitmap.Save($"{destTexturePath}{texture2D.m_Name}.png", ImageFormat.Png);
                    }
                }
                //motions
                var motions = new List<string>();
                var animator = (Animator)assets.First(x => x is Animator);
                var animations = assets.OfType<AnimationClip>().ToArray();
                animator.m_GameObject.TryGet(out GameObject rootGameObject);
                var converter = new CubismMotion3Converter(rootGameObject, animations);
                foreach (ImportedKeyframedAnimation animation in converter.AnimationList)
                {
                    var json = new CubismMotion3Json
                    {
                        Version = 3,
                        Meta = new CubismMotion3Json.SerializableMeta
                        {
                            Duration = animation.Duration,
                            Fps = animation.SampleRate,
                            Loop = true,
                            AreBeziersRestricted = true,
                            CurveCount = animation.TrackList.Count,
                            UserDataCount = animation.Events.Count
                        },
                        Curves = new CubismMotion3Json.SerializableCurve[animation.TrackList.Count]
                    };
                    int totalSegmentCount = 1;
                    int totalPointCount = 1;
                    for (int i = 0; i < animation.TrackList.Count; i++)
                    {
                        var track = animation.TrackList[i];
                        json.Curves[i] = new CubismMotion3Json.SerializableCurve
                        {
                            Target = track.Target,
                            Id = track.Name,
                            Segments = new List<float> { 0f, track.Curve[0].value }
                        };
                        for (var j = 1; j < track.Curve.Count; j++)
                        {
                            var curve = track.Curve[j];
                            var preCurve = track.Curve[j - 1];
                            if (Math.Abs(curve.time - preCurve.time - 0.01f) < 0.0001f) //InverseSteppedSegment
                            {
                                var nextCurve = track.Curve[j + 1];
                                if (nextCurve.value == curve.value)
                                {
                                    json.Curves[i].Segments.Add(3f);
                                    json.Curves[i].Segments.Add(nextCurve.time);
                                    json.Curves[i].Segments.Add(nextCurve.value);
                                    j += 1;
                                    totalPointCount += 1;
                                    totalSegmentCount++;
                                    continue;
                                }
                            }
                            if (float.IsPositiveInfinity(curve.inSlope)) //SteppedSegment
                            {
                                json.Curves[i].Segments.Add(2f);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 1;
                            }
                            else if (preCurve.outSlope == 0f && Math.Abs(curve.inSlope) < 0.0001f) //LinearSegment
                            {
                                json.Curves[i].Segments.Add(0f);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 1;
                            }
                            else //BezierSegment
                            {
                                var tangentLength = (curve.time - preCurve.time) / 3f;
                                json.Curves[i].Segments.Add(1f);
                                json.Curves[i].Segments.Add(preCurve.time + tangentLength);
                                json.Curves[i].Segments.Add(preCurve.outSlope * tangentLength + preCurve.value);
                                json.Curves[i].Segments.Add(curve.time - tangentLength);
                                json.Curves[i].Segments.Add(curve.value - curve.inSlope * tangentLength);
                                json.Curves[i].Segments.Add(curve.time);
                                json.Curves[i].Segments.Add(curve.value);
                                totalPointCount += 3;
                            }
                            totalSegmentCount++;
                        }
                    }
                    json.Meta.TotalSegmentCount = totalSegmentCount;
                    json.Meta.TotalPointCount = totalPointCount;

                    json.UserData = new CubismMotion3Json.SerializableUserData[animation.Events.Count];
                    var totalUserDataSize = 0;
                    for (var i = 0; i < animation.Events.Count; i++)
                    {
                        var @event = animation.Events[i];
                        json.UserData[i] = new CubismMotion3Json.SerializableUserData
                        {
                            Time = @event.time,
                            Value = @event.value
                        };
                        totalUserDataSize += @event.value.Length;
                    }
                    json.Meta.TotalUserDataSize = totalUserDataSize;

                    motions.Add($"motions/{animation.Name}.motion3.json");
                    File.WriteAllText($"{destAnimationPath}{animation.Name}.motion3.json", JsonConvert.SerializeObject(json, Formatting.Indented, new MyJsonConverter()));
                }
                //model
                var job = new JObject();
                var jarray = new JArray();
                foreach (var motion in motions)
                {
                    var tempjob = new JObject();
                    tempjob["File"] = motion;
                    jarray.Add(tempjob);
                }
                job[""] = jarray;

                var groups = new List<CubismModel3Json.SerializableGroup>();
                var eyeBlinkParameters = monoBehaviours.Where(x =>
                {
                    x.m_Script.TryGet(out var m_Script);
                    return m_Script.m_ClassName == "CubismEyeBlinkParameter";
                }).Select(x =>
                {
                    x.m_GameObject.TryGet(out var m_GameObject);
                    return m_GameObject.m_Name;
                }).ToArray();
                if (eyeBlinkParameters.Length > 0)
                {
                    groups.Add(new CubismModel3Json.SerializableGroup
                    {
                        Target = "Parameter",
                        Name = "EyeBlink",
                        Ids = eyeBlinkParameters
                    });
                }
                var lipSyncParameters = monoBehaviours.Where(x =>
                {
                    x.m_Script.TryGet(out var m_Script);
                    return m_Script.m_ClassName == "CubismMouthParameter";
                }).Select(x =>
                {
                    x.m_GameObject.TryGet(out var m_GameObject);
                    return m_GameObject.m_Name;
                }).ToArray();
                if (lipSyncParameters.Length > 0)
                {
                    groups.Add(new CubismModel3Json.SerializableGroup
                    {
                        Target = "Parameter",
                        Name = "LipSync",
                        Ids = lipSyncParameters
                    });
                }

                var model3 = new CubismModel3Json
                {
                    Version = 3,
                    FileReferences = new CubismModel3Json.SerializableFileReferences
                    {
                        Moc = $"{name}.moc3",
                        Textures = textures.ToArray(),
                        //Physics = $"{name}.physics3.json",
                        Motions = job
                    },
                    Groups = groups.ToArray()
                };
                if (physics != null)
                {
                    model3.FileReferences.Physics = $"{name}.physics3.json";
                }
                File.WriteAllText($"{destPath}{name}.model3.json", JsonConvert.SerializeObject(model3, Formatting.Indented));
            }
            Console.WriteLine("Done!");
            Console.Read();
        }

        private static string ParsePhysics(MonoBehaviour physics)
        {
            var reader = physics.reader;
            reader.Reset();
            reader.Position += 28; //PPtr<GameObject> m_GameObject, m_Enabled, PPtr<MonoScript>
            reader.ReadAlignedString(); //m_Name
            var cubismPhysicsRig = new CubismPhysicsRig(reader);

            var physicsSettings = new CubismPhysics3Json.SerializablePhysicsSettings[cubismPhysicsRig.SubRigs.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                var subRigs = cubismPhysicsRig.SubRigs[i];
                physicsSettings[i] = new CubismPhysics3Json.SerializablePhysicsSettings
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Input = new CubismPhysics3Json.SerializableInput[subRigs.Input.Length],
                    Output = new CubismPhysics3Json.SerializableOutput[subRigs.Output.Length],
                    Vertices = new CubismPhysics3Json.SerializableVertex[subRigs.Particles.Length],
                    Normalization = new CubismPhysics3Json.SerializableNormalization
                    {
                        Position = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Position.Minimum,
                            Default = subRigs.Normalization.Position.Default,
                            Maximum = subRigs.Normalization.Position.Maximum
                        },
                        Angle = new CubismPhysics3Json.SerializableNormalizationValue
                        {
                            Minimum = subRigs.Normalization.Angle.Minimum,
                            Default = subRigs.Normalization.Angle.Default,
                            Maximum = subRigs.Normalization.Angle.Maximum
                        }
                    }
                };
                for (int j = 0; j < subRigs.Input.Length; j++)
                {
                    var input = subRigs.Input[j];
                    physicsSettings[i].Input[j] = new CubismPhysics3Json.SerializableInput
                    {
                        Source = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = input.SourceId
                        },
                        Weight = input.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), input.SourceComponent),
                        Reflect = input.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Output.Length; j++)
                {
                    var output = subRigs.Output[j];
                    physicsSettings[i].Output[j] = new CubismPhysics3Json.SerializableOutput
                    {
                        Destination = new CubismPhysics3Json.SerializableParameter
                        {
                            Target = "Parameter", //同名GameObject父节点的名称
                            Id = output.DestinationId
                        },
                        VertexIndex = output.ParticleIndex,
                        Scale = output.AngleScale,
                        Weight = output.Weight,
                        Type = Enum.GetName(typeof(CubismPhysicsSourceComponent), output.SourceComponent),
                        Reflect = output.IsInverted
                    };
                }
                for (int j = 0; j < subRigs.Particles.Length; j++)
                {
                    var particles = subRigs.Particles[j];
                    physicsSettings[i].Vertices[j] = new CubismPhysics3Json.SerializableVertex
                    {
                        Position = new CubismPhysics3Json.SerializableVector2
                        {
                            X = particles.InitialPosition.X,
                            Y = particles.InitialPosition.Y
                        },
                        Mobility = particles.Mobility,
                        Delay = particles.Delay,
                        Acceleration = particles.Acceleration,
                        Radius = particles.Radius
                    };
                }
            }
            var physicsDictionary = new CubismPhysics3Json.SerializablePhysicsDictionary[physicsSettings.Length];
            for (int i = 0; i < physicsSettings.Length; i++)
            {
                physicsDictionary[i] = new CubismPhysics3Json.SerializablePhysicsDictionary
                {
                    Id = $"PhysicsSetting{i + 1}",
                    Name = $"Dummy{i + 1}"
                };
            }
            var physicsJson = new CubismPhysics3Json
            {
                Version = 3,
                Meta = new CubismPhysics3Json.SerializableMeta
                {
                    PhysicsSettingCount = cubismPhysicsRig.SubRigs.Length,
                    TotalInputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Input.Length),
                    TotalOutputCount = cubismPhysicsRig.SubRigs.Sum(x => x.Output.Length),
                    VertexCount = cubismPhysicsRig.SubRigs.Sum(x => x.Particles.Length),
                    EffectiveForces = new CubismPhysics3Json.SerializableEffectiveForces
                    {
                        Gravity = new CubismPhysics3Json.SerializableVector2
                        {
                            X = 0,
                            Y = -1
                        },
                        Wind = new CubismPhysics3Json.SerializableVector2
                        {
                            X = 0,
                            Y = 0
                        }
                    },
                    PhysicsDictionary = physicsDictionary
                },
                PhysicsSettings = physicsSettings
            };
            return JsonConvert.SerializeObject(physicsJson, Formatting.Indented, new MyJsonConverter2());
        }

        private static byte[] ParseMoc(MonoBehaviour moc)
        {
            var reader = moc.reader;
            reader.Reset();
            reader.Position += 28; //PPtr<GameObject> m_GameObject, m_Enabled, PPtr<MonoScript>
            reader.ReadAlignedString(); //m_Name
            return reader.ReadBytes(reader.ReadInt32());
        }
    }
}
