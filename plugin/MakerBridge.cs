using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using KKAPI.Maker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KKMakerBridge
{
    [BepInPlugin("local.koikatu.makerbridge", "KK Maker Bridge", "0.2.0")]
    [BepInDependency("marco.kkapi")]
    [BepInProcess("Koikatu.exe")]
    public sealed partial class MakerBridge : BaseUnityPlugin
    {
        private readonly Queue<Work> queue = new Queue<Work>();
        private readonly Stack<ShapeSnapshot> undo = new Stack<ShapeSnapshot>();
        private TcpListener listener;
        private volatile bool stopping;
        private string token, runtime, gameRoot;
        private bool busy, originalBackground;
        private ChaControl lastCharacter;

        private sealed class Work
        {
            public JObject Request;
            public JObject Response;
            public readonly object Gate = new object();
            public int State; // 0 queued, 1 running, 2 complete, 3 canceled
        }
        private sealed class ShapeSnapshot
        {
            public ChaControl Character;
            public float[] Face, Body;
        }

        private void Awake()
        {
            gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            runtime = Path.GetFullPath(Config.Bind<string>("Connection", "RuntimeDirectory", Path.Combine(gameRoot, "MCP/MakerBridge/runtime"), "Local endpoint and capture directory; match KK_MAKER_ENDPOINT in the MCP client").Value);
            Directory.CreateDirectory(runtime);
            token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            originalBackground = Application.runInBackground;
            Application.runInBackground = true;
            int port = Config.Bind<int>("Connection", "Port", 8766, "Loopback TCP port for MakerBridge").Value;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(8);
                File.WriteAllText(Path.Combine(runtime, "endpoint.json"), new JObject {
                    {"host", "127.0.0.1"}, {"port", port}, {"token", token},
                    {"pid", System.Diagnostics.Process.GetCurrentProcess().Id}, {"version", "0.2.0"}
                }.ToString());
                new Thread(Listen) { IsBackground = true, Name = "KK Maker Bridge" }.Start();
                MakerAPI.MakerExiting += OnMakerExit;
                MakerAPI.ReloadCustomInterface += OnCharacterReload;
                Logger.LogInfo("MakerBridge ready on loopback port " + port);
            }
            catch (Exception ex) { Logger.LogError(ex); enabled = false; }
        }

        private void OnMakerExit(object sender, EventArgs e) { undo.Clear(); lastCharacter = null; }
        private void OnCharacterReload(object sender, EventArgs e) { undo.Clear(); }

        private void OnDestroy()
        {
            stopping = true;
            if (listener != null) listener.Stop();
            MakerAPI.MakerExiting -= OnMakerExit;
            MakerAPI.ReloadCustomInterface -= OnCharacterReload;
            Application.runInBackground = originalBackground;
            try
            {
                string p = Path.Combine(runtime, "endpoint.json");
                if (File.Exists(p) && (string)JObject.Parse(File.ReadAllText(p))["token"] == token) File.Delete(p);
            }
            catch { }
        }

        private void Listen()
        {
            while (!stopping)
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 10000;
                        using (NetworkStream stream = client.GetStream())
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                        {
                            JObject reply;
                            try
                            {
                                var sb = new StringBuilder();
                                int c;
                                while ((c = reader.Read()) != -1 && c != '\n')
                                {
                                    if (sb.Length >= 65536) throw new Exception("Request exceeds 64 KiB");
                                    sb.Append((char)c);
                                }
                                var request = JObject.Parse(sb.ToString());
                                if ((string)request["token"] != token) throw new Exception("Invalid bridge token");
                                if (request["method"] == null || request["method"].Type != JTokenType.String) throw new Exception("method must be a string");
                                if (request["params"] != null && !(request["params"] is JObject)) throw new Exception("params must be an object");
                                var work = new Work { Request = request };
                                lock (queue) { if (queue.Count >= 8) throw new Exception("Bridge busy"); queue.Enqueue(work); }
                                lock (work.Gate)
                                {
                                    DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                                    while (work.State < 2 && !stopping && DateTime.UtcNow < deadline)
                                        Monitor.Wait(work.Gate, 250);
                                    if (work.State != 2)
                                    {
                                        bool started = work.State == 1;
                                        work.State = 3;
                                        reply = Error(started ? "Operation timed out after starting; inspect state before retrying" : "Game did not process request; queued operation canceled");
                                    }
                                    else reply = work.Response;
                                }
                            }
                            catch (Exception ex) { reply = Error(ex.Message); }
                            writer.WriteLine(reply.ToString(Formatting.None));
                        }
                    }
                }
                catch (Exception ex) { if (!stopping) Logger.LogWarning("Bridge connection: " + ex.Message); }
            }
        }

        private static JObject Error(string message) { return new JObject { {"ok", false}, {"error", message} }; }
        private static JObject Ok(JToken result) { return new JObject { {"ok", true}, {"result", result} }; }

        private void Update()
        {
            var cha = MakerAPI.GetCharacterControl();
            if (cha != lastCharacter) { undo.Clear(); lastCharacter = cha; }
            if (busy) return;
            Work work = null;
            lock (queue) { if (queue.Count > 0) work = queue.Dequeue(); }
            if (work == null) return;
            lock (work.Gate) { if (work.State == 3) return; work.State = 1; }
            busy = true;
            StartCoroutine(Execute(work));
        }

        private IEnumerator Execute(Work work)
        {
            // Drive nested game coroutines ourselves so their exceptions become RPC errors.
            // Unity's default nested coroutine runner otherwise logs and loses these failures.
            var context = new OperationContext();
            var stack = new Stack<IEnumerator>();
            stack.Push(Operation((string)work.Request["method"], work.Request["params"] as JObject ?? new JObject(), context));
            Exception failure = null;
            try
            {
                while (stack.Count > 0)
                {
                    bool more = false;
                    object current = null;
                    try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
                    catch (Exception ex) { failure = ex; break; }
                    if (!more) { var done = stack.Pop() as IDisposable; if (done != null) done.Dispose(); continue; }
                    var nested = current as IEnumerator;
                    if (nested != null) stack.Push(nested);
                    else yield return current;
                }
            }
            finally
            {
                while (stack.Count > 0) { try { var disposable = stack.Pop() as IDisposable; if (disposable != null) disposable.Dispose(); } catch (Exception ex) { Logger.LogWarning(ex.Message); } }
                JObject reply = failure == null ? Ok(context.Result ?? JValue.CreateNull()) : Error(failure.Message);
                if (context.Backup != null) reply["backup_card"] = context.Backup;
                if (failure != null) Logger.LogWarning((string)work.Request["method"] + ": " + failure.Message);
                lock (work.Gate) { work.Response = reply; work.State = 2; Monitor.PulseAll(work.Gate); }
                busy = false;
            }
        }

        private sealed class OperationContext { public JToken Result; public string Backup; }

        private IEnumerator Operation(string method, JObject p, OperationContext context)
        {
            if (method == "set_hair") { yield return EditHair(Character(), p, context); yield break; }
            if (method == "set_clothes") { yield return EditClothes(Character(), p, context); yield break; }
            if (method == "load_card") { yield return LoadCard(Character(), p, context); yield break; }
            if (method == "capture" || method == "save_card") yield return new WaitForEndOfFrame();
            context.Result = Dispatch(method, p);
        }

        private ChaControl Character()
        {
            if (!MakerAPI.InsideAndLoaded) throw new Exception("Open Character Maker and wait for it to finish loading");
            var cha = MakerAPI.GetCharacterControl();
            if (cha == null) throw new Exception("No Maker character available");
            return cha;
        }

        private JToken Dispatch(string method, JObject p)
        {
            if (method == "status") return new JObject {
                {"bridge_version", "0.2.0"}, {"inside_maker", MakerAPI.InsideMaker},
                {"maker_ready", MakerAPI.InsideAndLoaded}, {"unity", Application.unityVersion},
                {"game_root", gameRoot}, {"undo_count", undo.Count}
            };
            var cha = Character();
            switch (method)
            {
                case "get_character": return CharacterInfo(cha);
                case "set_shapes": return SetShapes(cha, p);
                case "undo_shapes": return UndoShapes(cha);
                case "capture": return Capture(p);
                case "save_card": return SaveCard(cha, p);
                case "verify_card": return VerifyCard(cha, p);
                case "get_camera": return CameraInfo();
                case "set_camera": return SetCamera(p);
                case "list_assets": return ListAssets(p);
                case "get_appearance": return Appearance(cha);
                case "list_cards": return ListCards(cha, p);
                default: throw new Exception("Unknown method: " + method);
            }
        }

        private static JArray Shapes(float[] values, Type enumType)
        {
            var rows = new JArray();
            for (int i = 0; i < values.Length; i++) rows.Add(new JObject {
                {"index", i}, {"name", Enum.GetName(enumType, i) ?? ("shape_" + i)},
                {"value", values[i]}, {"percent", values[i] * 100f}
            });
            return rows;
        }

        private JObject CharacterInfo(ChaControl cha)
        {
            return new JObject {
                {"name", cha.chaFile.parameter.fullname}, {"sex", cha.sex == 0 ? "male" : "female"},
                {"face", Shapes(cha.chaFile.custom.face.shapeValueFace, typeof(ChaFileDefine.FaceShapeIdx))},
                {"body", Shapes(cha.chaFile.custom.body.shapeValueBody, typeof(ChaFileDefine.BodyShapeIdx))},
                {"undo_count", undo.Count}, {"value_units", "normalized: 0.5 = 50 in the standard Maker UI"}
            };
        }

        private static float Number(JToken value, string name, float min, float max)
        {
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) throw new Exception(name + " must be numeric");
            float n = (float)value;
            if (float.IsNaN(n) || float.IsInfinity(n) || n < min || n > max) throw new Exception(name + " must be between " + min + " and " + max);
            return n;
        }

        private static Dictionary<int, float> ParseChanges(JToken raw, float[] values, Type names)
        {
            var result = new Dictionary<int, float>();
            if (raw == null) return result;
            var changes = raw as JObject;
            if (changes == null) throw new Exception("Shapes must be an object keyed by enum name or index");
            foreach (var prop in changes.Properties())
            {
                int index;
                if (!int.TryParse(prop.Name, out index))
                {
                    try { index = Convert.ToInt32(Enum.Parse(names, prop.Name, true)); }
                    catch { throw new Exception("Unknown shape: " + prop.Name); }
                }
                if (index < 0 || index >= values.Length) throw new Exception("Shape index out of range: " + index);
                // Existing out-of-range modded values can be read/undone; v0.1 edits use vanilla bounds.
                if (result.ContainsKey(index)) throw new Exception("Duplicate shape index: " + index);
                result.Add(index, Number(prop.Value, prop.Name, 0, 1));
            }
            return result;
        }

        private static ShapeSnapshot Snapshot(ChaControl cha)
        {
            return new ShapeSnapshot { Character = cha, Face = (float[])cha.chaFile.custom.face.shapeValueFace.Clone(), Body = (float[])cha.chaFile.custom.body.shapeValueBody.Clone() };
        }

        private static void RefreshShapes(ChaControl cha)
        {
            cha.UpdateShapeFaceValueFromCustomInfo();
            cha.UpdateShapeBodyValueFromCustomInfo();
            cha.UpdateShapeFace();
            cha.UpdateShapeBody();
            var maker = MakerAPI.GetMakerBase();
            maker.updateCvsFaceShapeAll = true;
            maker.updateCvsBodyShapeAll = true;
            maker.updateCustomUI = true;
        }

        private static void Restore(ShapeSnapshot snap)
        {
            Array.Copy(snap.Face, snap.Character.chaFile.custom.face.shapeValueFace, snap.Face.Length);
            Array.Copy(snap.Body, snap.Character.chaFile.custom.body.shapeValueBody, snap.Body.Length);
            RefreshShapes(snap.Character);
        }

        private JToken SetShapes(ChaControl cha, JObject p)
        {
            var face = ParseChanges(p["face"], cha.chaFile.custom.face.shapeValueFace, typeof(ChaFileDefine.FaceShapeIdx));
            var body = ParseChanges(p["body"], cha.chaFile.custom.body.shapeValueBody, typeof(ChaFileDefine.BodyShapeIdx));
            if (face.Count + body.Count == 0) throw new Exception("Specify at least one face or body shape");
            if (undo.Count >= 100) throw new Exception("Undo history full; undo changes or reload the character");
            var before = Snapshot(cha);
            try
            {
                foreach (var kv in face) if (!cha.SetShapeFaceValue(kv.Key, kv.Value)) throw new Exception("Face shape rejected: " + kv.Key);
                foreach (var kv in body) if (!cha.SetShapeBodyValue(kv.Key, kv.Value)) throw new Exception("Body shape rejected: " + kv.Key);
                RefreshShapes(cha);
                foreach (var kv in face) if (Math.Abs(cha.chaFile.custom.face.shapeValueFace[kv.Key] - kv.Value) > 0.00001f) throw new Exception("This character model did not accept face shape " + kv.Key);
                foreach (var kv in body) if (Math.Abs(cha.chaFile.custom.body.shapeValueBody[kv.Key] - kv.Value) > 0.00001f) throw new Exception("This character model did not accept body shape " + kv.Key);
            }
            catch { Restore(before); throw; }
            undo.Push(before);
            return CharacterInfo(cha);
        }

        private JToken UndoShapes(ChaControl cha)
        {
            if (undo.Count == 0) throw new Exception("No shape changes to undo");
            var snap = undo.Peek();
            if (snap.Character != cha) { undo.Clear(); throw new Exception("Character changed; undo history cleared"); }
            Restore(snap);
            undo.Pop();
            return CharacterInfo(cha);
        }

        private JToken Capture(JObject p)
        {
            int width = (int)Number(p["width"] ?? new JValue(1280), "width", 256, 2048);
            int height = (int)Number(p["height"] ?? new JValue(720), "height", 256, 2048);
            string folder = Path.Combine(runtime, "captures");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "maker_" + Guid.NewGuid().ToString("N") + ".png");
            RenderTexture render = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            try
            {
                Screencap.ScreenshotManager.FirePreCapture();
                render = Screencap.ScreenshotManager.CaptureRender(width, height, 1, null);
                if (render == null) throw new Exception("Screencap returned no render");
                RenderTexture.active = render;
                image = new Texture2D(render.width, render.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
                return new JObject { {"path", path}, {"width", render.width}, {"height", render.height} };
            }
            finally
            {
                RenderTexture.active = previous;
                if (image != null) Destroy(image);
                if (render != null) RenderTexture.ReleaseTemporary(render);
                Screencap.ScreenshotManager.FirePostCapture();
            }
        }

        private static string SafeFilename(JObject p)
        {
            string name = (string)p["name"];
            if (string.IsNullOrEmpty(name)) name = "maker_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            if (name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) throw new Exception("Use a simple filename, without a directory");
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
            return name;
        }

        private JToken SaveCard(ChaControl cha, JObject p)
        {
            return SaveCardTo(cha, p, false);
        }

        private JToken SaveCardTo(ChaControl cha, JObject p, bool backup)
        {
            string dir = Path.Combine(gameRoot, "UserData/chara/" + (cha.sex == 0 ? "male" : "female") + "/MCP");
            if (backup) dir = Path.Combine(dir, "_backups");
            RejectLinks(dir, Path.GetFullPath(Path.Combine(gameRoot, "UserData/chara")));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SafeFilename(p));
            if (File.Exists(path)) throw new Exception("Card already exists; choose another name");
            var maker = MakerAPI.GetMakerBase();
            var capture = UnityEngine.Object.FindObjectOfType<ChaCustom.CustomCapture>();
            if (capture == null) throw new Exception("Maker card capture component not found");
            cha.chaFile.pngData = capture.CapCharaCard(false, maker.saveFrameAssist);
            cha.chaFile.facePngData = capture.CapCharaFace(false);
            // Flush the active coordinate; it is a working copy separate from card coordinates.
            cha.AssignCoordinate((ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            // Public game save entry point runs the installed ExtendedSave hooks.
            if (!cha.chaFile.SaveCharaFile(path, cha.sex, false)) throw new Exception("Game rejected card save");
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new Exception("Game did not create the card");
            var result = (JObject)VerifyCard(cha, new JObject { {"path", path} });
            result["path"] = path;
            result["bytes"] = new FileInfo(path).Length;
            result["verification_scope"] = "Card parses; sex/name/vanilla face/body shapes and exposed hair/clothes values compared. Other plugin data not verified.";
            return result;
        }

        private JToken VerifyCard(ChaControl cha, JObject p)
        {
            string path = CardPath((string)p["path"]);
            var card = new ChaFileControl();
            if (!card.LoadCharaFile(path, cha.sex, false, true)) throw new Exception("Game could not read the card");
            bool face = card.custom.face.shapeValueFace.SequenceEqual(cha.chaFile.custom.face.shapeValueFace);
            bool body = card.custom.body.shapeValueBody.SequenceEqual(cha.chaFile.custom.body.shapeValueBody);
            bool hair = JToken.DeepEquals(HairInfo(card.custom.hair), HairInfo(cha.chaFile.custom.hair));
            bool clothes = JToken.DeepEquals(ClothesInfo(card.coordinate[cha.chaFile.status.coordinateType].clothes), ClothesInfo(cha.nowCoordinate.clothes));
            return new JObject { {"readable", true}, {"name", card.parameter.fullname}, {"face_matches", face}, {"body_matches", body}, {"hair_matches", hair}, {"clothes_matches", clothes}, {"sex_matches", card.parameter.sex == cha.sex}, {"name_matches", card.parameter.fullname == cha.chaFile.parameter.fullname} };
        }

        private static BaseCameraControl_Ver2 CameraControl()
        {
            var control = MakerAPI.GetMakerBase().customCtrl.GetComponentInChildren<BaseCameraControl_Ver2>(true);
            if (control == null) control = UnityEngine.Object.FindObjectsOfType<CameraControl_Ver2>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy);
            if (control == null) throw new Exception("Maker camera controller not found");
            return control;
        }
        private static JArray Vec(Vector3 v) { return new JArray(v.x, v.y, v.z); }
        private static Vector3 ParseVec(JToken token, string name)
        {
            var v = token as JArray;
            if (v == null || v.Count != 3) throw new Exception(name + " must have three numbers");
            return new Vector3(Number(v[0], name, -100, 100), Number(v[1], name, -100, 100), Number(v[2], name, -100, 100));
        }
        private static JObject CameraInfo()
        {
            var c = CameraControl();
            return new JObject { {"target", Vec(c.TargetPos)}, {"rotation", Vec(c.CameraAngle)}, {"direction", Vec(c.CameraDir)}, {"fov", c.CameraFov} };
        }
        private static JToken SetCamera(JObject p)
        {
            var c = CameraControl();
            // Validate the whole request before changing anything.
            Vector3 target = p["target"] == null ? c.TargetPos : ParseVec(p["target"], "target");
            Vector3 direction = p["direction"] == null ? c.CameraDir : ParseVec(p["direction"], "direction");
            Vector3 rotation = c.CameraAngle;
            if (p["rotation"] != null)
            {
                var v = p["rotation"] as JArray;
                if (v == null || v.Count != 3) throw new Exception("rotation must have three numbers");
                rotation = new Vector3(Number(v[0], "pitch", -360, 360), Number(v[1], "yaw", -360, 360), Number(v[2], "roll", -360, 360));
            }
            float fov = p["fov"] == null ? c.CameraFov : Number(p["fov"], "fov", 10, 90);
            c.TargetPos = target; c.CameraDir = direction; c.CameraAngle = rotation; c.CameraFov = fov;
            return CameraInfo();
        }

        private static JToken ListAssets(JObject p)
        {
            string category = (string)p["category"];
            if (string.IsNullOrEmpty(category)) return JArray.FromObject(Enum.GetNames(typeof(ChaListDefine.CategoryNo)));
            ChaListDefine.CategoryNo cat;
            try { cat = (ChaListDefine.CategoryNo)Enum.Parse(typeof(ChaListDefine.CategoryNo), category, true); }
            catch { throw new Exception("Unknown asset category"); }
            int offset = (int)Number(p["offset"] ?? new JValue(0), "offset", 0, 100000);
            int limit = (int)Number(p["limit"] ?? new JValue(30), "limit", 1, 100);
            string search = (string)p["search"] ?? "";
            var entries = Singleton<Manager.Character>.Instance.chaListCtrl.GetCategoryInfo(cat);
            if (entries == null) return new JObject { {"total", 0}, {"items", new JArray()} };
            var found = entries.OrderBy(x => x.Key).Where(x => string.IsNullOrEmpty(search) || (x.Value.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return new JObject { {"total", found.Count}, {"items", new JArray(found.Skip(offset).Take(limit).Select(x => new JObject { {"id", x.Key}, {"name", x.Value.Name}, {"sex_metadata", x.Value.GetInfo(ChaListDefine.KeyType.Sex)} }))} };
        }
    }
}
